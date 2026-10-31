using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace ClassPixelPro.Services.FaceAttendance;

/// <summary>
/// 负责定位/下载/导入人脸模型文件。
/// </summary>
/// <remarks>
/// <para>
/// <b>关键点：模型优先从「随应用包发布的 Assets\Models」里找。</b>
/// 主程序运行时 <c>AppContext.BaseDirectory</c> 指向的是<b>可执行文件</b>所在目录，
/// 模型就放在那里，所以两者一致；但仍然额外按几种常见位置兜底，
/// 换安装方式时不会突然找不到模型。
/// </para>
/// <para>
/// 默认使用的两个模型都来自 OpenCV Zoo（Apache-2.0，https://github.com/opencv/opencv_zoo）：
/// <list type="bullet">
/// <item>YuNet 人脸检测：face_detection_yunet_2023mar.onnx，约 230 KB</item>
/// <item>SFace 人脸识别：face_recognition_sface_2021dec.onnx，约 37 MB</item>
/// </list>
/// 两者都是纯 CPU 推理的小模型，本功能在 8 GB 机器上余量充足。
/// 另外可选 InsightFace 的 MobileFaceNet（w600k_mbf.onnx，约 13 MB），精度更高，
/// 但<b>不随包发布</b>（非商业研究许可），只提供下载与手动导入入口。
/// </para>
/// </remarks>
public sealed class FaceModelProvider
{
    /// <summary>YuNet 检测模型文件名。</summary>
    public const string DetectorFileName = "face_detection_yunet_2023mar.onnx";

    /// <summary>识别模型的备选文件名，用户从别处拷来的文件叫什么都有可能。</summary>
    private static readonly string[] ArcFaceAliases =
        { "w600k_mbf.onnx", "arcface_w600k_mbf.onnx", "arcface.onnx", "mobilefacenet.onnx" };

    private static readonly string[] DetectorUrls =
    {
        // GitHub 公开仓库 + gh-proxy 加速（免流量费），官网又拍云为备用，最后 HuggingFace 系
        "https://gh-proxy.com/https://raw.githubusercontent.com/CaizheZhou/ClassPixelPro-Models/main/face_detection_yunet_2023mar.onnx",
        "https://gh-proxy.org/https://raw.githubusercontent.com/CaizheZhou/ClassPixelPro-Models/main/face_detection_yunet_2023mar.onnx",
        "https://class.imzcz.cn/api/public/media/models/face_detection_yunet_2023mar.onnx",
        "https://hf-mirror.com/opencv/face_detection_yunet/resolve/main/face_detection_yunet_2023mar.onnx",
        "https://huggingface.co/opencv/face_detection_yunet/resolve/main/face_detection_yunet_2023mar.onnx",
    };

    /// <summary>模型的期望最小体积，用来判断下载结果是否是真实模型（而不是 Git LFS 指针文件）。</summary>
    private const long DetectorMinSize = 100 * 1024;

    private readonly FaceAttendanceSettings _settings;
    private readonly ILogger<FaceModelProvider> _logger;

    public FaceModelProvider(FaceAttendanceSettings settings, ILogger<FaceModelProvider> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    /// <summary>模型缓存目录：用户导入的模型放这里。</summary>
    public string ModelDirectory => FaceAttendancePaths.ModelsFolder;

    /// <summary>当前设置下应该使用的识别模型文件名。</summary>
    public string CurrentRecognizerFileName => ResolveRequestedModel()?.FileName ?? FaceModelCatalog.SFaceId;

    /// <summary>
    /// 设置里「想要」的那个模型。选「自动」时返回 null（表示交给 <see cref="PickAutoModel"/> 决定）。
    /// </summary>
    public FaceRecognizerModel? ResolveRequestedModel()
    {
        var id = _settings.RecognizerModelId;
        if (string.IsNullOrWhiteSpace(id) || id == FaceModelCatalog.AutoId)
        {
            return null;
        }

        return FaceModelCatalog.Find(id);
    }

    /// <summary>
    /// 自动模式：挑一款**磁盘上已经存在**的模型；都不在就挑优先级最高的一款（准备下载）。
    /// </summary>
    public FaceRecognizerModel PickAutoModel()
    {
        // 先看哪些已经在磁盘上（随包带过、以前下载过、或用户导入过）。
        var available = FaceModelCatalog.All
            .Where(m => FindExisting("", new[] { m.FileName }) != null)
            .ToList();

        if (available.Count > 0)
        {
            // 已存在的里面挑特征维度高的（一般更准）。
            return available.OrderByDescending(m => m.FeatureLength).First();
        }

        // 都没有就挑优先级最高的，后面会按需下载。
        return FaceModelCatalog.All.OrderByDescending(m => m.AutoPriority).First();
    }

    /// <summary>
    /// 按「设置 → 自动挑选 → 回退」的顺序定出实际要用的识别模型路径。
    /// 返回（路径, 是否发生了回退, 实际模型）。
    /// </summary>
    public (string? Path, bool UsedFallback, FaceRecognizerModel? Model) ResolveRecognizerPath()
    {
        var requested = ResolveRequestedModel();

        // 自动模式：挑一个磁盘上有的。
        if (requested == null)
        {
            var auto = PickAutoModel();
            var autoPath = FindExisting(_settings.RecognizerModelPath, new[] { auto.FileName });
            if (autoPath != null)
            {
                return (autoPath, false, auto);
            }

            // 自动挑的那款不在，看看别的模型有没有在的。
            foreach (var candidate in FaceModelCatalog.All.OrderByDescending(m => m.FeatureLength))
            {
                var path = FindExisting("", new[] { candidate.FileName });
                if (path != null)
                {
                    return (path, true, candidate);
                }
            }

            return (null, false, auto);
        }

        // 手动指定：先按设置里的路径/文件名找。
        var aliases = requested.Id == FaceModelCatalog.ArcFaceId ? ArcFaceAliases : new[] { requested.FileName };
        var found = FindExisting(_settings.RecognizerModelPath, aliases);
        if (found != null)
        {
            return (found, false, requested);
        }

        // 用户指定的模型不在 → 退回自带 SFace，保证功能可用。
        var sface = FaceModelCatalog.Find(FaceModelCatalog.SFaceId);
        if (sface != null)
        {
            var fallback = FindExisting("", new[] { sface.FileName });
            if (fallback != null)
            {
                return (fallback, true, sface);
            }
        }

        return (null, false, requested);
    }

    /// <summary>
    /// 找检测模型；找不到返回 null。
    /// </summary>
    public string? FindDetector() => FindExisting(_settings.DetectorModelPath, new[] { DetectorFileName });

    /// <summary>
    /// 确保两个模型文件都存在。返回（检测模型路径, 识别模型路径, 错误信息）。
    /// </summary>
    public async Task<(string Detector, string Recognizer, string Error)> EnsureAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default,
        IProgress<double>? percent = null)
    {
        var detector = FindDetector();
        var (recognizer, usedFallback, resolvedModel) = ResolveRecognizerPath();

        if (usedFallback && resolvedModel != null)
        {
            _logger.LogWarning("未找到指定的识别模型，临时回退到 {Model}", resolvedModel.DisplayName);
            progress?.Report($"没有找到指定的识别模型，本次先用{resolvedModel.DisplayName}。");
        }

        if (detector == null && _settings.AllowModelDownload)
        {
            progress?.Report("正在下载人脸检测模型（YuNet，约 230 KB）…");
            // 检测模型只有 230KB，占总量前 5%；识别模型占其余 95%。
            detector = await DownloadAsync(DetectorFileName, DetectorUrls, DetectorMinSize, progress, cancellationToken,
                new MappedProgress(percent, 0, 5))
                .ConfigureAwait(false);
        }

        if (recognizer == null && _settings.AllowModelDownload)
        {
            var model = resolvedModel ?? PickAutoModel();
            progress?.Report($"正在下载识别模型 {model.DisplayName}（约 {model.ApproxSizeMb:F0} MB，仅首次需要）…");
            recognizer = await DownloadAsync(model.FileName, model.Urls, model.MinSize, progress, cancellationToken,
                new MappedProgress(percent, 5, 100))
                .ConfigureAwait(false);
        }

        if (detector != null && recognizer != null)
        {
            return (detector, recognizer, "");
        }

        // 失败时把「找过哪些地方」全部列出来，否则用户完全不知道该把模型放哪儿。
        var searched = string.Join("\n", BuildSearchPaths(DetectorFileName)
            .Concat(BuildSearchPaths(CurrentRecognizerFileName))
            .Distinct());
        var missingList = string.Join("、", new[]
        {
            detector == null ? DetectorFileName : null,
            recognizer == null ? CurrentRecognizerFileName : null,
        }.Where(x => x != null));

        var message =
            $"缺少模型文件：{missingList}\n\n" +
            $"程序已经把这些位置都找过了：\n{searched}\n\n" +
            $"最省事的解决办法：\n" +
            $"1. 保持网络畅通，模型会自动从官网下载（首次使用需要，检测模型约 0.2MB、识别模型约 37MB）；\n" +
            $"2. 学校网络限制了官网下载时，点「导入模型文件…」按钮，手动选中 .onnx 文件，程序会自动复制到：\n   {ModelDirectory}";

        _logger.LogError("人脸模型准备失败：{Message}", message);
        return ("", "", message);
    }

    /// <summary>
    /// 把清单里所有**还没下载**的模型都下下来，供离线使用。
    /// </summary>
    public async Task<(int Downloaded, int AlreadyHad, List<string> Failures)> DownloadAllAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var downloaded = 0;
        var alreadyHad = 0;
        var failures = new List<string>();

        foreach (var model in FaceModelCatalog.All.OrderBy(m => m.ApproxSizeMb))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (FindExisting("", new[] { model.FileName }) != null)
            {
                alreadyHad++;
                continue;
            }

            progress?.Report($"正在下载 {model.DisplayName}（约 {model.ApproxSizeMb:F0} MB）…");
            var path = await DownloadAsync(model.FileName, model.Urls, model.MinSize, progress, cancellationToken)
                .ConfigureAwait(false);

            if (path != null)
            {
                downloaded++;
            }
            else
            {
                failures.Add(model.DisplayName);
            }
        }

        return (downloaded, alreadyHad, failures);
    }

    /// <summary>
    /// 在候选目录里按名称查找一个可用的模型文件。
    /// </summary>
    public string? FindExisting(string configuredPath, IEnumerable<string> fileNames)
    {
        var names = fileNames.ToList();

        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            try
            {
                if (File.Exists(configuredPath) && new FileInfo(configuredPath).Length > 1024)
                {
                    return configuredPath;
                }
            }
            catch
            {
                // 忽略非法路径
            }
        }

        foreach (var candidate in names.SelectMany(BuildSearchPaths))
        {
            try
            {
                if (File.Exists(candidate) && new FileInfo(candidate).Length > 1024)
                {
                    return candidate;
                }
            }
            catch
            {
                // 忽略非法路径
            }
        }

        return null;
    }

    /// <summary>
    /// 生成某个文件名的全部候选路径，按优先级从高到低。
    /// </summary>
    public static IEnumerable<string> BuildSearchPaths(string fileName)
    {
        // 1) 应用目录下的 Assets\Models —— 随包发布的模型就在这里
        yield return Path.Combine(AppContext.BaseDirectory, "Assets", "Models", fileName);
        yield return Path.Combine(AppContext.BaseDirectory, "Assets", fileName);
        yield return Path.Combine(AppContext.BaseDirectory, "models", fileName);

        // 2) 配置目录 —— 用户导入/下载的模型放这里
        yield return Path.Combine(FaceAttendancePaths.ModelsFolder, fileName);
        yield return Path.Combine(FaceAttendancePaths.RootFolder, fileName);
    }

    /// <summary>
    /// 把用户选中的模型文件复制到模型目录，并返回复制后的路径。
    /// </summary>
    /// <param name="sourcePath">用户选择的 .onnx 文件（U 盘、下载目录里的都行）。</param>
    /// <param name="isDetector">true 表示这是检测模型，false 表示识别模型。</param>
    public async Task<(bool Ok, string Message)> ImportAsync(string sourcePath, bool isDetector)
    {
        try
        {
            if (!File.Exists(sourcePath))
            {
                return (false, "找不到这个文件：" + sourcePath);
            }

            var length = new FileInfo(sourcePath).Length;
            if (length < 1024)
            {
                return (false, "这个文件太小了（只有 " + length + " 字节），不像是真正的模型文件。");
            }

            FaceAttendancePaths.EnsureDirectory(ModelDirectory);

            // 识别模型：先按文件名认出是哪一款，认不出就按体积猜（大的当 SFace，小的当 MobileFaceNet）。
            var detected = isDetector ? null : FaceModelCatalog.MatchByPath(sourcePath);
            if (!isDetector && detected == null)
            {
                detected = length > 20 * 1024 * 1024
                    ? FaceModelCatalog.Find(FaceModelCatalog.SFaceId)
                    : FaceModelCatalog.Find(FaceModelCatalog.ArcFaceId);
            }

            var targetName = isDetector ? DetectorFileName : (detected?.FileName ?? FaceModelCatalog.SFaceId);
            var target = Path.Combine(ModelDirectory, targetName);

            // 源文件和目标文件是同一个就不必复制
            if (!string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
            {
                await using var src = File.OpenRead(sourcePath);
                await using var dst = File.Create(target);
                await src.CopyToAsync(dst).ConfigureAwait(false);
            }

            if (isDetector)
            {
                _settings.DetectorModelPath = target;
            }
            else
            {
                _settings.RecognizerModelPath = target;

                // 顺手把「手动选择」指到这款模型上，用户导入完就能直接用。
                if (detected != null)
                {
                    _settings.RecognizerModelId = detected.Id;
                }
            }

            _logger.LogInformation("已导入模型 {Source} -> {Target}", sourcePath, target);

            var note = isDetector ? "" : $"（识别为：{detected?.DisplayName ?? "未知型号"}）";
            return (true, $"已导入：{Path.GetFileName(sourcePath)} → {target}{note}");
        }
        catch (Exception e)
        {
            _logger.LogError(e, "导入模型失败");
            return (false, "导入失败：" + e.Message);
        }
    }

    /// <summary>把单文件下载的 0~100 进度线性映射到整体进度区间（如 5~100）。</summary>
    private sealed class MappedProgress(IProgress<double>? sink, double lo, double hi) : IProgress<double>
    {
        public void Report(double value) => sink?.Report(lo + (hi - lo) * Math.Clamp(value, 0, 100) / 100d);
    }

    private async Task<string?> DownloadAsync(
        string fileName,
        string[] urls,
        long minSize,
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        IProgress<double>? percent = null)
    {
        try
        {
            FaceAttendancePaths.EnsureDirectory(ModelDirectory);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "无法创建模型目录 {Dir}", ModelDirectory);
            return null;
        }

        var target = Path.Combine(ModelDirectory, fileName);
        var temp = target + ".part";

        using var handler = new HttpClientHandler { AllowAutoRedirect = true };
        // 超时给短一点：学校网络里这些域名通常直接连不上，没必要让用户干等十分钟。
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "ClassPixelPro/1.0");

        foreach (var url in urls)
        {
            try
            {
                progress?.Report($"正在下载 {fileName} …");
                _logger.LogInformation("尝试从 {Url} 下载模型", url);
                using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("下载 {Url} 失败：{Code}", url, response.StatusCode);
                    continue;
                }

                var total = response.Content.Headers.ContentLength;
                var lastPct = -1;
                await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
                await using (var destination = File.Create(temp))
                {
                    // 手动分块拷贝并按字节数报进度；总长拿不到时就不报（进度条停在前一格）。
                    var buffer = new byte[81920];
                    long written = 0;
                    int read;
                    while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        written += read;
                        if (total is > 0 && percent != null)
                        {
                            var pct = (int)(100d * written / total.Value);
                            if (pct != lastPct) { lastPct = pct; percent.Report(pct); }
                        }
                    }
                }

                var length = new FileInfo(temp).Length;
                if (length < minSize)
                {
                    _logger.LogWarning("从 {Url} 下载到的文件只有 {Size} 字节，判定为无效（可能是 Git LFS 指针）", url, length);
                    SafeDelete(temp);
                    continue;
                }

                if (File.Exists(target))
                {
                    SafeDelete(target);
                }

                File.Move(temp, target);
                _logger.LogInformation("模型已就绪：{Path}（{Size} 字节）", target, length);
                return target;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "从 {Url} 下载模型时出错", url);
            }
        }

        SafeDelete(temp);
        return null;
    }

    private static void SafeDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 忽略
        }
    }
}
