using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NAudio.CoreAudioApi;
using Microsoft.Extensions.Logging;

namespace ClassPixelPro.Services.RemoteMonitor;

/// <summary>
/// 麦克风采集到的设备信息。
/// </summary>
public sealed class MicDeviceInfo
{
    /// <summary>设备编号（枚举顺序）。</summary>
    public int Index { get; set; }

    /// <summary>设备 ID。</summary>
    public string Id { get; set; } = "";

    /// <summary>设备友好名称。</summary>
    public string Name { get; set; } = "";

    /// <summary>是否为系统默认设备。</summary>
    public bool IsDefault { get; set; }
}

/// <summary>
/// 麦克风采集服务：把大屏客户端所在环境的现场声音采集成 PCM 推给班主任端。
/// </summary>
/// <remarks>
/// 与 <see cref="AudioPlaybackService"/>（传话：老师 → 大屏）方向相反，这里是收音：
/// 大屏麦克风 → 班主任端。
///
/// <para>
/// 只在班主任端建立 <c>/mic/stream</c> 连接期间才真正打开麦克风，断开即释放，
/// 不会在后台常驻占用设备。
/// </para>
///
/// <para>
/// WASAPI 共享模式拿到的是设备混音格式（通常 48kHz 立体声 IEEE float），
/// 这里自行转成 16kHz / 16-bit / 单声道 PCM，浏览器 AudioContext 可直接播放。
/// 没有用 NAudio 的 SampleToWaveProvider 之类的封装——NAudio 2.2.x 拆包后
/// 这些便捷类并不在导出类型里，自行转换反而更可控。
/// </para>
///
/// <para>
/// 仅在 Windows 上可用；非 Windows 平台调用会抛 <see cref="PlatformNotSupportedException"/>。
/// </para>
/// </remarks>
public sealed class AudioCaptureService : IDisposable
{
    /// <summary>采样率（Hz）。</summary>
    public const int SampleRate = 16000;

    /// <summary>位深（bit）。</summary>
    public const int BitsPerSample = 16;

    /// <summary>声道数（单声道）。</summary>
    public const int Channels = 1;

    /// <summary>每帧时长（毫秒）。越小延迟越低，但包越多。</summary>
    public const int FrameMilliseconds = 100;

    /// <summary>每帧字节数：16000Hz × 16bit × 1ch × 0.1s。</summary>
    public static readonly int FrameBytes = SampleRate * BitsPerSample / 8 * FrameMilliseconds / 1000;

    private readonly ILogger<AudioCaptureService> _logger;
    private readonly object _captureLock = new();
    private WasapiCapture? _capture;
    private MMDevice? _device;
    private string _deviceId = "";
    private int _currentIndex;
    private bool _disposed;

    // 重采样状态：设备原生采样率 → 16kHz
    private double _pos;
    private float _lastSample;

    // 某些驱动（尤其是虚拟化环境 / 声卡回环）虽然声称给的是 IEEE float，
    // 实际送来的却是 0~255 的原始字节。不做归一化的话，这些值乘 32767 会被
    // 截断成满刻度——听感是巨大噪音，电平恒为 0 dBFS。这里按首包的实际取值
    // 范围反推编码并归一化。
    private double _scale = 1.0;
    private bool _scaleDetected;
    private bool _formatAnomaly;

    public AudioCaptureService(ILogger<AudioCaptureService> logger)
    {
        _logger = logger;
    }

    /// <summary>平台是否支持麦克风采集。</summary>
    public bool IsSupported => OperatingSystem.IsWindows();

    /// <summary>PCM 格式描述，供前端对齐播放参数。</summary>
    public string FormatDescriptor => $"{SampleRate}/{BitsPerSample}/{Channels}";

    private List<MicDeviceInfo>? _deviceCache;
    private DateTime _deviceCacheAt = DateTime.MinValue;

    /// <summary>
    /// 枚举可用麦克风（缓存 60 秒，避免每次 /status 都重新枚举设备）。
    /// </summary>
    public List<MicDeviceInfo> GetDevices()
    {
        var list = new List<MicDeviceInfo>();
        if (!IsSupported)
        {
            return list;
        }

        lock (_captureLock)
        {
            if (_deviceCache != null && (DateTime.UtcNow - _deviceCacheAt).TotalSeconds < 60)
            {
                return new List<MicDeviceInfo>(_deviceCache);
            }
        }

        MMDevice? defaultDevice = null;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);

            var i = 0;
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                list.Add(new MicDeviceInfo
                {
                    Index = i++,
                    Id = device.ID,
                    Name = device.FriendlyName,
                    IsDefault = defaultDevice != null &&
                                string.Equals(device.ID, defaultDevice.ID, StringComparison.OrdinalIgnoreCase)
                });
            }
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "枚举麦克风失败");
        }
        finally
        {
            defaultDevice?.Dispose();
        }

        lock (_captureLock)
        {
            _deviceCache = new List<MicDeviceInfo>(list);
            _deviceCacheAt = DateTime.UtcNow;
        }

        return list;
    }

    /// <summary>
    /// 打开麦克风并开始采集。
    /// </summary>
    /// <param name="onFrame">每帧回调，参数为 16kHz/16-bit/单声道 PCM。</param>
    /// <param name="onLevel">每帧电平回调，参数为 dBFS（-100 ~ 0）。</param>
    /// <returns>实际使用的设备名称。</returns>
    public string Start(Action<byte[]> onFrame, Action<double> onLevel)
    {
        lock (_captureLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            StopCore();
            if (!IsSupported)
            {
                throw new PlatformNotSupportedException("麦克风采集仅在 Windows 上可用");
            }

            MMDevice device;
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                device = ResolveDevice(enumerator);
            }
            catch (Exception e)
            {
                throw new InvalidOperationException("没有检测到可用的麦克风设备", e);
            }

            var capture = new WasapiCapture(device);
            _device = device;
            _capture = capture;

            _pos = 0;
            _lastSample = 0;

            // 输出缓冲区跨回调累积：DataAvailable 的批次大小和 100ms 一帧对不齐，
            // 每次回调都用临时数组的话，凑不满一帧的部分会被整段丢掉。
            var outBuf = new byte[FrameBytes];
            var outFilled = 0;

            capture.DataAvailable += (_, e) =>
            {
                if (e.BytesRecorded <= 0)
                {
                    return;
                }

                var inputChannels = Math.Max(1, capture.WaveFormat.Channels);
                var inputRate = capture.WaveFormat.SampleRate;
                var step = inputRate / (double)SampleRate;

                try
                {
                    var samples = e.Buffer;
                    var count = e.BytesRecorded / (4 * inputChannels);

                    // 首包做一次取值范围探测，确定归一化系数
                    if (!_scaleDetected && count > 0)
                    {
                        _scaleDetected = true;
                        var peak = 0f;
                        for (var i = 0; i < count && i < 4096; i++)
                        {
                            for (var c = 0; c < inputChannels; c++)
                            {
                                var a = Math.Abs(samples[i * inputChannels + c]);
                                if (a > peak) peak = a;
                            }
                        }

                        if (peak > 4f)
                        {
                            // 不是归一化浮点：按量级反推原始位深再归一化
                            _scale = peak > 20000f ? 1.0 / 2147483648.0
                                    : peak > 2000f ? 1.0 / 32768.0
                                    : 1.0 / 128.0;
                            _formatAnomaly = true;
                            _logger.LogWarning(
                                "麦克风返回的样本超出 IEEE float 范围（峰值 {Peak}），已按 {Div} 自动归一化；" +
                                "该设备的驱动可能未按协商格式送数据", peak, _scale);
                        }
                    }

                    while (_pos < count)
                    {
                        var idx = (int)_pos;

                        // 左右声道求平均得到单声道
                        var mono = 0f;
                        var baseIdx = idx * inputChannels;
                        for (var c = 0; c < inputChannels; c++)
                        {
                            mono += samples[baseIdx + c];
                        }

                        mono /= inputChannels;

                        // 线性插值后推进位置
                        var frac = _pos - idx;
                        var scaled = (float)(mono * _scale);
                        var interpolated = _lastSample + (scaled - _lastSample) * frac;
                        _lastSample = scaled;
                        _pos += step;

                        var v = (short)Math.Clamp(interpolated * 32767f, -32768f, 32767f);
                        BitConverter.GetBytes(v).CopyTo(outBuf, outFilled);
                        outFilled += 2;

                        if (outFilled == FrameBytes)
                        {
                            onLevel(CalculateDb(outBuf, BitsPerSample));
                            onFrame(outBuf);
                            outFilled = 0;
                        }
                    }

                    // 本批读完，把位置挪回缓冲区起点，供下一批续上
                    _pos -= count;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "麦克风帧处理失败");
                }
            };

            capture.StartRecording();

            var name = device.FriendlyName;
            _logger.LogInformation("麦克风采集已启动（设备 {Name}，输出 {Rate}Hz/{Bits}bit/{Channels}ch）",
                name, SampleRate, BitsPerSample, Channels);
            return name;
        }
    }

    private MMDevice ResolveDevice(MMDeviceEnumerator enumerator)
    {
        var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
        if (string.IsNullOrWhiteSpace(_deviceId))
        {
            return defaultDevice;
        }

        foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            if (string.Equals(d.ID, _deviceId, StringComparison.OrdinalIgnoreCase))
            {
                defaultDevice.Dispose();
                return d;
            }
        }

        // 指定的设备没了，回落到默认
        return defaultDevice;
    }

    /// <summary>
    /// 由 PCM 数据计算电平（dBFS）。全静音时返回 -100。
    /// </summary>
    public static double CalculateDb(byte[] pcm, int bitsPerSample)
    {
        if (pcm == null || pcm.Length < 2 || bitsPerSample != 16)
        {
            return -100;
        }

        double sum = 0;
        int samples = pcm.Length / 2;
        for (int i = 0; i < samples; i++)
        {
            short s = BitConverter.ToInt16(pcm, i * 2);
            double v = s / 32768.0;
            sum += v * v;
        }

        if (samples == 0)
        {
            return -100;
        }

        double rms = Math.Sqrt(sum / samples);
        if (rms <= 1e-7)
        {
            return -100;
        }

        double db = 20 * Math.Log10(rms);
        return Math.Clamp(Math.Round(db, 1), -100, 0);
    }

    /// <summary>选择采集设备（按枚举索引）。</summary>
    public void SelectDevice(int index)
    {
        var devices = GetDevices();
        if (index >= 0 && index < devices.Count)
        {
            lock (_captureLock)
            {
                _deviceId = devices[index].Id;
                _deviceCache = null;
            }
        }
    }

    /// <summary>当前采集设备编号。</summary>
    public int CurrentIndex
    {
        get
        {
            lock (_captureLock)
            {
                return _currentIndex;
            }
        }
    }

    /// <summary>是否正在采集。</summary>
    public bool IsCapturing
    {
        get
        {
            lock (_captureLock)
            {
                return _capture != null;
            }
        }
    }

    /// <summary>停止采集并释放麦克风。</summary>
    public void Stop() => StopCore();

    private void StopCore()
    {
        var capture = _capture;
        var device = _device;
        _capture = null;
        _device = null;

        if (capture != null)
        {
            try
            {
                capture.StopRecording();
            }
            catch
            {
                // 设备可能已被系统移除
            }

            capture.Dispose();
            _logger.LogInformation("麦克风采集已停止");
        }

        device?.Dispose();
    }

    public void Dispose()
    {
        lock (_captureLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        StopCore();
    }
}
