using System;
using System.Threading.Tasks;
using Avalonia.Threading;
using ClassPixelPro.Core.Abstractions.Services;
using ClassPixelPro.Core.Abstractions.Services.NotificationProviders;
using ClassPixelPro.Core.Attributes;
using ClassPixelPro.Core.Models.Notification;
using ClassPixelPro.Shared;
using ClassPixelPro.Shared.Interfaces;
using ClassPixelPro.Shared.Models.Management;
using Microsoft.Extensions.Logging;

namespace ClassPixelPro.Services.NotificationProviders;

/// <summary>
/// 班主任端下发的单台通知。与集控提醒（<see cref="ManagementNotificationProvider"/>）的区别：
/// <list type="bullet">
/// <item>来源是 serverless 轮询拉取（TeacherNotificationPullService），不是 gRPC 指令；</item>
/// <item>只有班主任向单台设备发送的才会走到这里；组织管理员群发的通知（scope=org）
/// 由 <see cref="ManagementNotificationProvider"/> 以「组织通知」展示。</item>
/// </list>
/// 音效/朗读由 <see cref="NotificationPlaybackHelper"/> 播放并等待完成，以支持「最高音量播放」。
/// </summary>
[NotificationProviderInfo("7f3a2c1e-6b8d-4a5e-9c0f-1d2e3f4a5b6c", "班主任通知", "\uE7E7", "来自班主任端下发的通知。")]
public class TeacherNotificationProvider : NotificationProviderBase
{
    private const int OverlaySeconds = 12;

    private INotificationHostService NotificationHostService { get; }
    private IAudioService AudioService { get; }
    private SettingsService SettingsService { get; }
    private ILogger<TeacherNotificationProvider> Logger { get; }

    public TeacherNotificationProvider(INotificationHostService notificationHostService,
        IAudioService audioService,
        SettingsService settingsService,
        ILogger<TeacherNotificationProvider> logger) : base(false)
    {
        NotificationHostService = notificationHostService;
        AudioService = audioService;
        SettingsService = settingsService;
        Logger = logger;

        NotificationHostService.RegisterNotificationProvider(this);
    }

    public async Task HandleAsync(TeacherNotificationPayload payload)
    {
        var durationSeconds = payload.DurationSeconds > 0
            ? Math.Clamp(payload.DurationSeconds, 3, 300)
            : OverlaySeconds;
        var ttsRepeat = Math.Clamp(payload.TtsRepeat, 1, 10);

        Logger.LogInformation("收到班主任通知：{}（提示音={}，朗读={}次，最高音量={}，全屏={}，显示={}秒）",
            payload.Text, payload.SoundEnabled, ttsRepeat, payload.MaxVolume, payload.Fullscreen, durationSeconds);

        // 1) 屏幕通知。全屏模式使用独立窗口（全屏置顶、自适应大字），否则走滚动弹幕。
        // 管线的音效/朗读全部关闭，由下面的播放逻辑接管。
        // 本方法由集控轮询的后台线程调用，而通知内容会创建 Avalonia 控件（IconSource 等），
        // 必须切回 UI 线程，否则抛 "Call from invalid thread"。
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (payload.Fullscreen)
            {
                if (!SettingsService.Settings.IsNotificationEnabled) return;
                IAppHost.TryGetService<Views.FullscreenNotificationWindow>()?
                    .ShowNotification(payload.Text, TimeSpan.FromSeconds(durationSeconds));
                return;
            }

            ShowNotification(new NotificationRequest
            {
                MaskContent = NotificationContent.CreateTwoIconsMask("班主任通知", rightIcon: "\uE7E7"),
                OverlayContent = NotificationContent.CreateRollingTextContent(
                    payload.Text, TimeSpan.FromSeconds(durationSeconds), 1),
                RequestNotificationSettings =
                {
                    IsSettingsEnabled = true,
                    IsSpeechEnabled = false,
                    IsNotificationSoundEnabled = false,
                    // 置顶：设置页/其他窗口在前时也要能看到班主任通知
                    IsNotificationTopmostEnabled = true
                }
            });
        });

        // 2) 音效与朗读（含最高音量语义）
        await NotificationPlaybackHelper.PlayAsync(payload, AudioService, SettingsService, Logger, "班主任通知");
    }
}
