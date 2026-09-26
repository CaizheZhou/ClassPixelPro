using System;
using System.Collections.Generic;
using System.IO;
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
    private bool _disposed;

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
    /// 枚举可用摄像头列表。
    /// </summary>
    /// <remarks>
    /// OpenCV 无法直接枚举摄像头名称，此处通过逐个尝试打开索引 0-9 来探测可用设备。
    /// 仅在 Windows 上执行探测；其他平台返回空列表。
    /// </remarks>
    public List<CameraInfo> GetCameras()
    {
        var result = new List<CameraInfo>();
        if (!OperatingSystem.IsWindows())
        {
            return result;
        }

        for (int i = 0; i < 10; i++)
        {
            try
            {
                using var probe = new VideoCapture(i, VideoCaptureAPIs.DSHOW);
                if (probe.IsOpened())
                {
                    result.Add(new CameraInfo
                    {
                        Index = i,
                        Name = $"Camera {i}",
                        IsDefault = i == 0,
                    });
                }
            }
            catch
            {
                // 忽略探测失败
            }
        }

        if (result.Count == 0)
        {
            _logger.LogWarning("未检测到可用摄像头");
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
            // 释放旧 capture，下次采集时重新打开
            _capture?.Release();
            _capture?.Dispose();
            _capture = null;
        }
        _logger.LogInformation("已选择摄像头索引 {Index}", index);
    }

    /// <summary>
    /// 采集一帧并编码为 JPEG。
    /// </summary>
    /// <param name="quality">JPEG 编码质量（1-100）。</param>
    /// <returns>JPEG 字节数组；采集失败时返回空数组。</returns>
    public byte[] CaptureFrameAsJpeg(int quality = 80)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("摄像头采集仅在 Windows 上可用");
        }

        lock (_captureLock)
        {
            if (_capture == null || !_capture.IsOpened())
            {
                _capture?.Dispose();
                _capture = new VideoCapture(_currentIndex, VideoCaptureAPIs.DSHOW);
                if (!_capture.IsOpened())
                {
                    _logger.LogWarning("无法打开摄像头 {Index}", _currentIndex);
                    return Array.Empty<byte>();
                }
            }

            using var frame = new Mat();
            if (!_capture.Read(frame) || frame.Empty())
            {
                return Array.Empty<byte>();
            }

            // 编码为 JPEG
            var paramsVec = new int[] { (int)ImwriteFlags.JpegQuality, Math.Clamp(quality, 1, 100) };
            Cv2.ImEncode(".jpg", frame, out var bytes, paramsVec);
            return bytes;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_captureLock)
        {
            _capture?.Release();
            _capture?.Dispose();
            _capture = null;
        }
    }
}