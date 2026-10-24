using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClassPixelPro.Core;
using ClassPixelPro.Core.Abstractions.Services.Management;
using ClassPixelPro.Services.RemoteMonitor;
using ClassPixelPro.Shared;
using Microsoft.Extensions.Logging;
using OpenCvSharp;

namespace ClassPixelPro.Services.Management;

/// <summary>
/// 录制计划执行服务。
/// <para>
/// 集控后台下发录制计划后，本服务每 60 秒拉取一次，并按设备本地时间执行：
/// <list type="bullet">
/// <item>video 模式：在计划时间段内以 5fps 采集<b>摄像头</b>画面录成 MP4，结束后直传又拍云。</item>
/// <item>photo 模式：在指定时间点或按固定间隔抓拍<b>摄像头</b>画面，逐张直传又拍云。</item>
/// </list>
/// 采集源是教室电脑的摄像头（与远程监控共用同一个 <see cref="CameraCaptureService"/>，
/// 因此跟随监控设置里选中的设备），不是桌面画面。
/// 上传使用服务端签发的短期直传签名，操作员密码不会下发到教室电脑。
/// </para>
/// </summary>
public sealed class RecordingService : IDisposable
{
    private const int PlanPollSeconds = 60;
    // 摄像头录像按 5fps 抽帧：Tick 每秒只跑一次，靠 TickMs 细化才能凑够帧数。
    private const int TickMs = 200;
    private const double VideoFps = 5.0;
    private const int VideoMaxWidth = 1280;
    private const int JpegQuality = 70;
    private const int MaxVideoSeconds = 3 * 60 * 60;
    private const long MaxUploadBytes = 2L * 1024 * 1024 * 1024;

    private readonly ILogger<RecordingService> _logger;
    private readonly CameraCaptureService _cameraCapture;

    private readonly object _lock = new();
    private readonly Dictionary<string, PlanState> _states = new();
    private List<RecordingPlan> _plans = [];
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private DateTime _lastPoll = DateTime.MinValue;

    public RecordingService(ILogger<RecordingService> logger, CameraCaptureService cameraCapture)
    {
        _logger = logger;
        _cameraCapture = cameraCapture;
    }

    public bool IsRunning => _loop is { IsCompleted: false };

    public void Start()
    {
        lock (_lock)
        {
            if (IsRunning) return;
            // 跟随远程监控里选中的摄像头：该设置是持久化的，但平时只有打开监控设置页才同步给
            // CameraCaptureService；录制计划是无人值守执行的，这里启动时主动同步一次，
            // 否则多摄像头机器上会录到默认的 0 号（比如讲台上的文件展台）。
            var savedCamera = IAppHost.TryGetService<SettingsService>()?.Settings.RemoteMonitorSelectedCameraIndex ?? 0;
            if (savedCamera != _cameraCapture.CurrentCameraIndex)
            {
                _cameraCapture.SelectCamera(savedCamera.ToString());
            }

            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _loop = Task.Run(() => RunAsync(ct), ct);
            _logger.LogInformation("录制计划服务已启动");
        }
    }

    /// <summary>
    /// 停止轮询并尽量完成正在录制的文件上传（应用退出前调用，最多等待约 20 秒）。
    /// </summary>
    public async Task StopAsync()
    {
        CancellationTokenSource? cts;
        Task? loop;
        lock (_lock)
        {
            cts = _cts;
            loop = _loop;
            _cts = null;
            _loop = null;
        }
        cts?.Cancel();
        if (loop != null)
        {
            try { await loop.WaitAsync(TimeSpan.FromSeconds(20)); }
            catch (Exception ex) { _logger.LogWarning(ex, "等待录制服务退出超时"); }
        }
        cts?.Dispose();
        await FinalizeAllAsync();
        _logger.LogInformation("录制计划服务已停止");
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (DateTime.Now - _lastPoll > TimeSpan.FromSeconds(PlanPollSeconds))
                {
                    _lastPoll = DateTime.Now;
                    await RefreshPlansAsync(ct);
                }
                Tick();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "录制计划执行失败");
            }

            try { await Task.Delay(TickMs, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task RefreshPlansAsync(CancellationToken ct)
    {
        var mgmt = IAppHost.TryGetService<IManagementService>();
        if (mgmt is not { IsManagementEnabled: true }) return;
        var baseUrl = mgmt.GetManagementBaseUrl();
        if (string.IsNullOrEmpty(baseUrl)) return;
        var orgId = mgmt.GetOrganizationId();
        if (string.IsNullOrEmpty(orgId)) return;
        var cuid = mgmt.Persist.ClientUniqueId.ToString();
        var url = $"{baseUrl}/api/mgmt/{orgId}/serverless/recording-plans?cuid={cuid}";

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var text = await http.GetStringAsync(url, ct);
        using var doc = JsonDocument.Parse(text);
        if (!doc.RootElement.TryGetProperty("plans", out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            _plans = [];
            return;
        }
        var plans = new List<RecordingPlan>();
        foreach (var p in arr.EnumerateArray())
        {
            try { plans.Add(RecordingPlan.FromJson(p)); }
            catch (Exception ex) { _logger.LogWarning(ex, "跳过无法解析的录制计划"); }
        }
        _plans = plans;
        _logger.LogInformation("已拉取 {Count} 个录制计划", plans.Count);

        // 计划被删除/停用后，正在录制的窗口必须收尾，否则会一直录到进程退出。
        var validIds = plans.Select(p => p.Id).ToHashSet();
        foreach (var (id, state) in _states.ToList())
        {
            if (!validIds.Contains(id) && state.IsRecording)
            {
                _ = StopVideoAsync(id, state, "录制计划已删除或停用");
            }
        }
    }

    private void Tick()
    {
        if (_plans.Count == 0) return;
        var now = DateTime.Now;
        foreach (var plan in _plans)
        {
            var state = GetState(plan.Id);
            var active = plan.IsActive(now);
            if (!active)
            {
                if (state.IsRecording) _ = StopVideoAsync(plan.Id, state, "计划时间段结束");
                state.FiredMinutes.Clear();
                continue;
            }

            if (plan.Mode == "video")
            {
                // 录像必须有时间窗，缺少窗口的脏数据直接跳过，避免全天录制。
                if (plan.Start is null || plan.End is null) continue;
                if (!state.IsRecording) StartVideo(plan, state, now);
                if (state.IsRecording) WriteVideoFrame(plan, state, now);
            }
            else
            {
                var minuteKey = now.ToString("yyyy-MM-dd HH:mm");
                var shouldShoot = false;

                if (plan.PhotoTimes.Count > 0 && plan.PhotoTimes.Contains(now.ToString("HH:mm"))
                    && state.FiredMinutes.Add(minuteKey))
                {
                    shouldShoot = true;
                }
                if (plan.PhotoIntervalSeconds > 0
                    && (now - state.LastPhotoAt).TotalSeconds >= plan.PhotoIntervalSeconds)
                {
                    shouldShoot = true;
                }

                if (shouldShoot)
                {
                    _logger.LogInformation("触发定时拍照（{Plan}）", plan.Name);
                    state.LastPhotoAt = now;
                    _ = CapturePhotoAsync(plan, now);
                }
            }
        }
    }

    private PlanState GetState(string id)
    {
        lock (_lock)
        {
            if (!_states.TryGetValue(id, out var s))
            {
                s = new PlanState();
                _states[id] = s;
            }
            return s;
        }
    }

    private void StartVideo(RecordingPlan plan, PlanState state, DateTime now)
    {
        if (!_cameraCapture.IsSupported)
        {
            _logger.LogWarning("当前平台不支持摄像头采集，跳过录制计划 {Plan}", plan.Name);
            return;
        }
        try
        {
            var dir = GetRecordingDir();
            Directory.CreateDirectory(dir);
            var safeName = Sanitize(plan.Name);
            var path = Path.Combine(dir, $"{safeName}_{now:yyyyMMdd_HHmmss}.mp4");
            state.TempPath = path;
            state.StartedAt = now;
            state.Writer = null;
            state.FrameSize = default;
            state.IsRecording = true;
            _logger.LogInformation("开始录制（{Plan}）→ {Path}", plan.Name, path);
        }
        catch (Exception ex)
        {
            state.IsRecording = false;
            _logger.LogError(ex, "启动录制失败");
        }
    }

    private void WriteVideoFrame(RecordingPlan plan, PlanState state, DateTime now)
    {
        if ((now - state.LastFrameAt).TotalSeconds < 1.0 / VideoFps) return;
        state.LastFrameAt = now;
        try
        {
            // 单次录制超过上限强制收尾，避免异常情况下把磁盘写满。
            if ((now - (state.StartedAt ?? now)).TotalSeconds > MaxVideoSeconds)
            {
                _ = StopVideoAsync(plan.Id, state, "单次录制超过时长上限");
                return;
            }

            var jpeg = _cameraCapture.CaptureFrameAsJpeg(JpegQuality);
            // 摄像头没出帧（被占用 / 未接 / 驱动异常）时直接跳过这一拍，不写空帧。
            if (jpeg.Length == 0) return;
            using var decoded = Cv2.ImDecode(jpeg, ImreadModes.Color);
            if (decoded.Empty()) return;

            var size = FitSize(decoded.Width, decoded.Height);
            using var frame = new Mat();
            if (decoded.Width != size.Width || decoded.Height != size.Height)
            {
                Cv2.Resize(decoded, frame, size, 0, 0, InterpolationFlags.Area);
            }
            else
            {
                decoded.CopyTo(frame);
            }

            if (state.Writer == null)
            {
                var fourcc = VideoWriter.FourCC('m', 'p', '4', 'v');
                var writer = new VideoWriter(state.TempPath!, fourcc, VideoFps, size);
                if (!writer.IsOpened())
                {
                    writer.Dispose();
                    throw new InvalidOperationException("无法创建 MP4 视频编码器（mp4v）");
                }
                state.Writer = writer;
                state.FrameSize = size;
            }
            state.Writer.Write(frame);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "写入录制帧失败，结束本次录制");
            _ = StopVideoAsync(plan.Id, state, "写入录制帧失败：" + ex.Message);
        }
    }

    private async Task StopVideoAsync(string planId, PlanState state, string reason)
    {
        if (!state.IsRecording) return;
        state.IsRecording = false;
        var path = state.TempPath;
        var startedAt = state.StartedAt ?? DateTime.Now;
        try
        {
            state.Writer?.Release();
            state.Writer?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "关闭视频编码器失败");
        }
        state.Writer = null;
        state.TempPath = null;
        state.StartedAt = null;
        ReleaseCameraIfIdle();

        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        var endedAt = DateTime.Now;
        _logger.LogInformation("录制结束（{Reason}），开始上传 {Path}", reason, path);
        await UploadAsync(planId, "video", path, startedAt, endedAt, endedAt - startedAt);
    }

    private async Task CapturePhotoAsync(RecordingPlan plan, DateTime now)
    {
        if (!_cameraCapture.IsSupported) return;
        string? path = null;
        try
        {
            var jpeg = _cameraCapture.CaptureFrameAsJpeg(85);
            if (jpeg.Length == 0)
            {
                _logger.LogWarning("定时拍照未取到摄像头画面（{Plan}）", plan.Name);
                return;
            }
            var dir = GetRecordingDir();
            Directory.CreateDirectory(dir);
            path = Path.Combine(dir, $"{Sanitize(plan.Name)}_{now:yyyyMMdd_HHmmss}.jpg");
            await File.WriteAllBytesAsync(path, jpeg);
            await UploadAsync(plan.Id, "photo", path, now, now, TimeSpan.Zero);
            // 按间隔拍照（可能每几秒一张）保留设备占用，避免每次重新打开摄像头；
            // 只在按时间点拍照时释放，让指示灯别一直亮着。
            if (plan.PhotoIntervalSeconds == 0) ReleaseCameraIfIdle();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "拍照失败");
            if (path != null) TryDelete(path);
        }
    }

    /// <summary>
    /// 录完 / 拍完后释放摄像头，避免摄像头指示灯一直亮着。
    /// 远程监控有人正在看时不动设备，否则会把画面抢断。
    /// </summary>
    private void ReleaseCameraIfIdle()
    {
        if (IAppHost.TryGetService<RemoteMonitorService>() is { HasActiveViewer: true }) return;
        _cameraCapture.ReleaseCamera();
    }

    private async Task UploadAsync(string planId, string kind, string path, DateTime startedAt, DateTime endedAt, TimeSpan duration)
    {
        var mgmt = IAppHost.TryGetService<IManagementService>();
        var baseUrl = mgmt?.GetManagementBaseUrl();
        var orgId = mgmt?.GetOrganizationId();
        if (mgmt == null || string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(orgId))
        {
            _logger.LogWarning("未加入集控，丢弃录制产物 {Path}", path);
            TryDelete(path);
            return;
        }

        var cuid = mgmt.Persist.ClientUniqueId.ToString();
        var size = new FileInfo(path).Length;
        if (size <= 0 || size > MaxUploadBytes)
        {
            _logger.LogWarning("录制文件大小异常（{Size} 字节），跳过上传：{Path}", size, path);
            await ReportResultAsync(baseUrl, orgId, cuid, planId, kind, path, size, duration, startedAt, endedAt,
                "failed", "文件大小不合法").ConfigureAwait(false);
            TryDelete(path);
            return;
        }

        try
        {
            var ticketUrl = $"{baseUrl}/api/mgmt/{orgId}/serverless/recording-upload-ticket?cuid={cuid}";
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            using var ticketResp = await http.PostAsync(ticketUrl,
                new StringContent(JsonSerializer.Serialize(new { size, kind }),
                    System.Text.Encoding.UTF8, "application/json"));
            var ticketText = await ticketResp.Content.ReadAsStringAsync();
            if (!ticketResp.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"获取上传签名失败：HTTP {(int)ticketResp.StatusCode} {ticketText}");
            }
            using var ticketDoc = JsonDocument.Parse(ticketText);
            var root = ticketDoc.RootElement;
            var url = root.GetProperty("url").GetString()!;
            var objectPath = root.GetProperty("objectPath").GetString()!;
            var date = root.GetProperty("date").GetString()!;
            var authorization = root.GetProperty("authorization").GetString()!;
            var contentType = root.TryGetProperty("contentType", out var ctEl) ? ctEl.GetString()! : "application/octet-stream";

            using (var stream = File.OpenRead(path))
            using (var req = new HttpRequestMessage(HttpMethod.Put, url))
            {
                req.Headers.TryAddWithoutValidation("Authorization", authorization);
                req.Headers.TryAddWithoutValidation("Date", date);
                req.Content = new StreamContent(stream);
                req.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
                req.Content.Headers.ContentLength = size;
                using var putResp = await http.SendAsync(req);
                if (!putResp.IsSuccessStatusCode)
                {
                    var body = await putResp.Content.ReadAsStringAsync();
                    throw new InvalidOperationException($"上传又拍云失败：HTTP {(int)putResp.StatusCode} {body}");
                }
            }

            await ReportResultAsync(baseUrl, orgId, cuid, planId, kind, path, size, duration, startedAt, endedAt,
                "done", null, objectPath).ConfigureAwait(false);
            _logger.LogInformation("录制产物已上传：{Path} → {Object}", path, objectPath);
            TryDelete(path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "上传录制产物失败：{Path}", path);
            await ReportResultAsync(baseUrl, orgId, cuid, planId, kind, path, size, duration, startedAt, endedAt,
                "failed", ex.Message).ConfigureAwait(false);
            TryDelete(path);
        }
    }

    private async Task ReportResultAsync(string baseUrl, string orgId, string cuid, string planId, string kind,
        string path, long size, TimeSpan duration, DateTime startedAt, DateTime endedAt,
        string status, string? error, string? objectPath = null)
    {
        try
        {
            var title = Path.GetFileNameWithoutExtension(path);
            var body = JsonSerializer.Serialize(new
            {
                planId,
                kind,
                title,
                objectPath = objectPath ?? "",
                size,
                durationSeconds = (int)Math.Max(0, duration.TotalSeconds),
                startedAt = startedAt.ToString("O"),
                endedAt = endedAt.ToString("O"),
                status,
                error = error ?? ""
            });
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            using var resp = await http.PostAsync(
                $"{baseUrl}/api/mgmt/{orgId}/serverless/recording-result?cuid={cuid}",
                new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("回传录制结果失败：HTTP {Status}", (int)resp.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "回传录制结果失败");
        }
    }

    private async Task FinalizeAllAsync()
    {
        foreach (var (id, state) in _states.ToList())
        {
            if (state.IsRecording) await StopVideoAsync(id, state, "应用退出");
        }
    }

    private static string GetRecordingDir()
        => Path.Combine(CommonDirectories.AppDataFolderPath, "Recordings");

    private static OpenCvSharp.Size FitSize(int width, int height)
    {
        if (width <= VideoMaxWidth) return new OpenCvSharp.Size(MakeEven(width), MakeEven(height));
        var scale = (double)VideoMaxWidth / width;
        return new OpenCvSharp.Size(MakeEven((int)(width * scale)), MakeEven((int)(height * scale)));
    }

    private static int MakeEven(int v) => v % 2 == 0 ? v : v - 1;

    private static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "recording" : name;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 清理失败不影响主流程 */ }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    private sealed class PlanState
    {
        public bool IsRecording;
        public VideoWriter? Writer;
        public string? TempPath;
        public DateTime? StartedAt;
        public DateTime LastFrameAt;
        public DateTime LastPhotoAt = DateTime.MinValue;
        public OpenCvSharp.Size FrameSize;
        public readonly HashSet<string> FiredMinutes = [];
    }

    private sealed class RecordingPlan
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public string Mode { get; init; } = "video";
        public string ScheduleType { get; init; } = "weekly";
        public DateTime? RunDate { get; init; }
        public int[] Weekdays { get; init; } = [];
        public TimeSpan? Start { get; init; }
        public TimeSpan? End { get; init; }
        public HashSet<string> PhotoTimes { get; init; } = [];
        public int PhotoIntervalSeconds { get; init; }

        public bool IsActive(DateTime now)
        {
            // 调度方式：weekly 按星期重复；once 只在指定日期执行。
            if (ScheduleType == "once")
            {
                if (RunDate is null || RunDate.Value.Date != now.Date) return false;
            }
            else
            {
                var dow = now.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)now.DayOfWeek;
                if (!Weekdays.Contains(dow)) return false;
            }

            // 没有时间窗（定时拍照只填时间点）表示当天全天有效。
            if (Start is null || End is null) return true;
            var t = now.TimeOfDay;
            return t >= Start.Value && t < End.Value;
        }

        public static RecordingPlan FromJson(JsonElement e)
        {
            var days = new List<int>();
            if (e.TryGetProperty("weekdays", out var wd) && wd.ValueKind == JsonValueKind.Array)
            {
                foreach (var d in wd.EnumerateArray())
                {
                    if (d.TryGetInt32(out var day) && day is >= 1 and <= 7) days.Add(day);
                }
            }
            return new RecordingPlan
            {
                Id = e.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
                Name = e.TryGetProperty("name", out var n) ? n.GetString() ?? "录制计划" : "录制计划",
                Mode = e.TryGetProperty("mode", out var m) ? m.GetString() ?? "video" : "video",
                ScheduleType = e.TryGetProperty("schedule_type", out var st) ? st.GetString() ?? "weekly" : "weekly",
                RunDate = ParseDate(e, "run_date"),
                Start = ParseTimeOrNull(e, "start_time"),
                End = ParseTimeOrNull(e, "end_time"),
                PhotoIntervalSeconds = e.TryGetProperty("photo_interval_seconds", out var iv) && iv.TryGetInt32(out var v) ? v : 0,
                Weekdays = days.ToArray(),
                PhotoTimes = ParsePhotoTimes(e)
            };
        }

        private static DateTime? ParseDate(JsonElement e, string prop)
        {
            var s = e.TryGetProperty(prop, out var v) ? v.GetString() : null;
            return DateTime.TryParse(s, out var d) ? d.Date : null;
        }

        private static TimeSpan? ParseTimeOrNull(JsonElement e, string prop)
        {
            var s = e.TryGetProperty(prop, out var v) ? v.GetString() : null;
            if (string.IsNullOrWhiteSpace(s)) return null;
            return TimeSpan.TryParse(s, out var t) ? t : null;
        }

        private static HashSet<string> ParsePhotoTimes(JsonElement e)
        {
            var set = new HashSet<string>();
            if (!e.TryGetProperty("photo_times", out var v)) return set;
            var raw = v.GetString() ?? "";
            foreach (var part in raw.Split([',', '，', ' ', ';'], StringSplitOptions.RemoveEmptyEntries))
            {
                // TimeSpan 的自定义格式只有小写 hh（表示 00-23 小时，没有上下午概念）；
                // 大写 HH 不是合法的 TimeSpan 格式符，ToString 会直接抛 FormatException，
                // 导致所有含拍照时间点的计划在解析阶段被整条丢弃——这是「录制计划完全不可用」的根因。
                if (TimeSpan.TryParse(part.Trim(), out var t)) set.Add(t.ToString(@"hh\:mm"));
            }
            return set;
        }
    }
}
