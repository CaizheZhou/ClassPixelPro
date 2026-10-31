using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClassPixelPro.Core;
using ClassPixelPro.Core.Abstractions.Services.Management;
using ClassPixelPro.Shared;
using Microsoft.Extensions.Logging;

namespace ClassPixelPro.Services.FaceAttendance;

/// <summary>
/// 集控下发的人脸考勤计划服务。
/// <para>
/// 计划时间由组织主导：本服务每 60 秒从集控后台拉取本组织启用的考勤计划，
/// 换算成 <see cref="WeekSchedule"/> 后覆盖本机设置里的计划，客户端本地不能改。
/// </para>
/// <para>
/// 同时负责两件只在集控环境下才有意义的事：
/// <list type="bullet">
/// <item>把班主任端上传的待录入照片拉到本机收件箱（<c>DownloadPendingAsync</c> 的入口）；</item>
/// <item>响应班主任端的「立即识别一次」命令（<c>RunRequested</c> 事件）。</item>
/// </list>
/// </para>
/// </summary>
public sealed class FacePlanSyncService : IDisposable
{
    private const int PollSeconds = 60;

    private readonly ILogger<FacePlanSyncService> _logger;
    private readonly EnrollmentService _enrollment;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private DateTime _lastAutoPull = DateTime.MinValue;
    private static readonly TimeSpan AutoPullInterval = TimeSpan.FromMinutes(1);
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private DateTime _lastPoll = DateTime.MinValue;
    private string _lastPlanSignature = "";

    public FacePlanSyncService(ILogger<FacePlanSyncService> logger, EnrollmentService enrollment)
    {
        _logger = logger;
        _enrollment = enrollment;
    }

    public bool IsRunning => _loop is { IsCompleted: false };

    /// <summary>最近一次拉取到的计划描述（空表示尚未拉到或没有启用计划）。</summary>
    public string LastPlanDescription { get; private set; } = "";

    /// <summary>班主任端请求「立即识别一次」时触发。参数为命令产生时间。</summary>
    public event Action<DateTime>? RunRequested;

    /// <summary>
    /// 班主任端请求「摄像头拍照录入」时触发：参数为花名册姓名与抓拍张数。
    /// 处理方返回录入结果（是否成功、新增样本数、给班主任看的话），
    /// 本服务随命令确认回传给服务端，班主任端据此提示。
    /// </summary>
    public event Func<string, int, Task<(bool Ok, int Added, string Message)>>? EnrollCameraRequested;

    public void Start()
    {
        if (IsRunning) return;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _loop = Task.Run(() => RunAsync(ct), ct);
        _logger.LogInformation("人脸考勤计划同步服务已启动");
    }

    public async Task StopAsync()
    {
        var cts = _cts;
        var loop = _loop;
        _cts = null;
        _loop = null;
        if (cts == null) return;
        cts.Cancel();
        if (loop != null)
        {
            try { await loop.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception ex) { _logger.LogWarning(ex, "等待考勤计划同步服务退出超时"); }
        }
        cts.Dispose();
        _logger.LogInformation("人脸考勤计划同步服务已停止");
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (DateTime.Now - _lastPoll > TimeSpan.FromSeconds(PollSeconds))
                {
                    _lastPoll = DateTime.Now;
                    if (await RefreshAsync(ct))
                    {
                        // 计划拉成功说明集控在线，顺路把班主任上传的录入照片拉下来。
                        await AutoPullEnrollmentsAsync(ct);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // 轮询失败不影响本地已有计划：下一次轮询还会再试。
                _logger.LogWarning(ex, "拉取人脸考勤计划失败");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(2), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// 拉取一次计划并应用到本机设置。返回是否成功拉到（未加入集控时返回 false）。
    /// </summary>
    public async Task<bool> RefreshAsync(CancellationToken ct = default)
    {
        var mgmt = IAppHost.TryGetService<IManagementService>();
        if (mgmt is not { IsManagementEnabled: true }) return false;
        var baseUrl = mgmt.GetManagementBaseUrl();
        var orgId = mgmt.GetOrganizationId();
        if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(orgId)) return false;
        var cuid = mgmt.Persist.ClientUniqueId.ToString();

        var url = $"{baseUrl}/api/mgmt/{orgId}/serverless/face-plans?cuid={cuid}";
        using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("拉取考勤计划失败：HTTP {Code}", (int)resp.StatusCode);
            return false;
        }

        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(text);
        if (!doc.RootElement.TryGetProperty("plans", out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        // 班主任端的「立即识别一次」命令与计划搭同一班车（见服务端 face-plans 端点）
        if (doc.RootElement.TryGetProperty("runAt", out var runAtEl))
        {
            var runAt = runAtEl.GetString() ?? "";
            if (!string.IsNullOrEmpty(runAt) && runAt != _lastRunAt)
            {
                _lastRunAt = runAt;
                _logger.LogInformation("班主任端请求立即执行一次人脸考勤（{RunAt}）", runAt);
                RunRequested?.Invoke(DateTime.Now);

                // 收到就回传确认，让服务端把这条命令删掉。否则它会一直搭在 face-plans 的返回里，班主任端也无从判断到底执行没执行。
                await AckCommandAsync(null, ct).ConfigureAwait(false);
            }
        }

        // 班主任端的「摄像头拍照录入」命令搭同一班车：设备端抓几张照片当场提取特征归档，
        // 结果（成功与否、新增样本数）随确认回传，班主任端轮询后即可看到。
        if (doc.RootElement.TryGetProperty("enroll", out var enrollEl) && enrollEl.ValueKind == JsonValueKind.Object)
        {
            var name = enrollEl.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "";
            var shots = enrollEl.TryGetProperty("shots", out var shotsEl) && shotsEl.TryGetInt32(out var sc) ? sc : 3;
            var ts = enrollEl.TryGetProperty("ts", out var tsEl) ? tsEl.GetString() ?? "" : "";
            if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(ts) && ts != _lastEnrollTs)
            {
                _lastEnrollTs = ts;
                _logger.LogInformation("班主任端请求摄像头拍照录入：{Name}（{Shots} 张）", name, shots);
                var (ok, added, message) = await RunEnrollAsync(name, shots).ConfigureAwait(false);
                await AckCommandAsync(new CommandAckResult(ok, added, message), ct).ConfigureAwait(false);
            }
        }

        var weekdays = new Dictionary<DayOfWeek, List<TimeSpan>>();
        for (var i = 0; i < 7; i++)
        {
            weekdays[(DayOfWeek)i] = new List<TimeSpan>();
        }

        foreach (var plan in arr.EnumerateArray())
        {
            foreach (var day in ParseWeekdays(plan))
            {
                if (!weekdays.TryGetValue(day, out var times)) continue;
                foreach (var t in ParseTimes(plan))
                {
                    if (!times.Contains(t)) times.Add(t);
                }
            }
        }

        // 计划没变就不动设置：每次轮询都重写会触发一次设置保存与 UI 刷新。
        var signature = BuildSignature(weekdays);
        if (signature == _lastPlanSignature) return true;

        ApplyToSettings(weekdays);
        _lastPlanSignature = signature;
        var settings = IAppHost.TryGetService<FaceAttendanceSettings>();
        LastPlanDescription = settings?.WeekSchedule.Describe() ?? "";
        _logger.LogInformation("已应用集控下发的考勤计划：{Description}", LastPlanDescription);
        return true;
    }

    /// <summary>
    /// 自动拉取班主任端上传的待录入照片并按自动模式导入：
    /// 班主任上传时已指明姓名，姓名唯一对应花名册且照片单脸的直接归档进花名册，
    /// 其余留在收件箱等人工确认。每轮计划同步至多跑一次（60 秒节流），
    /// 没有待录入照片时就是一次廉价的列表请求。
    /// </summary>
    private async Task AutoPullEnrollmentsAsync(CancellationToken ct)
    {
        if (DateTime.Now - _lastAutoPull < AutoPullInterval) return;
        _lastAutoPull = DateTime.Now;
        try
        {
            var downloadedFiles = new List<string>();
            var consumedIds = new List<string>();
            var (downloaded, _) = await _enrollment
                .DownloadPendingAsync(null, ct, consumedIds, downloadedFiles)
                .ConfigureAwait(false);
            if (downloaded <= 0 || downloadedFiles.Count == 0) return;

            _logger.LogInformation("自动拉取班主任端录入照片 {Count} 张，开始自动导入", downloaded);
            var summary = await _enrollment
                .SmartImportAsync(downloadedFiles, null, null, ct)
                .ConfigureAwait(false);
            _logger.LogInformation(
                "自动导入完成：成功 {Ok} 张，待人工确认 {Pending} 张",
                summary.OkCount, summary.Items.Count(x => !x.Ok));
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "自动拉取/导入班主任端录入照片失败");
        }
    }

    /// <summary>
    /// 最近一次从服务端读到的「立即识别」命令时间戳。用于避免同一条命令重复触发。
    /// </summary>
    private string _lastRunAt = "";

    /// <summary>最近一次已处理的「摄像头拍照录入」命令时间戳，作用同上。</summary>
    private string _lastEnrollTs = "";

    /// <summary>命令确认携带的结果：随 ack 回传给服务端，供班主任端展示。</summary>
    private sealed record CommandAckResult(bool Ok, int Added, string Message);

    /// <summary>
    /// 执行一次「摄像头拍照录入」命令。没有处理方（比如录入服务不可用）时返回失败，
    /// 结果同样回传，让班主任端立刻知道设备端没做成。
    /// </summary>
    private async Task<(bool Ok, int Added, string Message)> RunEnrollAsync(string name, int shots)
    {
        var handler = EnrollCameraRequested;
        if (handler == null)
        {
            return (false, 0, "设备端没有响应录入命令（人脸考勤服务可能未运行）。");
        }

        try
        {
            return await handler(name, shots).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "处理摄像头拍照录入命令时出错");
            return (false, 0, "录入执行出错：" + e.Message);
        }
    }

    private static string BuildSignature(Dictionary<DayOfWeek, List<TimeSpan>> weekdays)
    {
        return string.Join("|", weekdays
            .OrderBy(x => (int)x.Key)
            .Select(x => $"{(int)x.Key}:{string.Join(",", x.Value.OrderBy(t => t).Select(FaceScheduleParser.Format))}"));
    }

    private void ApplyToSettings(Dictionary<DayOfWeek, List<TimeSpan>> weekdays)
    {
        var settings = IAppHost.TryGetService<FaceAttendanceSettings>();
        if (settings == null) return;

        var schedule = WeekSchedule.CreateDefault();
        foreach (var day in schedule.Days)
        {
            if (!weekdays.TryGetValue(day.Day, out var times) || times.Count == 0) continue;
            day.Enabled = true;
            day.Times = string.Join(",", times.OrderBy(t => t).Select(FaceScheduleParser.Format));
        }

        settings.WeekSchedule = schedule;
    }

    ///     /// <summary>
    /// 告诉服务端命令（立即识别 / 摄像头录入）已经收到，可以删掉了。
    /// <paramref name="result"/> 非空时把录入结果一并回传，服务端会存下来给班主任端看。
    /// 失败只记日志：本地 <c>_lastRunAt</c>/<c>_lastEnrollTs</c> 仍能挡住同一条命令被反复触发。
    /// </summary>
    private async Task AckCommandAsync(CommandAckResult? result, CancellationToken ct)
    {
        try
        {
            var mgmt = IAppHost.TryGetService<IManagementService>();
            if (mgmt is not { IsManagementEnabled: true }) return;
            var baseUrl = mgmt.GetManagementBaseUrl();
            var orgId = mgmt.GetOrganizationId();
            if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(orgId)) return;

            var url = $"{baseUrl}/api/mgmt/{orgId}/serverless/face-command-ack?cuid={mgmt.Persist.ClientUniqueId}";
            using var content = result == null
                ? null
                : new StringContent(
                    System.Text.Json.JsonSerializer.Serialize(new
                    {
                        status = result.Ok ? "ok" : "error",
                        added = result.Added,
                        message = result.Message,
                    }),
                    System.Text.Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync(url, content, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("回传考勤命令确认失败：HTTP {Code}", (int)resp.StatusCode);
            }
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "回传考勤命令确认失败");
        }
    }

private static IEnumerable<DayOfWeek> ParseWeekdays(JsonElement plan)
    {
        if (!plan.TryGetProperty("weekdays", out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var el in arr.EnumerateArray())
        {
            if (!el.TryGetInt32(out var d) || d is < 1 or > 7) continue;
            // 1=周一 … 7=周日 → DayOfWeek（0=周日 … 6=周六）
            yield return d == 7 ? DayOfWeek.Sunday : (DayOfWeek)d;
        }
    }

    /// <summary>解析计划里的抓拍时刻列表（HH:mm 或 HH:mm:ss）。</summary>
    private static IEnumerable<TimeSpan> ParseTimes(JsonElement plan)
    {
        if (!plan.TryGetProperty("times", out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var el in arr.EnumerateArray())
        {
            var text = el.GetString();
            if (FaceScheduleParser.TryParseTime(text, out var t)) yield return t;
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _http.Dispose();
    }
}
