using System.Windows;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using ClassPixelPro.Controls;
using ClassPixelPro.Core.Abstractions.Controls;
using ClassPixelPro.Core.Abstractions.Services.Management;
using ClassPixelPro.Core.Attributes;
using ClassPixelPro.Core.Enums.SettingsWindow;
using ClassPixelPro.ViewModels.SettingsPages;
using FluentAvalonia.UI.Controls;
using Net.Codecrete.QrCodeGenerator;

namespace ClassPixelPro.Views.SettingPages;

/// <summary>
/// ManagementSettingsPage.xaml 的交互逻辑
/// </summary>
[SettingsPageInfo("management", "集控", true, SettingsPageCategory.About)]
public partial class ManagementSettingsPage : SettingsPageBase
{
    public IManagementService ManagementService { get; }

    public ManagementSettingsViewModel ViewModel { get; } = new();

    public ManagementSettingsPage(IManagementService managementService)
    {
        ManagementService = managementService;
        DataContext = this;
        InitializeComponent();
    }

    private void ButtonJoinManagement_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new JoinManagementDialog();
        dialog.ShowDialog((TopLevel.GetTopLevel(this) as Window)!);
    }

    private void ManagementSettingsPage_OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!ManagementService.IsManagementEnabled)
        {
            return;
        }

        var qrcode = QrCode.EncodeText(ManagementService.Persist.ClientUniqueId.ToString(), QrCode.Ecc.Medium);
        ViewModel.CuidQrCodePath = Geometry.Parse(qrcode.ToGraphicsPath());
    }
}

