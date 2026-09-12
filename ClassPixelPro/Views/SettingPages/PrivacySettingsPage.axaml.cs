using System;
using System.ComponentModel;
using System.Windows;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ClassPixelPro.Core.Abstractions.Controls;
using ClassPixelPro.Core.Attributes;
using ClassPixelPro.Core.Controls;
using ClassPixelPro.Core.Enums.SettingsWindow;
using ClassPixelPro.Services;

namespace ClassPixelPro.Views.SettingPages;

/// <summary>
/// PrivacySettingsPage.xaml 的交互逻辑
/// </summary>
[Group("classpixelpro.general")]
[SettingsPageInfo("privacy", "隐私", "\uef65", "\uef64", SettingsPageCategory.Internal)]
public partial class PrivacySettingsPage : SettingsPageBase
{
    public SettingsService SettingsService { get; }

    public PrivacySettingsPage(SettingsService settingsService)
    {
        InitializeComponent();
        DataContext = this;
        SettingsService = settingsService;
        SettingsService.Settings.PropertyChanged += OnSettingsOnPropertyChanged;
    }

    private void OnSettingsOnPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(SettingsService.Settings.IsSentryEnabled))
        {
            RequestRestart();
        }
    }

    private void HyperlinkMsAppCenter_OnClick(object sender, RoutedEventArgs e)
    {
        new DocumentReaderWindow()
        {
            Source = new Uri("avares://ClassPixelPro/Assets/Documents/Privacy_.md"),
            Title = "ClassPixelPro 隐私政策"
        }.ShowDialog((TopLevel.GetTopLevel(this) as Window)!);
    }

    private void PrivacySettingsPage_OnLoaded(object sender, RoutedEventArgs e)
    {
        SettingsService.Settings.PropertyChanged += OnSettingsOnPropertyChanged;
    }

    private void PrivacySettingsPage_OnUnloaded(object sender, RoutedEventArgs e)
    {
        SettingsService.Settings.PropertyChanged -= OnSettingsOnPropertyChanged;
    }
}

