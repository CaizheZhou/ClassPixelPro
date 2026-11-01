using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using ClassPixelPro.Core;
using ClassPixelPro.Core.Abstractions.Services.Management;
using ClassPixelPro.Shared;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClassPixelPro.Services.Management;

/// <summary>
/// 考试模式服务：每 60 秒从集控拉取对本机生效的考试计划，按本地时间评估是否处于考试窗口。
/// 生效期间按计划要求隐藏课表/倒计时、静音自动提醒（远程喊话不禁用），并显示「考试中」角标。
/// 考试计划条目由后台按组织统一作息解析成具体起止时刻，这里无需再查作息表。
/// </summary>
public class ExamModeService : Microsoft.Extensions.Hosting.BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(60);

    public static ExamModeService? Instance { get; private set; }

    private readonly ILogger<ExamModeService> _logger;
    private readonly List<JsonElement> _plans = [];

    private List<JsonElement> Plans
    {
        get
        {
            lock (this) { return _plans; }
        }
    }

    /// <summary>当前是否处于考试窗口。</summary>
    public bool IsActive { get; private set; }

    /// <summary>当前生效的考试名称（多个时取第一个）。</summary>
    public string ExamName { get; private set; } = "";

    /// <summary>是否要求隐藏课表与倒计时。</summary>
    public bool HideSchedule { get; private set; }

    /// <summary>是否要求静音自动提醒。</summary>
    public bool MuteReminders { get; private set; }

    /// <summary>考试状态变化（进入/退出考试窗口）时触发。</summary>
    public event Action? StateChanged;

    public ExamModeService(ILogger<ExamModeService> logger)
    {
        _logger = logger;
        Instance = this;
    }

    /// <summary>自动提醒（上下课/放学/天气）是否应被静音。供各提醒提供方快速判断。</summary>
    public static bool IsMuteActive => Instance is { IsActive: true, MuteReminders: true };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("考试模式服务已启动");
        using var timer = new PeriodicTimer(TickInterval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RefreshPlansAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "拉取考试计划失败，沿用上次结果");
            }

            try
            {
                Evaluate();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "评估考试窗口失败");
            }

            try { await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false); }
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
        var url = $"{baseUrl}/api/mgmt/{orgId}/serverless/exam-schedules?cuid={cuid}";

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var text = await http.GetStringAsync(url, ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(text);
        var plans = new List<JsonElement>();
        if (doc.RootElement.TryGetProperty("plans", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in arr.EnumerateArray())
            {
                plans.Add(p.Clone());
            }
        }
        lock (this) { _plans.Clear(); _plans.AddRange(plans); }
    }

    private void Evaluate()
    {
        var now = DateTime.Now;
        bool isActive = false;
        var name = "";
        var hide = false;
        var mute = false;
        foreach (var plan in Plans)
        {
            if (!IsPlanActiveNow(plan, now)) continue;
            isActive = true;
            hide |= plan.TryGetProperty("hideSchedule", out var hs) && hs.ValueKind == JsonValueKind.True;
            mute |= plan.TryGetProperty("muteReminders", out var mr) && mr.ValueKind == JsonValueKind.True;
            if (name.Length == 0 && plan.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
            {
                name = n.GetString() ?? "";
            }
        }

        if (isActive == IsActive && name == ExamName && hide == HideSchedule && mute == MuteReminders)
        {
            return;
        }

        IsActive = isActive;
        ExamName = name;
        HideSchedule = hide;
        MuteReminders = mute;
        _logger.LogInformation("考试模式状态变更：{State}（{Name}，隐藏课表={Hide}，静音提醒={Mute}）",
            isActive ? "进入考试窗口" : "考试结束", name, hide, mute);
        Dispatcher.UIThread.Post(() =>
        {
            StateChanged?.Invoke();
            UpdateBadge();
        });
    }

    private static bool IsPlanActiveNow(JsonElement plan, DateTime now)
    {
        if (!plan.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
        {
            return false;
        }
        foreach (var entry in entries.EnumerateArray())
        {
            if (IsEntryActive(entry, now)) return true;
        }
        return false;
    }

    private static bool IsEntryActive(JsonElement entry, DateTime now)
    {
        var today = now.ToString("yyyy-MM-dd");
        var dateStart = entry.TryGetProperty("dateStart", out var ds) && ds.ValueKind == JsonValueKind.String
            ? ds.GetString() : null;
        if (string.IsNullOrEmpty(dateStart)) return false;
        var dateEnd = entry.TryGetProperty("dateEnd", out var de) && de.ValueKind == JsonValueKind.String
            ? de.GetString() : dateStart;
        if (today.CompareTo(dateStart) < 0 || today.CompareTo(dateEnd) > 0) return false;

        var timeMode = entry.TryGetProperty("timeMode", out var tm) && tm.ValueKind == JsonValueKind.String
            ? tm.GetString() : "range";
        if (timeMode == "all_day") return true;

        var minutes = now.Hour * 60 + now.Minute;
        var start = ParseMinutes(entry, "startTime");
        var end = ParseMinutes(entry, "endTime");
        return start is int s && end is int e2 && minutes >= s && minutes < e2;
    }

    private static int? ParseMinutes(JsonElement entry, string prop)
    {
        if (!entry.TryGetProperty(prop, out var v) || v.ValueKind != JsonValueKind.String) return null;
        var t = v.GetString();
        if (string.IsNullOrEmpty(t) || t.Length < 4) return null;
        var parts = t.Split(':');
        if (!int.TryParse(parts[0], out var h) || !int.TryParse(parts[1], out var m)) return null;
        return h * 60 + m;
    }

    // ===== 「考试中」角标窗口 =====
    private ClassPixelPro.Views.Controls.ExamBadgeWindow? _badge;

    private void UpdateBadge()
    {
        var dispatcher = Dispatcher.UIThread;
        if (dispatcher == null) return;
        _ = dispatcher.InvokeAsync(() =>
        {
            try
            {
                if (IAppHost.TryGetService<ClassPixelPro.Views.Controls.ExamBadgeWindow>() is not { } badge) return;
                _badge = badge;
                if (!IsActive)
                {
                    if (badge.IsVisible) badge.Hide();
                    return;
                }
                badge.ShowExam(string.IsNullOrEmpty(ExamName) ? "考试中" : $"考试中：{ExamName}");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "更新考试角标失败");
            }
        });
    }

}
