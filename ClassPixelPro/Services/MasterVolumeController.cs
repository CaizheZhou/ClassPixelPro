using System;
using NAudio.CoreAudioApi;

namespace ClassPixelPro.Services;

/// <summary>
/// 系统主音量（默认播放设备）的临时提升控制。
/// <para>
/// 用法：<c>var restore = MasterVolumeController.Begin();</c> … 播放 …
/// <c>MasterVolumeController.Restore(restore);</c>
/// </para>
/// <para>只在班主任通知开启"最高音量播放"时使用：播放前保存当前音量并调到 100%，播完恢复。</para>
/// </summary>
public static class MasterVolumeController
{
    /// <summary>把默认播放设备主音量调到 100%，并返回可用于恢复的句柄。</summary>
    public static VolumeRestoreHandle? Begin()
    {
        try
        {
            var device = new MMDeviceEnumerator().GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var previous = device.AudioEndpointVolume.MasterVolumeLevelScalar;
            device.AudioEndpointVolume.MasterVolumeLevelScalar = 1f;
            return new VolumeRestoreHandle(device, previous);
        }
        catch (Exception)
        {
            // 找不到音频设备（如无输出设备/驱动异常）时放弃音量控制，不影响通知本身
            return null;
        }
    }

    /// <summary>恢复到播放前的主音量。</summary>
    public static void Restore(VolumeRestoreHandle? handle)
    {
        if (handle == null) return;
        try
        {
            handle.Device.AudioEndpointVolume.MasterVolumeLevelScalar = handle.PreviousVolume;
        }
        catch
        {
            // 设备在播放期间被移除等场景：恢复失败可忽略
        }
        finally
        {
            handle.Device.Dispose();
        }
    }

    public class VolumeRestoreHandle
    {
        public MMDevice Device { get; }
        public float PreviousVolume { get; }

        internal VolumeRestoreHandle(MMDevice device, float previousVolume)
        {
            Device = device;
            PreviousVolume = previousVolume;
        }
    }
}
