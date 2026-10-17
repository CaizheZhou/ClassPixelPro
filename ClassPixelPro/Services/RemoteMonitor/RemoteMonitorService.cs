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
/// <item><c>WS /audio/receive</c>：接收 PCM 音频并播放（传话）。</item>
/// <item><c>WS /mic/stream</c>：推送现场声音 PCM 与 dB 电平（收音）。</item>
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
    private readonly AudioCaptureService _audioCapture;
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
    // 最近一次就绪的隧道地址（定期重报用）
    private string? _lastTunnelUrl;
    private Timer? _tunnelReportTimer;

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
        AudioCaptureService audioCapture,
        CloudflaredManager cloudflared)
    {
        _logger = logger;
        _screenCapture = screenCapture;
        _cameraCapture = cameraCapture;
        _audioPlayback = audioPlayback;
        _audioCapture = audioCapture;
        _cloudflared = cloudflared;
    }

    private static string GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>
    /// 启动远程监控服务：HTTP 服务器立即就绪（局域网可用），cloudflared 转入后台任务
    /// 自动下载（含失败重试）并建立隧道，成功后自动上报 tunnel 信息。
    /// <para>
    /// 原实现 await 整个下载（55MB），开关要卡到下载结束才弹回；下载失败仅记日志且
    /// 不会重试，隧道就永远建立不起来。现改为：下载在后台进行，失败自动重试
    /// （指数退避），无需用户干预。
    /// </para>
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

        // 1. 启动 HTTP 服务器（立即可用，局域网模式）
        StartHttpServer();
        _logger.LogInformation("HTTP 服务器已启动，端口 {Port}，token={Token}", LocalPort, Token);

        // 版本更新后首次启动自动放行防火墙（规则按程序而非端口，端口每次启动随机变化）：
        // 自动更新会换 app-<版本> 目录，Windows 按旧 exe 路径记录的规则随即失效，
        // 局域网直连（教师手机）被拦截。已存在规则时静默跳过，不会打扰用户。
        FirewallHelper.EnsureInboundRuleOnUpdate(msg => _logger.LogInformation("{Msg}", msg));

        _running = true;
        _logger.LogInformation("远程监控服务已启动");

        // 2. 立即上报 LAN 信息（localIp + localPort），不等 cloudflared 下载/建隧道。
        //    局域网内班主任端可凭此直接访问，cloudflared 就绪后再补报 tunnelUrl。
        _ = Task.Run(ReportLanInfoAsync);

        // 3. cloudflared（仅 Windows）：后台下载（如缺失）→ 建立隧道，不阻塞 StartAsync
        if (OperatingSystem.IsWindows())
        {
            _ = Task.Run(() => EnsureTunnelLoopAsync(_serviceCts.Token));
        }
        else
        {
            _logger.LogWarning("当前平台不支持 cloudflared，远程监控仅限局域网");
        }
    }

    /// <summary>
    /// 后台确保隧道可用：cloudflared 未下载时先下载（失败自动重试，指数退避），
    /// 就绪后启动隧道。整个循环随 <see cref="StopAsync"/> 的取消令牌结束。
    /// </summary>
    private async Task EnsureTunnelLoopAsync(CancellationToken ct)
    {
        _cloudflared.TunnelReady += OnTunnelReady;
        _cloudflared.TunnelLost += OnTunnelLost;

        var backoff = TimeSpan.FromSeconds(3);
        var maxBackoff = TimeSpan.FromSeconds(60);

        while (!ct.IsCancellationRequested && _running)
        {
            try
            {
                if (!_cloudflared.IsExecutableReady)
                {
                    _logger.LogInformation("cloudflared.exe 未下载，开始后台下载…");
                    await _cloudflared.DownloadAsync(cancellationToken: ct);
                    _logger.LogInformation("cloudflared.exe 后台下载完成，启动隧道");
                }

                await _cloudflared.StartAsync(LocalPort, ct);
                return; // 隧道监控循环已接管，本任务结束
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "cloudflared 准备失败，{Backoff} 后重试", backoff);
                try
                {
                    await Task.Delay(backoff, ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, maxBackoff.Ticks));
            }
        }
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

        // 4. 释放摄像头
        _tunnelReportTimer?.Dispose();
        _tunnelReportTimer = null;
        _cameraCapture.ReleaseCamera();

        // 5. 清理截图活跃计时器并重置活跃计数
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
                case ("/mic/stream", "GET") when context.WebSockets.IsWebSocketRequest:
                    await HandleMicStreamAsync(context, ct);
                    break;
                default:
                    context.Response.StatusCode = 404;
                    await context.Response.WriteAsync("Not Found", ct);
                    break;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 客户端主动取消请求（如前端 tryLocalConnect 5秒超时 abort），不是错误，静默忽略
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
        // GetCameras() 内部已缓存 60 秒，避免每次 /status 都逐个打开 0-9 号摄像头探测
        var cameras = _cameraCapture.IsSupported ? _cameraCapture.GetCameras() : new List<CameraInfo>();
        var status = new
        {
            screenCaptureSupported = _screenCapture.IsSupported,
            cameraCaptureSupported = _cameraCapture.IsSupported,
            audioPlaybackSupported = _audioPlayback.IsSupported,
            audioCaptureSupported = _audioCapture.IsSupported,
            audioFormat = AudioCaptureService.SampleRate + "/" + AudioCaptureService.BitsPerSample + "/" + AudioCaptureService.Channels,
            micCapturing = _audioCapture.IsCapturing,
            cameras = cameras.Select(c => new { c.Index, c.Name, c.IsDefault }),
            currentCameraIndex = _cameraCapture.CurrentCameraIndex,
            tunnelUrl = TunnelUrl,
            localPort = LocalPort,
            localIps = GetLocalIpAddresses(),
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

        // 按连接来源分档码率：
        // - 局域网直连：带宽充足（通常 100Mbps+），提高画质上限与帧率下限
        //   (maxQuality=85, minQuality=60, minFps=20, maxFps=30)
        // - tunnel（cloudflared 本机转发，RemoteIp 为 loopback）或公网直连：
        //   保持原保守参数 (maxQuality=70, minQuality=35, minFps=10, maxFps=30)，
        //   避免隧道拥塞（2-5 Mbps）导致缓冲堆积数秒延迟。
        var remoteIp = context.Connection.RemoteIpAddress;
        var isLan = IsLanConnection(remoteIp);
        _logger.LogInformation("摄像头 WebSocket 已连接，来源 {Ip}（{Type}）",
            remoteIp, isLan ? "局域网" : "tunnel");

        // 注册活跃查看者
        Interlocked.Increment(ref _activeViewerCount);
        ActiveViewerChanged?.Invoke();

        // 自适应码率参数。
        // 延迟累积机制：SendAsync 返回只代表数据写入 TCP 发送缓冲（默认数百 KB），
        // 当码率超过链路吞吐（经 Cloudflare 隧道通常只有 2-5 Mbps），缓冲持续堆积
        // 数 MB = 数秒延迟。SendAsync 耗时是最佳的拥塞信号：缓冲空闲时瞬时完成，
        // 缓冲增长时耗时上升。据此动态调整 JPEG 质量与帧率，让码率贴合链路吞吐，
        // 缓冲不再堆积，延迟稳定在 RTT + 单帧。
        int minQuality, maxQuality, minFps, maxFps;
        if (isLan)
        {
            minQuality = 60;
            maxQuality = 85;
            minFps = 20;
            maxFps = 30;
        }
        else
        {
            minQuality = 45;  // 从35提到45，避免质量降太低
            maxQuality = 75;  // 从70提到75，允许更高画质
            minFps = 15;      // 从10提到15，保证最低帧率不太低
            maxFps = 30;
        }
        var jpegQuality = (minQuality + maxQuality) / 2;  // 从中点起步（链路未知，宁可先流畅后清晰）
        var currentFps = (minFps + maxFps) / 2;
        var frameIntervalMs = 1000.0 / currentFps;
        var totalBytesSent = 0L;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var frameStart = DateTime.UtcNow;

                // 采集 + 编码（OpenCV Read 天然读取设备最新帧，天然跳帧）
                var frame = _cameraCapture.CaptureFrameAsJpeg(jpegQuality);
                if (frame.Length == 0)
                {
                    await Task.Delay(100, ct);
                    continue;
                }

                // 帧过大时降质量重采一次（阈值随质量走，避免隧道下单帧过大）
                if (frame.Length > 120 * 1024 && jpegQuality > minQuality)
                {
                    var smaller = _cameraCapture.CaptureFrameAsJpeg(Math.Max(minQuality, jpegQuality - 15));
                    if (smaller.Length > 0 && smaller.Length < frame.Length)
                    {
                        frame = smaller;
                    }
                }

                // 发送并测量耗时（拥塞信号）
                sw.Restart();
                await ws.SendAsync(frame, WebSocketMessageType.Binary, endOfMessage: true, ct);
                var sendMs = sw.ElapsedMilliseconds;
                totalBytesSent += frame.Length;

                // 自适应策略：
                // - 发送 > 250ms：缓冲明显堆积，快速降质 + 降帧（延迟优先）
                // - 发送 > 120ms：轻微堆积，缓慢降
                // - 发送 < 60ms：链路空闲，缓慢升质升帧
                if (sendMs > 250)
                {
                    jpegQuality = Math.Max(minQuality, jpegQuality - 8);   // 发送慢时降质量（从-12改为-8）
                    currentFps = Math.Max(minFps, currentFps - 2);
                }
                else if (sendMs > 120)
                {
                    jpegQuality = Math.Max(minQuality, jpegQuality - 3);   // 帧大时降质量（从-5改为-3）
                    currentFps = Math.Max(minFps, currentFps - 1);
                }
                else if (sendMs < 60)
                {
                    jpegQuality = Math.Min(maxQuality, jpegQuality + 4);   // 发送快时升质量（从+2改为+4，更快恢复）
                    currentFps = Math.Min(maxFps, currentFps + 3);
                }
                frameIntervalMs = 1000.0 / currentFps;

                var elapsedMs = (DateTime.UtcNow - frameStart).TotalMilliseconds;
                var delayMs = (int)Math.Max(0, frameIntervalMs - elapsedMs);
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
            _logger.LogInformation(
                "摄像头 WebSocket 已断开（fps={Fps}, quality={Quality}, 累计发送 {MB:F1} MB）",
                currentFps, jpegQuality, totalBytesSent / 1048576.0);
            // 最后一个查看者断开后释放摄像头：班主任关闭页面后设备摄像头不应继续被占用。
            // 重连时 CaptureFrameAsJpeg 会自动重新打开（首次打开慢 1-3 秒，可接受）。
            var newCount = Interlocked.Decrement(ref _activeViewerCount);
            if (newCount < 0)
            {
                Interlocked.Exchange(ref _activeViewerCount, 0);
            }
            if (newCount <= 0)
            {
                try { _cameraCapture.ReleaseCamera(); }
                catch (Exception exRelease) { _logger.LogWarning(exRelease, "查看者断开后释放摄像头失败"); }
            }
            ActiveViewerChanged?.Invoke();
        }
    }

    // ===== 端点：WS /mic/stream（收音：大屏麦克风 → 班主任端）=====
    private async Task HandleMicStreamAsync(HttpContext context, CancellationToken ct)
    {
        if (!_audioCapture.IsSupported)
        {
            context.Response.StatusCode = 501;
            await context.Response.WriteAsync("麦克风采集仅在 Windows 上可用", ct);
            return;
        }

        using var ws = await context.WebSockets.AcceptWebSocketAsync();
        _logger.LogInformation("收音 WebSocket 已连接，班主任端开始监听现场声音");

        // 帧先攒在这里，由发送循环按序发出。NAudio 的 DataAvailable 在自己的线程触发，
        // 直接在里面 SendAsync 会和 WebSocket 的发送并发冲突。
        var frameChannel = System.Threading.Channels.Channel.CreateUnbounded<byte[]>();
        var captureCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var captureTask = Task.Run(() =>
        {
            void PushFrame(byte[] f) => frameChannel.Writer.TryWrite(f);
            void PushLevel(double db) => frameChannel.Writer.TryWrite(
                System.Text.Encoding.UTF8.GetBytes(
                    "{\"type\":\"level\",\"db\":" +
                    db.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}"));

            try
            {
                // Start 内部即开始采集，帧与电平通过回调推入队列
                _audioCapture.Start(PushFrame, PushLevel);
            }
            catch (OperationCanceledException)
            {
                // 客户端断开，正常退出
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "麦克风采集失败");
                var err = System.Text.Encoding.UTF8.GetBytes("{\"type\":\"error\",\"message\":\"" +
                    e.Message.Replace("\"", "'") + "\"}");
                frameChannel.Writer.TryWrite(err);
                frameChannel.Writer.TryComplete();
            }
        }, captureCts.Token);

        try
        {
            // 先告诉前端 PCM 参数，前端据此创建 AudioContext 的缓冲
            var hello = System.Text.Encoding.UTF8.GetBytes(
                "{\"type\":\"format\",\"sampleRate\":" + AudioCaptureService.SampleRate +
                ",\"bits\":" + AudioCaptureService.BitsPerSample +
                ",\"channels\":" + AudioCaptureService.Channels + "}");
            await ws.SendAsync(hello, WebSocketMessageType.Text, true, ct);

            await foreach (var frame in frameChannel.Reader.ReadAllAsync(ct))
            {
                // 文本帧（电平/格式/错误）以 '{' 开头便于前端区分
                var isText = frame.Length > 0 && frame[0] == (byte)'{';
                if (ws.State != WebSocketState.Open)
                {
                    break;
                }

                await ws.SendAsync(frame,
                    isText ? WebSocketMessageType.Text : WebSocketMessageType.Binary,
                    endOfMessage: true, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // 客户端断开或取消，正常路径
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "收音 WebSocket 发送失败");
        }
        finally
        {
            captureCts.Cancel();
            _audioCapture.Stop();
            frameChannel.Writer.TryComplete();
            try
            {
                await captureTask;
            }
            catch
            {
                // 忽略
            }

            _logger.LogInformation("收音 WebSocket 已断开，麦克风已释放");
        }
    }

    // ===== 端点：WS /audio/receive =====
    private async Task HandleAudioReceiveAsync(HttpContext context, CancellationToken ct)
    {
        using var ws = await context.WebSockets.AcceptWebSocketAsync();
        _logger.LogInformation("音频 WebSocket 已连接");

        // 喊话「以最大音量播放」：班主任端在 URL 上带 maxVolume=1。
        // 音量在连接期间提升，连接关闭时恢复原音量（对讲结束 = 连接结束）。
        MasterVolumeController.VolumeRestoreHandle? talkVolume = null;
        if (context.Request.Query.TryGetValue("maxVolume", out var mv) &&
            (mv == "1" || string.Equals(mv.ToString(), "true", StringComparison.OrdinalIgnoreCase)))
        {
            talkVolume = MasterVolumeController.Begin();
            if (talkVolume != null)
            {
                _logger.LogInformation("喊话将以系统最大音量播放（播完后恢复）");
            }
        }

        // 新连接建立时清空上一次传话的残留缓冲
        _audioPlayback.ClearBuffer();

        // 注册活跃查看者
        Interlocked.Increment(ref _activeViewerCount);
        ActiveViewerChanged?.Invoke();

        // 大消息分片拼接：教师端可能一次发送整段录音（数百 KB），
        // 必须按 EndOfMessage 拼完整后再播放，否则 PCM 数据虽然连续但
        // 无法识别消息边界（结束标记会被当成音频字节混入播放，产生爆音）。
        var messageBuffer = new List<byte>(64 * 1024);

        try
        {
            var recvBuffer = new byte[32 * 1024];
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await ws.ReceiveAsync(new ArraySegment<byte>(recvBuffer), ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                messageBuffer.AddRange(new ArraySegment<byte>(recvBuffer, 0, result.Count));

                if (!result.EndOfMessage)
                {
                    continue; // 消息未完，继续收下一片
                }

                var data = messageBuffer.ToArray();
                messageBuffer.Clear();

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    // 教师端说完，不清空缓冲区——让已接收的音频自然播放完毕
                    // 清空会在下一次传话连接建立时执行
                    _logger.LogInformation("收到传话控制消息：{Text}，等待缓冲区播放完毕", System.Text.Encoding.UTF8.GetString(data));
                    continue;
                }

                if (data.Length > 0)
                {
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
            MasterVolumeController.Restore(talkVolume);
            _logger.LogInformation("音频 WebSocket 已断开");
            // 注意：这里不释放摄像头——传话与摄像头是两条独立 WS，
            // 原实现在传话结束时误释放摄像头，导致远程画面中断。
            // 注销活跃查看者
            var newCount = Interlocked.Decrement(ref _activeViewerCount);
            if (newCount < 0)
            {
                // 防止计数变负（不应该发生但防御性编程）
                Interlocked.Exchange(ref _activeViewerCount, 0);
            }
            ActiveViewerChanged?.Invoke();
        }
    }

    // ===== tunnel 信息上报 =====
    private void OnTunnelReady(string url)
    {
        _logger.LogInformation("隧道就绪，上报到服务端：{Url}", url);
        _lastTunnelUrl = url;
        _ = Task.Run(() => ReportTunnelInfoAsync(url));

        // 运行期定期重报：cloudflared quick tunnel 会在连接断开后自行重建并换地址，
        // 教师端手上那个旧地址会失效。定期重报让教师端能尽快拿到当前有效地址，
        // 也顺带自愈偶发的上报失败。
        _tunnelReportTimer?.Dispose();
        _tunnelReportTimer = new Timer(_ =>
        {
            var current = _lastTunnelUrl;
            if (!string.IsNullOrEmpty(current))
            {
                _ = Task.Run(() => ReportTunnelInfoAsync(current));
            }
        }, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    private void OnTunnelLost()
    {
        _logger.LogWarning("隧道丢失，清除服务端 tunnel 信息");
        _ = Task.Run(() => ReportTunnelInfoAsync(null));
    }

    /// <summary>
    /// 仅上报 LAN 信息（localIp/localPort）到集控后端，不等 cloudflared 就绪。
    /// 在 HTTP 服务器启动后立即调用，让班主任端能尽快拿到局域网直连地址。
    /// 复用 tunnel 端点，body 中 tunnelUrl/tunnelToken 为 null 表示"仅 LAN 信息"。
    /// </summary>
    private async Task ReportLanInfoAsync()
    {
        var managementBaseUrl = GetManagementBaseUrl();
        if (string.IsNullOrEmpty(managementBaseUrl))
        {
            _logger.LogWarning("无法获取集控 base URL，跳过 LAN 信息上报");
            return;
        }

        var cuid = GetClientUniqueId();
        if (cuid == Guid.Empty)
        {
            _logger.LogWarning("无法获取 ClientUniqueId，跳过 LAN 信息上报");
            return;
        }

        var tunnelApiUrl = $"{managementBaseUrl}/api/teacher/device/{cuid}/tunnel";
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        var body = new
        {
            tunnelUrl = (string?)null,
            tunnelToken = (string?)null,
            localIp = GetLocalIpAddress(),
            localIps = GetLocalIpAddresses(),
            localPort = LocalPort,
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };
        var json = JsonSerializer.Serialize(body);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        try
        {
            var resp = await httpClient.PostAsync(tunnelApiUrl, content);
            if (resp.IsSuccessStatusCode)
            {
                _logger.LogInformation("已上报 LAN 信息到服务端（localPort={Port}）", LocalPort);
            }
            else
            {
                _logger.LogWarning("上报 LAN 信息返回 {Status}", (int)resp.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "上报 LAN 信息失败");
        }
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
            // 上报 tunnel 信息（同时携带 LAN 信息，便于后端一次落库）
            var body = new
            {
                tunnelUrl = url,
                tunnelToken = Token,
                localIp = GetLocalIpAddress(),
                localIps = GetLocalIpAddresses(),
                localPort = LocalPort,
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            };
            var json = JsonSerializer.Serialize(body);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            // 重试：上报只做一次的话，一次瞬态失败（网关抖动/限流）就会让教师端
            // 永远拿不到隧道地址，表现为「监控一直没画面」且无任何报错
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    var postResp = await httpClient.PostAsync(tunnelApiUrl, content);
                    if (postResp.IsSuccessStatusCode)
                    {
                        _logger.LogInformation("已上报 tunnel 信息到服务端（第 {Attempt} 次尝试）", attempt);
                        return;
                    }
                    _logger.LogWarning("上报 tunnel 信息返回 {Status}（第 {Attempt} 次尝试）",
                        (int)postResp.StatusCode, attempt);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "上报 tunnel 信息失败（第 {Attempt} 次尝试）", attempt);
                }
                if (attempt < 3) await Task.Delay(2000 * attempt);
            }
            _logger.LogError("上报 tunnel 信息连续 3 次失败，教师端可能无法通过 Tunnel 接入，请检查网络或稍后重启客户端");
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
    /// 返回 <see cref="GetLocalIpAddresses"/> 的首个候选，便于只需单个 IP 的调用点。
    /// </summary>
    private static string GetLocalIpAddress()
    {
        var ips = GetLocalIpAddresses();
        return ips.Count > 0 ? ips[0] : "127.0.0.1";
    }

    /// <summary>
    /// 获取本机所有可用局域网 IPv4 地址，过滤虚拟网卡后按物理网卡优先排序。
    /// </summary>
    /// <remarks>
    /// 原实现取 <see cref="Dns.GetHostEntry"/> 的第一个非 loopback IPv4，可能取到
    /// Hyper-V/WSL/Docker/VirtualBox/VMware 等虚拟网卡 IP，导致上报的 localIp 无法
    /// 在局域网内访问。现改为通过 <see cref="NetworkInterface"/> 枚举，按描述/名称
    /// 过滤虚拟网卡，物理网卡 IP 排在前面，虚拟网卡 IP 作为兜底候选排在后面。
    /// </remarks>
    private static List<string> GetLocalIpAddresses()
    {
        var result = new List<string>();
        try
        {
            // 虚拟网卡描述/名称关键字（小写匹配）
            var virtualKeywords = new[]
            {
                "hyper-v", "wsl", "vpn", "docker", "virtualbox", "vmware",
                "loopback", "vethernet", "tap", "tun"
            };

            var physicalIps = new List<string>();
            var virtualIps = new List<string>();

            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                var desc = (ni.Description ?? string.Empty).ToLowerInvariant();
                var name = (ni.Name ?? string.Empty).ToLowerInvariant();
                var isVirtual = virtualKeywords.Any(k => desc.Contains(k) || name.Contains(k));

                foreach (var ip in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ip.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (IPAddress.IsLoopback(ip.Address)) continue;
                    var ipStr = ip.Address.ToString();
                    if (isVirtual)
                    {
                        virtualIps.Add(ipStr);
                    }
                    else
                    {
                        physicalIps.Add(ipStr);
                    }
                }
            }

            // 物理网卡优先，虚拟网卡兜底
            result.AddRange(physicalIps);
            result.AddRange(virtualIps);

            // 完全没拿到时回退到 DNS 解析（保留原行为兜底）
            if (result.Count == 0)
            {
                var host = Dns.GetHostEntry(Dns.GetHostName());
                var fallback = host.AddressList
                    .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
                    .Select(a => a.ToString())
                    .ToList();
                result.AddRange(fallback);
            }

            if (result.Count == 0)
            {
                result.Add("127.0.0.1");
            }
        }
        catch
        {
            if (result.Count == 0)
            {
                result.Add("127.0.0.1");
            }
        }
        return result;
    }

    /// <summary>
    /// 判断连接来源是否为局域网直连（非 loopback 的私有 IPv4）。
    /// </summary>
    /// <remarks>
    /// cloudflared 在本机转发请求到 HTTP 服务器，<see cref="HttpContext.Connection.RemoteIpAddress"/>
    /// 为 loopback (127.0.0.1/::1)，据此与局域网内其他设备的直连区分开。
    /// 公网 IP 也返回 false（走 tunnel 保守参数）。
    /// </remarks>
    private static bool IsLanConnection(IPAddress? remoteIp)
    {
        if (remoteIp == null) return false;
        if (IPAddress.IsLoopback(remoteIp)) return false; // cloudflared 本机转发
        if (remoteIp.AddressFamily != AddressFamily.InterNetwork) return false;

        var bytes = remoteIp.GetAddressBytes();
        if (bytes.Length != 4) return false;

        // RFC 1918 私有地址段：
        //   10.0.0.0/8、172.16.0.0/12（172.16-172.31）、192.168.0.0/16
        // 链路本地 169.254.0.0/16 不算可用的局域网直连。
        if (bytes[0] == 10) return true;
        if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
        if (bytes[0] == 192 && bytes[1] == 168) return true;
        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ = StopAsync();
    }
}