using System;
using System.IO;
using System.Net.Mime;
using System.Text;
using Avalonia.Data.Converters;
using Avalonia.Interactivity;
using Avalonia.Platform;
using ClassPixelPro.Core;
using ClassPixelPro.Core.Abstractions.Controls;
using ClassPixelPro.Core.Attributes;
using ClassPixelPro.Core.Enums.SettingsWindow;
using ClassPixelPro.Enums.AppUpdating;
using ClassPixelPro.Models.AppUpdating;
using ClassPixelPro.Services.AppUpdating;
using ClassPixelPro.Shared;
using ClassPixelPro.Shared.Enums;
using ClassPixelPro.ViewModels.SettingsPages;
using DynamicData.Binding;
using FluentAvalonia.UI.Controls;
using ReactiveUI;

namespace ClassPixelPro.Views.SettingPages;

[SettingsPageInfo("update", "更新", "\ue161", "\ue160", SettingsPageCategory.Internal)]
public partial class UpdateSettingsPage : SettingsPageBase
{
    private IDisposable? _updateSettingsObserver;
    private IDisposable? _newVersionChangeLogObserver;
    
    public UpdateSettingsPageViewModel ViewModel { get; } = IAppHost.GetService<UpdateSettingsPageViewModel>();

    public static readonly FuncValueConverter<UpdateStatus, string> UpdateStatusToIconGlyphConverter =
        new(x => x switch
        {
            UpdateStatus.UpToDate => "\ue1a1",
            UpdateStatus.UpdateAvailable => "\ue161",
            UpdateStatus.UpdateDownloaded => "\ue0d3",
            UpdateStatus.UpdateDeployed => "\ue163",
            _ => ""
        });
    
    public static readonly FuncValueConverter<UpdateStatus, string> UpdateStatusToMessageConverter =
        new(x => x switch
        {
            UpdateStatus.UpToDate => "您已更新到最新版本。",
            UpdateStatus.UpdateAvailable => "检测到更新。" ,
            UpdateStatus.UpdateDownloaded => "已准备好安装更新。",
            UpdateStatus.UpdateDeployed => "更新已就绪。",
            _ => ""
        });
    
    public static readonly FuncValueConverter<UpdateWorkingStatus, string> UpdateWorkingStatusToMessageConverter =
        new(x => x switch
        {
            UpdateWorkingStatus.Idle => "就绪",
            UpdateWorkingStatus.CheckingUpdates => "正在检查更新…",
            UpdateWorkingStatus.DownloadingUpdates => "正在下载更新…",
            UpdateWorkingStatus.ExtractingUpdates => "正在部署更新…",
            _ => "???"
        });

    public static readonly FuncValueConverter<DownloadState, string> DownloadStateToMessageConverter =
        new(x => x switch
        {
            DownloadState.Pending => "等待下载",
            DownloadState.Downloading => "正在下载",
            DownloadState.Completed => "完成",
            DownloadState.Error => "错误",
            _ => "???"
        });
    
    public UpdateSettingsPage()
    {
        DataContext = this;
        InitializeComponent();
    }
    
    private async void ButtonCheckUpdate_OnClick(object sender, RoutedEventArgs e)
    {
        await ViewModel.UpdateService.CheckUpdateAsync();
    }

    private async void ButtonDownloadUpdate_OnClick(object sender, RoutedEventArgs e)
    {
        await ViewModel.UpdateService.DownloadUpdateAsync();
        if (ViewModel.SettingsService.Settings.LastUpdateStatus == UpdateStatus.UpdateDownloaded)
        {
            await ViewModel.UpdateService.ExtractUpdateAsync();
        }
    }

    private void UpdateChannelInfo()
    {
        ViewModel.SelectedChannel =
            ViewModel.UpdateService.DistributionMetadata.Channels.TryGetValue(
                ViewModel.SettingsService.Settings.SelectedUpdateChannelV3, out var v1)
                ? v1
                : ViewModel.SelectedChannel;
    }

    private void UpdateNewVersionChangeLog()
    {
        ViewModel.NewVersionChangeLogDocument =
            ViewModel.SettingsService.Settings.LastUpdateStatus != UpdateStatus.UpToDate
                ? ViewModel.UpdateService.DistributionInfo.ChangeLog
                : "";
    }

    private void Control_OnLoaded(object? sender, RoutedEventArgs e)
    {
        UpdateChannelInfo();
        UpdateNewVersionChangeLog();
        _updateSettingsObserver ??= ViewModel.SettingsService.Settings
            .ObservableForProperty(x => x.SelectedUpdateChannelV3)
            .Subscribe(_ => UpdateChannelInfo());
        _newVersionChangeLogObserver ??= ViewModel.UpdateService
            .WhenAnyPropertyChanged()
            .Subscribe(_ => UpdateNewVersionChangeLog());
        
    }


    private void Control_OnUnloaded(object? sender, RoutedEventArgs e)
    {
        _updateSettingsObserver?.Dispose();
        _updateSettingsObserver = null;
        _newVersionChangeLogObserver?.Dispose();
        _newVersionChangeLogObserver = null;
    }

    private void ButtonOpenDownloadTasks_OnClick(object? sender, RoutedEventArgs e)
    {
        OpenDrawer("DownloadInfoDrawer");
    }

    private async void ButtonCancelDownload_OnClick(object? sender, RoutedEventArgs e)
    {
        await ViewModel.UpdateService.StopDownloading();
    }

    private async void ButtonDeployUpdate_OnClick(object? sender, RoutedEventArgs e)
    {
        await ViewModel.UpdateService.ExtractUpdateAsync();
    }

    private void InfoBarError_OnCloseButtonClick(InfoBar sender, EventArgs args)
    {
        ViewModel.UpdateService.NetworkErrorException = null;
    }
    
    private void InfoBarDeployError_OnCloseButtonClick(InfoBar sender, EventArgs args)
    {
        ViewModel.UpdateService.DeployErrorException = null;
    }

    private void ButtonRestart_OnClick(object? sender, RoutedEventArgs e)
    {
        AppBase.Current.Restart(["-m"], true);
    }

    private async void SettingsExpanderItemCheckUpdateForce_OnClick(object? sender, RoutedEventArgs e)
    {
        await ViewModel.UpdateService.CheckUpdateAsync(true);
    }

    private async void ButtonShowChangeLogs_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            // 从服务器获取所有版本的更新日志
            var changelogs = await ViewModel.UpdateService.GetChangeLogsAsync();

            if (changelogs.Count > 0)
            {
                // 合并为 Markdown 文档（每个版本一个标题 + changelog）
                var sb = new StringBuilder();
                foreach (var entry in changelogs)
                {
                    var versionTitle = string.IsNullOrEmpty(entry.FriendlyVersion)
                        ? entry.Version
                        : entry.FriendlyVersion;
                    sb.AppendLine($"# {versionTitle}");
                    sb.AppendLine();
                    if (!string.IsNullOrEmpty(entry.ChangeLog))
                    {
                        sb.AppendLine(entry.ChangeLog);
                    }
                    else
                    {
                        sb.AppendLine("（无更新日志）");
                    }
                    sb.AppendLine();
                    sb.AppendLine("---");
                    sb.AppendLine();
                }
                ViewModel.ChangeLogDocument = sb.ToString();
            }
            else
            {
                // 服务器返回空，回退到本地文件
                LoadLocalChangeLog();
            }
        }
        catch
        {
            // 网络请求失败，回退到本地文件
            LoadLocalChangeLog();
        }
        OpenDrawer("ChangeLogDrawer");
    }

    private void LoadLocalChangeLog()
    {
        using var sr =
            new StreamReader(AssetLoader.Open(new Uri("avares://ClassPixelPro/Assets/Documents/ChangeLog.md")));
        ViewModel.ChangeLogDocument = sr.ReadToEnd();
    }

    private async void MenuItemDownloadOnly_OnClick(object? sender, RoutedEventArgs e)
    {
        await ViewModel.UpdateService.DownloadUpdateAsync();
    }
}