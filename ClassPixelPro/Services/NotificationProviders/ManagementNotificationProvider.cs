using System;
using System.Threading.Tasks;
using Avalonia.Threading;
using ClassPixelPro.Core.Abstractions.Services;
using ClassPixelPro.Core.Abstractions.Services.Management;
using ClassPixelPro.Core.Abstractions.Services.NotificationProviders;
using ClassPixelPro.Core.Attributes;
using ClassPixelPro.Core.Models.Notification;
using ClassPixelPro.Shared;
using ClassPixelPro.Shared.Interfaces;
using ClassPixelPro.Shared.Models.Management;
using ClassPixelPro.Shared.Protobuf.Command;
using ClassPixelPro.Shared.Protobuf.Enum;
using Microsoft.Extensions.Logging;

namespace ClassPixelPro.Services.NotificationProviders;

[NotificationProviderInfo("0117fb4f-5374-434a-97fb-4f5374634a07", "集控提醒", "\uE821", "来自集控服务器的提醒。")]
public class ManagementNotificationProvider : NotificationProviderBase
{
    private const int OverlaySeconds = 12;

    private INotificationHostService NotificationHostService { get; }
    
    private IManagementService ManagementService { get; }

    private IAudioService AudioService { get; }

    private SettingsService SettingsService { get; }
    
    private ILogger<ManagementNotificationProvider> Logger { get; }

    public ManagementNotificationProvider(INotificationHostService notificationHostService, 
        IManagementService managementService,
        IAudioService audioService,
        SettingsService settingsService,
        ILogger<ManagementNotificationProvider> logger) : base(false)
    {
        NotificationHostService = notificationHostService;
        ManagementService = managementService;
        AudioService = audioService;
        SettingsService = settingsService;
        Logger = logger;
        
        if (!ManagementService.IsManagementEnabled)
            return;
        NotificationHostService.RegisterNotificationProvider(this);
        if (ManagementService.Connection != null)
        {
            ManagementService.Connection.CommandReceived += ConnectionOnCommandReceived;
        }
    }

    private void ConnectionOnCommandReceived(object? sender, ClientCommandEventArgs e)
    {
        if (e.Type != CommandTypes.SendNotification)
            return;
        SendNotification payload;
        try
        {
            payload = SendNotification.Parser.ParseFrom(e.Payload);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "解析集控通知指令失败。");
            return;
        }

        if (payload == null)
            return;

        var repeatCounts = Math.Max(1, payload.RepeatCounts);
        var durationSeconds = payload.DurationSeconds <= 0 ? 5 : payload.DurationSeconds;
        var maskText = string.IsNullOrWhiteSpace(payload.MessageMask) ? "集控通知" : payload.MessageMask;
        var messageContent = string.IsNullOrWhiteSpace(payload.MessageContent) ? "" : payload.MessageContent;

        Logger.LogInformation("接受集控消息：{} {}", payload.MessageMask, payload.MessageContent);
        ShowNotification(new NotificationRequest()
        {
            MaskContent = NotificationContent.CreateTwoIconsMask(maskText, rightIcon: "\uE7E7"),
            OverlayContent = NotificationContent.CreateRollingTextContent(messageContent, TimeSpan.FromSeconds(durationSeconds) * repeatCounts, repeatCounts),
            IsPriorityOverride = payload.IsEmergency,
            PriorityOverride = -1,
            RequestNotificationSettings =
            {
                IsSettingsEnabled = true,
                IsSpeechEnabled = payload.IsSpeechEnabled,
                IsNotificationEffectEnabled = payload.IsEffectEnabled,
                IsNotificationSoundEnabled = payload.IsSoundEnabled,
                IsNotificationTopmostEnabled = payload.IsTopmost
            }
        });
    }

    /// <summary>
    /// 组织管理员在后台群发的通知（serverless 轮询拉取，scope=org）。
    /// 与 gRPC 集控指令走同一个提醒提供方，因此用户在「提醒设置 → 集控提醒」里的
    /// 开关、优先级和提示音渠道都对它生效。
    /// </summary>
    public async Task HandleAsync(TeacherNotificationPayload payload)
    {
        var durationSeconds = payload.DurationSeconds > 0
            ? Math.Clamp(payload.DurationSeconds, 3, 300)
            : OverlaySeconds;
        var ttsRepeat = Math.Clamp(payload.TtsRepeat, 1, 10);

        Logger.LogInformation("收到组织通知：{}（提示音={}，朗读={}次，最高音量={}，全屏={}，显示={}秒）",
            payload.Text, payload.SoundEnabled, ttsRepeat, payload.MaxVolume, payload.Fullscreen, durationSeconds);

        // 通知内容会创建 Avalonia 控件，必须切回 UI 线程（本方法由集控轮询的后台线程调用）
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
                MaskContent = NotificationContent.CreateTwoIconsMask("组织通知", rightIcon: "\uE7E7"),
                OverlayContent = NotificationContent.CreateRollingTextContent(
                    payload.Text, TimeSpan.FromSeconds(durationSeconds), 1),
                RequestNotificationSettings =
                {
                    IsSettingsEnabled = true,
                    // 音效/朗读由 NotificationPlaybackHelper 播放，以便支持「最高音量播放」
                    IsSpeechEnabled = false,
                    IsNotificationSoundEnabled = false,
                    IsNotificationTopmostEnabled = true
                }
            });
        });

        await NotificationPlaybackHelper.PlayAsync(payload, AudioService, SettingsService, Logger, "组织通知");
    }
}