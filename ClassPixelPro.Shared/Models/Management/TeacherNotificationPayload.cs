using System;

namespace ClassPixelPro.Shared.Models.Management;

/// <summary>
/// 班主任端下发的通知。客户端在集控轮询循环里拉取后逐条处理。
/// </summary>
public class TeacherNotificationPayload
{
    /// <summary>通知 ID（服务端 teacher_notifications.id）。</summary>
    public long Id { get; set; }

    /// <summary>通知正文。</summary>
    public string Text { get; set; } = "";

    /// <summary>是否播放提示音。</summary>
    public bool SoundEnabled { get; set; }

    /// <summary>是否用语音朗读正文。</summary>
    public bool TtsEnabled { get; set; }

    /// <summary>
    /// 播放音效/朗读时把系统主音量临时调到 100%，全部播放完成后恢复原音量。
    /// </summary>
    public bool MaxVolume { get; set; }
}
