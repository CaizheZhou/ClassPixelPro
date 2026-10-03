using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClassPixelPro.Core;
using ClassPixelPro.Shared;
using ClassPixelPro.Shared.Helpers;
using Microsoft.Extensions.Logging;

namespace ClassPixelPro.Services;

/// <summary>
/// 匿名使用情况分析持久化数据（仅保存在本机）。
/// </summary>
public class UsageStatisticsPersist
{
    /// <summary>
    /// 匿名安装标识，首次运行时随机生成，不含任何硬件或账号信息。
    /// </summary>
    public string ClientId { get; set; } = "";

    /// <summary>
    /// 上次上报日活的时间（UTC），用于控制每天最多上报一次。
    /// </summary>
    public DateTime LastDailyReportUtc { get; set; } = DateTime.MinValue;

    /// <summary>
    /// 本次会话的开始时间（UTC），用于计算会话时长。
    /// </summary>
    public DateTime SessionStartUtc { get; set; } = DateTime.MinValue;

    /// <summary>
    /// 未成功上报的事件队列。
    ///
    /// 必须先落盘再发送：崩溃和退出这两条路径上进程随时可能终止，
    /// 直接发出去的话请求几乎必然被掐断，线上「崩溃数」严重偏低。
    /// 剩下的事件留到下次启动补发。
    /// </summary>
    public List<PendingEvent> Pending { get; set; } = [];
}

/// <summary>
/// 排队中的一条事件。
/// </summary>
public class PendingEvent
{
    public string Name { get; set; } = "";
    public object? Data { get; set; }
    public int Attempts { get; set; }
}

/// <summary>
/// 匿名使用情况分析上报服务，由【设置 - 通用 - 隐私】中的开关控制。
/// 事件上报到自有服务端的分析接口，内容为版本、系统与事件名，不含个人身份信息。
/// 上报为尽力而为：任何网络异常都只记录日志，绝不影响应用运行。
/// </summary>
public class UsageStatisticsService : IAsyncDisposable
{
    private const string CollectUrl = "https://class.imzcz.cn/api/analytics/collect";
    private const int MaxStackLength = 8000;
    private const int MaxPending = 200;
    private const int MaxAttempts = 5;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly string PersistPath =
        Path.Combine(CommonDirectories.AppConfigPath, "UsageStatistics.json");

    private readonly ILogger<UsageStatisticsService> _logger;
    private readonly SettingsService _settingsService;
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(8) };
    private readonly object _persistLock = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    private UsageStatisticsPersist? _persist;
    private DateTime _lastFlushAttemptUtc = DateTime.MinValue;
    private bool _disposed;

    public UsageStatisticsService(SettingsService settingsService, ILogger<UsageStatisticsService> logger)
    {
        _settingsService = settingsService;
        _logger = logger;
    }

    /// <summary>
    /// 用户是否允许上报匿名使用情况分析。
    /// </summary>
    public bool IsEnabled
    {
        get
        {
            try
            {
                return _settingsService.Settings.IsSentryEnabled;
            }
            catch
            {
                return false;
            }
        }
    }

    private UsageStatisticsPersist Persist
    {
        get
        {
            lock (_persistLock)
            {
                if (_persist != null)
                {
                    return _persist;
                }

                var persist = ConfigureFileHelper.LoadConfig<UsageStatisticsPersist>(PersistPath);
                if (string.IsNullOrWhiteSpace(persist.ClientId))
                {
                    persist.ClientId = Guid.NewGuid().ToString();
                }

                persist.Pending ??= [];
                return _persist = persist;
            }
        }
    }

    /// <summary>
    /// 保存本地状态。调用方必须已持有 _persistLock。
    /// </summary>
    private void SavePersistLocked()
    {
        try
        {
            ConfigureFileHelper.SaveConfig(PersistPath, Persist);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "使用情况分析：保存本地队列失败");
        }
    }

    /// <summary>
    /// 应用启动后调用：补发上次残留的事件、上报启动事件、每天上报一次活跃。
    /// </summary>
    public void ReportStart()
    {
        bool needDaily;
        int pendingFromLastRun;
        lock (_persistLock)
        {
            if (Persist.SessionStartUtc == DateTime.MinValue)
            {
                Persist.SessionStartUtc = DateTime.UtcNow;
            }

            needDaily = Persist.LastDailyReportUtc.Date < DateTime.UtcNow.Date;
            if (needDaily)
            {
                Persist.LastDailyReportUtc = DateTime.UtcNow;
            }

            pendingFromLastRun = Persist.Pending.Count;
            SavePersistLocked();
        }

        // 先把上次残留的发出去，避免队列越积越多
        FlushPending();

        Report("app_start", new Dictionary<string, object?>
        {
            ["subChannel"] = AppBase.Current.AppSubChannel,
            ["packagingType"] = AppBase.Current.PackagingType,
            ["isDevelopmentBuild"] = AppBase.Current.IsDevelopmentBuild,
            ["pendingFromLastRun"] = pendingFromLastRun
        });

        if (needDaily)
        {
            Report("app_daily_active");
        }
    }

    /// <summary>
    /// 应用退出前调用：上报会话结束事件，并尽力把队列发完。
    /// </summary>
    public void ReportExit(string reason = "normal")
    {
        if (!IsEnabled)
        {
            return;
        }

        double durationSec;
        lock (_persistLock)
        {
            durationSec = Persist.SessionStartUtc == DateTime.MinValue
                ? 0
                : Math.Round((DateTime.UtcNow - Persist.SessionStartUtc).TotalSeconds, 1);
            Persist.SessionStartUtc = DateTime.MinValue;
            SavePersistLocked();
        }

        Report("app_exit", new Dictionary<string, object?>
        {
            ["reason"] = reason,
            ["sessionSeconds"] = durationSec
        });

        // 同步等一小会儿尽量发完；剩下的留到下次启动补发
        FlushPending(TimeSpan.FromSeconds(3));
    }

    /// <summary>
    /// 上报一次崩溃，供排查线上问题使用。
    /// </summary>
    public void ReportCrash(Exception exception, bool isFatal = true, object? extra = null)
    {
        var stack = exception.ToString();
        if (stack.Length > MaxStackLength)
        {
            stack = stack[..MaxStackLength] + "...(已截断)";
        }

        var data = new Dictionary<string, object?>
        {
            ["fatal"] = isFatal,
            ["type"] = exception.GetType().FullName,
            ["message"] = exception.Message,
            ["stack"] = stack
        };
        if (extra != null)
        {
            data["extra"] = extra;
        }

        Report("crash", data);
    }

    /// <summary>
    /// 上报一个自定义事件。事件先入队落盘，再后台发送；失败保留在队列里下次补发。
    /// </summary>
    public void Report(string eventName, object? data = null)
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(eventName))
        {
            return;
        }

        try
        {
            lock (_persistLock)
            {
                Persist.Pending.Add(new PendingEvent { Name = eventName, Data = data });
                if (Persist.Pending.Count > MaxPending)
                {
                    // 队列爆了就丢最旧的，避免配置文件无限膨胀
                    Persist.Pending.RemoveRange(0, Persist.Pending.Count - MaxPending);
                }

                SavePersistLocked();
            }
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "使用情况分析：事件入队失败");
        }

        _ = FlushPending();
    }

    /// <summary>
    /// 把队列里的事件发出去。成功才出队，失败按次数退避重试，重试用尽则丢弃。
    /// </summary>
    public async Task FlushPending(TimeSpan? maxWait = null)
    {
        if (!IsEnabled || _disposed)
        {
            return;
        }

        // 崩溃路径可能一次性入队多条，短时间内不再重复触发发送
        if ((DateTime.UtcNow - _lastFlushAttemptUtc).TotalSeconds < 2)
        {
            return;
        }

        _lastFlushAttemptUtc = DateTime.UtcNow;

        try
        {
            await _sendGate.WaitAsync().ConfigureAwait(false);
        }
        catch
        {
            return;
        }

        var deadline = maxWait.HasValue ? DateTime.UtcNow + maxWait.Value : DateTime.MaxValue;

        try
        {
            while (true)
            {
                List<PendingEvent> batch;
                lock (_persistLock)
                {
                    if (Persist.Pending.Count == 0)
                    {
                        return;
                    }

                    batch = Persist.Pending.Take(20).ToList();
                }

                if (!await SendBatchAsync(batch).ConfigureAwait(false))
                {
                    lock (_persistLock)
                    {
                        foreach (var e in batch)
                        {
                            e.Attempts++;
                        }

                        Persist.Pending.RemoveAll(x => x.Attempts >= MaxAttempts);
                        SavePersistLocked();
                    }

                    return;
                }

                lock (_persistLock)
                {
                    Persist.Pending.RemoveRange(0, Math.Min(batch.Count, Persist.Pending.Count));
                    SavePersistLocked();
                }

                if (DateTime.UtcNow >= deadline)
                {
                    return;
                }
            }
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private async Task<bool> SendBatchAsync(List<PendingEvent> batch)
    {
        try
        {
            var payload = new Dictionary<string, object?>
            {
                ["cuid"] = Persist.ClientId,
                ["appVersion"] = AppBase.AppVersion,
                ["os"] = AppBase.Current.OperatingSystem,
                ["arch"] = AppBase.Current.Platform,
                ["events"] = batch.Select(e => new Dictionary<string, object?>
                {
                    ["name"] = e.Name,
                    ["data"] = e.Data
                }).ToArray()
            };

            using var response = await _httpClient
                .PostAsJsonAsync(CollectUrl, payload, JsonOptions)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // 失败必须留可见日志：原来用的是 LogDebug，正式版里根本看不到，
                // 以至于上报断了整整一个多月都没人发现。
                _logger.LogWarning("使用情况分析上报失败：HTTP {StatusCode}（已保留 {Count} 条待重试）",
                    (int)response.StatusCode, batch.Count);
                return false;
            }

            return true;
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "使用情况分析上报失败（已保留 {Count} 条待重试）", batch.Count);
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        try
        {
            await FlushPending(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch
        {
            // 忽略
        }
    }
}
