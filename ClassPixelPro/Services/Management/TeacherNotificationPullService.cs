using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ClassPixelPro.Core.Abstractions.Services.Management;
using ClassPixelPro.Shared;
using ClassPixelPro.Shared.Models.Management;
using Microsoft.Extensions.Logging;

namespace ClassPixelPro.Services.Management;

/// <summary>
/// 拉取班主任端下发的通知并分发到 <see cref="NotificationProviders.TeacherNotificationProvider"/>。
/// 由 <see cref="ManagementService"/> 的集控轮询循环（每 5 秒）调用。
/// </summary>
public class TeacherNotificationPullService
{
    private ManagementService ManagementService { get; }
    private ILogger<TeacherNotificationPullService> Logger { get; }

    public TeacherNotificationPullService(ManagementService managementService,
        ILogger<TeacherNotificationPullService> logger)
    {
        ManagementService = managementService;
        Logger = logger;
    }

    public async Task PullAsync()
    {
        if (!ManagementService.IsManagementEnabled || ManagementService.Connection is not ServerlessConnection connection)
            return;

        var baseUrl = ManagementService.GetManagementBaseUrl();
        if (string.IsNullOrEmpty(baseUrl))
            return;

        var url = $"{baseUrl}/serverless/notification?cuid={ManagementService.Persist.ClientUniqueId}";
        var result = await connection.GetJsonAsync<TeacherNotificationList>(url);
        if (result?.Notifications is not { Count: > 0 } list)
            return;

        var provider = IAppHost.TryGetService<NotificationProviders.TeacherNotificationProvider>();
        if (provider == null)
        {
            Logger.LogWarning("TeacherNotificationProvider 未注册，丢弃 {} 条班主任通知", list.Count);
            return;
        }

        Logger.LogInformation("收到 {} 条班主任通知", list.Count);
        foreach (var notification in list)
        {
            try
            {
                await provider.HandleAsync(notification);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "处理班主任通知失败（id={}）", notification.Id);
            }
        }
    }

    private class TeacherNotificationList
    {
        public List<TeacherNotificationPayload>? Notifications { get; set; }
    }
}
