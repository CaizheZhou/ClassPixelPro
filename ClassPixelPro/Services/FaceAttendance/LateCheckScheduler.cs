using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClassPixelPro.Services.RemoteMonitor;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClassPixelPro.Services.FaceAttendance;

/// <summary>
/// 定时考勤服务：到达计划时间时自动执行一次考勤。
/// <para>
/// 判定逻辑：每 1 秒轮询一次，取出「今天这个星期几」的计划，
/// 找到第一个「已经到点、还没执行过、且没超出补执行窗口」的时刻就执行。
/// 执行过的时刻会记进 <c>_ranKeys</c>，同一天同一个时刻不会重复执行。
/// </para>
/// <para>
/// <b>未加入集控或未绑定班主任时，本服务直接不启动</b>（见 <see cref="ShouldRun"/>）。
/// 否则定时器会在设备没有归属的情况下抓拍并存照片，这是隐私上不可接受的。
/// </para>
/// </summary>
public sealed class LateCheckScheduler : BackgroundService
{
    /// <summary>轮询间隔。抓拍时间支持精确到秒，轮询太慢会错过目标秒。</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly FaceAttendanceSettings _settings;
    private readonly AttendanceService _attendance;
    private readonly TeacherBindingGate _bindingGate;
    private readonly ILogger<LateCheckScheduler> _logger;

    /// <summary>已经执行过的时刻，键为 <c>yyyyMMdd + HH:mm:ss</c>。</summary>
    private readonly HashSet<string> _ranKeys = new();

    /// <summary>距离上次检查绑定状态的时间。检查本身是一次 HTTP 请求，不能每秒都打。</summary>
    private DateTime _lastBindingCheck = DateTime.MinValue;
    private static readonly TimeSpan BindingCheckInterval = TimeSpan.FromSeconds(60);
    private bool? _cachedBound;

    public LateCheckScheduler(
        FaceAttendanceSettings settings,
        AttendanceService attendance,
        TeacherBindingGate bindingGate,
        ILogger<LateCheckScheduler> logger)
    {
        _settings = settings;
        _attendance = attendance;
        _bindingGate = bindingGate;
        _logger = logger;
    }

    /// <summary>
    /// 本服务当前是否应该运行：功能开着、已加入集控、且已绑定班主任。
    /// </summary>
    private async ValueTask<bool> ShouldRunAsync(CancellationToken ct)
    {
        if (!_settings.IsEnabled) return false;

        // 绑定状态缓存 60 秒：这是一次 HTTP 请求，而本循环每秒跑一次。
        if (_cachedBound is not true || DateTime.Now - _lastBindingCheck > BindingCheckInterval)
        {
            _lastBindingCheck = DateTime.Now;
            _cachedBound = await _bindingGate.IsTeacherBoundAsync().ConfigureAwait(false);
            if (_cachedBound is not true)
            {
                _logger.LogInformation("人脸考勤未启用定时执行：设备尚未加入集控或未绑定班主任");
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("人脸考勤定时服务已启动");

        using var timer = new PeriodicTimer(PollInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    if (!await ShouldRunAsync(stoppingToken).ConfigureAwait(false))
                    {
                        continue;
                    }

                    if (TryConsumeTrigger(DateTime.Now, out var target, out var reason))
                    {
                        _logger.LogInformation("人脸考勤触发自动执行：{Reason}", reason);
                        await _attendance.RunCheckAsync(false, stoppingToken).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception e)
                {
                    // 定时任务里任何异常都不能让循环退出，否则当天的考勤就再也不会有机会执行了。
                    _logger.LogError(e, "人脸考勤定时执行出错");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }

        _logger.LogInformation("人脸考勤定时服务已停止");
    }

    /// <summary>
    /// 判断此刻是否应该执行考勤；如果应该，会把该时刻登记为「已执行」。
    /// <para>登记在返回 <c>true</c> 的同一刻完成，避免识别还没跑完时下一个轮询又触发一次。</para>
    /// </summary>
    /// <param name="now">当前时间。</param>
    /// <param name="target">命中的抓拍时刻。</param>
    /// <param name="reason">触发原因，用于日志。</param>
    public bool TryConsumeTrigger(DateTime now, out TimeSpan target, out string reason)
    {
        target = TimeSpan.Zero;
        reason = "";

        if (!_settings.IsEnabled) return false;

        var today = DateOnly.FromDateTime(now);
        PruneOldKeys(today);

        var schedule = _settings.WeekSchedule;
        var day = schedule.GetDay(now.DayOfWeek);
        if (day is not { Enabled: true }) return false;

        var times = day.ParseTimes();
        if (times.Count == 0) return false;

        var grace = TimeSpan.FromMinutes(Math.Max(1, _settings.GraceMinutes));
        foreach (var candidate in times)
        {
            if (_ranKeys.Contains(Key(today, candidate))) continue;

            var moment = now.Date.Add(candidate);
            if (now < moment) break;

            // 超出补执行窗口的时刻直接放弃，不再补做（避免中午启动时把早读那次也补上）。
            if (now - moment > grace) continue;

            _ranKeys.Add(Key(today, candidate));
            target = candidate;
            reason = $"已到抓拍时间 {FaceScheduleParser.Format(candidate)}（{day.DayName}）";
            return true;
        }

        return false;
    }

    private void PruneOldKeys(DateOnly today)
    {
        var prefix = today.ToString("yyyyMMdd");
        _ranKeys.RemoveWhere(k => !k.StartsWith(prefix, StringComparison.Ordinal));
    }

    private static string Key(DateOnly date, TimeSpan time) => date.ToString("yyyyMMdd") + "-" + FaceScheduleParser.Format(time);
}
