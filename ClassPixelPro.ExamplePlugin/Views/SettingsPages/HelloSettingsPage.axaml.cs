using ClassPixelPro.Core.Abstractions.Controls;
using ClassPixelPro.Core.Attributes;

namespace ClassPixelPro.ExamplePlugin.Views.SettingsPages;

[SettingsPageInfo("classpixelpro.example-plugin.hello", "Hello world!")]
public partial class HelloSettingsPage : SettingsPageBase
{
    public HelloSettingsPage()
    {
        InitializeComponent();
    }
}