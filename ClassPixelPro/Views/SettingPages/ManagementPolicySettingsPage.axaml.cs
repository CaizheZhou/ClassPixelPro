using System.Windows;
using Avalonia.Interactivity;
using ClassPixelPro.Core;
using ClassPixelPro.Core.Abstractions.Controls;
using ClassPixelPro.Core.Abstractions.Services.Management;
using ClassPixelPro.Core.Attributes;
using ClassPixelPro.Core.Enums.SettingsWindow;
using ClassPixelPro.ViewModels.SettingsPages;

namespace ClassPixelPro.Views.SettingPages;

/// <summary>
/// ManagementPolicySettingsPage.xaml 的交互逻辑
/// </summary>
[SettingsPageInfo("management.policy", "集控策略", true, SettingsPageCategory.About)]
public partial class ManagementPolicySettingsPage : SettingsPageBase
{
    public IManagementService ManagementService { get; }
    public ManagementPolicySettingsViewModel ViewModel { get; } = new();

    public ManagementPolicySettingsPage(IManagementService managementService)
    {
        ManagementService = managementService;
        DataContext = this;
        InitializeComponent();
    }

    private void ButtonRestart_OnClick(object sender, RoutedEventArgs e)
    {
        AppBase.Current.Restart();
    }

    private async void ManagementPolicySettingsPage_OnLoaded(object sender, RoutedEventArgs e)
    {
        if (ManagementService.IsManagementEnabled)
        {
            return;
        }
        var result =
            await ManagementService.AuthorizeByLevel(ManagementService.CredentialConfig
                .EditPolicyAuthorizeLevel);
        if (result)
        {
            ViewModel.IsLocked = false;
        }
    }

    private void ManagementPolicySettingsPage_OnUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.IsLocked = true;
    }
}
