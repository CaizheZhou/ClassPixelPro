using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using ClassPixelPro.Core;
using ClassPixelPro.Core.Abstractions.Services.Management;
using ClassPixelPro.Services.RemoteMonitor;
using ClassPixelPro.Shared;
using Microsoft.Extensions.Logging;
using OpenCvSharp;

namespace ClassPixelPro.Services.FaceAttendance;

/// <summary>
/// 考勤核心服务：抓拍 → 检测 → 特征提取 → 与花名册比对 → 得出迟到名单。
/// <para>
/// 一次完整流程的耗时构成（默认设置，4 张照片）：打开摄像头 ≈ 1 s + 预热 1 s
/// + 抓拍 4×0.4 s ≈ 1.6 s + 推理 ≈ 1~4 s，合计约 5~8 秒。
/// </para>
/// <para>
/// 摄像头经 <see cref="CameraCaptureService.TryAcquireCamera"/> 申请，与远程监控、
/// 录制计划共用同一路设备。班主任正在看监控时考勤抢不到摄像头，本轮直接跳过
/// （考勤有补执行窗口，错过一轮影响小；打断班主任监控的体验代价更大）。
/// </para>
/// </summary>
public sealed class AttendanceService : IDisposable
{
    /// <summary>
    /// 相对阈值的最小人数保护：即使只抓到 1 张照片，也至少要命中 1 次。
    /// </summary>
    private const int MinHitsFloor = 1;

    /// <summary>一次考勤的硬超时下限，避免设置成 0 导致永远不中止。</summary>
    private const int MinTimeoutSeconds = 10;

    private readonly FaceAttendanceSettings _settings;
    private readonly RosterService _roster;
    private readonly FaceAttendanceEngine _engine;
    private readonly CameraCaptureService _camera;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly ILogger<AttendanceService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private AttendanceState _state = AttendanceState.Idle;
    private string _statusText = "尚未执行考勤";
    private string _stageText = "空闲";
    private double _progressPercent;
    private bool _progressVisible;
    private LateRecord? _lastResult;
    private bool _disposed;

    public AttendanceService(
        FaceAttendanceSettings settings,
        RosterService roster,
        FaceAttendanceEngine engine,
        CameraCaptureService camera,
        ILogger<AttendanceService> logger)
    {
        _settings = settings;
        _roster = roster;
        _engine = engine;
        _camera = camera;
        _logger = logger;
    }

    /// <summary>当前状态。</summary>
    public AttendanceState State
    {
        get => _state;
        private set
        {
            if (_state == value) return;
            _state = value;
            Notify(nameof(State));
        }
    }

    /// <summary>状态描述文本，可直接显示在主界面组件上。</summary>
    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (_statusText == value) return;
            _statusText = value;
            Notify(nameof(StatusText));
        }
    }

    /// <summary>当前正在做的事，例如「正在抓拍（2/4）…」。</summary>
    public string StageText
    {
        get => _stageText;
        private set
        {
            if (_stageText == value) return;
            _stageText = value;
            Notify(nameof(StageText));
        }
    }

    /// <summary>当前进度百分比（0~100）。</summary>
    public double ProgressPercent
    {
        get => _progressPercent;
        private set
        {
            if (Math.Abs(_progressPercent - value) < 1e-9) return;
            _progressPercent = value;
            Notify(nameof(ProgressPercent));
        }
    }

    /// <summary>进度条是否应该显示（只在识别进行中有意义）。</summary>
    public bool ProgressVisible
    {
        get => _progressVisible;
        private set
        {
            if (_progressVisible == value) return;
            _progressVisible = value;
            Notify(nameof(ProgressVisible));
        }
    }

    /// <summary>最近一次考勤结果。</summary>
    public LateRecord? LastResult
    {
        get => _lastResult;
        private set
        {
            _lastResult = value;
            Notify(nameof(LastResult));
            Notify(nameof(LastResultSummary));
        }
    }

    /// <summary>最近一次结果的文字摘要。</summary>
    public string LastResultSummary => LastResult?.Summary ?? "尚未执行考勤";

    /// <summary>状态或结果变化时触发。始终在 UI 线程上抛出。</summary>
    public event EventHandler? ResultChanged;

    /// <inheritdoc />
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// 所有属性变化通知都切回 UI 线程。
    /// <para>
    /// 识别流程跑在后台线程上。字段更新本身是同步的，调用方 <c>await</c> 之后能立刻读到最新值；
    /// 但 PropertyChanged 会被 Avalonia 的绑定消费，跨线程就会抛
    /// <c>InvalidOperationException: Call from invalid thread</c>，所以统一改到 UI 线程再抛。
    /// </para>
    /// </summary>
    private void Notify(string propertyName)
    {
        Dispatcher.UIThread.Post(() =>
        {
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));
            ResultChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    /// <summary>
    /// 更新进度：一次设置状态文本、阶段说明、进度百分比并通知界面。
    /// </summary>
    /// <param name="status">一句话状态。</param>
    /// <param name="stage">阶段说明。</param>
    /// <param name="percent">进度百分比 0~100；传负数表示不显示进度条（流程已结束）。</param>
    private void ReportProgress(string status, string stage, double percent)
    {
        StatusText = status;
        StageText = stage;
        ProgressVisible = percent >= 0;
        ProgressPercent = percent < 0 ? 0 : Math.Clamp(percent, 0, 100);
    }

    private int _ensureModelsRunning;

    /// <summary>
    /// 后台确保识别模型就绪：文件缺失且允许联网下载时，自动下载并载入。
    /// <para>
    /// 集控强制开启人脸考勤时，定时服务会在抓拍时间没到之前先调用本方法，
    /// 把几十 MB 的模型下载提前完成——否则第一次到点执行时才开始下载，
    /// 课堂上白等几分钟。幂等：模型已就绪、正在准备中或已有一次识别在跑
    /// （识别流程自己会载入模型）时直接返回。
    /// </para>
    /// </summary>
    public async Task<bool> EnsureModelsReadyAsync(CancellationToken external = default)
    {
        if (_engine.IsReady) return true;
        // 已有一次识别在跑：它的流程里会载入模型，不需要并发再来一份下载
        if (!await _gate.WaitAsync(0, external).ConfigureAwait(false))
        {
            return _engine.IsReady;
        }
        try
        {
            if (_engine.IsReady) return true;
            if (Interlocked.Exchange(ref _ensureModelsRunning, 1) == 1) return _engine.IsReady;
            try
            {
                if (!_settings.AllowModelDownload && !_engine.AreModelFilesPresent())
                {
                    _logger.LogWarning("人脸考勤需要识别模型但未允许联网下载，跳过后台准备");
                    return false;
                }
                ReportProgress("正在准备识别模型…", "准备识别模型…", 3);
                var (ok, message) = await _engine.LoadAsync(
                    new Progress<string>(s => ReportProgress("正在准备识别模型…", s, 5)),
                    new Progress<double>(p => ReportProgress("正在准备识别模型…", "下载/载入识别模型…", 5 + p * 0.9)),
                    external).ConfigureAwait(false);
                if (ok)
                {
                    ReportProgress("模型已就绪，等待抓拍时间", "空闲", -1);
                    _logger.LogInformation("人脸考勤模型已在后台准备就绪");
                }
                else
                {
                    ReportProgress("模型准备失败，到点执行时会重试", message, -1);
                    _logger.LogWarning("后台准备人脸模型失败：{Message}", message);
                }
                return ok;
            }
            finally
            {
                Interlocked.Exchange(ref _ensureModelsRunning, 0);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 执行一次完整的考勤。
    /// </summary>
    /// <param name="manual">是否由用户手动触发（仅用于记录，不影响流程）。</param>
    /// <param name="external">外部取消令牌。</param>
    public async Task<LateRecord> RunCheckAsync(bool manual, CancellationToken external = default)
    {
        if (!await _gate.WaitAsync(0).ConfigureAwait(false))
        {
            StatusText = "已有一次识别正在进行中，已跳过本次";
            return LastResult ?? new LateRecord { Success = false, Message = "已有一次识别正在进行中。" };
        }

        try
        {
            State = AttendanceState.Running;
            ReportProgress("正在抓拍并识别…", "准备开始…", 2);

            var timeout = Math.Max(MinTimeoutSeconds, _settings.MaxDurationSeconds);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(external);
            cts.CancelAfter(TimeSpan.FromSeconds(timeout));

            LateRecord record;
            try
            {
                record = await Task.Run(() => RunCoreAsync(manual, cts.Token), CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                record = new LateRecord
                {
                    CaptureTime = DateTime.Now,
                    Manual = manual,
                    Success = false,
                    Message = $"识别超过 {timeout} 秒仍未完成，已中止。可以适当减少抓拍张数。",
                };
            }
            catch (Exception e)
            {
                _logger.LogError(e, "人脸考勤执行时发生未处理异常");
                record = new LateRecord
                {
                    CaptureTime = DateTime.Now,
                    Manual = manual,
                    Success = false,
                    Message = $"识别出错：{e.Message}",
                };
            }

            LastResult = record;
            State = record.Success ? AttendanceState.Completed : AttendanceState.Failed;
            ReportProgress(record.Summary, record.Success ? "已完成" : "已中止", -1);

            SaveLastResult(record);
            CleanupSnapshots();
            _ = ReportResultAsync(record);

            if (record.Success)
            {
                _logger.LogInformation(
                    "人脸考勤结束：照片={Photos} 人脸={Faces} 应到={Expected} 识别={Recognized} 迟到={Late} 耗时={Elapsed:F2}s",
                    record.PhotoCount, record.FaceCount, record.ExpectedCount,
                    record.Recognized.Count, record.Late.Count, record.ElapsedSeconds);
            }
            else
            {
                _logger.LogWarning("人脸考勤失败：{Message}", record.Message);
            }

            return record;
        }
        finally
        {
            _gate.Release();
        }
    }

    private LateRecord RunCoreAsync(bool manual, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        var record = new LateRecord { CaptureTime = DateTime.Now, Manual = manual };

        // 1. 模型
        if (!_engine.IsReady)
        {
            ReportProgress("正在载入人脸模型…", "载入人脸模型…", 5);
            var (ok, message) = _engine.LoadAsync(null, null, ct).GetAwaiter().GetResult();
            if (!ok)
            {
                return Fail(record, message, stopwatch);
            }
        }

        // 2. 花名册
        ReportProgress("正在读取花名册…", "读取花名册…", 10);
        var gallery = _roster.BuildGallery();
        var activeStudents = _roster.GetActiveStudents();
        var enrollable = activeStudents.Where(x => x.Faces.Count > 0).ToList();
        record.ExpectedCount = enrollable.Count;

        if (gallery.Count == 0)
        {
            // 分清楚「根本没录」和「录了但维度对不上」——后者多半是中途换了识别模型。
            var sampleCount = activeStudents.Sum(x => x.Faces.Count);
            var msg = sampleCount == 0
                ? "花名册里还没有任何人脸样本。请先到「人脸考勤 · 花名册」录入，或在「批量导入」里导入照片。"
                : $"花名册里有 {sampleCount} 条样本，但都不是当前识别模型（{_engine.RecognizerPath}）的特征维度，无法比对。"
                  + "通常是因为中途换过识别模型（SFace ↔ ArcFace），请用当前模型重新录入或重新导入照片。";
            return Fail(record, msg, stopwatch);
        }

        // 3. 摄像头：向共享服务申请，拿不到说明监控/录制正占着，本轮跳过
        ReportProgress("正在申请摄像头…", "申请摄像头…", 15);
        if (!_camera.IsSupported || !_camera.TryAcquireCamera(CameraUser.Attendance))
        {
            return Fail(record, "摄像头正被远程监控或录制计划占用（或本机没有可用摄像头），本次考勤已跳过。"
                                + "考勤有补执行窗口，下一轮会再试；也可以等监控结束后再手动执行一次。", stopwatch);
        }

        try
        {
            ReportProgress("正在等摄像头自动曝光稳定…", "预热摄像头…", 20);
            WarmUp(Math.Max(0, _settings.WarmUpSeconds), ct);

            // 4. 连拍
            var shotCount = Math.Clamp(_settings.CaptureCount, 1, 20);
            var interval = Math.Clamp(_settings.CaptureIntervalMs, 0, 5000);
            var snapshotDir = _settings.SaveSnapshots
                ? FaceAttendancePaths.EnsureDirectory(FaceAttendancePaths.SnapshotsFolder,
                    record.CaptureTime.ToString("yyyyMMdd-HHmmss"))
                : "";

            var frames = new List<Mat>();
            for (var i = 0; i < shotCount; i++)
            {
                ct.ThrowIfCancellationRequested();
                ReportProgress($"正在抓拍（{i}/{shotCount}）…", $"正在抓拍 {i}/{shotCount}",
                    22 + 28.0 * i / Math.Max(1, shotCount));

                var frame = _camera.GrabFrame();
                if (frame == null) continue;

                frames.Add(frame);
                if (!string.IsNullOrEmpty(snapshotDir))
                {
                    SaveJpeg(frame, Path.Combine(snapshotDir, $"shot{i:00}.jpg"));
                }

                if (i < shotCount - 1 && interval > 0)
                {
                    Thread.Sleep(interval);
                }
            }

            record.PhotoCount = frames.Count;
            if (frames.Count == 0)
            {
                foreach (var f in frames) f.Dispose();
                return Fail(record, "没有从摄像头读到任何画面，请检查摄像头是否可用。", stopwatch);
            }

            // 5. 检测 + 特征提取 + 逐帧比对
            // 阈值跟着**当前实际载入的模型**走：SFace 是 0.363，ArcFace 系是 0.4~0.6 那一档，
            // 不能拿设置里某个写死的数字到处用。
            var threshold = Math.Clamp(_engine.EffectiveThreshold, 0.05, 0.99);
            var hits = new Dictionary<string, LateEntry>();
            var scoreBoard = new Dictionary<string, double>();

            try
            {
                for (var frameIndex = 0; frameIndex < frames.Count; frameIndex++)
                {
                    ct.ThrowIfCancellationRequested();
                    ReportProgress(
                        $"正在比对人脸（{frameIndex + 1}/{frames.Count}）…",
                        $"分析第 {frameIndex + 1}/{frames.Count} 张照片",
                        50 + 40.0 * frameIndex / Math.Max(1, frames.Count));

                    var faces = _engine.Analyze(frames[frameIndex], withFeature: true);
                    record.FaceCount += faces.Count;

                    // 同一张照片里，同一位同学只记一次命中
                    var matchedInThisFrame = new HashSet<string>();
                    foreach (var face in faces)
                    {
                        if (face.Feature == null) continue;

                        var (student, score) = FaceAttendanceEngine.FindBest(face.Feature, gallery, threshold);
                        if (student == null) continue;

                        if (!scoreBoard.TryGetValue(student.Id, out var best) || score > best)
                        {
                            scoreBoard[student.Id] = score;
                        }

                        if (!matchedInThisFrame.Add(student.Id)) continue;

                        if (!hits.TryGetValue(student.Id, out var entry))
                        {
                            entry = new LateEntry
                            {
                                Name = student.Name,
                                ClassName = student.ClassName,
                                Reason = "抓拍命中",
                            };
                            hits[student.Id] = entry;
                        }

                        entry.Hits++;
                    }
                }
            }
            finally
            {
                foreach (var f in frames) f.Dispose();
            }

            // 6. 汇总：命中次数达到 MinHits 才算「被识别到」
            ReportProgress("正在统计结果…", "统计迟到名单…", 95);
            var minHits = Math.Clamp(_settings.MinHits, MinHitsFloor, Math.Max(MinHitsFloor, record.PhotoCount));
            var recognizedIds = hits
                .Where(x => x.Value.Hits >= minHits)
                .OrderByDescending(x => x.Value.Hits)
                .Select(x => x.Key)
                .ToList();
            var recognizedSet = recognizedIds.ToHashSet();

            record.Recognized = recognizedIds.Select(id => new LateEntry
            {
                Name = hits[id].Name,
                ClassName = hits[id].ClassName,
                Reason = "抓拍命中",
                Score = scoreBoard.TryGetValue(id, out var s) ? s : 0d,
                Hits = hits[id].Hits,
            }).ToList();

            if (_settings.JudgeMode == LateJudgeMode.Absence)
            {
                // 教室抓拍：应在场却没被认出来的人 = 迟到
                record.Late = enrollable
                    .Where(x => !recognizedSet.Contains(x.Id))
                    .Select(x => new LateEntry
                    {
                        Name = x.Name,
                        ClassName = x.ClassName,
                        Reason = "早读抓拍未检测到",
                        Hits = 0,
                    }).ToList();

                var noSample = activeStudents.Where(x => x.Faces.Count == 0).Select(x => x.Name).ToList();
                if (noSample.Count > 0)
                {
                    record.Message = $"有 {noSample.Count} 位同学尚未录入人脸，未参与判定：{string.Join("、", noSample)}";
                }
            }
            else
            {
                // 门口抓拍：被认出来的人 = 迟到
                record.Late = record.Recognized.Select(x => new LateEntry
                {
                    Name = x.Name,
                    ClassName = x.ClassName,
                    Reason = "门口抓拍出现",
                    Score = x.Score,
                    Hits = x.Hits,
                }).ToList();
            }

            record.Success = true;
            record.ElapsedSeconds = stopwatch.Elapsed.TotalSeconds;
            return record;
        }
        finally
        {
            _camera.ReleaseCamera(CameraUser.Attendance);
        }
    }

    /// <summary>
    /// 摄像头预热：丢掉若干帧，让自动曝光/白平衡稳定下来。
    /// </summary>
    private void WarmUp(int seconds, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            _camera.GrabFrame()?.Dispose();
            Thread.Sleep(60);
        }
    }

    private static LateRecord Fail(LateRecord record, string message, Stopwatch stopwatch)
    {
        record.Success = false;
        record.Message = message;
        record.ElapsedSeconds = stopwatch.Elapsed.TotalSeconds;
        return record;
    }

    /// <summary>
    /// 把一次考勤结果上报集控后台，供班主任端与集控后台查看。
    /// <para>
    /// <b>只上报姓名、班级与统计数字。</b>特征向量、人脸小图、抓拍照片路径一律不离开本机，
    /// 这是隐私设计的硬边界：服务端与网络链路上都不出现生物识别信息。
    /// </para>
    /// <para>失败只记日志：上报不成功不影响本机功能，下一轮考勤会再报。</para>
    /// </summary>
    private async Task ReportResultAsync(LateRecord record)
    {
        try
        {
            var mgmt = IAppHost.TryGetService<IManagementService>();
            if (mgmt is not { IsManagementEnabled: true }) return;
            var baseUrl = mgmt.GetManagementBaseUrl();
            var orgId = mgmt.GetOrganizationId();
            if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(orgId)) return;

            var payload = new
            {
                date = record.CaptureTime.ToString("yyyy-MM-dd"),
                triggerTime = record.CaptureTime.ToString("HH:mm:ss"),
                manual = record.Manual,
                success = record.Success,
                message = record.Message,
                expectedCount = record.ExpectedCount,
                photoCount = record.PhotoCount,
                faceCount = record.FaceCount,
                recognizedCount = record.Recognized.Count,
                elapsedSeconds = record.ElapsedSeconds,
                late = record.Late.Select(x => new
                {
                    name = x.Name,
                    className = x.ClassName,
                    reason = x.Reason,
                    hits = x.Hits,
                    score = x.Score,
                }).ToList(),
            };

            var url = $"{baseUrl}/api/mgmt/{orgId}/serverless/face-result?cuid={mgmt.Persist.ClientUniqueId}";
            using var content = new StringContent(
                System.Text.Json.JsonSerializer.Serialize(payload),
                System.Text.Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync(url, content).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                _logger.LogInformation("已上报考勤结果到集控（迟到 {Count} 人）", record.Late.Count);
            }
            else
            {
                _logger.LogWarning("上报考勤结果失败：HTTP {Code}", (int)resp.StatusCode);
            }
        }
        catch (Exception e)
        {
            // 上报失败不影响考勤本身：结果已存本机，班主任端下次仍能看到新的。
            _logger.LogWarning(e, "上报考勤结果失败");
        }
    }

    /// <summary>
    /// 保存最近一次结果到本机。只存姓名与统计，不存任何特征向量或照片路径。
    /// </summary>
    private void SaveLastResult(LateRecord record)
    {
        try
        {
            FaceAttendancePaths.EnsureDirectory(FaceAttendancePaths.RootFolder);
            var path = Path.Combine(FaceAttendancePaths.RootFolder, "last-result.json");
            var json = System.Text.Json.JsonSerializer.Serialize(record,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            var temp = path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, path, true);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "保存最近考勤结果失败");
        }
    }

    private static void SaveJpeg(Mat frame, string path)
    {
        try
        {
            if (File.Exists(path)) return;
            var bytes = frame.ToBytes(".jpg", new[] { (int)ImwriteFlags.JpegQuality, 88 });
            File.WriteAllBytes(path, bytes);
        }
        catch
        {
            // 抓拍照片只是存档用途，失败不影响识别
        }
    }

    /// <summary>
    /// 清理超过保留期的抓拍存档。
    /// </summary>
    private void CleanupSnapshots()
    {
        try
        {
            var days = Math.Max(1, _settings.RetentionDays);
            if (!Directory.Exists(FaceAttendancePaths.SnapshotsFolder)) return;

            var deadline = DateTime.Now.AddDays(-days);
            foreach (var dir in Directory.GetDirectories(FaceAttendancePaths.SnapshotsFolder))
            {
                if (Directory.GetCreationTime(dir) < deadline)
                {
                    Directory.Delete(dir, true);
                }
            }
        }
        catch (Exception e)
        {
            _logger.LogDebug(e, "清理过期抓拍失败");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gate.Dispose();
    }
}
