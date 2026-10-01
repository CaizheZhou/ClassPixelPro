using System;
using NAudio.Wave;
using Microsoft.Extensions.Logging;

namespace ClassPixelPro.Services.RemoteMonitor;

/// <summary>
/// 音频播放服务：接收班主任端发送的 PCM 音频数据并播放。
/// </summary>
/// <remarks>
/// 当前实现使用 NAudio 的 WaveOutEvent + BufferedWaveProvider 播放 PCM 音频。
/// 默认格式：16kHz、16-bit、单声道 PCM（与浏览器 WebRTC/Opus 解码后常用格式一致）。
/// 班主任端需将音频解码为 PCM 后发送，本服务不处理 WebM/Opus 解码。
/// 仅在 Windows 上可用；非 Windows 平台调用会抛出 <see cref="PlatformNotSupportedException"/>。
/// </remarks>
public sealed class AudioPlaybackService : IDisposable
{
    private readonly ILogger<AudioPlaybackService> _logger;
    private readonly object _playbackLock = new();
    private WaveOutEvent? _waveOut;
    private BufferedWaveProvider? _bufferedProvider;
    private WaveFormat _waveFormat = new(16000, 16, 1);
    private bool _disposed;

    public AudioPlaybackService(ILogger<AudioPlaybackService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// 平台是否支持音频播放。
    /// </summary>
    public bool IsSupported => OperatingSystem.IsWindows();

    /// <summary>
    /// 当前音频格式。
    /// </summary>
    public WaveFormat WaveFormat => _waveFormat;

    /// <summary>
    /// 设置音频格式（在播放前调用）。
    /// </summary>
    /// <param name="sampleRate">采样率（Hz）。</param>
    /// <param name="bits">位深（8/16/24/32）。</param>
    /// <param name="channels">声道数（1=单声道，2=立体声）。</param>
    public void SetFormat(int sampleRate, int bits, int channels)
    {
        lock (_playbackLock)
        {
            _waveFormat = new WaveFormat(sampleRate, bits, channels);
            // 重建 buffered provider
            if (_bufferedProvider != null)
            {
                _bufferedProvider = new BufferedWaveProvider(_waveFormat)
                {
                    BufferDuration = TimeSpan.FromSeconds(2),
                    DiscardOnBufferOverflow = true,
                };
                _waveOut?.Init(_bufferedProvider);
            }
        }
        _logger.LogInformation("音频格式设置为 {SampleRate}Hz/{Bits}bit/{Channels}ch", sampleRate, bits, channels);
    }

    /// <summary>
    /// 播放 PCM 音频数据。
    /// </summary>
    /// <param name="audioData">PCM 字节数据。</param>
    /// <exception cref="PlatformNotSupportedException">当前平台不支持。</exception>
    public void PlayAudio(byte[] audioData)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("音频播放仅在 Windows 上可用");
        }
        if (audioData == null || audioData.Length == 0) return;

        lock (_playbackLock)
        {
            try
            {
                EnsurePlaybackInitialized();
            }
            catch (Exception ex)
            {
                // 播放设备初始化失败（如无音频输出设备/被独占）：只记一次日志，
                // 不抛出——否则一条音频数据就会打断整个 WS 接收循环。
                if (!_playbackInitFailedLogged)
                {
                    _logger.LogError(ex, "音频播放设备初始化失败，传话播放不可用");
                    _playbackInitFailedLogged = true;
                }
                return;
            }

            _bufferedProvider!.AddSamples(audioData, 0, audioData.Length);
            _logger.LogDebug("已添加 {Size} 字节到播放缓冲区", audioData.Length);
        }
    }

    private bool _playbackInitFailedLogged;

    private void EnsurePlaybackInitialized()
    {
        if (_waveOut != null && _bufferedProvider != null) return;

        _bufferedProvider = new BufferedWaveProvider(_waveFormat)
        {
            BufferDuration = TimeSpan.FromSeconds(2),
            DiscardOnBufferOverflow = true,
        };

        _waveOut = new WaveOutEvent();
        _waveOut.Init(_bufferedProvider);
        _waveOut.Play();
        _logger.LogInformation("音频播放已初始化（{Format}）", _waveFormat);
    }

    /// <summary>
    /// 清空播放缓冲区。
    /// </summary>
    public void ClearBuffer()
    {
        lock (_playbackLock)
        {
            _bufferedProvider?.ClearBuffer();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_playbackLock)
        {
            _waveOut?.Stop();
            _waveOut?.Dispose();
            _waveOut = null;
            _bufferedProvider = null;
        }
    }
}