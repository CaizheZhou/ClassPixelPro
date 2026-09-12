using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using ClassPixelPro.Core.Abstractions.Automation;
using ClassPixelPro.Core.Attributes;
using ClassPixelPro.Models.Actions;
using ClassPixelPro.Services.NotificationProviders;
using ClassPixelPro.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
namespace ClassPixelPro.Services.Automation.Actions;

[ActionInfo("classpixelpro.notification.weather", "显示天气提醒", "\uf44f", addDefaultToMenu:false)]
public class WeatherNotificationAction : ActionBase<WeatherNotificationActionSettings>
{
    static WeatherNotificationProvider WeatherNotificationProvider { get; } =
        IAppHost.Host.Services.GetServices<IHostedService>().OfType<WeatherNotificationProvider>().First();

    protected override async Task OnInvoke()
    {
        await base.OnInvoke();
        _ = Dispatcher.UIThread.InvokeAsync(() =>
        {
            switch (Settings.NotificationKind)
            {
                case 0:
                    WeatherNotificationProvider.ShowWeatherForecastCore();
                    break;
                case 1:
                    WeatherNotificationProvider.ShowAlertsNotificationCore();
                    break;
                case 2:
                    WeatherNotificationProvider.ShowWeatherForecastHourlyCore();
                    break;
            }
        });
    }
}