using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;
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
/// 直接向 WASAPI 请求 16kHz / 16-bit / 单声道：NAudio 在请求格式与设备混音格式
/// 不同时会带上 AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM 标志，由 Windows 音频引擎
/// 完成重采样与格式转换。这样 <see cref="WaveInEventArgs.Buffer"/> 拿到的就已经是
/// 目标格式的 PCM，不需要自己再做「字节 → 浮点 → 单声道 → 重采样」的转换。
///
/// 之前自己写过一版转换，结果把 <see cref="WaveInEventArgs.Buffer"/> 当成了
/// float[] 来读——它其实是 byte[]，读出来的是原始字节而非音频样本，
/// 表现为班主任端听到一片杂音。教训：拿到的是字节流，格式由
/// <see cref="WasapiCapture.WaveFormat"/> 决定，别凭想当然解释。
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

    /// <summary>每帧字节数：16000Hz × 16bit × 1ch × 0.1s = 3200。</summary>
    public static readonly int FrameBytes = SampleRate * BitsPerSample / 8 * FrameMilliseconds / 1000;

    private readonly ILogger<AudioCaptureService> _logger;
    private readonly object _captureLock = new();
    private WasapiCapture? _capture;
    private MMDevice? _device;
    private string _deviceId = "";
    private int _currentIndex;
    private bool _disposed;

    // 输出缓冲区跨回调累积：DataAvailable 的批次大小与 100ms 一帧对不齐，
    // 每次回调用临时数组的话凑不满一帧的部分会被整段丢掉。
    private readonly List<byte> _outBuf = new(FrameBytes * 4);

    private bool _formatChecked;

    private List<MicDeviceInfo>? _deviceCache;
    private DateTime _deviceCacheAt = DateTime.MinValue;

    public AudioCaptureService(ILogger<AudioCaptureService> logger)
    {
        _logger = logger;
    }

    /// <summary>平台是否支持麦克风采集。</summary>
    public bool IsSupported => OperatingSystem.IsWindows();

    /// <summary>PCM 格式描述，供前端对齐播放参数。</summary>
    public string FormatDescriptor => $"{SampleRate}/{BitsPerSample}/{Channels}";

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

            // 直接要 16kHz/16-bit/单声道。NAudio 发现与设备混音格式不同时会自动加
            // AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM，由 Windows 音频引擎做转换，
            // 拿到的 Buffer 就是目标格式，不需要自己解释字节。
            var capture = new WasapiCapture(device)
            {
                WaveFormat = new WaveFormat(SampleRate, BitsPerSample, Channels)
            };
            _device = device;
            _capture = capture;
            _outBuf.Clear();

            capture.DataAvailable += (_, e) =>
            {
                if (e.BytesRecorded <= 0)
                {
                    return;
                }

                try
                {
                    // 校验引擎确实按请求格式送数据。若不是，说明格式协商失败，
                    // 此时按错误格式解释只会输出噪音，直接报错比静音更负责。
                    if (!_formatChecked)
                    {
                        _formatChecked = true;
                        if (e.BytesRecorded % 2 != 0)
                        {
                            throw new InvalidOperationException(
                                $"麦克风返回的数据长度（{e.BytesRecorded} 字节）不是 16-bit PCM 的整数倍，格式协商失败");
                        }

                        _logger.LogInformation("麦克风采集格式确认：{Recorded} 字节/批 ≈ {Ms}ms",
                            e.BytesRecorded,
                            Math.Round(e.BytesRecorded * 1000.0 / (SampleRate * BitsPerSample / 8 * Channels)));
                    }

                    _outBuf.AddRange(e.Buffer.Take(e.BytesRecorded));

                    // 凑满一帧发一帧
                    while (_outBuf.Count >= FrameBytes)
                    {
                        var frame = _outBuf.Take(FrameBytes).ToArray();
                        _outBuf.RemoveRange(0, FrameBytes);
                        onLevel(CalculateDb(frame, BitsPerSample));
                        onFrame(frame);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "麦克风帧处理失败");
                }
            };

            capture.RecordingStopped += (_, e) =>
            {
                if (e?.Exception != null)
                {
                    _logger.LogWarning(e.Exception, "麦克风采集异常停止");
                }
            };

            capture.StartRecording();

            var name = device.FriendlyName;
            _logger.LogInformation("麦克风采集已启动（设备 {Name}，{Rate}Hz/{Bits}bit/{Channels}ch）",
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
                _currentIndex = index;
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
        _formatChecked = false;
        _outBuf.Clear();

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
