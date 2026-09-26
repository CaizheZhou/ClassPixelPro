using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ClassPixelPro.Core.Abstractions.Controls;
using ClassPixelPro.Core.Attributes;
using ClassPixelPro.Core.Enums.SettingsWindow;
using ClassPixelPro.Core.Helpers.UI;
using ClassPixelPro.Services;
using ClassPixelPro.Shared;
using ClassPixelPro.ViewModels.SettingsPages;

namespace ClassPixelPro.Views.SettingPages;

/// <summary>
/// RemoteMonitorSettingsPage.xaml 的交互逻辑
/// </summary>
[SettingsPageInfo("remote-monitor", "远程监控", "\ue8b6", "\ue8b6", SettingsPageCategory.Internal)]
public partial class RemoteMonitorSettingsPage : SettingsPageBase
{
    public RemoteMonitorSettingsViewModel ViewModel { get; } = IAppHost.GetService<RemoteMonitorSettingsViewModel>();

    public RemoteMonitorSettingsPage()
    {
        InitializeComponent();
        DataContext = this;
    }

    private void RemoteMonitorSettingsPage_OnLoaded(object sender, RoutedEventArgs e)
    {
        ViewModel.Initialize();
    }

    private void RemoteMonitorSettingsPage_OnUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.Deinitialize();
    }

    /// <summary>
    /// 监控开关切换时调用 ViewModel 命令启动/停止服务。
    /// 开关状态已双向绑定到 Settings，此处仅在用户操作时触发服务启停。
    /// </summary>
    private async void ToggleRemoteMonitor_OnIsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        await ViewModel.ToggleRemoteMonitorCommand.ExecuteAsync(null);
    }

    private async void ButtonCopyTunnelUrl_OnClick(object sender, RoutedEventArgs e)
    {
        var url = ViewModel.TunnelUrl;
        if (string.IsNullOrEmpty(url))
        {
            this.ShowWarningToast("隧道 URL 尚未就绪");
            return;
        }
        await SetClipboardTextAsync(url);
        this.ShowSuccessToast("已复制隧道 URL");
    }

    private async void ButtonCopyToken_OnClick(object sender, RoutedEventArgs e)
    {
        var token = ViewModel.RemoteMonitorService.Token;
        await SetClipboardTextAsync(token);
        this.ShowSuccessToast("已复制访问 Token");
    }

    private void ButtonRefreshCameras_OnClick(object sender, RoutedEventArgs e)
    {
        ViewModel.RefreshCamerasCommand.Execute(null);
        this.ShowToast($"已刷新摄像头列表（{ViewModel.AvailableCameras.Count} 个设备）");
    }

    private async void ButtonTestScreenshot_OnClick(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.IsScreenCaptureSupported)
        {
            this.ShowWarningToast("当前平台不支持截屏");
            return;
        }
        this.ShowToast("正在测试截屏…");
        await ViewModel.TestScreenshotCommand.ExecuteAsync(null);
        if (!ViewModel.IsBusy)
        {
            this.ShowSuccessToast("截屏测试完成，请查看日志确认结果");
        }
    }

    private async System.Threading.Tasks.Task SetClipboardTextAsync(string text)
    {
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard != null)
            {
                await clipboard.SetTextAsync(text);
            }
        }
        catch (Exception)
        {
            this.ShowErrorToast("复制失败，请手动复制");
        }
    }
}