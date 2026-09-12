using System;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ClassPixelPro.Core.Abstractions.Controls;
using ClassPixelPro.Core.Abstractions.Services;
using ClassPixelPro.Core.Attributes;
using ClassPixelPro.Platforms.Abstraction.Services;
using ClassPixelPro.Core.Controls;
using ClassPixelPro.Core.Enums.SettingsWindow;
using ClassPixelPro.Core.Helpers.UI;
using ClassPixelPro.Core.Models.UI;
using ClassPixelPro.Core.Models.Weather;
using ClassPixelPro.Services;
using ClassPixelPro.Shared;
using ClassPixelPro.ViewModels.SettingsPages;
using FluentAvalonia.UI.Controls;
using Microsoft.Extensions.Logging;

namespace ClassPixelPro.Views.SettingPages;

/// <summary>
/// WeatherSettingsPage.xaml 的交互逻辑
/// </summary>
[SettingsPageInfo("weather", "天气", "\uf44f", "\uf44e", SettingsPageCategory.Internal)]
public partial class WeatherSettingsPage : SettingsPageBase
{
    public WeatherSettingsViewModel ViewModel { get; } = IAppHost.GetService<WeatherSettingsViewModel>();

    private ILogger<WeatherSettingsPage> Logger => ViewModel.Logger;

    public SettingsService SettingsService { get; }

    public WeatherSettingsPage(SettingsService settingsService, IWeatherService weatherService, ILocationService locationService, ILogger<WeatherSettingsPage> logger)
    {
        InitializeComponent();
        DataContext = this;
        SettingsService = settingsService;
    }

    private void ButtonEditCurrentCity_OnClick(object sender, RoutedEventArgs e)
    {
        OpenDrawer("CitySearcher");
    }

    private void TextBoxSearchCity_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        ViewModel.OnSearchTextChanged(((TextBox)sender).Text);
    }

    private async void SelectorCity_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var listbox = (ListBox)sender;
        var city = (City?)listbox.SelectedItem;
        if (city == null)
        {
            e.Handled = true;
            return;
        }
        await ViewModel.SelectCityAsync(city);
    }

    private async void ButtonGetCurrentPos_OnClick(object sender, RoutedEventArgs e)
    {
        var toast = this.ShowToastRef(new ToastMessage("正在定位...") { AutoClose = false });
        try
        {
            await ViewModel.GetCurrentPositionAsync();
            toast.Close();
            this.ShowSuccessToast("定位成功");
        }
        catch (Exception exception)
        {
            toast.Close();
            Logger.LogError(exception, "无法获取当前位置");
            this.ShowErrorToast($"无法获取当前位置：{exception.Message}");
        }
    }

    private void ButtonShowPos_OnClick(object sender, RoutedEventArgs e)
    {
        ViewModel.ShowLocationCoordinates();
    }

    private async void ButtonRefreshWeather_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var result = await ViewModel.RefreshWeatherWithResultAsync();

            if (result.IsSuccess)
            {
                if (result.IsPrecisionDegraded)
                {
                    this.ShowWarningToast($"此天气信息降级了坐标精度: {result.DegradedPrecision}位小数");
                    this.ShowSuccessToast("天气刷新成功");
                }
                else
                {
                    this.ShowSuccessToast("天气刷新成功");
                }
            }
            else
            {
                this.ShowErrorToast($"天气刷新失败: {result.ErrorMessage}");
            }
        }
        catch (Exception exception)
        {
            Logger.LogError(exception, "天气刷新失败");
            this.ShowErrorToast($"天气刷新失败: {exception.Message}");
        }
    }

    private void OnSettingsOnPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(SettingsService.Settings.NoTLSWeatherRequests))
        {
            RequestRestart();
        }
    }

    private void WeatherSettingsPage_OnLoaded(object sender, RoutedEventArgs e)
    {
        SettingsService.Settings.PropertyChanged += OnSettingsOnPropertyChanged;
        _ = ViewModel.InitializeAsync();
    }

    private void WeatherSettingsPage_OnUnloaded(object sender, RoutedEventArgs e)
    {
        SettingsService.Settings.PropertyChanged -= OnSettingsOnPropertyChanged;
    }
}
