using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
    /// cloudflared.exe 下载地址列表（多源竞速）。公共代理速度波动大（快时数 MB/s，慢时几十 KB/s），
    /// 单源下载经常非常慢；同时向多个源发起下载，最先完成者胜出，其余取消。
    /// </summary>
    public static readonly string[] DownloadUrls =
    [
        // 公共 GitHub 加速代理（两家独立基础设施，互相回退）
        "https://gh-proxy.com/https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-windows-amd64.exe",
        "https://gh-proxy.org/https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-windows-amd64.exe",
        // 直连 GitHub（用户开启加速器/hosts 加速时此源最快）
        "https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-windows-amd64.exe",
    ];

    /// <summary>兼容保留：默认（首选）下载地址。</summary>
    public static readonly string DownloadUrl = DownloadUrls[0];

    /// <summary>
    /// 合法 cloudflared.exe 的最小体积（字节）。官方 amd64 版本约 55MB，
    /// 用于剔除代理返回的错误页/半截文件。
    /// </summary>
    private const long MinValidExecutableSize = 40 * 1024 * 1024;

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

    /// <summary>
    /// 下载百分比进度变化时触发，参数范围为 0.0-100.0。
    /// 当无法获取总大小时，参数为 -1。
    /// </summary>
    public event Action<double>? DownloadProgressChanged;

    public CloudflaredManager(ILogger<CloudflaredManager> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// 下载 cloudflared.exe 到本地缓存目录。如果已存在则跳过。
    /// <para>
    /// 多源竞速：同时向 <see cref="DownloadUrls"/> 中的所有源发起下载，
    /// 第一个完成且文件有效的源胜出（写入最终路径），其余任务被取消并清理。
    /// 单个公共代理速度波动大，竞速可显著缩短最坏情况下的等待时间。
    /// </para>
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

        _logger.LogInformation("开始多源竞速下载 cloudflared.exe（{Count} 个源）", DownloadUrls.Length);
        var startTime = Stopwatch.StartNew();

        using var raceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var raceToken = raceCts.Token;

        var tasks = DownloadUrls.Select(url => DownloadFromSingleSourceAsync(url, raceToken)).ToList();
        string? winner = null;
        Exception? lastError = null;

        while (tasks.Count > 0 && winner == null)
        {
            var completed = await Task.WhenAny(tasks);
            tasks.Remove(completed);
            try
            {
                var tmpPath = await completed; // 失败会在这里抛出
                if (ValidateDownloadedFile(tmpPath))
                {
                    winner = tmpPath;
                }
                else
                {
                    _logger.LogWarning("源下载完成但文件无效（大小/格式不符），等待其他源");
                    TryDeleteFile(tmpPath);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                _logger.LogWarning(ex, "单个源下载失败，等待其他源");
            }
        }

        if (winner == null)
        {
            _logger.LogError(lastError, "所有下载源均失败");
            throw new InvalidOperationException($"cloudflared.exe 所有下载源均失败：{lastError?.Message}", lastError);
        }

        // 竞速结束：取消并清理剩余任务
        raceCts.Cancel();
        try
        {
            await Task.WhenAll(tasks);
        }
        catch
        {
            // 剩余任务被取消/失败属预期，忽略
        }

        // 原子替换为最终路径
        if (File.Exists(ExecutablePath)) File.Delete(ExecutablePath);
        File.Move(winner, ExecutablePath);
        startTime.Stop();
        _logger.LogInformation("cloudflared.exe 竞速下载完成：{Path}（耗时 {Elapsed:F1}s）",
            ExecutablePath, startTime.Elapsed.TotalSeconds);
    }

    /// <summary>
    /// 从单个源下载到独立的临时文件（每个源一个 .tmp.N 文件，互不冲突）。
    /// 进度按"已完成字节数最多的源"汇总上报。
    /// </summary>
    private async Task<string> DownloadFromSingleSourceAsync(string url, CancellationToken cancellationToken)
    {
        var tmpPath = ExecutablePath + $".tmp.{Guid.NewGuid():N}";
        long downloaded = 0;
        long total = -1;

        var httpClient = new HttpClient();
        // 各源独立限时：超时/失败只淘汰该源，不影响其他源继续竞速
        var perSourceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        perSourceCts.CancelAfter(TimeSpan.FromMinutes(6));
        try
        {
            httpClient.Timeout = Timeout.InfiniteTimeSpan; // 用 perSourceCts 控制整体限时

            using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, perSourceCts.Token);
            response.EnsureSuccessStatusCode();

            total = response.Content.Headers.ContentLength ?? -1;
            if (total > 0 && total < MinValidExecutableSize)
            {
                throw new InvalidOperationException($"源响应体积异常（{total} 字节，疑似错误页）");
            }

            await using var contentStream = await response.Content.ReadAsStreamAsync(perSourceCts.Token);
            await using var fileStream = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None);
            var buffer = new byte[81920];
            int read;
            while ((read = await contentStream.ReadAsync(buffer, perSourceCts.Token)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read), perSourceCts.Token);
                downloaded += read;
                DownloadProgress?.Invoke(downloaded, total);
                // 触发百分比进度（多源竞速时显示最快源的进度）
                DownloadProgressChanged?.Invoke(total > 0 ? (double)downloaded / total * 100.0 : -1);
            }

            _logger.LogInformation("源下载完成：{Url}（{Downloaded} 字节）", url, downloaded);
            return tmpPath;
        }
        catch
        {
            TryDeleteFile(tmpPath);
            throw;
        }
        finally
        {
            httpClient.Dispose();
            perSourceCts.Dispose();
        }
    }

    /// <summary>
    /// 校验下载产物：存在、体积不低于阈值、以 MZ（PE 可执行文件头）开头。
    /// </summary>
    private static bool ValidateDownloadedFile(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length < MinValidExecutableSize) return false;

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            Span<byte> header = stackalloc byte[2];
            return fs.Read(header) == 2 && header[0] == (byte)'M' && header[1] == (byte)'Z';
        }
        catch
        {
            return false;
        }
    }

    private void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "清理下载临时文件失败：{Path}", path);
        }
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

            // 清扫孤儿 cloudflared 进程。优雅退出（Stop）会终止子进程，但强杀进程、崩溃、
            // 更新自重启等路径不会经过 Stop，旧的 cloudflared 就会残留为孤儿：
            // 既持续占用隧道与网络，又持有旧版本 app-* 目录的句柄，导致更新清理
            // （CleanupPrevDeployments）删除旧目录时一直"目录被占用"而失败。
            // 本客户端启动的 quick tunnel 命令行固定含 "tunnel --url http://localhost:"，
            // 以此特征识别，避免误杀用户自行运行的命名隧道 cloudflared。
            try
            {
                foreach (var p in System.Diagnostics.Process.GetProcessesByName("cloudflared"))
                {
                    try
                    {
                        var cmdline = QueryCommandLine(p);
                        if (cmdline != null && cmdline.Contains("tunnel --url http://localhost:", StringComparison.OrdinalIgnoreCase))
                        {
                            _logger.LogInformation("清扫孤儿 cloudflared 进程（PID={Pid}）", p.Id);
                            p.Kill(entireProcessTree: true);
                        }
                    }
                    catch (Exception exChild)
                    {
                        _logger.LogDebug(exChild, "清扫 cloudflared 进程失败（PID={Pid}，可能已退出）", p.Id);
                    }
                    finally
                    {
                        p.Dispose();
                    }
                }
            }
            catch (Exception exSweep)
            {
                _logger.LogWarning(exSweep, "清扫孤儿 cloudflared 进程失败");
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

    /// <summary>
    /// 查询进程的完整命令行。孤儿 cloudflared 识别依赖它（Process 类不暴露命令行）。
    /// </summary>
    private static string? QueryCommandLine(System.Diagnostics.Process process)
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT CommandLine FROM Win32_Process WHERE ProcessId = " + process.Id);
            foreach (var o in searcher.Get())
            {
                return o["CommandLine"] as string;
            }
        }
        catch
        {
            // 权限不足或进程已退出时拿不到命令行，返回 null 由调用方跳过
        }
        return null;
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