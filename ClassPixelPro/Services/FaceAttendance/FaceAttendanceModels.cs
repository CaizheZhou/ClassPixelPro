using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ClassPixelPro.Services.FaceAttendance;

/// <summary>
/// 考勤执行状态。
/// </summary>
public enum AttendanceState
{
    /// <summary>尚未执行。</summary>
    Idle,

    /// <summary>正在抓拍 / 识别。</summary>
    Running,

    /// <summary>已完成。</summary>
    Completed,

    /// <summary>执行失败。</summary>
    Failed,
}

/// <summary>
/// 迟到判定方式。
/// </summary>
public enum LateJudgeMode
{
    /// <summary>
    /// 教室抓拍·缺席判定：应在场却没被认出来的人算迟到。
    /// 适合早读、晚自习等「人已经在教室里」的场景。
    /// </summary>
    Absence,

    /// <summary>
    /// 门口抓拍·出现判定：被认出来的人算迟到。
    /// 适合校门口、楼道等「人还没进班」的场景。
    /// </summary>
    Presence,
}

/// <summary>
/// 一条迟到/到场记录。
/// </summary>
public sealed class LateEntry
{
    /// <summary>姓名。</summary>
    public string Name { get; set; } = "";

    /// <summary>班级名。</summary>
    public string ClassName { get; set; } = "";

    /// <summary>原因说明，例如「早读抓拍未检测到」。</summary>
    public string Reason { get; set; } = "";

    /// <summary>最高相似度。</summary>
    public double Score { get; set; }

    /// <summary>命中的照片张数。</summary>
    public int Hits { get; set; }
}

/// <summary>
/// 一次考勤的结果。
/// <para>
/// <b>只有这个对象里的内容允许离开本机</b>（上报集控/班主任端）：
/// 姓名、班级、统计数字。特征向量与人脸小图永远不在这里出现。
/// </para>
/// </summary>
public sealed class LateRecord
{
    /// <summary>抓拍时间（设备本地时间）。</summary>
    public DateTime CaptureTime { get; set; } = DateTime.Now;

    /// <summary>是否成功。</summary>
    public bool Success { get; set; }

    /// <summary>失败或提示信息。</summary>
    public string Message { get; set; } = "";

    /// <summary>本次使用的判定方式。</summary>
    public LateJudgeMode JudgeMode { get; set; } = LateJudgeMode.Absence;

    /// <summary>抓拍张数。</summary>
    public int PhotoCount { get; set; }

    /// <summary>检测到的人脸总数（所有照片累加）。</summary>
    public int FaceCount { get; set; }

    /// <summary>应到人数（已启用、未请假、且有人脸样本）。</summary>
    public int ExpectedCount { get; set; }

    /// <summary>识别到的人。</summary>
    public List<LateEntry> Recognized { get; set; } = new();

    /// <summary>迟到的人。</summary>
    public List<LateEntry> Late { get; set; } = new();

    /// <summary>耗时（秒）。</summary>
    public double ElapsedSeconds { get; set; }

    /// <summary>是否由用户手动触发（手动触发不受时间计划限制）。</summary>
    public bool Manual { get; set; }

    /// <summary>一句话摘要，直接显示在组件上。</summary>
    public string Summary
    {
        get
        {
            if (!Success) return $"{CaptureTime:MM-dd HH:mm:ss} 识别失败：{Message}";
            return Late.Count == 0
                ? $"{CaptureTime:MM-dd HH:mm:ss} 识别完成，没有迟到（识别到 {Recognized.Count} 人）"
                : $"{CaptureTime:MM-dd HH:mm:ss} 识别完成，迟到 {Late.Count} 人：{LateNamesText}";
        }
    }

    /// <summary>迟到者姓名，顿号连接。</summary>
    public string LateNamesText => string.Join("、", Late.Select(x => x.Name));
}

/// <summary>
/// 一天的抓拍时间计划。
/// </summary>
public sealed class DaySchedule
{
    /// <summary>星期几。</summary>
    public DayOfWeek Day { get; set; }

    /// <summary>这一天是否启用。</summary>
    public bool Enabled { get; set; }

    /// <summary>这一天的抓拍时刻，逗号分隔，支持 HH:mm 与 HH:mm:ss。</summary>
    public string Times { get; set; } = "";

    /// <summary>中文星期名，用于界面与日志。</summary>
    public string DayName => Day switch
    {
        DayOfWeek.Monday => "周一",
        DayOfWeek.Tuesday => "周二",
        DayOfWeek.Wednesday => "周三",
        DayOfWeek.Thursday => "周四",
        DayOfWeek.Friday => "周五",
        DayOfWeek.Saturday => "周六",
        _ => "周日",
    };

    /// <summary>
    /// 解析 <see cref="Times"/> 为时刻列表。非法项直接跳过，不让一处写错毁掉一整天。
    /// </summary>
    public List<TimeSpan> ParseTimes()
    {
        var result = new List<TimeSpan>();
        if (string.IsNullOrWhiteSpace(Times)) return result;

        foreach (var part in Times.Split(new[] { ',', '，', ';', '；', ' ', '\t' },
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (FaceScheduleParser.TryParseTime(part, out var t) && !result.Contains(t))
            {
                result.Add(t);
            }
        }

        result.Sort();
        return result;
    }
}

/// <summary>
/// 一周的时间计划。可来自本机设置，也可由集控组织统一下发。
/// </summary>
public sealed class WeekSchedule
{
    /// <summary>七天各自的计划。</summary>
    public List<DaySchedule> Days { get; set; } = new();

    /// <summary>
    /// 建一份默认的一周计划（默认全部关闭，时间留空）。
    /// </summary>
    public static WeekSchedule CreateDefault()
    {
        var schedule = new WeekSchedule();
        for (var i = 0; i < 7; i++)
        {
            schedule.Days.Add(new DaySchedule { Day = (DayOfWeek)i });
        }

        return schedule;
    }

    /// <summary>取某一天的计划；没有则返回 null。</summary>
    public DaySchedule? GetDay(DayOfWeek day) => Days.FirstOrDefault(x => x.Day == day);

    /// <summary>
    /// 用一句话描述整周计划，用于日志与界面提示。
    /// <para>
    /// 连着几天时间一样时会并成一段（「周一至周五 07:20:00」），
    /// 七天完全一样就直接写「每天」——否则一周七天各念一遍，一句话变成一屏。
    /// </para>
    /// </summary>
    public string Describe()
    {
        var map = Days.Where(x => x.Enabled && x.ParseTimes().Count > 0).ToDictionary(x => x.Day, x => x);
        if (map.Count == 0)
        {
            return "还没有任何一天设置了抓拍时间。";
        }

        var order = new[]
        {
            DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
            DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday,
        };

        var segments = new List<(int Start, int End, string Times)>();
        for (var i = 0; i < order.Length; i++)
        {
            if (!map.TryGetValue(order[i], out var item)) continue;

            var times = DescribeTimes(item.ParseTimes());
            var end = i;
            while (end + 1 < order.Length
                   && map.TryGetValue(order[end + 1], out var next)
                   && DescribeTimes(next.ParseTimes()) == times)
            {
                end++;
            }

            segments.Add((i, end, times));
            i = end;
        }

        if (segments.Count == 1 && segments[0].Start == 0 && segments[0].End == order.Length - 1)
        {
            return $"每天 {segments[0].Times}。";
        }

        var parts = segments.Select(s => s.Start == s.End
            ? $"{new DaySchedule { Day = order[s.Start] }.DayName} {s.Times}"
            : $"{new DaySchedule { Day = order[s.Start] }.DayName}至{new DaySchedule { Day = order[s.End] }.DayName} {s.Times}");

        return string.Join("；", parts) + "。";
    }

    /// <summary>
    /// 算出下一次执行的时间，用于设置页给出「下次将在……执行」的提示。
    /// </summary>
    public string DescribeNextRun(DateTime now)
    {
        for (var offset = 0; offset <= 7; offset++)
        {
            var day = now.Date.AddDays(offset);
            var item = GetDay(day.DayOfWeek);
            if (item == null || !item.Enabled) continue;

            foreach (var time in item.ParseTimes())
            {
                if (day.Add(time) > now)
                {
                    var prefix = offset switch
                    {
                        0 => "今天",
                        1 => "明天",
                        _ => item.DayName,
                    };
                    return $"下次执行：{prefix} {FaceScheduleParser.Format(time)}";
                }
            }
        }

        return "未来 7 天内没有安排执行。";
    }

    private static string DescribeTimes(IReadOnlyList<TimeSpan> times)
        => string.Join("/", times.Select(FaceScheduleParser.Format));
}

/// <summary>
/// 时间计划的时间解析与格式化。
/// </summary>
/// <remarks>
/// 抽成静态类而不是挂在 <see cref="WeekSchedule"/> 上，是因为集控下发的计划、
/// 设置页的手动输入、定时器的判定都要用它，三处都不该各写一份解析。
/// </remarks>
public static class FaceScheduleParser
{
    /// <summary>
    /// 解析 HH:mm / H:mm / HH:mm:ss / H:mm:ss 格式的时间。
    /// <para>秒是可选的；不写秒时按 0 秒处理，兼容旧版只填到分钟的配置。</para>
    /// </summary>
    public static bool TryParseTime(string? text, out TimeSpan result)
    {
        result = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var parts = text.Trim().Split(':');
        if (parts.Length is < 2 or > 3) return false;

        if (!int.TryParse(parts[0], out var hour) || !int.TryParse(parts[1], out var minute))
        {
            return false;
        }

        var second = 0;
        // 允许写成 "07:20:" 这种末尾留空的格式
        if (parts.Length == 3 && !string.IsNullOrWhiteSpace(parts[2])
            && !int.TryParse(parts[2], out second))
        {
            return false;
        }

        if (hour is < 0 or > 23 || minute is < 0 or > 59 || second is < 0 or > 59)
        {
            return false;
        }

        result = new TimeSpan(hour, minute, second);
        return true;
    }

    /// <summary>把时间格式化成 HH:mm:ss，用于日志和界面提示。</summary>
    public static string Format(TimeSpan value) => $"{value.Hours:00}:{value.Minutes:00}:{value.Seconds:00}";
}
