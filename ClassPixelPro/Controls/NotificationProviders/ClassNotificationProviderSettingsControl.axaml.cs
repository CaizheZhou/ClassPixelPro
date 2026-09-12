
using System;
using System.Linq;
using System.Windows;
using Avalonia.Interactivity;
using ClassPixelPro.Core.Abstractions.Controls;
using ClassPixelPro.Core.Abstractions.Services;
using ClassPixelPro.Core.Controls;
using ClassPixelPro.Models.NotificationProviderSettings;

namespace ClassPixelPro.Controls.NotificationProviders;

/// <summary>
/// ClassNotificationProviderSettingsControl.xaml 的交互逻辑
/// </summary>
public partial class ClassNotificationProviderSettingsControl : NotificationProviderControlBase<ClassNotificationSettings>
{
    public ClassNotificationProviderSettingsControl()
    {
        InitializeComponent();
    }

    private void ButtonShowAttachedSettingsInfo_OnClick(object sender, RoutedEventArgs e)
    {
        SettingsPageBase.OpenDrawerCommand.Execute(new RootAttachedSettingsDependencyControl(IAttachedSettingsHostService.RegisteredControls.First(x => x.Guid == new Guid("08F0D9C3-C770-4093-A3D0-02F3D90C24BC"))));
    }
}
