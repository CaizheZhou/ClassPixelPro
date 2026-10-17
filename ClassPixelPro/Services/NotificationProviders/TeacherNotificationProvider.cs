using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Platform;
using Avalonia.Threading;
using ClassPixelPro.Core.Abstractions.Services;
using ClassPixelPro.Core.Abstractions.Services.NotificationProviders;
using ClassPixelPro.Core.Abstractions.Services.SpeechService;
using ClassPixelPro.Core.Attributes;
using ClassPixelPro.Core.Models.Notification;
using ClassPixelPro.Services.SpeechService;
using ClassPixelPro.Shared.Interfaces;
using ClassPixelPro.Shared;
using ClassPixelPro.Shared.Models.Management;
using Microsoft.Extensions.Logging;

namespace ClassPixelPro.Services.NotificationProviders;

/// <summary>
/// 班主任端下发的通知。与集控提醒（ManagementNotificationProvider）的区别：
/// <list type="bullet">
/// <item>来源是 serverless 轮询拉取（TeacherNotificationPullService），不是 gRPC 指令；</item>
/// <item>音效/朗读由本类自行播放并 await 完成，以便支持"最高音量播放"：
/// 播放前把系统主音量调到 100%，全部播放完成后恢复原音量。
/// 管线内置的音效/朗读按设置音量播放且不等待完成，无法实现该语义，因此这里关闭它们。</item>
/// </list>
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

        Logger.LogInformation("收到班主任通知：{}（提示音={}，朗读={}次，最高音量={}，显示={}秒）",
            payload.Text, payload.SoundEnabled, ttsRepeat, payload.MaxVolume, durationSeconds);

        // 1) 屏幕通知（滚动弹幕 + 遮罩）。管线的音效/朗读全部关闭，由下面的播放逻辑接管。
        // 本方法由集控轮询的后台线程调用，而通知内容会创建 Avalonia 控件（IconSource 等），
        // 必须切回 UI 线程，否则抛 "Call from invalid thread"。
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            ShowNotification(new NotificationRequest
            {
                MaskContent = NotificationContent.CreateTwoIconsMask("班主任通知", rightIcon: ""),
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

        // 2) 音效与朗读。开启最高音量时：播放前主音量 → 100%，全部播放完成后恢复。
        if (payload.SoundEnabled || payload.TtsEnabled)
        {
            var restore = payload.MaxVolume ? MasterVolumeController.Begin() : null;
            try
            {
                if (payload.SoundEnabled)
                {
                    // 提示音失败不应连带跳过朗读，单独捕获
                    try
                    {
                        await PlayNotificationSoundAsync(payload.MaxVolume);
                    }
                    catch (Exception exSound)
                    {
                        Logger.LogWarning(exSound, "播放班主任通知提示音失败");
                    }
                }

                if (payload.TtsEnabled)
                {
                    for (var i = 0; i < ttsRepeat; i++)
                    {
                        await PlaySpeechAsync(payload.Text, payload.MaxVolume);
                        if (i < ttsRepeat - 1) await Task.Delay(400);
                    }
                }

                // 播放结束稍作停留再恢复音量，避免音频尾部被截断或爆音
                if (restore != null)
                {
                    await Task.Delay(500);
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "播放班主任通知音效/朗读失败");
            }
            finally
            {
                MasterVolumeController.Restore(restore);
            }
        }
    }

    private async Task PlayNotificationSoundAsync(bool atMaxVolume)
    {
        var volume = atMaxVolume
            ? 1f
            : (float)Math.Clamp(SettingsService.Settings.NotificationSoundVolume, 0.0, 1.0);

        var customSoundPath = SettingsService.Settings.NotificationSoundPath;
        if (!string.IsNullOrWhiteSpace(customSoundPath) && File.Exists(customSoundPath))
        {
            await AudioService.PlayAudioAsync(customSoundPath, volume);
        }
        else
        {
            using var stream = AssetLoader.Open(INotificationProvider.DefaultNotificationSoundUri);
            await AudioService.PlayAudioAsync(stream, volume);
        }
    }

    private async Task PlaySpeechAsync(string text, bool atMaxVolume)
    {
        // 语音服务由设置选择（EdgeTTS / 系统 / GPT-SoVITS）。只有 EdgeTtsService 支持
        // "合成到文件并等待播完"，能配合最高音量语义；其他实现退回普通朗读队列（按设置音量）。
        var speech = IAppHost.TryGetService<ISpeechService>();
        if (speech is not EdgeTtsService edgeTts)
        {
            Logger.LogInformation("当前语音服务不支持精确等待播放完成，退回普通朗读队列");
            speech?.EnqueueSpeechQueue(text);
            return;
        }

        var path = await edgeTts.SynthesizeToFileAsync(text);
        if (path == null)
        {
            // EdgeTTS 合成失败（常见于教室网络访问不了 speech.platform.bing.com）。
            // 直接跳过会让"勾选了文本朗读却听不到"，退回朗读队列兜底发声。
            Logger.LogWarning("EdgeTTS 朗读合成失败，退回朗读队列：{}", text);
            edgeTts.EnqueueSpeechQueue(text);
            return;
        }

        var volume = atMaxVolume
            ? 1f
            : (float)Math.Clamp(SettingsService.Settings.SpeechVolume, 0.0, 1.0);
        await AudioService.PlayAudioAsync(path, volume);
    }
}
