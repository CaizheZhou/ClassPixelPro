using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClassPixelPro.Core.Abstractions.Services.Management;
using ClassPixelPro.Services.RemoteMonitor;
using Microsoft.Extensions.Logging;
using OpenCvSharp;

namespace ClassPixelPro.Services.FaceAttendance;

/// <summary>
/// 一次导入里单张照片的处理结果。
/// </summary>
public sealed class ImportItemResult
{
    /// <summary>文件名。</summary>
    public string FileName { get; init; } = "";

    /// <summary>归属的同学；没有则为 null。</summary>
    public Student? Student { get; init; }

    /// <summary>实际新增的样本条数。</summary>
    public int Added { get; init; }

    /// <summary>是否处理成功。</summary>
    public bool Ok { get; init; }

    /// <summary>说明。</summary>
    public string Message { get; init; } = "";
}

/// <summary>
/// 一次导入的汇总。
/// </summary>
public sealed class ImportSummary
{
    /// <summary>逐张照片的结果。</summary>
    public List<ImportItemResult> Items { get; } = new();

    /// <summary>因为前置条件不满足而整批失败时的原因。</summary>
    public string FatalError { get; set; } = "";

    /// <summary>是否被用户中止。</summary>
    public bool Cancelled { get; set; }

    /// <summary>成功导入的照片数。</summary>
    public int OkCount => Items.Count(x => x.Ok);

    /// <summary>新增样本总条数。</summary>
    public int AddedCount => Items.Sum(x => x.Added);

    /// <summary>没导入成功的照片数。</summary>
    public int SkippedCount => Items.Count(x => !x.Ok);
}

/// <summary>
/// 智能导入时给用户看的一条候选匹配。
/// </summary>
public sealed class SmartCandidate
{
    /// <summary>候选同学。</summary>
    public Student Student { get; init; } = null!;

    /// <summary>相似度。</summary>
    public double Score { get; init; }
}

/// <summary>
/// 智能导入时给用户看的一张候选脸（照片里检测到的某一张脸）。
/// </summary>
public sealed class SmartFaceOption
{
    /// <summary>这张脸在照片里的序号（从 1 开始）。</summary>
    public int Index { get; init; }

    /// <summary>这张脸的缩略图（JPEG）。</summary>
    public byte[]? Thumbnail { get; init; }

    /// <summary>人脸框宽度（原图像素）。</summary>
    public int Width { get; init; }

    /// <summary>人脸框高度（原图像素）。</summary>
    public int Height { get; init; }

    /// <summary>检测置信度。</summary>
    public float Score { get; init; }

    /// <summary>这张脸最像的同学（可能为 null）。</summary>
    public Student? Best { get; init; }

    /// <summary>与最像的同学的相似度。</summary>
    public double BestScore { get; init; }

    /// <summary>界面上显示的一行文字。</summary>
    public string DisplayText => Best == null
        ? $"脸 {Index}　{Width}×{Height}px　没认出像谁"
        : $"脸 {Index}　{Width}×{Height}px　最像「{Best.Name}」({BestScore:P0})";
}

/// <summary>
/// 智能导入时抛给界面的提问：「这张照片是不是 XXX？」
/// </summary>
public sealed class SmartPrompt
{
    /// <summary>文件名。</summary>
    public string FileName { get; init; } = "";

    /// <summary>第几张 / 共几张。</summary>
    public int Index { get; init; }

    /// <summary>总数。</summary>
    public int Total { get; init; }

    /// <summary>人脸缩略图（JPEG），可能为 null。</summary>
    public byte[]? Thumbnail { get; init; }

    /// <summary>这张照片里检测到几张脸。</summary>
    public int FaceCount { get; init; }

    /// <summary>照片里每一张脸的可选项，按面积从大到小排。</summary>
    public List<SmartFaceOption> Faces { get; init; } = new();

    /// <summary>整张照片带编号方框的预览图。</summary>
    public byte[]? AnnotatedPreview { get; init; }

    /// <summary>按相似度排序的候选（只包含超过阈值的）。</summary>
    public List<SmartCandidate> Matches { get; init; } = new();

    /// <summary>按脸识别给出的最佳建议。</summary>
    public Student? Best { get; init; }

    /// <summary>最佳相似度。</summary>
    public double BestScore { get; init; }

    /// <summary>文件名本身能对上的同学（如果有）。</summary>
    public Student? FileNameMatch { get; init; }

    /// <summary>文件名是否同时能对上多位同学。</summary>
    public bool FileNameAmbiguous { get; init; }
}

/// <summary>
/// 用户在智能导入提问界面上做出的选择。
/// </summary>
public sealed class SmartDecision
{
    /// <summary>放弃整批导入。</summary>
    public bool Cancel { get; set; }

    /// <summary>跳过这一张。</summary>
    public bool Skip { get; set; }

    /// <summary>选中的脸序号（1-based）；0 表示用默认那张。</summary>
    public int FaceIndex { get; set; }

    /// <summary>指定的归属同学；为 null 时看 <see cref="NewName"/>。</summary>
    public Student? Target { get; set; }

    /// <summary>新建同学的名字。</summary>
    public string NewName { get; set; } = "";
}

/// <summary>
/// 人脸录入服务：摄像头连拍录入、照片文件导入、智能导入。
/// <para>
/// 远程录入（班主任端上传照片）也走这里：下载完成后就是普通的「照片文件导入」，
/// 与本地批量导入共用同一条提取链路，不另写一套。
/// </para>
/// </summary>
public sealed class EnrollmentService : IDisposable
{
    private readonly FaceAttendanceSettings _settings;
    private readonly RosterService _roster;
    private readonly FaceAttendanceEngine _engine;
    private readonly CameraCaptureService _camera;
    private readonly IManagementService _management;
    private readonly ILogger<EnrollmentService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private bool _disposed;

    public EnrollmentService(
        FaceAttendanceSettings settings,
        RosterService roster,
        FaceAttendanceEngine engine,
        CameraCaptureService camera,
        IManagementService management,
        ILogger<EnrollmentService> logger)
    {
        _settings = settings;
        _roster = roster;
        _engine = engine;
        _camera = camera;
        _management = management;
        _logger = logger;
    }

    /// <summary>
    /// 用摄像头为某位同学录入人脸样本。可反复调用，样本会不断累积。
    /// </summary>
    /// <param name="student">目标同学。</param>
    /// <param name="shotCount">连拍张数。</param>
    /// <param name="onPreview">每抓到一张照片时的回调，参数为该帧的 JPEG 字节。</param>
    /// <param name="ct">取消令牌。</param>
    public async Task<(int Added, string Message)> EnrollFromCameraAsync(
        Student student,
        int shotCount,
        Action<byte[]>? onPreview = null,
        CancellationToken ct = default)
    {
        if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false))
        {
            return (0, "已有一次摄像头任务在进行中，请稍后再试。");
        }

        try
        {
            if (!await EnsureModelsAsync(ct).ConfigureAwait(false))
            {
                return (0, _engine.LastError);
            }

            if (!_camera.IsSupported || !_camera.TryAcquireCamera(CameraUser.Attendance))
            {
                return (0, "摄像头正被远程监控或录制计划占用（或本机没有可用摄像头），请稍后再试。");
            }

            try
            {
                WarmUp(Math.Max(0, _settings.WarmUpSeconds), ct);

                var count = Math.Clamp(shotCount, 1, 20);
                var interval = Math.Clamp(_settings.CaptureIntervalMs, 100, 3000);
                var added = 0;
                var noFace = 0;

                for (var i = 0; i < count; i++)
                {
                    ct.ThrowIfCancellationRequested();

                    using var frame = _camera.GrabFrame();
                    if (frame == null) continue;

                    if (onPreview != null)
                    {
                        var preview = frame.ToBytes(".jpg", new[] { (int)ImwriteFlags.JpegQuality, 80 });
                        if (preview.Length > 0) onPreview(preview);
                    }

                    var faces = _engine.Analyze(frame, withFeature: true);
                    var target = faces.OrderByDescending(x => x.Area).FirstOrDefault();
                    if (target?.Feature == null)
                    {
                        noFace++;
                    }
                    else
                    {
                        var crop = SaveFaceCrop(frame, target, student);
                        if (_roster.AddFaceSample(student, target.Feature, "camera", crop))
                        {
                            added++;
                        }
                    }

                    if (i < count - 1 && interval > 0)
                    {
                        await Task.Delay(interval, ct).ConfigureAwait(false);
                    }
                }

                var message = noFace == 0
                    ? $"成功录入 {added} 条样本。"
                    : $"成功录入 {added} 条样本，有 {noFace} 张照片没找到人脸（请正对摄像头、保证光线充足）。";
                return (added, message);
            }
            finally
            {
                _camera.ReleaseCamera(CameraUser.Attendance);
            }
        }
        catch (OperationCanceledException)
        {
            return (0, "已取消。");
        }
        catch (Exception e)
        {
            _logger.LogError(e, "人脸录入时出错");
            return (0, $"录入失败：{e.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 从一张已有的图片文件为某位同学录入人脸样本。适合用学生证件照录入。
    /// </summary>
    public async Task<(int Added, string Message)> EnrollFromImageFileAsync(
        Student student, string path, CancellationToken ct = default)
    {
        try
        {
            if (!await EnsureModelsAsync(ct).ConfigureAwait(false))
            {
                return (0, _engine.LastError);
            }

            using var image = await ReadImageFileAsync(path).ConfigureAwait(false);
            if (image == null)
            {
                return (0, "读取不到图片，或图片格式不支持。");
            }

            var faces = await Task.Run(() => _engine.Analyze(image, withFeature: true), ct).ConfigureAwait(false);
            if (faces.Count == 0)
            {
                return (0, "这张图片里没有检测到人脸（人脸太小或太模糊时会被忽略）。");
            }

            var target = faces.OrderByDescending(x => x.Area).First();
            if (target.Feature == null)
            {
                return (0, "没能从选中的那张脸提取出特征，请换一张更清晰的照片。");
            }

            var crop = SaveFaceCrop(image, target, student);
            var added = _roster.AddFaceSample(student, target.Feature, "file", crop) ? 1 : 0;
            return (added, $"从图片中录入 {added} 条样本。");
        }
        catch (Exception e)
        {
            _logger.LogError(e, "从图片录入人脸时出错");
            return (0, $"录入失败：{e.Message}");
        }
    }

    /// <summary>
    /// 批量导入照片。按文件名自动匹配花名册里的同学，识别不出来的留给界面处理。
    /// </summary>
    /// <param name="files">照片文件路径列表。</param>
    /// <param name="progress">进度回调：(已完成数, 总数, 当前文件名)。</param>
    /// <param name="ct">取消令牌。</param>
    public async Task<ImportSummary> ImportFromFilesAsync(
        IReadOnlyList<string> files,
        IProgress<(int Done, int Total, string Current)>? progress = null,
        CancellationToken ct = default)
    {
        var summary = new ImportSummary();
        if (!await EnsureModelsAsync(ct).ConfigureAwait(false))
        {
            summary.FatalError = _engine.LastError;
            return summary;
        }

        var done = 0;
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report((done, files.Count, Path.GetFileName(file)));
            done++;

            try
            {
                using var image = await ReadImageFileAsync(file).ConfigureAwait(false);
                if (image == null)
                {
                    summary.Items.Add(new ImportItemResult
                    {
                        FileName = Path.GetFileName(file), Ok = false, Message = "读取不到图片"
                    });
                    continue;
                }

                var faces = await Task.Run(() => _engine.Analyze(image, withFeature: true), ct)
                    .ConfigureAwait(false);
                if (faces.Count == 0)
                {
                    summary.Items.Add(new ImportItemResult
                    {
                        FileName = Path.GetFileName(file), Ok = false, Message = "没有检测到人脸"
                    });
                    continue;
                }

                var target = faces.OrderByDescending(x => x.Area).First();
                if (target.Feature == null)
                {
                    summary.Items.Add(new ImportItemResult
                    {
                        FileName = Path.GetFileName(file), Ok = false, Message = "特征提取失败"
                    });
                    continue;
                }

                var match = _roster.MatchByFileName(file);
                if (match.Student == null)
                {
                    summary.Items.Add(new ImportItemResult
                    {
                        FileName = Path.GetFileName(file), Ok = false, Message = match.Reason
                    });
                    continue;
                }

                var crop = SaveFaceCrop(image, target, match.Student);
                var added = _roster.AddFaceSample(match.Student, target.Feature, "import", crop, save: false);
                summary.Items.Add(new ImportItemResult
                {
                    FileName = Path.GetFileName(file),
                    Student = match.Student,
                    Added = added ? 1 : 0,
                    Ok = added,
                    Message = added ? $"导入给「{match.Student.Name}」" : "未能写入样本",
                });
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _logger.LogWarning(e, "导入照片失败：{File}", file);
                summary.Items.Add(new ImportItemResult
                {
                    FileName = Path.GetFileName(file), Ok = false, Message = e.Message
                });
            }
        }

        // 批量导入几百张时每加一条就整份序列化一次会非常慢，整批做完只保存一次。
        _roster.RaiseChanged(save: true);
        progress?.Report((files.Count, files.Count, ""));
        return summary;
    }

    /// <summary>
    /// 智能导入：对每张照片先检测所有脸，再按相似度猜是谁，拿不准的抛给界面问用户。
    /// </summary>
    /// <param name="files">照片文件路径列表。</param>
    /// <param name="ask">提问回调；返回用户的选择。</param>
    /// <param name="progress">进度回调。</param>
    /// <param name="ct">取消令牌。</param>
    /// <param name="files">待导入的照片路径。</param>
    /// <param name="ask">
    /// 提问回调；传 <c>null</c> 走自动模式：姓名唯一匹配且单脸的照片直接归档，
    /// 其余跳过并留在收件箱。自动模式跑在后台服务里，绝不能碰 UI。
    /// </param>
    /// <param name="progress">进度回调。</param>
    /// <param name="ct">取消令牌。</param>
    public async Task<ImportSummary> SmartImportAsync(
        IReadOnlyList<string> files,
        Func<SmartPrompt, Task<SmartDecision>>? ask,
        IProgress<(int Done, int Total, string Current)>? progress = null,
        CancellationToken ct = default)
    {
        var summary = new ImportSummary();
        if (!await EnsureModelsAsync(ct).ConfigureAwait(false))
        {
            summary.FatalError = _engine.LastError;
            return summary;
        }

        var gallery = _roster.BuildGallery();
        var threshold = Math.Clamp(_engine.EffectiveThreshold, 0.05, 0.99);
        var done = 0;

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report((done, files.Count, Path.GetFileName(file)));
            done++;

            try
            {
                using var image = await ReadImageFileAsync(file).ConfigureAwait(false);
                if (image == null)
                {
                    summary.Items.Add(new ImportItemResult
                    {
                        FileName = Path.GetFileName(file), Ok = false, Message = "读取不到图片"
                    });
                    continue;
                }

                var faces = await Task.Run(() => _engine.Analyze(image, withFeature: true), ct)
                    .ConfigureAwait(false);
                if (faces.Count == 0)
                {
                    summary.Items.Add(new ImportItemResult
                    {
                        FileName = Path.GetFileName(file), Ok = false, Message = "没有检测到人脸"
                    });
                    continue;
                }

                var ordered = faces.OrderByDescending(x => x.Area).ToList();
                var target = ordered[0];

                // 每张脸各自猜一次是谁，界面上并列展示让用户挑。
                var faceOptions = new List<SmartFaceOption>();
                for (var fi = 0; fi < ordered.Count; fi++)
                {
                    var face = ordered[fi];
                    Student? best = null;
                    var bestScore = 0d;
                    if (face.Feature != null)
                    {
                        var (st, sc) = FaceAttendanceEngine.FindBest(face.Feature, gallery, threshold);
                        best = st;
                        bestScore = sc;
                    }

                    faceOptions.Add(new SmartFaceOption
                    {
                        Index = fi + 1,
                        Thumbnail = EncodeFaceThumbnail(image, face),
                        Width = face.Width,
                        Height = face.Height,
                        Score = face.Score,
                        Best = best,
                        BestScore = bestScore,
                    });
                }

                var candidates = faceOptions
                    .Where(x => x.Best != null)
                    .Select(x => new SmartCandidate { Student = x.Best!, Score = x.BestScore })
                    .GroupBy(x => x.Student.Id)
                    .Select(g => g.OrderByDescending(x => x.Score).First())
                    .OrderByDescending(x => x.Score)
                    .ToList();

                var nameMatch = _roster.MatchByFileName(file);

                SmartDecision decision;
                if (ask != null)
                {
                    var prompt = new SmartPrompt
                    {
                        FileName = Path.GetFileName(file),
                        Index = done,
                        Total = files.Count,
                        Thumbnail = EncodeFaceThumbnail(image, target),
                        FaceCount = ordered.Count,
                        Faces = faceOptions,
                        AnnotatedPreview = ordered.Count > 1 ? EncodeAnnotatedPreview(image, ordered) : null,
                        Matches = candidates,
                        Best = candidates.FirstOrDefault()?.Student,
                        BestScore = candidates.FirstOrDefault()?.Score ?? 0d,
                        FileNameMatch = nameMatch.Student,
                        FileNameAmbiguous = nameMatch.IsAmbiguous,
                    };

                    decision = await ask(prompt).ConfigureAwait(false);
                }
                else
                {
                    // 自动模式（设备端后台拉取班主任上传的照片）：上传时班主任已经指明了
                    // 姓名，文件名能唯一对上花名册、且照片里只有一张脸时直接归档；
                    // 其余情况不猜，留在收件箱等人工在「智能导入」里确认。
                    var autoOwner = nameMatch.Student;
                    if (autoOwner == null || nameMatch.IsAmbiguous || ordered.Count != 1)
                    {
                        var reason = autoOwner == null
                            ? "文件名没能唯一对应花名册姓名"
                            : nameMatch.IsAmbiguous
                                ? "姓名对应了多位同学"
                                : "照片里检测到多张人脸";
                        summary.Items.Add(new ImportItemResult
                        {
                            FileName = Path.GetFileName(file), Ok = false,
                            Message = $"{reason}，已留在收件箱，请在「智能导入」里手动确认",
                        });
                        continue;
                    }
                    decision = new SmartDecision { Target = autoOwner };
                }

                if (decision.Cancel)
                {
                    summary.Cancelled = true;
                    break;
                }

                if (decision.Skip)
                {
                    summary.Items.Add(new ImportItemResult
                    {
                        FileName = Path.GetFileName(file), Ok = false, Message = "用户跳过"
                    });
                    continue;
                }

                if (decision.FaceIndex > 0 && decision.FaceIndex <= ordered.Count)
                {
                    target = ordered[decision.FaceIndex - 1];
                }

                var owner = decision.Target;
                if (owner == null && !string.IsNullOrWhiteSpace(decision.NewName))
                {
                    owner = _roster.AddStudent(decision.NewName);
                }

                if (owner == null)
                {
                    summary.Items.Add(new ImportItemResult
                    {
                        FileName = Path.GetFileName(file), Ok = false, Message = "没有指定归属同学"
                    });
                    continue;
                }

                if (target.Feature == null)
                {
                    summary.Items.Add(new ImportItemResult
                    {
                        FileName = Path.GetFileName(file), Student = owner, Ok = false, Message = "特征提取失败"
                    });
                    continue;
                }

                var crop = SaveFaceCrop(image, target, owner);
                var added = _roster.AddFaceSample(owner, target.Feature, "smart", crop, save: false);
                summary.Items.Add(new ImportItemResult
                {
                    FileName = Path.GetFileName(file),
                    Student = owner,
                    Added = added ? 1 : 0,
                    Ok = added,
                    Message = added ? $"导入给「{owner.Name}」" : "未能写入样本",
                });
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _logger.LogWarning(e, "智能导入照片失败：{File}", file);
                summary.Items.Add(new ImportItemResult
                {
                    FileName = Path.GetFileName(file), Ok = false, Message = e.Message
                });
            }
        }

        _roster.RaiseChanged(save: true);
        progress?.Report((files.Count, files.Count, ""));
        return summary;
    }

    /// <summary>
    /// 摄像头自检：抓一帧，返回 JPEG 预览与描述文本。
    /// </summary>
    public async Task<(byte[]? Jpeg, string Message)> TestCameraAsync(CancellationToken ct = default)
    {
        if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false))
        {
            return (null, "已有一次摄像头任务在进行中，请稍后再试。");
        }

        try
        {
            if (!_camera.IsSupported || !_camera.TryAcquireCamera(CameraUser.Attendance))
            {
                return (null, "摄像头正被远程监控或录制计划占用，请稍后再试。");
            }

            try
            {
                WarmUp(Math.Min(Math.Max(0, _settings.WarmUpSeconds), 2), ct);
                using var frame = _camera.GrabFrame();
                if (frame == null)
                {
                    return (null, "摄像头已打开，但读不到画面。");
                }

                var jpeg = frame.ToBytes(".jpg", new[] { (int)ImwriteFlags.JpegQuality, 85 });
                return (jpeg, $"摄像头正常，分辨率 {frame.Width}×{frame.Height}。");
            }
            finally
            {
                _camera.ReleaseCamera(CameraUser.Attendance);
            }
        }
        catch (OperationCanceledException)
        {
            return (null, "已取消。");
        }
        catch (Exception e)
        {
            _logger.LogError(e, "摄像头自检失败");
            return (null, $"摄像头自检失败：{e.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---------------- 远程录入 ----------------

    /// <summary>
    /// 拉取班主任端上传的待录入照片，下载到本地缓存目录。
    /// </summary>
    /// <remarks>
    /// 只下载<b>照片</b>，不下载任何特征向量：班主任端传上来的就是普通图片，
    /// 特征提取在教室电脑上完成。这样服务端与传输链路上都不出现生物识别特征。
    /// <para>
    /// 传 <paramref name="consumedIds"/> 时，下载成功的条目会记进列表，并在全部落盘后
    /// 回传服务端标记为已消费——这些照片不该在服务端无限期留存。
    /// </para>
    /// </remarks>
    public async Task<(int Downloaded, string Message)> DownloadPendingAsync(
        IProgress<(int Done, int Total, string Current)>? progress = null,
        CancellationToken ct = default,
        List<string>? consumedIds = null,
        List<string>? downloadedFiles = null)
    {
        var baseUrl = _management.GetManagementBaseUrl();
        var orgId = _management.GetOrganizationId();
        if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(orgId))
        {
            return (0, "设备尚未加入集控，无法拉取远程录入照片。");
        }

        var cuid = _management.Persist.ClientUniqueId.ToString();
        try
        {
            var listUrl = $"{baseUrl}/api/mgmt/{orgId}/serverless/face-enrollments?cuid={cuid}";
            using var resp = await _http.GetAsync(listUrl, ct).ConfigureAwait(false);
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return (0, $"拉取远程录入列表失败：HTTP {(int)resp.StatusCode}");
            }

            using var doc = JsonDocument.Parse(text);
            if (!doc.RootElement.TryGetProperty("items", out var arr) || arr.ValueKind != JsonValueKind.Array)
            {
                return (0, "服务端没有返回待录入照片。");
            }

            var items = arr.EnumerateArray().ToList();
            if (items.Count == 0)
            {
                return (0, "没有待录入的照片。");
            }

            var targetDir = FaceAttendancePaths.EnsureDirectory(
                Path.Combine(FaceAttendancePaths.RootFolder, "inbox"));
            var downloaded = 0;
            var done = 0;

            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                var studentName = item.TryGetProperty("studentName", out var snEl) ? snEl.GetString() ?? "" : "";
                progress?.Report((done, items.Count,
                    item.TryGetProperty("fileName", out var fn) ? fn.GetString() ?? "" : ""));
                done++;

                if (!item.TryGetProperty("url", out var urlEl)) continue;
                var url = urlEl.GetString();
                var fileName = item.TryGetProperty("fileName", out var f) ? f.GetString() : null;
                if (string.IsNullOrEmpty(url)) continue;

                // 班主任填的姓名放最前面：MatchByFileName 靠「文件名里出现姓名」认人，
                // 这样下载回来的照片在智能导入里能被直接认出来，不用再手工挑一次。
                var safeName = SanitizeFileName(string.Join("_",
                    new[] { studentName, fileName }.Where(x => !string.IsNullOrWhiteSpace(x))));
                var target = Path.Combine(targetDir, $"{Guid.NewGuid().ToString("N")[..8]}_{safeName}");

                using var fileResp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);
                if (!fileResp.IsSuccessStatusCode) continue;

                await using (var source = await fileResp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
                await using (var dest = File.Create(target))
                {
                    await source.CopyToAsync(dest, ct).ConfigureAwait(false);
                }

                downloadedFiles?.Add(target);
                downloaded++;
                if (item.TryGetProperty("id", out var idEl))
                {
                    var id = idEl.GetString();
                    if (!string.IsNullOrEmpty(id)) consumedIds?.Add(id);
                }
            }

            progress?.Report((items.Count, items.Count, ""));

            // 落盘成功就通知服务端消费掉：这些是学生的人脸照片，不该在服务端无限期留存。
            if (consumedIds is { Count: > 0 })
            {
                await ConsumePendingAsync(consumedIds, cuid, ct).ConfigureAwait(false);
            }

            return (downloaded, $"已下载 {downloaded} 张待录入照片，可以在「智能导入」里选择它们。");
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "拉取远程录入照片失败");
            return (0, $"拉取远程录入照片失败：{e.Message}");
        }
    }

    /// <summary>
    /// 告诉服务端这些待录入项已经处理完毕。失败只记日志：下一次轮询会再拉到一遍，
    /// 重复导入由花名册按姓名去重，不会污染人脸库。
    /// </summary>
    private async Task ConsumePendingAsync(List<string> ids, string cuid, CancellationToken ct)
    {
        try
        {
            var baseUrl = _management.GetManagementBaseUrl();
            var orgId = _management.GetOrganizationId();
            if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(orgId)) return;

            var url = $"{baseUrl}/api/mgmt/{orgId}/serverless/face-enrollments/consume?cuid={cuid}";
            var payload = System.Text.Json.JsonSerializer.Serialize(new { ids });
            using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync(url, content, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("回传远程录入消费状态失败：HTTP {Code}", (int)resp.StatusCode);
            }
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "回传远程录入消费状态失败");
        }
    }

    /// <summary>
    /// 拉取待录入照片并直接完成导入：下载后按 <see cref="SmartImportAsync"/> 逐张问用户归属。
    /// </summary>
    public async Task<(int Downloaded, ImportSummary Summary, string Message)> DownloadAndImportAsync(
        Func<SmartPrompt, Task<SmartDecision>> ask,
        IProgress<(int Done, int Total, string Current)>? progress = null,
        CancellationToken ct = default)
    {
        // 传入列表：下载完成即回传消费状态，不必等用户把整批导入做完。
        var consumedIds = new List<string>();
        var (downloaded, message) = await DownloadPendingAsync(progress, ct, consumedIds).ConfigureAwait(false);
        if (downloaded <= 0)
        {
            return (0, new ImportSummary { FatalError = message }, message);
        }

        var inbox = Path.Combine(FaceAttendancePaths.RootFolder, "inbox");
        var files = Directory.GetFiles(inbox, "*.*")
            .Where(x => x.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                        || x.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
                        || x.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x)
            .ToList();

        if (files.Count == 0)
        {
            return (downloaded, new ImportSummary { FatalError = "下载完成的照片不在预期的目录里。" }, "下载完成的照片不见了。");
        }

        var summary = await SmartImportAsync(files, ask, progress, ct).ConfigureAwait(false);

        // 录入完成后清理收件箱：里面是学生的人脸照片，不该无限期留在教室电脑上。
        foreach (var file in files)
        {
            TryDelete(file);
        }

        return (downloaded, summary, $"已处理 {summary.OkCount} 张照片（新增 {summary.AddedCount} 条样本）。");
    }

    // ---------------- 内部工具 ----------------

    /// <summary>确保模型已载入；没有则尝试载入一次。</summary>
    private async Task<bool> EnsureModelsAsync(CancellationToken ct)
    {
        if (_engine.IsReady) return true;
        var (ok, _) = await _engine.LoadAsync(null, ct).ConfigureAwait(false);
        return ok;
    }

    /// <summary>摄像头预热：丢掉若干帧，让自动曝光/白平衡稳定下来。</summary>
    private void WarmUp(int seconds, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            _camera.GrabFrame()?.Dispose();
            Thread.Sleep(60);
        }
    }

    /// <summary>读取图片文件。读不动或格式不支持时返回 null。</summary>
    private static async Task<Mat?> ReadImageFileAsync(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            // 中文路径下 OpenCV 的窄字符实现读不了文件，改成先读字节再解码。
            var bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);

            var image = Cv2.ImDecode(bytes, ImreadModes.Color);
            return image.Empty() ? null : image;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>把选中的人脸裁成小图存档，返回相对 faces 目录的文件名。</summary>
    private string SaveFaceCrop(Mat image, DetectedFace face, Student student)
    {
        try
        {
            var dir = FaceAttendancePaths.EnsureDirectory(
                Path.Combine(FaceAttendancePaths.FacesFolder, student.Id));
            var name = $"{DateTime.Now:yyyyMMdd-HHmmss-fff}.jpg";
            var path = Path.Combine(dir, name);

            var pad = (int)(Math.Max(face.Width, face.Height) * 0.2);
            var x = face.X;
            var y = face.Y;
            var w = Math.Min(image.Width - x, face.Width + pad * 2);
            var h = Math.Min(image.Height - y, face.Height + pad * 2);
            if (w <= 0 || h <= 0) return "";

            using var crop = new Mat(image, new Rect(x, y, w, h));
            var bytes = crop.ToBytes(".jpg", new[] { (int)ImwriteFlags.JpegQuality, 90 });
            File.WriteAllBytes(path, bytes);
            return name;
        }
        catch (Exception e)
        {
            _logger.LogDebug(e, "保存人脸小图失败");
            return "";
        }
    }

    /// <summary>生成一张脸的缩略图（JPEG）。</summary>
    private static byte[]? EncodeFaceThumbnail(Mat image, DetectedFace face)
    {
        try
        {
            var pad = (int)(Math.Max(face.Width, face.Height) * 0.2);
            var x = face.X;
            var y = face.Y;
            var w = Math.Min(image.Width - x, face.Width + pad * 2);
            var h = Math.Min(image.Height - y, face.Height + pad * 2);
            if (w <= 0 || h <= 0) return null;

            using var crop = new Mat(image, new Rect(x, y, w, h));
            return crop.ToBytes(".jpg", new[] { (int)ImwriteFlags.JpegQuality, 80 });
        }
        catch
        {
            return null;
        }
    }

    /// <summary>生成带编号方框的整图预览，让用户能把「脸 2」对应到画面上具体哪个人。</summary>
    private static byte[]? EncodeAnnotatedPreview(Mat image, IReadOnlyList<DetectedFace> faces)
    {
        try
        {
            using var canvas = image.Clone();
            for (var i = 0; i < faces.Count; i++)
            {
                var face = faces[i];
                var rect = new Rect(face.X, face.Y, face.Width, face.Height);
                Cv2.Rectangle(canvas, rect, new Scalar(0, 200, 0), 2);
                Cv2.PutText(canvas, (i + 1).ToString(),
                    new Point(rect.X, Math.Max(12, rect.Y - 6)),
                    HersheyFonts.HersheySimplex, 0.7, new Scalar(0, 200, 0), 2);
            }

            return canvas.ToBytes(".jpg", new[] { (int)ImwriteFlags.JpegQuality, 85 });
        }
        catch
        {
            return null;
        }
    }

    /// <summary>把文件名里的路径分隔符等危险字符去掉，避免下载时写到目录外。</summary>
    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            sb.Append(invalid.Contains(ch) ? '_' : ch);
        }

        var result = sb.ToString().Trim();
        return result.Length == 0 ? "photo.jpg" : result;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 删不掉下次再删
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _http.Dispose();
        _gate.Dispose();
    }
}
