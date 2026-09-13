using System;
using System.Collections.Generic;
using System.IO;
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
}

/// <summary>
/// 匿名使用情况分析上报服务，由【设置 - 通用 - 隐私】中的开关控制。
/// 事件上报到自有服务端的分析接口，内容为版本、系统与事件名，不含个人身份信息。
/// 上报为尽力而为：任何网络异常都只记录日志，绝不影响应用运行。
/// </summary>
public class UsageStatisticsService
{
    private const string CollectUrl = "https://class.imcz.cn/api/analytics/collect";
    private const int MaxStackLength = 8000;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly string PersistPath =
        Path.Combine(CommonDirectories.AppConfigPath, "UsageStatistics.json");

    private readonly ILogger<UsageStatisticsService> _logger;
    private readonly SettingsService _settingsService;
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(8) };
    private readonly object _persistLock = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    private UsageStatisticsPersist? _persist;

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
                    ConfigureFileHelper.SaveConfig(PersistPath, persist);
                }

                return _persist = persist;
            }
        }
    }

    /// <summary>
    /// 应用启动后调用：上报一次启动事件，并每天上报一次活跃。
    /// </summary>
    public void ReportStart()
    {
        Report("app_start", new Dictionary<string, object?>
        {
            ["subChannel"] = AppBase.Current.AppSubChannel,
            ["isDevelopmentBuild"] = AppBase.Current.IsDevelopmentBuild
        });

        var persist = Persist;
        if (persist.LastDailyReportUtc.Date >= DateTime.UtcNow.Date)
        {
            return;
        }

        persist.LastDailyReportUtc = DateTime.UtcNow;
        lock (_persistLock)
        {
            ConfigureFileHelper.SaveConfig(PersistPath, persist);
        }

        Report("app_daily_active");
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
    /// 上报一个自定义事件。上报失败只会留下日志。
    /// </summary>
    public void Report(string eventName, object? data = null)
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(eventName))
        {
            return;
        }

        // 不阻塞调用方（崩溃路径也走这里），请求在后台发出，失败只记日志。
        _ = SendAsync(eventName, data);
    }

    private async Task SendAsync(string eventName, object? data)
    {
        try
        {
            await _sendGate.WaitAsync().ConfigureAwait(false);
        }
        catch
        {
            return;
        }

        try
        {
            var payload = new Dictionary<string, object?>
            {
                ["cuid"] = Persist.ClientId,
                ["appVersion"] = AppBase.AppVersion,
                ["os"] = AppBase.Current.OperatingSystem,
                ["arch"] = AppBase.Current.Platform,
                ["events"] = new[] { new Dictionary<string, object?> { ["name"] = eventName, ["data"] = data } }
            };

            using var response = await _httpClient
                .PostAsJsonAsync(CollectUrl, payload, JsonOptions)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("使用情况分析上报失败：HTTP {StatusCode}", (int)response.StatusCode);
            }
        }
        catch (Exception e)
        {
            _logger.LogDebug(e, "使用情况分析上报失败");
        }
        finally
        {
            _sendGate.Release();
        }
    }
}
