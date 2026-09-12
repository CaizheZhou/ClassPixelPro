using ClassPixelPro.Core.Abstractions.Controls;
using ClassPixelPro.Core.Abstractions.Services;
using ClassPixelPro.Models.Rules;

namespace ClassPixelPro.Controls.RuleSettingsControls;

/// <summary>
/// CurrentWeatherRuleSettingsControl.xaml 的交互逻辑
/// </summary>
public partial class CurrentWeatherRuleSettingsControl : RuleSettingsControlBase<CurrentWeatherRuleSettings>
{
    public IWeatherService WeatherService { get; }

    public CurrentWeatherRuleSettingsControl(IWeatherService weatherService)
    {
        WeatherService = weatherService;
        InitializeComponent();
    }
}