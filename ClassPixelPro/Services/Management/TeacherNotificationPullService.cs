using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ClassPixelPro.Core.Abstractions.Services.Management;
using ClassPixelPro.Shared;
using ClassPixelPro.Shared.Models.Management;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClassPixelPro.Services.Management;

/// <summary>
/// 拉取组织管理员/班主任下发的通知并分发：
/// <list type="bullet">
/// <item>scope=org（管理员群发）→ <see cref="NotificationProviders.ManagementNotificationProvider"/>（集控提醒）；</item>
/// <item>scope=device（班主任单台发送）→ <see cref="NotificationProviders.TeacherNotificationProvider"/>（班主任通知）。</item>
/// </list>
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

        // GetManagementBaseUrl() 只返回源站（不含 /api/mgmt/{orgId} 路径），
        // serverless 接口的组织前缀从 ManifestUrlTemplate 中截取，
        // 与 manifest 请求走同一组织，避免两处组织不一致。
        var template = ManagementService.Settings.ManifestUrlTemplate;
        var idx = template?.IndexOf("/serverless/", StringComparison.Ordinal) ?? -1;
        if (idx <= 0)
            return;

        var url = $"{template!.Substring(0, idx)}/serverless/notification?cuid={ManagementService.Persist.ClientUniqueId}";
        var result = await connection.GetJsonAsync<TeacherNotificationList>(url);
        if (result?.Notifications is not { Count: > 0 } list)
            return;

        // Provider 以 AddHostedService 方式注册，不能用 GetService<T> 直接解析，
        // 从 IHostedService 实例列表中按类型查找（即宿主实际运行的实例）
        var hostedServices = IAppHost.Host?.Services.GetServices<IHostedService>().ToList()
                             ?? new List<IHostedService>();
        var teacherProvider = hostedServices
            .OfType<NotificationProviders.TeacherNotificationProvider>().FirstOrDefault();
        // 组织管理员群发的通知（scope=org）以「组织通知」展示，与 gRPC 集控指令同一个提供方
        var managementProvider = hostedServices
            .OfType<NotificationProviders.ManagementNotificationProvider>().FirstOrDefault();

        Logger.LogInformation("收到 {} 条通知", list.Count);
        foreach (var notification in list)
        {
            try
            {
                if (notification.Scope == "org")
                {
                    if (managementProvider == null)
                    {
                        Logger.LogWarning("ManagementNotificationProvider 未注册，丢弃组织通知（id={}）", notification.Id);
                        continue;
                    }
                    await managementProvider.HandleAsync(notification);
                }
                else
                {
                    if (teacherProvider == null)
                    {
                        Logger.LogWarning("TeacherNotificationProvider 未注册，丢弃班主任通知（id={}）", notification.Id);
                        continue;
                    }
                    await teacherProvider.HandleAsync(notification);
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "处理通知失败（id={}）", notification.Id);
            }
        }
    }

    private class TeacherNotificationList
    {
        public List<TeacherNotificationPayload>? Notifications { get; set; }
    }
}
