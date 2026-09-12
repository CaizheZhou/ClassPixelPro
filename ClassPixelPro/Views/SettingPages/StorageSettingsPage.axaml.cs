using System;
using System.Diagnostics;
using System.Windows;
using Avalonia.Interactivity;
using ClassPixelPro.Core;
using ClassPixelPro.Core.Abstractions.Controls;
using ClassPixelPro.Core.Abstractions.Services.Management;
using ClassPixelPro.Core.Attributes;
using ClassPixelPro.Core.Enums.SettingsWindow;
using ClassPixelPro.Core.Helpers.UI;
using ClassPixelPro.Models;
using ClassPixelPro.Services;
using ClassPixelPro.Shared;
using ClassPixelPro.ViewModels.SettingsPages;
using Microsoft.Extensions.Logging;
using Path = System.IO.Path;

namespace ClassPixelPro.Views.SettingPages;

/// <summary>
/// StorageSettingsPage.xaml 的交互逻辑
/// </summary>
[Group("classpixelpro.general")]
[SettingsPageInfo("storage", "存储", "\ue6b7", "\ue6b6", SettingsPageCategory.Internal)]
public partial class StorageSettingsPage : SettingsPageBase
{
    public StorageSettingsViewModel ViewModel { get; } = IAppHost.GetService<StorageSettingsViewModel>();

    public ILogger<StorageSettingsPage> Logger => ViewModel.Logger;

    public StorageSettingsPage()
    {
        ViewModel.SettingsService.Settings.BackupFilesSize = Helpers.StorageSizeHelper.FormatSize(Helpers.StorageSizeHelper.GetFolderStorageSize(Path.Combine(CommonDirectories.AppRootFolderPath, "Backups/")));
        DataContext = this;
        InitializeComponent();
    }

    private async void ButtonCreateBackup_OnClick(object sender, RoutedEventArgs e)
    {
        ViewModel.IsBackupFinished = false;
        ViewModel.IsBackingUp = true;
        try
        {
            await FileFolderService.CreateBackupAsync();
            ViewModel.IsBackupFinished = true;
            this.ShowSuccessToast("备份成功。");
        }
        catch (Exception exception)
        {
            Logger.LogError(exception, "无法创建备份。");
            this.ShowErrorToast("无法创建备份", exception);
        }
        ViewModel.IsBackingUp = false;
    }

    private void ButtonViewBackupFiles_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo()
            {
                FileName = System.IO.Path.GetFullPath(Path.Combine(CommonDirectories.AppRootFolderPath, "Backups")),
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            Logger.LogError(exception, "无法浏览备份文件。");
            this.ShowErrorToast($"无法浏览备份文件", exception);
        }
    }

    private async void ButtonRecoverBackup_OnClick(object sender, RoutedEventArgs e)
    {
        if (!await ViewModel.ManagementService.AuthorizeByLevel(ViewModel.ManagementService.CredentialConfig.ExitApplicationAuthorizeLevel))
        {
            return;
        }
        AppBase.Current.Restart(["-m", "-r"]);
    }
}