using System;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Microsoft.Extensions.Logging;
using OpenCvSharp;

namespace ClassPixelPro.Services.RemoteMonitor;

/// <summary>
/// 摄像头信息。
/// </summary>
public sealed class CameraInfo
{
    /// <summary>设备索引（OpenCV VideoCapture 索引）。</summary>
    public int Index { get; set; }

    /// <summary>设备友好名称（可选）。</summary>
    public string Name { get; set; } = "";

    /// <summary>是否为默认设备。</summary>
    public bool IsDefault { get; set; }
}

/// <summary>
/// 摄像头采集服务：枚举摄像头、选择设备、采集单帧 JPEG。
/// </summary>
/// <remarks>
/// 当前实现基于 OpenCvSharp4（OpenCV 包装）。在 Windows 上 OpenCV 内部使用
/// MediaFoundation 或 DirectShow 采集摄像头。非 Windows 平台运行时调用会抛出
/// <see cref="PlatformNotSupportedException"/>。
/// </remarks>
public sealed class CameraCaptureService : IDisposable
{
    private readonly ILogger<CameraCaptureService> _logger;
    private readonly object _captureLock = new();
    private VideoCapture? _capture;
    private int _currentIndex = 0;
    private int _consecutiveFailures = 0;
    private const int MaxConsecutiveFailures = 10;
    private bool _disposed;

    // 摄像头是独占设备：远程监控、录制计划、人脸考勤三方共用这一路 VideoCapture。
    // 持有者计数为 0 时才真正释放设备；先到者用完后计数归零，后到者能立刻接手，
    // 不会出现「监控停了但录制还在跑，摄像头却被关掉」。
    private readonly Dictionary<CameraUser, int> _holders = new();
    private DateTime _lastHolderChangeAt = DateTime.MinValue;

    // GetCameras() 结果缓存：逐个打开 0-9 号摄像头探测耗时数秒，
    // /status 端点每次调用都探测会导致前端频繁刷新时累计卡顿。缓存 60 秒。
    private static List<CameraInfo>? _camerasCache;
    private static DateTime _camerasCacheTime;
    private static readonly object _camerasCacheLock = new();
    private static readonly TimeSpan CamerasCacheTtl = TimeSpan.FromSeconds(60);

    public CameraCaptureService(ILogger<CameraCaptureService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// 平台是否支持摄像头采集。
    /// </summary>
    public bool IsSupported => OperatingSystem.IsWindows();

    /// <summary>
    /// 当前选中的摄像头索引。
    /// </summary>
    public int CurrentCameraIndex => _currentIndex;

    /// <summary>
    /// 当前谁正在占用摄像头。没有任何人占用时为空集合。
    /// </summary>
    public IReadOnlyCollection<CameraUser> CurrentHolders
    {
        get
        {
            lock (_captureLock)
            {
                return _holders.Where(x => x.Value > 0).Select(x => x.Key).ToList();
            }
        }
    }

    /// <summary>
    /// 申请占用摄像头。已经被别人占用时返回 <c>false</c>，调用方应当跳过本轮，
    /// 而不是把别人的采集顶掉。
    /// <para>
    /// 同一使用者重复申请是幂等的（引用计数 +1），配合 <see cref="ReleaseCamera"/> 成对使用。
    /// </para>
    /// </summary>
    /// <param name="user">使用者。</param>
    public bool TryAcquireCamera(CameraUser user)
    {
        lock (_captureLock)
        {
            if (_disposed) return false;

            var existing = _holders.Where(x => x.Value > 0 && x.Key != user).ToList();
            if (existing.Count > 0)
            {
                _logger.LogInformation("摄像头正被 {Users} 占用，{User} 本次跳过",
                    string.Join("/", existing.Select(x => Describe(x.Key))), Describe(user));
                return false;
            }

            _holders[user] = _holders.TryGetValue(user, out var n) ? n + 1 : 1;
            _lastHolderChangeAt = DateTime.Now;
            return true;
        }
    }

    /// <summary>
    /// 释放一次占用。计数归零且没有别的使用者时才真正关闭设备。
    /// </summary>
    /// <param name="user">使用者。</param>
    public void ReleaseCamera(CameraUser user)
    {
        lock (_captureLock)
        {
            if (!_holders.TryGetValue(user, out var n) || n <= 0) return;

            _holders[user] = n - 1;
            _lastHolderChangeAt = DateTime.Now;
            if (_holders.Any(x => x.Value > 0)) return;

            // 没人用了才关设备，避免「监控查看者断开但录制还在跑」把摄像头关掉。
            ReleaseCameraLocked();
        }
    }

    /// <summary>
    /// 释放当前摄像头（停止占用设备），不区分使用者。
    /// <para>
    /// 保留这个无参重载是为了兼容既有的手动释放调用；新代码应使用
    /// <see cref="ReleaseCamera(CameraUser)"/>，否则计数会与实际情况脱节。
    /// </para>
    /// </summary>
    public void ReleaseCamera()
    {
        lock (_captureLock)
        {
            foreach (var key in _holders.Keys.ToList())
            {
                _holders[key] = 0;
            }

            _lastHolderChangeAt = DateTime.Now;
            ReleaseCameraLocked();
        }
    }

    private void ReleaseCameraLocked()
    {
        _capture?.Release();
        _capture?.Dispose();
        _capture = null;
        _consecutiveFailures = 0;
    }

    private static string Describe(CameraUser user) => user switch
    {
        CameraUser.Monitor => "远程监控",
        CameraUser.Recording => "录制计划",
        CameraUser.Attendance => "人脸考勤",
        _ => user.ToString(),
    };

    /// <summary>
    /// 枚举可用摄像头列表。
    /// </summary>
    /// <remarks>
    /// OpenCV 无法直接枚举摄像头名称，此处通过逐个尝试打开索引 0-9 来探测可用设备。
    /// 仅在 Windows 上执行探测；其他平台返回空列表。
    /// 探测结果缓存 60 秒（<see cref="CamerasCacheTtl"/>），避免 /status 端点每次调用
    /// 都逐个打开 10 个摄像头（累计耗时数秒）。
    /// </remarks>
    public List<CameraInfo> GetCameras()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new List<CameraInfo>();
        }

        // 命中缓存直接返回副本，避免外部修改污染缓存
        lock (_camerasCacheLock)
        {
            if (_camerasCache != null && DateTime.UtcNow - _camerasCacheTime < CamerasCacheTtl)
            {
                return new List<CameraInfo>(_camerasCache);
            }
        }

        var result = new List<CameraInfo>();

        // 摄像头列表以系统设备枚举为准（SetupAPI，纯 Win32，任何 Windows 都可用）。
        // 此前按索引 0~9 逐个 VideoCapture(i, DSHOW) 探测：DSHOW 打开失败时会回落到默认设备，
        // 导致只有 1 个摄像头也被报成 9 个；名称也只能拼 "Camera N" 占位符。
        var deviceNames = WindowsCameraEnumerator.GetCameraNames();
        if (deviceNames.Count > 0)
        {
            for (var i = 0; i < deviceNames.Count; i++)
            {
                result.Add(new CameraInfo
                {
                    Index = i,
                    Name = deviceNames[i],
                    IsDefault = i == 0,
                });
            }
        }
        else
        {
            // 系统枚举不可用（非 Windows / 权限受限）→ 回退到逐个探测，至少保证能用
            _logger.LogWarning("系统摄像头枚举不可用，回退为按索引探测");
            for (int i = 0; i < 10; i++)
            {
                try
                {
                    using var probe = new VideoCapture(i, VideoCaptureAPIs.MSMF);
                    if (probe.IsOpened())
                    {
                        result.Add(new CameraInfo { Index = i, Name = $"摄像头 {i}", IsDefault = i == 0 });
                        probe.Release();
                    }
                }
                catch
                {
                    // 忽略探测失败
                }
            }
        }

        if (result.Count > 0)
        {
            _logger.LogInformation("检测到 {Count} 个摄像头：{Names}",
                result.Count, string.Join(" / ", result.Select(c => $"{c.Index}:{c.Name}")));
        }

        if (result.Count == 0)
        {
            _logger.LogWarning("未检测到可用摄像头");
        }

        // 写入缓存（替换旧值，下次 60 秒内直接命中）
        lock (_camerasCacheLock)
        {
            _camerasCache = result;
            _camerasCacheTime = DateTime.UtcNow;
        }

        return result;
    }

    /// <summary>
    /// 选择要使用的摄像头。
    /// </summary>
    /// <param name="deviceId">设备索引字符串（如 "0"、"1"）。</param>
    public void SelectCamera(string deviceId)
    {
        if (!int.TryParse(deviceId, out var index) || index < 0)
        {
            _logger.LogWarning("无效的摄像头索引：{DeviceId}", deviceId);
            return;
        }

        lock (_captureLock)
        {
            _currentIndex = index;
            _capture?.Release();
            _capture?.Dispose();
            _capture = null;
            _consecutiveFailures = 0;
        }
        _logger.LogInformation("已选择摄像头索引 {Index}", index);
    }

    /// <summary>
    /// 打开当前索引对应的摄像头，并设置 1280x720@30fps 以兼顾画质与传输流畅度。
    /// 调用方需持有 <see cref="_captureLock"/>。
    /// </summary>
    private void OpenCamera()
    {
        _capture?.Dispose();
        // MSMF 优先：实测该后端能干净地打开设备并返回真实后端名；
        // DSHOT 后端在本机打开不存在的索引时不报错而是回落默认设备，还会刷 QI 错误，
        // 这正是此前「采集连续失败 → 重开摄像头」的来源。仅在 MSMF 打不开时回退 DSHOW。
        _capture = new VideoCapture(_currentIndex, VideoCaptureAPIs.MSMF);
        if (!_capture.IsOpened())
        {
            _capture.Release();
            _capture.Dispose();
            _logger.LogWarning("MSMF 后端打开摄像头 {Index} 失败，回退 DSHOW", _currentIndex);
            _capture = new VideoCapture(_currentIndex, VideoCaptureAPIs.DSHOW);
        }
        if (_capture.IsOpened())
        {
            // 设置分辨率 1280x720（720p）保证画质
            _capture.Set(VideoCaptureProperties.FrameWidth, 1280);
            _capture.Set(VideoCaptureProperties.FrameHeight, 720);
            // 设置帧率 30fps 保证流畅度
            _capture.Set(VideoCaptureProperties.Fps, 30);
            // 关键：强制 MJPG 编码。默认 YUY2 是未压缩格式，720p@30 需要 ~220 MB/s，
            // 远超 USB 2.0 总线带宽（~35 MB/s），设备实际只能跑 5-10fps——这就是
            // "画面不流畅"的隐藏根因。MJPG 压缩后 ~20 MB/s，30fps 可达。
            _capture.Set(VideoCaptureProperties.FourCC, FourCC.MJPG);
            _logger.LogInformation(
                "摄像头 {Index} 已打开，1280x720@30fps MJPG（实际 {Fps:F0}fps {W}x{H}）",
                _currentIndex, _capture.Get(VideoCaptureProperties.Fps),
                _capture.Get(VideoCaptureProperties.FrameWidth),
                _capture.Get(VideoCaptureProperties.FrameHeight));
        }
        else
        {
            _logger.LogWarning("无法打开摄像头 {Index}", _currentIndex);
        }
    }

    /// <summary>
    /// 采集一帧并编码为 JPEG。
    /// </summary>
    /// <param name="quality">JPEG 编码质量（1-100）。</param>
    /// <returns>JPEG 字节数组；采集失败时返回空数组。</returns>
    public byte[] CaptureFrameAsJpeg(int quality = 40)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("摄像头采集仅在 Windows 上可用");
        }

        lock (_captureLock)
        {
            if (_capture == null || !_capture.IsOpened())
            {
                OpenCamera();
                if (_capture == null || !_capture.IsOpened())
                {
                    return Array.Empty<byte>();
                }
            }

            // 摄像头预热：首次打开后前几帧可能为空，重试最多 5 次
            for (int retry = 0; retry < 5; retry++)
            {
                using var frame = new Mat();
                if (_capture.Read(frame) && !frame.Empty())
                {
                    _consecutiveFailures = 0;
                    // 编码为 JPEG
                    var paramsVec = new int[] { (int)ImwriteFlags.JpegQuality, Math.Clamp(quality, 1, 100) };
                    Cv2.ImEncode(".jpg", frame, out var bytes, paramsVec);
                    return bytes;
                }
                // 空帧，短暂等待后重试
                Thread.Sleep(30);
            }

            // 连续采集失败
            _consecutiveFailures++;
            if (_consecutiveFailures >= MaxConsecutiveFailures)
            {
                _logger.LogWarning("连续 {Count} 次采集失败，重新打开摄像头 {Index}", _consecutiveFailures, _currentIndex);
                _capture?.Release();
                _capture?.Dispose();
                _capture = null;
                _consecutiveFailures = 0;
            }
            return Array.Empty<byte>();
        }
    }

    /// <summary>
    /// 读取一帧原始画面（BGR）。人脸识别直接用 <see cref="Mat"/>，不走 JPEG 编解码，
    /// 省一次压缩/解压，也避免画质损失影响识别率。
    /// </summary>
    /// <returns>调用方负责释放；失败返回 null。</returns>
    public Mat? GrabFrame()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("摄像头采集仅在 Windows 上可用");
        }

        lock (_captureLock)
        {
            if (_capture == null || !_capture.IsOpened())
            {
                OpenCamera();
                if (_capture == null || !_capture.IsOpened())
                {
                    return null;
                }
            }

            for (var retry = 0; retry < 5; retry++)
            {
                using var frame = new Mat();
                if (_capture.Read(frame) && !frame.Empty())
                {
                    _consecutiveFailures = 0;
                    return frame.Clone();
                }

                Thread.Sleep(30);
            }

            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ReleaseCamera();
    }
}

/// <summary>
/// 摄像头的使用者标识。用来在日志里说清「谁正占着摄像头」，也是独占仲裁的键。
/// </summary>
public enum CameraUser
{
    /// <summary>远程监控。</summary>
    Monitor,

    /// <summary>录制计划。</summary>
    Recording,

    /// <summary>人脸考勤。</summary>
    Attendance,
}