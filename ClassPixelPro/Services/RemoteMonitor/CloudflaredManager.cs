using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ClassPixelPro.Core;
using Microsoft.Extensions.Logging;

namespace ClassPixelPro.Services.RemoteMonitor;

/// <summary>
/// cloudflared 子进程管理器：负责下载 cloudflared.exe、启动 quick tunnel、
/// 解析隧道 URL、监控进程崩溃并自动重启。
/// </summary>
/// <remarks>
/// 隧道 URL 形如 https://xxx.trycloudflare.com，由 cloudflared 输出到 stdout。
/// 本类监听 stdout 行输出，匹配到 URL 后触发 <see cref="TunnelReady"/> 事件。
/// 进程异常退出时会自动重启并重新解析 URL，重启间隔指数退避（最多 30s）。
/// </remarks>
public sealed class CloudflaredManager : IDisposable
{
    /// <summary>
    /// cloudflared.exe 下载地址（通过 gh-proxy.org 加速）。
    /// </summary>
    public const string DownloadUrl =
        "https://gh-proxy.org/https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-windows-amd64.exe";

    /// <summary>
    /// 本地缓存目录，cloudflared.exe 下载到此目录下。
    /// </summary>
    public static readonly string CacheDirectory = Path.Combine(
        CommonDirectories.AppCacheFolderPath, "RemoteMonitor");

    /// <summary>
    /// cloudflared.exe 本地完整路径。
    /// </summary>
    public static readonly string ExecutablePath = Path.Combine(CacheDirectory, "cloudflared.exe");

    private static readonly Regex TunnelUrlRegex =
        new(@"https://[a-z0-9\-]+\.trycloudflare\.com", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly ILogger<CloudflaredManager> _logger;
    private readonly object _processLock = new();
    private Process? _process;
    private CancellationTokenSource? _monitorCts;
    private Task? _monitorTask;
    private bool _disposed;
    private bool _stopping;

    /// <summary>
    /// 当前隧道 URL。解析成功后赋值，停止时清空。
    /// </summary>
    public string? CurrentTunnelUrl { get; private set; }

    /// <summary>
    /// cloudflared 是否已下载就绪。
    /// </summary>
    public bool IsExecutableReady => File.Exists(ExecutablePath);

    /// <summary>
    /// 隧道 URL 解析成功时触发。
    /// </summary>
    public event Action<string>? TunnelReady;

    /// <summary>
    /// 隧道 URL 失效（进程崩溃或停止）时触发。
    /// </summary>
    public event Action? TunnelLost;

    /// <summary>
    /// 下载进度变化时触发。downloaded/total 为 -1 表示无法获取总大小。
    /// </summary>
    public event Action<long, long>? DownloadProgress;

    public CloudflaredManager(ILogger<CloudflaredManager> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// 下载 cloudflared.exe 到本地缓存目录。如果已存在则跳过。
    /// </summary>
    /// <param name="force">是否强制重新下载。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task DownloadAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            _logger.LogWarning("cloudflared 仅在 Windows 上可用，跳过下载");
            return;
        }

        Directory.CreateDirectory(CacheDirectory);
        if (IsExecutableReady && !force)
        {
            _logger.LogInformation("cloudflared.exe 已存在，跳过下载：{Path}", ExecutablePath);
            return;
        }

        _logger.LogInformation("开始下载 cloudflared.exe：{Url}", DownloadUrl);
        using var httpClient = new HttpClient();
        // gh-proxy.org 偶尔不稳定，设置较长超时
        httpClient.Timeout = TimeSpan.FromMinutes(10);

        using var response = await httpClient.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? -1;
        long downloaded = 0;

        await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var fileStream = new FileStream(ExecutablePath + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None);
        var buffer = new byte[81920];
        int read;
        while ((read = await contentStream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            downloaded += read;
            DownloadProgress?.Invoke(downloaded, total);
        }

        fileStream.Close();
        // 原子替换
        if (File.Exists(ExecutablePath)) File.Delete(ExecutablePath);
        File.Move(ExecutablePath + ".tmp", ExecutablePath);
        _logger.LogInformation("cloudflared.exe 下载完成：{Path}（{Size} 字节）", ExecutablePath, downloaded);
    }

    /// <summary>
    /// 启动 quick tunnel，将本地端口暴露到公网。
    /// </summary>
    /// <param name="localPort">要隧道的本地端口。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public Task StartAsync(int localPort, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            _logger.LogWarning("cloudflared 仅在 Windows 上可用，不启动隧道");
            return Task.CompletedTask;
        }

        if (!IsExecutableReady)
        {
            throw new InvalidOperationException("cloudflared.exe 未下载，请先调用 DownloadAsync()");
        }

        lock (_processLock)
        {
            if (_process != null && !_process.HasExited)
            {
                _logger.LogWarning("cloudflared 进程已在运行，忽略重复启动请求");
                return Task.CompletedTask;
            }

            _stopping = false;
            _monitorCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _monitorTask = Task.Run(() => MonitorLoopAsync(localPort, _monitorCts.Token), _monitorCts.Token);
        }

        _logger.LogInformation("cloudflared 监控任务已启动，本地端口 {Port}", localPort);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 停止 cloudflared 进程并取消监控。
    /// </summary>
    public void Stop()
    {
        lock (_processLock)
        {
            _stopping = true;
            _monitorCts?.Cancel();
            KillProcess();
            CurrentTunnelUrl = null;
        }
        _logger.LogInformation("cloudflared 已停止");
    }

    private async Task MonitorLoopAsync(int localPort, CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(2);
        var maxBackoff = TimeSpan.FromSeconds(30);

        while (!ct.IsCancellationRequested && !_stopping)
        {
            try
            {
                await RunProcessOnceAsync(localPort, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested || _stopping)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "cloudflared 进程异常退出");
            }

            if (_stopping || ct.IsCancellationRequested) break;

            // 进程退出，触发 TunnelLost 并按指数退避重启
            var lostUrl = CurrentTunnelUrl;
            CurrentTunnelUrl = null;
            if (!string.IsNullOrEmpty(lostUrl))
            {
                TunnelLost?.Invoke();
            }

            _logger.LogInformation("cloudflared 将在 {Backoff} 后重启", backoff);
            try
            {
                await Task.Delay(backoff, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, maxBackoff.Ticks));
        }
    }

    private async Task RunProcessOnceAsync(int localPort, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ExecutablePath,
            Arguments = $"tunnel --url http://localhost:{localPort}",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        Process process;
        lock (_processLock)
        {
            if (_stopping) return;
            process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            if (!process.Start())
            {
                throw new InvalidOperationException("cloudflared 进程启动失败");
            }
            _process = process;
        }

        _logger.LogInformation("cloudflared 进程已启动（PID={Pid}），等待隧道 URL…", process.Id);

        // 异步读取 stdout/stderr，匹配 URL
        var stdoutTask = ReadAndScanAsync(process.StandardOutput, "stdout", ct);
        var stderrTask = ReadAndScanAsync(process.StandardError, "stderr", ct);

        // 等待进程退出
        await process.WaitForExitAsync(ct);
        _logger.LogInformation("cloudflared 进程退出（code={ExitCode}）", process.ExitCode);

        // 读取完剩余输出
        await Task.WhenAll(stdoutTask, stderrTask);
    }

    private async Task ReadAndScanAsync(StreamReader reader, string streamName, CancellationToken ct)
    {
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            _logger.LogTrace("cloudflared[{Stream}] {Line}", streamName, line);

            // 仅在尚未获取 URL 时扫描，避免重复触发
            if (string.IsNullOrEmpty(CurrentTunnelUrl))
            {
                var match = TunnelUrlRegex.Match(line);
                if (match.Success)
                {
                    var url = match.Value;
                    CurrentTunnelUrl = url;
                    _logger.LogInformation("cloudflared 隧道已就绪：{Url}", url);
                    TunnelReady?.Invoke(url);
                }
            }
        }
    }

    private void KillProcess()
    {
        lock (_processLock)
        {
            if (_process == null || _process.HasExited) return;
            try
            {
                _logger.LogInformation("终止 cloudflared 进程（PID={Pid}）", _process.Id);
                _process.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "终止 cloudflared 进程失败");
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _monitorCts?.Dispose();
        _process?.Dispose();
    }
}