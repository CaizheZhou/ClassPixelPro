﻿using System;
using System.Threading;
using System.Threading.Tasks;

using ClassPixelPro.Services.RemoteMonitor;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClassPixelPro.Services;

public class BootService : IHostedService
{
    private MainWindow MainWindow { get; }
    private readonly RemoteMonitorService _remoteMonitorService;
    private readonly SettingsService _settingsService;
    private readonly ILogger<BootService> _logger;

    public BootService(
        MainWindow mainWindow,
        MiniInfoProviderHostService miniInfoProviderHostService,
        RemoteMonitorService remoteMonitorService,
        SettingsService settingsService,
        ILogger<BootService> logger)
    {
        MainWindow = mainWindow;
        _remoteMonitorService = remoteMonitorService;
        _settingsService = settingsService;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        MainWindow.Show();

        // 若用户已开启远程监控，重启后自动恢复监控服务。
        // 启动监控是异步耗时操作（可能需要下载 cloudflared），fire-and-forget 避免阻塞 BootService.StartAsync。
        if (_settingsService.Settings.IsRemoteMonitorEnabled)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await _remoteMonitorService.StartAsync();
                    _logger.LogInformation("远程监控已随启动自动恢复");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "自动启动远程监控失败");
                }
            });
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _remoteMonitorService.StopAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "停止远程监控失败");
        }
    }
}
