using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClassPixelPro.Core;
using ClassPixelPro.Services.Management;
using ClassPixelPro.Shared.Helpers;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using static ClassPixelPro.Shared.Helpers.ConfigureFileHelper;

namespace ClassPixelPro.Services.RemoteMonitor;

/// <summary>
/// 远程监控主服务：内嵌 Kestrel HTTP 服务器，通过 Cloudflare Tunnel 暴露到公网，
/// 班主任端可远程查看摄像头画面、截取屏幕、传话播放。
/// </summary>
/// <remarks>
/// 启动流程：HTTP 服务器（随机端口 + Bearer token）→ cloudflared quick tunnel → 上报 tunnel 信息到集控后端。
/// 端点：
/// <list type="bullet">
/// <item><c>GET /screenshot</c>：截屏返回 JPEG。</item>
/// <item><c>WS /camera/stream</c>：推送摄像头 JPEG 帧（5-30fps 自适应）。</item>
/// <item><c>WS /audio/receive</c>：接收 PCM 音频并播放。</item>
/// <item><c>GET /status</c>：返回摄像头列表与可用性。</item>
/// <item><c>POST /control</c>：控制命令（切换摄像头等）。</item>
/// </list>
/// </remarks>
public sealed class RemoteMonitorService : IDisposable
{
    private readonly ILogger<RemoteMonitorService> _logger;
    private readonly ScreenCaptureService _screenCapture;
    private readonly CameraCaptureService _cameraCapture;
    private readonly AudioPlaybackService _audioPlayback;
    private readonly CloudflaredManager _cloudflared;

    private IWebHost? _host;
    private CancellationTokenSource? _serviceCts;
    private bool _disposed;
    private bool _running;

    /// <summary>
    /// 当前活跃查看者数量（WebSocket 连接数 + 截图请求短期活跃计数）。
    /// 用 Interlocked 保证线程安全。
    /// </summary>
    private int _activeViewerCount;

    /// <summary>
    /// 截图请求短期活跃计时器。每次截图请求重置此计时器，
    /// 5 秒内若无新请求则递减活跃计数。
    /// </summary>
    private Timer? _screenshotActiveTimer;
    private bool _screenshotActiveRegistered;

    /// <summary>
    /// 随机生成的 Bearer token，所有请求需携带。
    /// </summary>
    public string Token { get; } = GenerateToken();

    /// <summary>
    /// 实际监听的本地端口。服务启动后赋值。
    /// </summary>
    public int LocalPort { get; private set; }

    /// <summary>
    /// 当前隧道 URL。cloudflared 解析成功后赋值。
    /// </summary>
    public string? TunnelUrl => _cloudflared.CurrentTunnelUrl;

    /// <summary>
    /// 服务是否正在运行。
    /// </summary>
    public bool IsRunning => _running;

    /// <summary>
    /// 是否有活跃的远程查看者（WebSocket 连接或近期截图请求）。
    /// 监控指示器应基于此属性显示，而非 <see cref="IsRunning"/>。
    /// </summary>
    public bool HasActiveViewer => Interlocked.CompareExchange(ref _activeViewerCount, 0, 0) > 0;

    /// <summary>
    /// 活跃查看者数量变化时触发（连接/断开/截图请求）。
    /// </summary>
    public event Action? ActiveViewerChanged;

    public RemoteMonitorService(
        ILogger<RemoteMonitorService> logger,
        ScreenCaptureService screenCapture,
        CameraCaptureService cameraCapture,
        AudioPlaybackService audioPlayback,
        CloudflaredManager cloudflared)
    {
        _logger = logger;
        _screenCapture = screenCapture;
        _cameraCapture = cameraCapture;
        _audioPlayback = audioPlayback;
        _cloudflared = cloudflared;
    }

    private static string GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>
    /// 启动远程监控服务：HTTP 服务器 → cloudflared → 上报 tunnel 信息。
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_running)
        {
            _logger.LogWarning("远程监控服务已在运行");
            return;
        }

        _serviceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _logger.LogInformation("正在启动远程监控服务…");

        // 1. 启动 HTTP 服务器
        StartHttpServer();
        _logger.LogInformation("HTTP 服务器已启动，端口 {Port}，token={Token}", LocalPort, Token);

        // 2. 启动 cloudflared（仅 Windows）
        if (OperatingSystem.IsWindows())
        {
            try
            {
                if (!_cloudflared.IsExecutableReady)
                {
                    _logger.LogInformation("首次启动，正在下载 cloudflared.exe…");
                    await _cloudflared.DownloadAsync(cancellationToken: cancellationToken);
                }

                _cloudflared.TunnelReady += OnTunnelReady;
                _cloudflared.TunnelLost += OnTunnelLost;
                await _cloudflared.StartAsync(LocalPort, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "cloudflared 启动失败，远程监控仅限局域网");
            }
        }
        else
        {
            _logger.LogWarning("当前平台不支持 cloudflared，远程监控仅限局域网");
        }

        _running = true;
        _logger.LogInformation("远程监控服务已启动");
    }

    /// <summary>
    /// 停止远程监控服务：HTTP 服务器 → cloudflared → 清除 tunnel 信息。
    /// </summary>
    public async Task StopAsync()
    {
        if (!_running) return;
        _logger.LogInformation("正在停止远程监控服务…");

        _cloudflared.TunnelReady -= OnTunnelReady;
        _cloudflared.TunnelLost -= OnTunnelLost;

        // 1. 停止 cloudflared
        _cloudflared.Stop();

        // 2. 清除服务端 tunnel 信息
        try
        {
            await ReportTunnelInfoAsync(null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "清除服务端 tunnel 信息失败");
        }

        // 3. 停止 HTTP 服务器
        if (_host != null)
        {
            try
            {
                await _host.StopAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "停止 HTTP 服务器失败");
            }
            _host.Dispose();
            _host = null;
        }

        // 4. 清理截图活跃计时器并重置活跃计数
        lock (this)
        {
            _screenshotActiveTimer?.Dispose();
            _screenshotActiveTimer = null;
            _screenshotActiveRegistered = false;
        }
        if (Interlocked.Exchange(ref _activeViewerCount, 0) > 0)
        {
            ActiveViewerChanged?.Invoke();
        }

        _serviceCts?.Cancel();
        _serviceCts?.Dispose();
        _serviceCts = null;
        _running = false;
        _logger.LogInformation("远程监控服务已停止");
    }

    private void StartHttpServer()
    {
        var hostBuilder = new WebHostBuilder()
            .UseKestrel()
            .UseUrls("http://0.0.0.0:0")
            .Configure(app =>
            {
                // CORS：允许所有来源跨域访问（远程监控服务需要被班主任端浏览器跨域请求）
                app.Use(async (context, next) =>
                {
                    context.Response.Headers["Access-Control-Allow-Origin"] = "*";
                    context.Response.Headers["Access-Control-Allow-Methods"] = "GET, POST, DELETE, OPTIONS";
                    context.Response.Headers["Access-Control-Allow-Headers"] = "Authorization, Content-Type";
                    if (context.Request.Method == "OPTIONS")
                    {
                        context.Response.StatusCode = 204;
                        return;
                    }
                    await next();
                });
                app.UseWebSockets(new WebSocketOptions
                {
                    KeepAliveInterval = TimeSpan.FromSeconds(15),
                });
                app.Run(HandleRequestAsync);
            });

        _host = hostBuilder.Build();
        _host.Start();

        // 获取实际监听端口
        var addresses = _host.ServerFeatures.Get<IServerAddressesFeature>()?.Addresses;
        if (addresses != null && addresses.Any())
        {
            var firstUrl = addresses.First();
            if (Uri.TryCreate(firstUrl, UriKind.Absolute, out var uri))
            {
                LocalPort = uri.Port;
            }
        }
        if (LocalPort == 0)
        {
            // 回退：从 Kestrel 的 IServer 获取
            LocalPort = 0;
        }
    }

    private async Task HandleRequestAsync(HttpContext context)
    {
        var ct = context.RequestAborted;

        // token 验证
        if (!ValidateToken(context.Request))
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsync("Unauthorized", ct);
            return;
        }

        var path = context.Request.Path.Value ?? "";
        var method = context.Request.Method;

        try
        {
            switch (path, method)
            {
                case ("/screenshot", "GET"):
                    await HandleScreenshotAsync(context, ct);
                    break;
                case ("/status", "GET"):
                    await HandleStatusAsync(context, ct);
                    break;
                case ("/control", "POST"):
                    await HandleControlAsync(context, ct);
                    break;
                case ("/camera/stream", "GET") when context.WebSockets.IsWebSocketRequest:
                    await HandleCameraStreamAsync(context, ct);
                    break;
                case ("/audio/receive", "GET") when context.WebSockets.IsWebSocketRequest:
                    await HandleAudioReceiveAsync(context, ct);
                    break;
                default:
                    context.Response.StatusCode = 404;
                    await context.Response.WriteAsync("Not Found", ct);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "处理请求 {Method} {Path} 失败", method, path);
            context.Response.StatusCode = 500;
            await context.Response.WriteAsync("Internal Server Error", ct);
        }
    }

    private bool ValidateToken(HttpRequest request)
    {
        // 1. 先检查 Authorization header
        var auth = request.Headers.Authorization.ToString();
        if (!string.IsNullOrEmpty(auth) && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var token = auth["Bearer ".Length..].Trim();
            if (string.Equals(token, Token, StringComparison.Ordinal)) return true;
        }

        // 2. 再检查 query string 中的 token（WebSocket 连接用）
        var queryToken = request.Query["token"].ToString();
        if (!string.IsNullOrEmpty(queryToken))
        {
            if (string.Equals(queryToken, Token, StringComparison.Ordinal)) return true;
        }

        return false;
    }

    // ===== 端点：GET /screenshot =====
    private async Task HandleScreenshotAsync(HttpContext context, CancellationToken ct)
    {
        if (!_screenCapture.IsSupported)
        {
            context.Response.StatusCode = 503;
            await context.Response.WriteAsync("Screen capture not supported on this platform", ct);
            return;
        }

        // 截图请求视为短期活跃查看：递增计数并重置 5 秒后递减的计时器
        RegisterScreenshotActive();

        var jpeg = _screenCapture.CaptureScreenAsJpeg(80);
        context.Response.ContentType = "image/jpeg";
        context.Response.ContentLength = jpeg.Length;
        await context.Response.Body.WriteAsync(jpeg, ct);
    }

    /// <summary>
    /// 注册截图请求为短期活跃查看者。
    /// 首次请求时递增活跃计数，5 秒内若无新请求则递减。
    /// </summary>
    private void RegisterScreenshotActive()
    {
        lock (this)
        {
            if (!_screenshotActiveRegistered)
            {
                _screenshotActiveRegistered = true;
                Interlocked.Increment(ref _activeViewerCount);
                ActiveViewerChanged?.Invoke();
            }

            // 重置计时器：5 秒后若无新请求则注销
            _screenshotActiveTimer?.Dispose();
            _screenshotActiveTimer = new Timer(_ =>
            {
                lock (this)
                {
                    if (_screenshotActiveRegistered)
                    {
                        _screenshotActiveRegistered = false;
                        Interlocked.Decrement(ref _activeViewerCount);
                        ActiveViewerChanged?.Invoke();
                    }
                    _screenshotActiveTimer?.Dispose();
                    _screenshotActiveTimer = null;
                }
            }, null, TimeSpan.FromSeconds(5), Timeout.InfiniteTimeSpan);
        }
    }

    // ===== 端点：GET /status =====
    private async Task HandleStatusAsync(HttpContext context, CancellationToken ct)
    {
        var cameras = _cameraCapture.IsSupported ? _cameraCapture.GetCameras() : new List<CameraInfo>();
        var status = new
        {
            screenCaptureSupported = _screenCapture.IsSupported,
            cameraCaptureSupported = _cameraCapture.IsSupported,
            audioPlaybackSupported = _audioPlayback.IsSupported,
            cameras = cameras.Select(c => new { c.Index, c.Name, c.IsDefault }),
            currentCameraIndex = _cameraCapture.CurrentCameraIndex,
            tunnelUrl = TunnelUrl,
            localPort = LocalPort,
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(status, ct);
    }

    // ===== 端点：POST /control =====
    private async Task HandleControlAsync(HttpContext context, CancellationToken ct)
    {
        using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
        var body = await reader.ReadToEndAsync(ct);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        var action = root.TryGetProperty("action", out var actionEl) ? actionEl.GetString() : null;
        switch (action)
        {
            case "selectCamera":
                var deviceId = root.TryGetProperty("deviceId", out var idEl) ? idEl.GetString() : "0";
                _cameraCapture.SelectCamera(deviceId ?? "0");
                context.Response.StatusCode = 200;
                await context.Response.WriteAsync($"Camera selected: {deviceId}", ct);
                break;
            case "setAudioFormat":
                var sampleRate = root.TryGetProperty("sampleRate", out var srEl) ? srEl.GetInt32() : 16000;
                var bits = root.TryGetProperty("bits", out var bitsEl) ? bitsEl.GetInt32() : 16;
                var channels = root.TryGetProperty("channels", out var chEl) ? chEl.GetInt32() : 1;
                _audioPlayback.SetFormat(sampleRate, bits, channels);
                context.Response.StatusCode = 200;
                await context.Response.WriteAsync("Audio format set", ct);
                break;
            default:
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync($"Unknown action: {action}", ct);
                break;
        }
    }

    // ===== 端点：WS /camera/stream =====
    private async Task HandleCameraStreamAsync(HttpContext context, CancellationToken ct)
    {
        using var ws = await context.WebSockets.AcceptWebSocketAsync();
        _logger.LogInformation("摄像头 WebSocket 已连接");

        // 注册活跃查看者
        Interlocked.Increment(ref _activeViewerCount);
        ActiveViewerChanged?.Invoke();

        // 自适应帧率参数
        const int minFps = 3;
        const int maxFps = 15;
        var currentFps = 8;
        var frameIntervalMs = 1000.0 / currentFps;

        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var sendStart = DateTime.UtcNow;
                var frame = _cameraCapture.CaptureFrameAsJpeg(40);
                if (frame.Length == 0)
                {
                    // 采集失败，降低帧率
                    currentFps = Math.Max(minFps, currentFps - 2);
                    frameIntervalMs = 1000.0 / currentFps;
                    await Task.Delay((int)frameIntervalMs, ct);
                    continue;
                }

                // 帧大小优化：超过 50KB 时降低质量重采
                if (frame.Length > 50 * 1024)
                {
                    var smallerFrame = _cameraCapture.CaptureFrameAsJpeg(25);
                    if (smallerFrame.Length > 0 && smallerFrame.Length < frame.Length)
                    {
                        frame = smallerFrame;
                    }
                }

                await ws.SendAsync(frame, WebSocketMessageType.Binary, endOfMessage: true, ct);

                var sendElapsed = DateTime.UtcNow - sendStart;

                // 自适应帧率：发送耗时长则降帧率，短则升帧率
                if (sendElapsed.TotalMilliseconds > 300)
                {
                    currentFps = Math.Max(minFps, currentFps - 3);
                }
                else if (sendElapsed.TotalMilliseconds < 100)
                {
                    currentFps = Math.Min(maxFps, currentFps + 1);
                }
                frameIntervalMs = 1000.0 / currentFps;

                var delayMs = (int)Math.Max(0, frameIntervalMs - sendElapsed.TotalMilliseconds);
                if (delayMs > 0)
                {
                    await Task.Delay(delayMs, ct);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex)
        {
            _logger.LogInformation("摄像头 WebSocket 断开：{Message}", ex.Message);
        }
        finally
        {
            _logger.LogInformation("摄像头 WebSocket 已断开（fps={Fps}）", currentFps);
            // 注销活跃查看者
            Interlocked.Decrement(ref _activeViewerCount);
            ActiveViewerChanged?.Invoke();
        }
    }

    // ===== 端点：WS /audio/receive =====
    private async Task HandleAudioReceiveAsync(HttpContext context, CancellationToken ct)
    {
        using var ws = await context.WebSockets.AcceptWebSocketAsync();
        _logger.LogInformation("音频 WebSocket 已连接");

        // 注册活跃查看者
        Interlocked.Increment(ref _activeViewerCount);
        ActiveViewerChanged?.Invoke();

        var buffer = new byte[16 * 1024];
        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await ws.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }
                if (result.MessageType == WebSocketMessageType.Binary && result.Count > 0)
                {
                    var data = new byte[result.Count];
                    Array.Copy(buffer, data, result.Count);
                    _audioPlayback.PlayAudio(data);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex)
        {
            _logger.LogInformation("音频 WebSocket 断开：{Message}", ex.Message);
        }
        finally
        {
            _logger.LogInformation("音频 WebSocket 已断开");
            // 注销活跃查看者
            Interlocked.Decrement(ref _activeViewerCount);
            ActiveViewerChanged?.Invoke();
        }
    }

    // ===== tunnel 信息上报 =====
    private void OnTunnelReady(string url)
    {
        _logger.LogInformation("隧道就绪，上报到服务端：{Url}", url);
        _ = Task.Run(() => ReportTunnelInfoAsync(url));
    }

    private void OnTunnelLost()
    {
        _logger.LogWarning("隧道丢失，清除服务端 tunnel 信息");
        _ = Task.Run(() => ReportTunnelInfoAsync(null));
    }

    /// <summary>
    /// 上报/清除 tunnel 信息到集控后端。
    /// url 为 null 时清除（DELETE），否则上报（POST）。
    /// </summary>
    private async Task ReportTunnelInfoAsync(string? url)
    {
        var managementBaseUrl = GetManagementBaseUrl();
        if (string.IsNullOrEmpty(managementBaseUrl))
        {
            _logger.LogWarning("无法获取集控 base URL，跳过 tunnel 信息上报");
            return;
        }

        var cuid = GetClientUniqueId();
        if (cuid == Guid.Empty)
        {
            _logger.LogWarning("无法获取 ClientUniqueId，跳过 tunnel 信息上报");
            return;
        }

        var tunnelApiUrl = $"{managementBaseUrl}/api/teacher/device/{cuid}/tunnel";
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        if (string.IsNullOrEmpty(url))
        {
            // 清除 tunnel 信息
            var delResp = await httpClient.DeleteAsync(tunnelApiUrl);
            if (!delResp.IsSuccessStatusCode)
            {
                _logger.LogWarning("清除 tunnel 信息返回 {Status}", (int)delResp.StatusCode);
            }
            else
            {
                _logger.LogInformation("已清除服务端 tunnel 信息");
            }
        }
        else
        {
            // 上报 tunnel 信息
            var body = new
            {
                tunnelUrl = url,
                tunnelToken = Token,
                localIp = GetLocalIpAddress(),
                localPort = LocalPort,
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            };
            var json = JsonSerializer.Serialize(body);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            var postResp = await httpClient.PostAsync(tunnelApiUrl, content);
            if (!postResp.IsSuccessStatusCode)
            {
                _logger.LogWarning("上报 tunnel 信息返回 {Status}", (int)postResp.StatusCode);
            }
            else
            {
                _logger.LogInformation("已上报 tunnel 信息到服务端");
            }
        }
    }

    /// <summary>
    /// 从 ManagementService 的 Settings.ManifestUrlTemplate 解析出集控 API base URL。
    /// </summary>
    private static string? GetManagementBaseUrl()
    {
        var instance = ManagementService.Instance;
        if (instance == null) return null;
        var template = instance.Settings.ManifestUrlTemplate;
        if (string.IsNullOrWhiteSpace(template)) return null;
        try
        {
            var uri = new Uri(template);
            var builder = new UriBuilder(uri.Scheme, uri.Host, uri.Port);
            return builder.ToString().TrimEnd('/');
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 从 ManagementService.Persist 获取 ClientUniqueId。
    /// </summary>
    private static Guid GetClientUniqueId()
    {
        return ManagementService.Instance?.Persist.ClientUniqueId ?? Guid.Empty;
    }

    /// <summary>
    /// 获取本机局域网 IP 地址（首选 IPv4）。
    /// </summary>
    private static string GetLocalIpAddress()
    {
        try
        {
            var host = Dns.GetHostEntry(Dns.GetHostName());
            var ip = host.AddressList.FirstOrDefault(a =>
                a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));
            return ip?.ToString() ?? "127.0.0.1";
        }
        catch
        {
            return "127.0.0.1";
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ = StopAsync();
    }
}