using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Platform;
using ClassPixelPro.Core.Abstractions.Services;
using ClassPixelPro.Core.Abstractions.Services.NotificationProviders;
using ClassPixelPro.Core.Abstractions.Services.SpeechService;
using ClassPixelPro.Services.SpeechService;
using ClassPixelPro.Shared;
using ClassPixelPro.Shared.Interfaces;
using ClassPixelPro.Shared.Models.Management;
using Microsoft.Extensions.Logging;

namespace ClassPixelPro.Services.NotificationProviders;

/// <summary>
/// 通知的提示音与朗读播放，支持「最高音量播放」：播放前把系统主音量调到 100%，
/// 全部播放完成后恢复原音量。需要等待播放结束，所以不走通知管线内置的音效/朗读。
/// 班主任通知（<see cref="TeacherNotificationProvider"/>）与组织通知
/// （<see cref="ManagementNotificationProvider"/>）共用这段逻辑。
/// </summary>
internal static class NotificationPlaybackHelper
{
    /// <param name="payload">通知内容与播放选项。</param>
    /// <param name="logTag">日志里的通知来源，例如「班主任通知」。</param>
    public static async Task PlayAsync(TeacherNotificationPayload payload, IAudioService audioService,
        SettingsService settingsService, ILogger logger, string logTag)
    {
        if (!payload.SoundEnabled && !payload.TtsEnabled) return;

        var restore = payload.MaxVolume ? MasterVolumeController.Begin() : null;
        try
        {
            if (payload.SoundEnabled)
            {
                // 提示音失败不应连带跳过朗读，单独捕获
                try
                {
                    await PlayNotificationSoundAsync(audioService, settingsService, payload.MaxVolume);
                }
                catch (Exception exSound)
                {
                    logger.LogWarning(exSound, "播放{}提示音失败", logTag);
                }
            }

            if (payload.TtsEnabled)
            {
                var ttsRepeat = Math.Clamp(payload.TtsRepeat, 1, 10);
                for (var i = 0; i < ttsRepeat; i++)
                {
                    await PlaySpeechAsync(payload.Text, payload.MaxVolume, audioService, settingsService, logger);
                    if (i < ttsRepeat - 1) await Task.Delay(400);
                }
            }

            // 播放结束稍作停留再恢复音量，避免音频尾部被截断或爆音
            if (restore != null) await Task.Delay(500);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "播放{}音效/朗读失败", logTag);
        }
        finally
        {
            MasterVolumeController.Restore(restore);
        }
    }

    private static async Task PlayNotificationSoundAsync(IAudioService audioService,
        SettingsService settingsService, bool atMaxVolume)
    {
        var volume = atMaxVolume
            ? 1f
            : (float)Math.Clamp(settingsService.Settings.NotificationSoundVolume, 0.0, 1.0);

        var customSoundPath = settingsService.Settings.NotificationSoundPath;
        if (!string.IsNullOrWhiteSpace(customSoundPath) && File.Exists(customSoundPath))
        {
            await audioService.PlayAudioAsync(customSoundPath, volume);
        }
        else
        {
            using var stream = AssetLoader.Open(INotificationProvider.DefaultNotificationSoundUri);
            await audioService.PlayAudioAsync(stream, volume);
        }
    }

    private static async Task PlaySpeechAsync(string text, bool atMaxVolume, IAudioService audioService,
        SettingsService settingsService, ILogger logger)
    {
        // 语音服务由设置选择（EdgeTTS / 系统 / GPT-SoVITS）。只有 EdgeTtsService 支持
        // 「合成到文件并等待播完」，能配合最高音量语义；其他实现退回普通朗读队列（按设置音量）。
        var speech = IAppHost.TryGetService<ISpeechService>();
        if (speech is not EdgeTtsService edgeTts)
        {
            logger.LogInformation("当前语音服务不支持精确等待播放完成，退回普通朗读队列");
            speech?.EnqueueSpeechQueue(text);
            return;
        }

        var path = await edgeTts.SynthesizeToFileAsync(text);
        if (path == null)
        {
            // EdgeTTS 合成失败（常见于教室网络访问不了 speech.platform.bing.com）。
            // 直接跳过会让「勾选了文本朗读却听不到」，退回朗读队列兜底发声。
            logger.LogWarning("EdgeTTS 朗读合成失败，退回朗读队列：{}", text);
            edgeTts.EnqueueSpeechQueue(text);
            return;
        }

        var volume = atMaxVolume
            ? 1f
            : (float)Math.Clamp(settingsService.Settings.SpeechVolume, 0.0, 1.0);
        await audioService.PlayAudioAsync(path, volume);
    }
}
