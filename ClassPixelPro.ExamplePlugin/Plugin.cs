using ClassPixelPro.Core.Abstractions;
using ClassPixelPro.Core.Attributes;
using ClassPixelPro.Core.Extensions.Registry;
using ClassPixelPro.ExamplePlugin.Views.SettingsPages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ClassPixelPro.ExamplePlugin;

[PluginEntrance]
public class Plugin : PluginBase
{
    public override void Initialize(HostBuilderContext context, IServiceCollection services)
    {
        Console.WriteLine("Hello world!");
        services.AddSettingsPage<HelloSettingsPage>();
    }
}