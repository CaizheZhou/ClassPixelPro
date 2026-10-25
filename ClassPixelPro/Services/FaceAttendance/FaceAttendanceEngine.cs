using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OpenCvSharp;

namespace ClassPixelPro.Services.FaceAttendance;

/// <summary>
/// 人脸考勤引擎：检测 + 特征提取 + 比对，串起整条链路。
/// </summary>
public sealed class FaceAttendanceEngine : IDisposable
{
    private readonly FaceAttendanceSettings _settings;
    private readonly FaceModelProvider _models;
    private readonly ILogger<FaceAttendanceEngine> _logger;

    private readonly YuNetDetector _detector;
    private readonly FaceFeatureExtractor _extractor;
    private readonly object _sync = new();

    private string _detectorPath = "";
    private string _recognizerPath = "";
    private string _lastError = "";

    public FaceAttendanceEngine(FaceAttendanceSettings settings, FaceModelProvider models,
        ILogger<FaceAttendanceEngine> logger, ILoggerFactory loggerFactory)
    {
        _settings = settings;
        _models = models;
        _logger = logger;
        _detector = new YuNetDetector(loggerFactory);
        _extractor = new FaceFeatureExtractor(loggerFactory);
    }

    /// <summary>模型是否都载入完毕。</summary>
    public bool IsReady
    {
        get
        {
            lock (_sync)
            {
                return _detector.IsLoaded && _extractor.IsLoaded;
            }
        }
    }

    /// <summary>当前使用的检测模型路径。</summary>
    public string DetectorPath
    {
        get { lock (_sync) return _detectorPath; }
    }

    /// <summary>当前使用的识别模型路径。</summary>
    public string RecognizerPath
    {
        get { lock (_sync) return _recognizerPath; }
    }

    /// <summary>识别模型是不是 ArcFace 系。</summary>
    public bool RecognizerIsArcFace => _extractor.IsArcFace;

    /// <summary>识别模型的显示名。</summary>
    public string RecognizerDisplayName
    {
        get
        {
            var path = RecognizerPath;
            if (string.IsNullOrEmpty(path))
            {
                return "缺少识别模型";
            }

            var model = FaceModelCatalog.MatchByPath(path);
            return model?.DisplayName ?? Path.GetFileName(path);
        }
    }

    /// <summary>
    /// 最近一次操作的失败原因；没有失败时为空字符串。
    /// </summary>
    public string LastError
    {
        get { lock (_sync) return _lastError; }
    }

    /// <summary>
    /// 当前应当使用的余弦相似度判定阈值。
    /// <para>
    /// <b>阈值跟着模型走</b>：不同模型的相似度分布不同，官方推荐值也不同
    /// （SFace 0.363；InsightFace 的 ArcFace 系在 0.4~0.6 这一档）。
    /// 设置里存的是用户值，但换模型时如果用户没单独调过，就用这款模型的推荐值。
    /// </para>
    /// </summary>
    public double EffectiveThreshold
    {
        get
        {
            var model = FaceModelCatalog.MatchByPath(RecognizerPath)
                       ?? FaceModelCatalog.Find(FaceModelCatalog.SFaceId);
            return _settings.ThresholdForModel(model?.Id ?? FaceModelCatalog.SFaceId,
                model?.RecommendedThreshold ?? 0.5);
        }
    }

    /// <summary>当前模型自己的推荐阈值（供界面显示与「恢复推荐值」）。</summary>
    public double RecommendedThreshold
    {
        get
        {
            var model = FaceModelCatalog.MatchByPath(RecognizerPath)
                       ?? FaceModelCatalog.Find(FaceModelCatalog.SFaceId);
            return model?.RecommendedThreshold ?? 0.5;
        }
    }

    /// <summary>当前模型推荐阈值的出处说明。</summary>
    public string ThresholdNote
    {
        get
        {
            var model = FaceModelCatalog.MatchByPath(RecognizerPath)
                       ?? FaceModelCatalog.Find(FaceModelCatalog.SFaceId);
            return model?.ThresholdNote ?? "";
        }
    }

    /// <summary>
    /// 载入（或重新载入）模型。必要时会先按需下载。
    /// </summary>
    public async Task<(bool Ok, string Message)> LoadAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var (detectorPath, recognizerPath, error) = await _models.EnsureAsync(progress, cancellationToken)
            .ConfigureAwait(false);
        if (!string.IsNullOrEmpty(error))
        {
            lock (_sync) _lastError = error;
            return (false, error);
        }

        // 两个模型的载入都是同步阻塞的（37MB 的 SFace 要好几秒），必须放后台线程，
        // 否则直接卡住界面。
        var ok = await Task.Run(() =>
        {
            var detOk = _detector.Load(detectorPath);
            var recOk = _extractor.Load(recognizerPath);
            return detOk && recOk;
        }, cancellationToken).ConfigureAwait(false);

        lock (_sync)
        {
            _detectorPath = detectorPath;
            _recognizerPath = recognizerPath;
        }

        if (!ok)
        {
            var msg = string.Join("\n", new[]
            {
                _detector.LastError,
                _extractor.LastError,
            }.Where(x => !string.IsNullOrEmpty(x)));
            lock (_sync) _lastError = msg;
            return (false, msg);
        }

        // 把「这次到底用的是哪款模型、阈值取多少、为什么取这个数」写进日志，
        // 省得以后又出现「阈值不对」却查不出用的是哪套参数。
        _logger.LogInformation("人脸考勤模型就绪：检测 {Detector}，识别 {Recognizer}（{Kind}），阈值 {Threshold:F3}（推荐 {Recommended:F3}）",
            Path.GetFileName(detectorPath), Path.GetFileName(recognizerPath),
            _extractor.IsArcFace ? "ArcFace 系 512 维" : "SFace 128 维",
            EffectiveThreshold, RecommendedThreshold);

        return (true, "模型已就绪");
    }

    /// <summary>
    /// 在图片中检测人脸并提取特征。
    /// </summary>
    public List<DetectedFace> Analyze(Mat bgr, bool withFeature = true)
    {
        var faces = _detector.Detect(bgr, _settings.MinFaceWidth);
        if (!withFeature || faces.Count == 0)
        {
            return faces;
        }

        var result = new List<DetectedFace>();
        foreach (var face in faces)
        {
            // 单张脸对齐/推理失败就跳过它，不影响同张照片里的其它人脸。
            var feature = _extractor.Extract(bgr, face);
            if (feature != null)
            {
                face.Feature = feature;
                result.Add(face);
            }
        }

        return result;
    }

    /// <summary>
    /// 在人脸库中查找与查询特征最相似的学生。
    /// </summary>
    public static (Student? Student, double Score) FindBest(
        float[] query,
        IReadOnlyList<GalleryEntry> gallery,
        double threshold)
    {
        Student? best = null;
        var bestScore = double.MinValue;

        foreach (var entry in gallery)
        {
            var score = FaceFeatureExtractor.Similarity(query, entry.Feature);
            if (score > bestScore)
            {
                bestScore = score;
                best = entry.Student;
            }
        }

        if (best == null || bestScore < threshold)
        {
            return (null, bestScore == double.MinValue ? 0d : bestScore);
        }

        return (best, bestScore);
    }

    /// <summary>
    /// 真实自检：不只检查文件在不在，而是<b>真建一次检测器、真跑一次推理</b>，
    /// 把「文件都在、但就是跑不起来」这种状态当场暴露出来。
    /// </summary>
    public async Task<(bool Ok, string Message)> SelfTestAsync()
    {
        var (ok, message) = await LoadAsync().ConfigureAwait(false);
        if (!ok)
        {
            return (false, message);
        }

        var (detOk, detMsg) = _detector.SelfTest();
        if (!detOk)
        {
            return (false, detMsg);
        }

        // 识别模型也要真跑一次：拿一张纯色图走完对齐 + 前向，确认输出维度正常。
        try
        {
            using var blank = new Mat(112, 112, MatType.CV_8UC3, new Scalar(0, 0, 0));
            var face = new DetectedFace();
            face.Box[0] = 0;
            face.Box[1] = 0;
            face.Box[2] = 112;
            face.Box[3] = 112;
            // 铺一组合理的虚拟关键点（标准正脸的 5 点位置）
            var template = new (double X, double Y)[]
            {
                (38.2946, 51.6963), (73.5318, 51.5014), (56.0252, 71.7366),
                (41.5493, 92.3655), (70.7299, 92.2041),
            };
            for (var k = 0; k < 5; k++)
            {
                face.Box[4 + k * 2] = (float)template[k].X;
                face.Box[5 + k * 2] = (float)template[k].Y;
            }

            var feature = _extractor.Extract(blank, face);
            if (feature == null || feature.Length < 64)
            {
                return (false, "识别模型前向输出异常：" + (_extractor.LastError ?? "维度不足"));
            }

            return (true, $"{detMsg} 识别模型输出 {feature.Length} 维特征。"
                          + $"当前阈值 {EffectiveThreshold:F3}（{RecognizerDisplayName}）。");
        }
        catch (Exception e)
        {
            _logger.LogError(e, "人脸考勤自检失败");
            return (false, "自检失败：" + e.Message);
        }
    }

    public void Dispose()
    {
        _detector.Dispose();
        _extractor.Dispose();
    }
}
