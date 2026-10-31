using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClassPixelPro.Core;
using Microsoft.Extensions.Logging;

namespace ClassPixelPro.Services.FaceAttendance;

/// <summary>
/// 人脸考勤插件的一堆路径。
/// <para>
/// 目录放在应用自己的配置目录下（<see cref="CommonDirectories.AppDataFolderPath"/>），
/// 重装应用数据不丢。人脸特征属于个人生物识别信息，<b>只落本机</b>：
/// 上报到集控/班主任端的只有姓名与统计数字，特征向量和人脸小图绝不出这台机器。
/// </para>
/// </summary>
public static class FaceAttendancePaths
{
    /// <summary>配置根目录。</summary>
    public static string RootFolder => Path.Combine(CommonDirectories.AppDataFolderPath, "FaceAttendance");

    /// <summary>人脸样本目录：按学生 Id 分子目录存放人脸小图。</summary>
    public static string FacesFolder => Path.Combine(RootFolder, "faces");

    /// <summary>抓拍存档目录：每次考勤建一个时间戳子目录。</summary>
    public static string SnapshotsFolder => Path.Combine(RootFolder, "snapshots");

    /// <summary>识别模型目录：导入/下载的模型放这里。</summary>
    public static string ModelsFolder => Path.Combine(RootFolder, "models");

    /// <summary>确保目录存在，返回传入的第一个非空路径。</summary>
    public static string EnsureDirectory(params string[] paths)
    {
        var first = "";
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            if (first.Length == 0) first = path;
            try { Directory.CreateDirectory(path); }
            catch { /* 建不了就让调用方在写入时报错 */ }
        }
        return first;
    }
}

/// <summary>
/// 一款人脸识别模型的完整描述。
/// <para>
/// 插件不再把「SFace / ArcFace」硬编码成两个分支，而是维护一张模型清单：
/// 新增模型只要往 <see cref="FaceModelCatalog.All"/> 里加一条，检测、下载、选择、
/// 自动推荐这些逻辑全都自动跟上。
/// </para>
/// </summary>
public sealed class FaceRecognizerModel
{
    /// <summary>稳定的内部标识，存进设置里。不要随便改，改了会让老配置失效。</summary>
    public string Id { get; init; } = "";

    /// <summary>界面上显示的名字。</summary>
    public string DisplayName { get; init; } = "";

    /// <summary>存到磁盘上用的文件名。</summary>
    public string FileName { get; init; } = "";

    /// <summary>特征维度（128 / 512），用于诊断。</summary>
    public int FeatureLength { get; init; } = 128;

    /// <summary>文件大概多大（MB），用于界面提示与下载前预告。</summary>
    public double ApproxSizeMb { get; init; }

    /// <summary>一句话说明这个模型的特点，帮用户选。</summary>
    public string Description { get; init; } = "";

    /// <summary>是不是 InsightFace 那一系（预处理要做 (x-127.5)/127.5）。</summary>
    public bool IsInsightFaceStyle { get; init; }

    /// <summary>下载地址，按优先级排（国内镜像在前）。</summary>
    public string[] Urls { get; init; } = Array.Empty<string>();

    /// <summary>判定下载是否成功的体积下限（避免把 Git LFS 指针当成模型）。</summary>
    public long MinSize { get; init; } = 5 * 1024 * 1024;

    /// <summary>自动模式下推荐的优先级，越大越优先。</summary>
    public int AutoPriority { get; init; }

    /// <summary>
    /// 该模型推荐的余弦相似度判定阈值（大于它才认为是同一个人）。
    /// <para>
    /// <b>每款模型的阈值必须各算各的，不能共用一个数字。</b>余弦相似度的分布随模型结构、
    /// 训练集、损失函数而变，把 A 模型的阈值套到 B 模型上没有任何依据——InsightFace
    /// 官方指南里也明确写着「never carry over a threshold across model versions」。
    /// </para>
    /// <para>
    /// 最典型的反例：SFace 的官方推荐值是 <b>0.363</b>，而 InsightFace 的 ArcFace 系
    /// 工作点在 <b>0.4~0.6</b> 这一档。沿用 0.363 会让 ArcFace 过于宽松、更容易串脸。
    /// </para>
    /// </summary>
    public double RecommendedThreshold { get; init; } = 0.5;

    /// <summary>这个推荐阈值的出处，显示在界面上，让用户知道能不能信、要不要再调。</summary>
    public string ThresholdNote { get; init; } = "";

    /// <summary>认这个文件名是不是本模型（用户可能把文件改成别的名字）。</summary>
    public bool MatchesFileName(string path)
    {
        try
        {
            var name = Path.GetFileName(path).ToLowerInvariant();
            if (name == FileName.ToLowerInvariant()) return true;

            return Id switch
            {
                FaceModelCatalog.ArcFaceId =>
                    name.Contains("w600k") || name.Contains("arcface")
                    || name.Contains("mobilefacenet") || name.Contains("glint")
                    || name.Contains("buffalo"),
                FaceModelCatalog.SFaceId => name.Contains("sface"),
                _ => false,
            };
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// 插件支持的识别模型清单。
/// </summary>
public static class FaceModelCatalog
{
    /// <summary>SFace（OpenCV Zoo 自带，默认）。</summary>
    public const string SFaceId = "sface";

    /// <summary>ArcFace / MobileFaceNet（InsightFace buffalo_s 里的 w600k_mbf）。</summary>
    public const string ArcFaceId = "arcface_mobilefacenet";

    /// <summary>「自动」——按优先级和文件缺失情况自己挑一个。</summary>
    public const string AutoId = "auto";

    /// <summary>全部可选的识别模型。</summary>
    public static readonly IReadOnlyList<FaceRecognizerModel> All = new List<FaceRecognizerModel>
    {
        new()
        {
            Id = SFaceId,
            DisplayName = "SFace（自带，稳妥）",
            FileName = "face_recognition_sface_2021dec.onnx",
            FeatureLength = 128,
            ApproxSizeMb = 37,
            Description = "OpenCV 官方推荐搭配，精度够用、速度快，是默认选择。"
                          + "不再随应用包发布，首次使用时会自动从官网下载（约 37 MB）。",
            IsInsightFaceStyle = false,
            AutoPriority = 10,
            RecommendedThreshold = 0.363,
            ThresholdNote = "OpenCV 官方给的值，一般不用改",
            MinSize = 5 * 1024 * 1024,
            Urls = new[]
            {
                // 官网又拍云镜像（国内 CDN），失败再走 HuggingFace 系
                "https://class.imzcz.cn/api/public/media/models/face_recognition_sface_2021dec.onnx",
                "https://hf-mirror.com/opencv/face_recognition_sface/resolve/main/face_recognition_sface_2021dec.onnx",
                "https://huggingface.co/opencv/face_recognition_sface/resolve/main/face_recognition_sface_2021dec.onnx",
            },
        },
        new()
        {
            Id = ArcFaceId,
            DisplayName = "ArcFace / MobileFaceNet（更准）",
            FileName = "w600k_mbf.onnx",
            FeatureLength = 512,
            ApproxSizeMb = 13,
            Description = "InsightFace 的 MobileFaceNet，512 维特征，在侧脸、戴眼镜、光线偏暗时"
                          + "通常比 SFace 更稳。文件更小，但需要单独下载或手动导入。",
            IsInsightFaceStyle = true,
            AutoPriority = 20,
            RecommendedThreshold = 0.50,
            ThresholdNote = "InsightFace 系的常用起点，可按班里情况微调",
            MinSize = 5 * 1024 * 1024,
            Urls = new[]
            {
                "https://hf-mirror.com/deepghs/insightface/resolve/main/buffalo_s/w600k_mbf.onnx",
                "https://huggingface.co/deepghs/insightface/resolve/main/buffalo_s/w600k_mbf.onnx",
            },
        },
    };

    /// <summary>按 Id 找一个模型；找不到返回 null。</summary>
    public static FaceRecognizerModel? Find(string? id)
        => All.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 取某款模型的推荐余弦阈值。认不出的 Id 返回一个中间值兜底。
    /// <para>注意这<b>不是</b>「通用阈值」——阈值必须跟着模型走。</para>
    /// </summary>
    public static double RecommendedThresholdFor(string? id) => Find(id)?.RecommendedThreshold ?? 0.5;

    /// <summary>按磁盘上的文件名反推是哪个模型；认不出返回 null。</summary>
    public static FaceRecognizerModel? MatchByPath(string? path)
        => string.IsNullOrEmpty(path) ? null : All.FirstOrDefault(x => x.MatchesFileName(path));

    /// <summary>这个模型是不是 InsightFace 那一系（决定预处理）。</summary>
    public static bool IsInsightFaceStyle(string? path)
        => MatchByPath(path)?.IsInsightFaceStyle ?? LooksLikeArcFaceName(path);

    /// <summary>
    /// 兼容用的老判定：按文件名猜是不是 ArcFace 系。
    /// </summary>
    public static bool LooksLikeArcFaceName(string path)
    {
        try
        {
            var name = Path.GetFileName(path).ToLowerInvariant();
            return name.Contains("w600k") || name.Contains("arcface")
                   || name.Contains("mobilefacenet") || name.Contains("glint")
                   || name.Contains("buffalo");
        }
        catch
        {
            return false;
        }
    }
}
