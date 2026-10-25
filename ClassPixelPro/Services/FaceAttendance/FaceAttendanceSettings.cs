using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ClassPixelPro.Services.FaceAttendance;

/// <summary>
/// 人脸考勤的设置。直接读写 <see cref="Models.Settings"/> 上对应的持久化属性，
/// 不单独存一份，避免两处状态不一致。
/// </summary>
public sealed class FaceAttendanceSettings
{
    private readonly Func<Models.Settings> _settings;

    public FaceAttendanceSettings(Func<Models.Settings> settings)
    {
        _settings = settings;
    }

    private Models.Settings S => _settings();

    /// <summary>功能总开关。</summary>
    public bool IsEnabled
    {
        get => S.IsFaceAttendanceEnabled;
        set => S.IsFaceAttendanceEnabled = value;
    }

    /// <summary>摄像头索引。</summary>
    public int CameraIndex
    {
        get => S.FaceAttendanceCameraIndex;
        set => S.FaceAttendanceCameraIndex = value;
    }

    /// <summary>一次考勤的抓拍张数。</summary>
    public int CaptureCount
    {
        get => S.FaceAttendanceCaptureCount;
        set => S.FaceAttendanceCaptureCount = value;
    }

    /// <summary>抓拍间隔（毫秒）。</summary>
    public int CaptureIntervalMs
    {
        get => S.FaceAttendanceCaptureIntervalMs;
        set => S.FaceAttendanceCaptureIntervalMs = value;
    }

    /// <summary>最少命中张数（多帧投票）。</summary>
    public int MinHits
    {
        get => S.FaceAttendanceMinHits;
        set => S.FaceAttendanceMinHits = value;
    }

    /// <summary>最小人脸像素宽度。</summary>
    public int MinFaceWidth
    {
        get => S.FaceAttendanceMinFaceWidth;
        set => S.FaceAttendanceMinFaceWidth = value;
    }

    /// <summary>余弦相似度判定阈值。</summary>
    public double Threshold
    {
        get => S.FaceAttendanceThreshold;
        set => S.FaceAttendanceThreshold = value;
    }

    /// <summary>抓拍保留天数。</summary>
    public int RetentionDays
    {
        get => S.FaceAttendanceRetentionDays;
        set => S.FaceAttendanceRetentionDays = value;
    }

    /// <summary>是否保留抓拍照片。</summary>
    public bool SaveSnapshots
    {
        get => S.FaceAttendanceSaveSnapshots;
        set => S.FaceAttendanceSaveSnapshots = value;
    }

    /// <summary>是否允许联网下载模型。</summary>
    public bool AllowModelDownload
    {
        get => S.FaceAttendanceAllowModelDownload;
        set => S.FaceAttendanceAllowModelDownload = value;
    }

    /// <summary>识别模型 Id：auto / sface / arcface_mobilefacenet。</summary>
    public string RecognizerModelId
    {
        get => S.FaceAttendanceRecognizerModelId;
        set => S.FaceAttendanceRecognizerModelId = value;
    }

    /// <summary>用户手动指定的检测模型路径。</summary>
    public string DetectorModelPath
    {
        get => S.FaceAttendanceDetectorModelPath;
        set => S.FaceAttendanceDetectorModelPath = value;
    }

    /// <summary>用户手动指定的识别模型路径。</summary>
    public string RecognizerModelPath
    {
        get => S.FaceAttendanceRecognizerModelPath;
        set => S.FaceAttendanceRecognizerModelPath = value;
    }

    /// <summary>
    /// 取「某款模型那一档」的阈值。
    /// </summary>
    /// <param name="modelId">模型 Id。</param>
    /// <param name="fallback">设置里没有存过这款模型的值时用哪个（一般传该模型的推荐值）。</param>
    /// <remarks>
    /// <b>阈值必须按模型分别保存</b>：你在 SFace 下调的 0.45、在 ArcFace 下调的 0.62 各记各的，
    /// 切换模型不会把上一个模型的值带过去。这既是正确行为，也不会丢用户自己的调参。
    /// </remarks>
    public double ThresholdForModel(string modelId, double fallback)
    {
        var key = "FaceAttendanceThreshold_" + modelId;
        var stored = S.FaceAttendancePerModelThresholds;
        if (stored != null && stored.TryGetValue(key, out var value) && value > 0)
        {
            return value;
        }

        // 老配置迁移：原来只有一个全局阈值，第一次取某款模型的值时如果用户改过它，就归到当前模型名下。
        if (Math.Abs(S.FaceAttendanceThreshold - 0.363) > 1e-9)
        {
            return S.FaceAttendanceThreshold;
        }

        return fallback;
    }

    /// <summary>把某款模型的阈值记下来。</summary>
    public void SetThresholdForModel(string modelId, double value)
    {
        if (value <= 0) return;
        var key = "FaceAttendanceThreshold_" + modelId;
        var dict = S.FaceAttendancePerModelThresholds;
        if (dict == null)
        {
            dict = new Dictionary<string, double>();
            S.FaceAttendancePerModelThresholds = dict;
        }

        dict[key] = value;
        S.FaceAttendancePerModelThresholds = dict;
    }

    /// <summary>抓拍画面宽度。</summary>
    public int CaptureWidth
    {
        get => S.FaceAttendanceCaptureWidth;
        set => S.FaceAttendanceCaptureWidth = value;
    }

    /// <summary>抓拍画面高度。</summary>
    public int CaptureHeight
    {
        get => S.FaceAttendanceCaptureHeight;
        set => S.FaceAttendanceCaptureHeight = value;
    }

    /// <summary>抓拍前预热秒数。</summary>
    public int WarmUpSeconds
    {
        get => S.FaceAttendanceWarmUpSeconds;
        set => S.FaceAttendanceWarmUpSeconds = value;
    }

    /// <summary>单次考勤的硬超时秒数。</summary>
    public int MaxDurationSeconds
    {
        get => S.FaceAttendanceMaxDurationSeconds;
        set => S.FaceAttendanceMaxDurationSeconds = value;
    }

    /// <summary>迟到判定方式。</summary>
    public LateJudgeMode JudgeMode
    {
        get => (LateJudgeMode)S.FaceAttendanceJudgeMode;
        set => S.FaceAttendanceJudgeMode = (int)value;
    }

    /// <summary>补执行窗口（分钟）。</summary>
    public int GraceMinutes
    {
        get => S.FaceAttendanceGraceMinutes;
        set => S.FaceAttendanceGraceMinutes = value;
    }

    /// <summary>
    /// 一周抓拍时间计划。
    /// <para>
    /// 底层存 JSON（<see cref="Models.Settings.FaceAttendanceWeekScheduleJson"/>），
    /// 集控下发的计划可以直接替换这个字符串，不需要逐天对字段。
    /// </para>
    /// </summary>
    public WeekSchedule WeekSchedule
    {
        get
        {
            var json = S.FaceAttendanceWeekScheduleJson;
            if (!string.IsNullOrWhiteSpace(json))
            {
                try
                {
                    var loaded = System.Text.Json.JsonSerializer.Deserialize<WeekSchedule>(json,
                        new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (loaded?.Days is { Count: > 0 })
                    {
                        NormalizeDays(loaded);
                        return loaded;
                    }
                }
                catch
                {
                    // 解析失败就退回到默认计划，别让一处脏数据让人脸考勤整个起不来
                }
            }

            return WeekSchedule.CreateDefault();
        }
        set
        {
            NormalizeDays(value);
            S.FaceAttendanceWeekScheduleJson = System.Text.Json.JsonSerializer.Serialize(value);
        }
    }

    /// <summary>把「七天必须齐全、且按周一到周日排序」补上，缺的日子按关闭处理。</summary>
    private static void NormalizeDays(WeekSchedule schedule)
    {
        schedule.Days ??= new List<DaySchedule>();
        for (var i = 0; i < 7; i++)
        {
            var day = (DayOfWeek)i;
            if (schedule.Days.All(x => x.Day != day))
            {
                schedule.Days.Add(new DaySchedule { Day = day });
            }
        }

        schedule.Days = schedule.Days
            .OrderBy(x => ((int)x.Day + 6) % 7) // 周一在最前
            .ToList();
    }
}
