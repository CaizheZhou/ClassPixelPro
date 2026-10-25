using System;
using System.Collections.Generic;
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
}
