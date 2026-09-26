using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace ClassPixelPro.Services.RemoteMonitor;

/// <summary>
/// 屏幕采集服务：截取主屏幕并编码为 JPEG。
/// </summary>
/// <remarks>
/// 当前实现使用 GDI BitBlt 方案（System.Drawing.Common），适用于 Windows。
/// 在非 Windows 平台上调用会抛出 <see cref="PlatformNotSupportedException"/>。
/// 后续可升级到 Windows Graphics Capture API 以支持多显示器与 DPI 感知。
/// </remarks>
public sealed class ScreenCaptureService
{
    private readonly ILogger<ScreenCaptureService> _logger;

    // GetSystemMetrics 索引常量
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;
    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    public ScreenCaptureService(ILogger<ScreenCaptureService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// 平台是否支持屏幕采集。
    /// </summary>
    public bool IsSupported => OperatingSystem.IsWindows();

    /// <summary>
    /// 截取整个主屏幕并返回 JPEG 字节数组。
    /// </summary>
    /// <param name="quality">JPEG 编码质量（1-100）。</param>
    /// <returns>JPEG 字节数组。</returns>
    /// <exception cref="PlatformNotSupportedException">当前平台不支持。</exception>
    public byte[] CaptureScreenAsJpeg(int quality = 80)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("屏幕采集仅在 Windows 上可用");
        }

        return CaptureWithGdi(quality);
    }

    private byte[] CaptureWithGdi(int quality)
    {
        // 通过 P/Invoke 获取虚拟屏幕边界（不依赖 System.Windows.Forms）
        var x = GetSystemMetrics(SM_XVIRTUALSCREEN);
        var y = GetSystemMetrics(SM_YVIRTUALSCREEN);
        var width = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        var height = GetSystemMetrics(SM_CYVIRTUALSCREEN);

        if (width <= 0 || height <= 0)
        {
            _logger.LogWarning("无法获取屏幕边界，使用默认 1920x1080");
            x = 0; y = 0; width = 1920; height = 1080;
        }

        var bounds = new Rectangle(x, y, width, height);

        using var bitmap = new Bitmap(bounds.Width, bounds.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            // BitBlt 从屏幕 DC 复制到内存 DC
            graphics.CopyFromScreen(bounds.X, bounds.Y, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
        }

        using var ms = new MemoryStream();
        var encoder = GetJpegEncoder();
        var encoderParams = new EncoderParameters(1);
        encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, (long)Math.Clamp(quality, 1, 100));
        bitmap.Save(ms, encoder, encoderParams);
        return ms.ToArray();
    }

    private static ImageCodecInfo GetJpegEncoder()
    {
        var encoders = ImageCodecInfo.GetImageEncoders();
        foreach (var e in encoders)
        {
            if (e.MimeType == "image/jpeg") return e;
        }
        throw new InvalidOperationException("未找到 JPEG 编码器");
    }
}
