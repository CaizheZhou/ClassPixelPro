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

    /// <summary>屏幕通知滚动显示时长（秒），服务端默认 12。</summary>
    public int DurationSeconds { get; set; } = 12;

    /// <summary>文本朗读重复次数，服务端默认 1。</summary>
    public int TtsRepeat { get; set; } = 1;

    /// <summary>
    /// 全屏通知：为 true 时不再滚动弹幕，而是在屏幕上全屏置顶展示正文，
    /// 字号随屏幕与文字长度自适应放大。
    /// </summary>
    public bool Fullscreen { get; set; }
}
