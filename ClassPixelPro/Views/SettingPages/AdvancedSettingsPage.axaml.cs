using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using ClassPixelPro.Core.Abstractions.Controls;
using ClassPixelPro.Core.Attributes;
using ClassPixelPro.Core.Enums.SettingsWindow;
using ClassPixelPro.Shared;
using ClassPixelPro.ViewModels.SettingsPages;

namespace ClassPixelPro.Views.SettingPages;

[Group("classpixelpro.general")]
[SettingsPageInfo("advanced", "高级", "\uf49f", "\uf49e", SettingsPageCategory.Internal)]
public partial class AdvancedSettingsPage : SettingsPageBase
{
    public AdvancedSettingsViewModel ViewModel { get; } = IAppHost.GetService<AdvancedSettingsViewModel>();
    
    public AdvancedSettingsPage()
    {
        DataContext = this;
        InitializeComponent();
        ViewModel.PropertyChanged += ViewModelOnPropertyChanged;
    }

    private void ViewModelOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ViewModel.RenderingModeSelectedIndex) or nameof(ViewModel.UseNativeTitlebar) or nameof(ViewModel.IgnoreQtScaling))
        {
            RequestRestart();
        }
    }
}