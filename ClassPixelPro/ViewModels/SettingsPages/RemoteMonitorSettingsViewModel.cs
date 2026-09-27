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

    /// <summary>
    /// cloudflared 下载进度（0-100）。当无法获取总大小时为 -1。
    /// </summary>
    [ObservableProperty] private double _cloudflaredDownloadProgress;

    /// <summary>
    /// 是否正在下载 cloudflared.exe。
    /// </summary>
    [ObservableProperty] private bool _isCloudflaredDownloading;

    /// <summary>
    /// cloudflared 是否已完全初始化（已下载且服务已就绪）。
    /// 下载中或未就绪时为 false，此时应隐藏功能设置项。
    /// </summary>
    [ObservableProperty] private bool _isFullyInitialized;

    /// <summary>
    /// 摄像头测试结果文本。
    /// </summary>
    [ObservableProperty] private string _cameraTestResult = "";

    /// <summary>
    /// 是否正在执行摄像头测试。
    /// </summary>
    [ObservableProperty] private bool _isTestingCamera;

    /// <summary>
    /// 截图测试结果文本。
    /// </summary>
    [ObservableProperty] private string _screenshotTestResult = "";

    /// <summary>

    /// 是否正在执行截图测试。
    /// </summary>
    [ObservableProperty] private bool _isTestingScreenshot;

    /// <summary>
    /// 是否正在初始化（后台加载摄像头列表等耗时操作）。
    /// 初始化完成前 UI 可据此显示加载状态，避免页面卡顿。
    /// </summary>
    [ObservableProperty] private bool _isInitializing;

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
        _cloudflaredManager.DownloadProgressChanged += OnDownloadProgressChanged;

        // 周期性刷新 cloudflared/tunnel 状态（cloudflared 下载与隧道解析均为异步）
        _statusRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _statusRefreshTimer.Tick += OnStatusRefreshTick;
    }

    /// <summary>
    /// 初始化 ViewModel 状态：刷新 cloudflared 就绪、摄像头列表、服务运行状态。
    /// 应在设置页 Loaded 时调用。摄像头枚举等耗时操作在后台线程执行，避免阻塞 UI。
    /// </summary>
    public async Task InitializeAsync()
    {
        IsInitializing = true;
        try
        {
            IsScreenCaptureSupported = _screenCaptureService.IsSupported;
            IsCameraCaptureSupported = _cameraCaptureService.IsSupported;
            RefreshStatus();
            IsMonitoring = _remoteMonitorService.IsRunning;

            // 摄像头枚举需要逐个尝试打开设备索引，耗时较长，放到后台线程执行
            await RefreshCamerasInternalAsync();
        }
        finally
        {
            IsInitializing = false;
        }
    }

    /// <summary>
    /// 同步初始化（兼容旧调用方）。耗时操作以 fire-and-forget 方式在后台执行。
    /// 推荐使用 <see cref="InitializeAsync"/>。
    /// </summary>
    public void Initialize()
    {
        IsScreenCaptureSupported = _screenCaptureService.IsSupported;
        IsCameraCaptureSupported = _cameraCaptureService.IsSupported;
        RefreshStatus();
        IsMonitoring = _remoteMonitorService.IsRunning;

        // 摄像头枚举后台执行，不阻塞 UI 线程
        _ = RefreshCamerasInternalAsync();
    }

    /// <summary>
    /// 停止状态轮询并解绑事件。应在设置页 Unloaded 时调用。
    /// </summary>
    public void Deinitialize()
    {
        _statusRefreshTimer.Stop();
        _cloudflaredManager.TunnelReady -= OnTunnelReady;
        _cloudflaredManager.TunnelLost -= OnTunnelLost;
        _cloudflaredManager.DownloadProgressChanged -= OnDownloadProgressChanged;
    }

    private void OnDownloadProgressChanged(double percent)
    {
        Dispatcher.UIThread.Post(() =>
        {
            CloudflaredDownloadProgress = percent;
            UpdateFullyInitialized();
        });
    }

    /// <summary>
    /// 根据下载状态和 cloudflared 就绪状态更新 IsFullyInitialized。
    /// </summary>
    private void UpdateFullyInitialized()
    {
        // 当 cloudflared 已就绪且不在下载中时，认为完全初始化
        // 在非 Windows 平台上 cloudflared 不可用，但服务仍可运行（仅局域网），此时也视为已初始化
        if (!OperatingSystem.IsWindows())
        {
            IsFullyInitialized = !IsCloudflaredDownloading;
        }
        else
        {
            IsFullyInitialized = !IsCloudflaredDownloading && _cloudflaredManager.IsExecutableReady;
        }
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
        UpdateFullyInitialized();
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
                // 启用前若 cloudflared 未就绪，标记为下载中
                if (OperatingSystem.IsWindows() && !_cloudflaredManager.IsExecutableReady)
                {
                    IsCloudflaredDownloading = true;
                    CloudflaredDownloadProgress = 0;
                }
                UpdateFullyInitialized();

                await _remoteMonitorService.StartAsync();

                // 启动完成后清除下载中状态
                IsCloudflaredDownloading = false;
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
                IsCloudflaredDownloading = false;
                _logger.LogInformation("远程监控已停止");
            }
            RefreshStatus();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "切换远程监控状态失败");
            // 回滚开关状态
            _settingsService.Settings.IsRemoteMonitorEnabled = !_settingsService.Settings.IsRemoteMonitorEnabled;
            IsCloudflaredDownloading = false;
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
    private async Task RefreshCamerasAsync()
    {
        await RefreshCamerasInternalAsync();
    }

    private async Task RefreshCamerasInternalAsync()
    {
        try
        {
            // 摄像头枚举需要逐个尝试打开设备索引 0-9，可能耗时数百毫秒，
            // 放到后台线程执行避免阻塞 UI 线程。
            var cameras = await Task.Run(() => _cameraCaptureService.GetCameras());
            var selectedIndex = _settingsService.Settings.RemoteMonitorSelectedCameraIndex;
            var selected = cameras.FirstOrDefault(c => c.Index == selectedIndex)
                          ?? cameras.FirstOrDefault(c => c.IsDefault)
                          ?? cameras.FirstOrDefault();
            // UI 属性更新需在主线程执行
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                AvailableCameras = cameras;
                SelectedCamera = selected;
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "刷新摄像头列表失败");
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                AvailableCameras = new List<CameraInfo>();
                SelectedCamera = null;
            });
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
    /// 测试摄像头功能：列出可用摄像头并尝试采集一帧，结果写入 <see cref="CameraTestResult"/>。
    /// </summary>
    [RelayCommand]
    private async Task TestCameraAsync()
    {
        IsTestingCamera = true;
        CameraTestResult = "正在测试…";
        try
        {
            await Task.Run(() =>
            {
                var cameras = _cameraCaptureService.GetCameras();
                if (cameras.Count == 0)
                {
                    CameraTestResult = "❌ 未检测到摄像头";
                    return;
                }
                var sb = new System.Text.StringBuilder();
                sb.Append($"✓ 检测到 {cameras.Count} 个摄像头：\n");
                foreach (var cam in cameras)
                {
                    sb.Append($"  - {cam.Name}（索引 {cam.Index}）\n");
                }
                // 尝试采集一帧
                var frame = _cameraCaptureService.CaptureFrameAsJpeg(70);
                if (frame.Length > 0)
                {
                    sb.Append($"✓ 采集成功，帧大小 {frame.Length} 字节");
                }
                else
                {
                    sb.Append("⚠ 采集失败，帧为空");
                }
                CameraTestResult = sb.ToString();
            });
        }
        catch (Exception ex)
        {
            CameraTestResult = $"❌ 测试失败：{ex.Message}";
        }
        finally
        {
            IsTestingCamera = false;
        }
    }

    /// <summary>
    /// 测试截屏功能：采集一帧并返回字节数，结果写入 <see cref="ScreenshotTestResult"/>。
    /// </summary>
    [RelayCommand]
    private async Task TestScreenshotAsync()
    {
        IsTestingScreenshot = true;
        ScreenshotTestResult = "正在测试…";
        try
        {
            await Task.Run(() =>
            {
                if (!_screenCaptureService.IsSupported)
                {
                    ScreenshotTestResult = "❌ 当前平台不支持截屏";
                    return;
                }
                var jpeg = _screenCaptureService.CaptureScreenAsJpeg(80);
                if (jpeg.Length > 0)
                {
                    ScreenshotTestResult = $"✓ 截图成功，大小 {jpeg.Length / 1024} KB";
                    _logger.LogInformation("截屏测试成功，JPEG 大小 {Size} 字节", jpeg.Length);
                }
                else
                {
                    ScreenshotTestResult = "❌ 截图失败，数据为空";
                }
            });
        }
        catch (Exception ex)
        {
            ScreenshotTestResult = $"❌ 测试失败：{ex.Message}";
            _logger.LogError(ex, "截屏测试失败");
        }
        finally
        {
            IsTestingScreenshot = false;
        }
    }
}