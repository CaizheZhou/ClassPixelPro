using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using ClassPixelPro.Services;
using ClassPixelPro.Services.RemoteMonitor;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace ClassPixelPro.ViewModels.SettingsPages;

/// <summary>
/// 远程监控设置页 ViewModel。负责将 <see cref="RemoteMonitorService"/> 的状态
/// 暴露给设置页 UI，并在用户操作时调用服务的启动/停止/摄像头选择等接口。
/// </summary>
/// <remarks>
/// 监控启用开关（<c>IsRemoteMonitorEnabled</c>）和指示器开关（<c>IsMonitorIndicatorEnabled</c>）
/// 直接双向绑定到 <see cref="Settings"/>，由 <see cref="SettingsService"/> 自动持久化。
/// 本 ViewModel 仅负责派生状态（如 cloudflared 就绪、tunnel URL、摄像头列表）和命令。
/// </remarks>
public partial class RemoteMonitorSettingsViewModel : ObservableRecipient
{
    private readonly SettingsService _settingsService;
    private readonly RemoteMonitorService _remoteMonitorService;
    private readonly CameraCaptureService _cameraCaptureService;
    private readonly CloudflaredManager _cloudflaredManager;
    private readonly ScreenCaptureService _screenCaptureService;
    private readonly ILogger<RemoteMonitorSettingsViewModel> _logger;
    private readonly DispatcherTimer _statusRefreshTimer;

    public SettingsService SettingsService => _settingsService;
    public RemoteMonitorService RemoteMonitorService => _remoteMonitorService;
    public CameraCaptureService CameraCaptureService => _cameraCaptureService;
    public CloudflaredManager CloudflaredManager => _cloudflaredManager;
    public ScreenCaptureService ScreenCaptureService => _screenCaptureService;

    /// <summary>
    /// cloudflared 是否已下载就绪。
    /// </summary>
    [ObservableProperty] private bool _isCloudflaredReady;

    /// <summary>
    /// cloudflared 状态文本（已就绪/未安装/下载中…）。
    /// </summary>
    [ObservableProperty] private string _cloudflaredStatus = "未安装";

    /// <summary>
    /// 当前隧道 URL。服务未启动或 cloudflared 尚未就绪时为空。
    /// </summary>
    [ObservableProperty] private string _tunnelUrl = "";

    /// <summary>
    /// 可用摄像头列表。
    /// </summary>
    [ObservableProperty] private List<CameraInfo> _availableCameras = new();

    /// <summary>
    /// 当前选中的摄像头。与 <see cref="Settings.RemoteMonitorSelectedCameraIndex"/> 同步。
    /// </summary>
    [ObservableProperty] private CameraInfo? _selectedCamera;

    /// <summary>
    /// 远程监控服务是否正在运行。
    /// </summary>
    [ObservableProperty] private bool _isMonitoring;

    /// <summary>
    /// 是否正在执行启动/停止/下载等异步操作。
    /// </summary>
    [ObservableProperty] private bool _isBusy;

    /// <summary>
    /// 截屏功能在当前平台是否可用。
    /// </summary>
    [ObservableProperty] private bool _isScreenCaptureSupported;

    /// <summary>
    /// 摄像头功能在当前平台是否可用。
    /// </summary>
    [ObservableProperty] private bool _isCameraCaptureSupported;

    public RemoteMonitorSettingsViewModel(
        SettingsService settingsService,
        RemoteMonitorService remoteMonitorService,
        CameraCaptureService cameraCaptureService,
        CloudflaredManager cloudflaredManager,
        ScreenCaptureService screenCaptureService,
        ILogger<RemoteMonitorSettingsViewModel> logger)
    {
        _settingsService = settingsService;
        _remoteMonitorService = remoteMonitorService;
        _cameraCaptureService = cameraCaptureService;
        _cloudflaredManager = cloudflaredManager;
        _screenCaptureService = screenCaptureService;
        _logger = logger;

        _cloudflaredManager.TunnelReady += OnTunnelReady;
        _cloudflaredManager.TunnelLost += OnTunnelLost;

        // 周期性刷新 cloudflared/tunnel 状态（cloudflared 下载与隧道解析均为异步）
        _statusRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _statusRefreshTimer.Tick += OnStatusRefreshTick;
    }

    /// <summary>
    /// 初始化 ViewModel 状态：刷新 cloudflared 就绪、摄像头列表、服务运行状态。
    /// 应在设置页 Loaded 时调用。
    /// </summary>
    public void Initialize()
    {
        IsScreenCaptureSupported = _screenCaptureService.IsSupported;
        IsCameraCaptureSupported = _cameraCaptureService.IsSupported;
        RefreshStatus();
        RefreshCamerasInternal();
        IsMonitoring = _remoteMonitorService.IsRunning;
    }

    /// <summary>
    /// 停止状态轮询并解绑事件。应在设置页 Unloaded 时调用。
    /// </summary>
    public void Deinitialize()
    {
        _statusRefreshTimer.Stop();
        _cloudflaredManager.TunnelReady -= OnTunnelReady;
        _cloudflaredManager.TunnelLost -= OnTunnelLost;
    }

    private void OnTunnelReady(string url)
    {
        Dispatcher.UIThread.Post(() =>
        {
            TunnelUrl = url;
            RefreshStatus();
        });
    }

    private void OnTunnelLost()
    {
        Dispatcher.UIThread.Post(() =>
        {
            TunnelUrl = "";
            RefreshStatus();
        });
    }

    private void OnStatusRefreshTick(object? sender, EventArgs e)
    {
        RefreshStatus();
    }

    /// <summary>
    /// 刷新 cloudflared 就绪状态与隧道 URL。
    /// </summary>
    private void RefreshStatus()
    {
        IsCloudflaredReady = _cloudflaredManager.IsExecutableReady;
        CloudflaredStatus = _cloudflaredManager.IsExecutableReady ? "已就绪" : "未安装";
        var url = _remoteMonitorService.TunnelUrl;
        TunnelUrl = string.IsNullOrEmpty(url) ? TunnelUrl : url;
        if (!string.IsNullOrEmpty(TunnelUrl) && string.IsNullOrEmpty(_remoteMonitorService.TunnelUrl))
        {
            // 服务侧已清空，本地同步清空
            TunnelUrl = "";
        }
        IsMonitoring = _remoteMonitorService.IsRunning;
    }

    /// <summary>
    /// 启用或关闭远程监控。启用时启动 HTTP 服务器与 cloudflared 隧道，
    /// 关闭时停止服务并清空隧道 URL。
    /// </summary>
    [RelayCommand]
    private async Task ToggleRemoteMonitorAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            if (_settingsService.Settings.IsRemoteMonitorEnabled)
            {
                await _remoteMonitorService.StartAsync();
                _statusRefreshTimer.Start();
                IsMonitoring = true;
                _logger.LogInformation("远程监控已启动");
            }
            else
            {
                await _remoteMonitorService.StopAsync();
                _statusRefreshTimer.Stop();
                IsMonitoring = false;
                TunnelUrl = "";
                _logger.LogInformation("远程监控已停止");
            }
            RefreshStatus();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "切换远程监控状态失败");
            // 回滚开关状态
            _settingsService.Settings.IsRemoteMonitorEnabled = !_settingsService.Settings.IsRemoteMonitorEnabled;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 刷新可用摄像头列表。仅在 Windows 平台有效。
    /// </summary>
    [RelayCommand]
    private void RefreshCameras()
    {
        RefreshCamerasInternal();
    }

    private void RefreshCamerasInternal()
    {
        try
        {
            AvailableCameras = _cameraCaptureService.GetCameras();
            var selectedIndex = _settingsService.Settings.RemoteMonitorSelectedCameraIndex;
            SelectedCamera = AvailableCameras.FirstOrDefault(c => c.Index == selectedIndex)
                             ?? AvailableCameras.FirstOrDefault(c => c.IsDefault)
                             ?? AvailableCameras.FirstOrDefault();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "刷新摄像头列表失败");
            AvailableCameras = new List<CameraInfo>();
            SelectedCamera = null;
        }
    }

    /// <summary>
    /// 选中摄像头变更时同步到 <see cref="CameraCaptureService"/> 与设置。
    /// </summary>
    partial void OnSelectedCameraChanged(CameraInfo? value)
    {
        if (value == null) return;
        _settingsService.Settings.RemoteMonitorSelectedCameraIndex = value.Index;
        try
        {
            _cameraCaptureService.SelectCamera(value.Index.ToString());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "切换摄像头失败");
        }
    }

    /// <summary>
    /// 测试截屏功能：采集一帧并返回字节数，用于在 UI 上反馈截屏是否可用。
    /// </summary>
    [RelayCommand]
    private async Task TestScreenshotAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            await Task.Run(() =>
            {
                if (!_screenCaptureService.IsSupported)
                {
                    _logger.LogWarning("当前平台不支持截屏");
                    return;
                }
                var jpeg = _screenCaptureService.CaptureScreenAsJpeg(80);
                _logger.LogInformation("截屏测试成功，JPEG 大小 {Size} 字节", jpeg.Length);
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "截屏测试失败");
        }
        finally
        {
            IsBusy = false;
        }
    }
}