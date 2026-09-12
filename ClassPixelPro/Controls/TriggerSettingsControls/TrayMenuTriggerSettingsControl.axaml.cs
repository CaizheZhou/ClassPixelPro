using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using ClassPixelPro.Core.Abstractions.Controls;
using ClassPixelPro.Models.Automation.Triggers;

namespace ClassPixelPro.Controls.TriggerSettingsControls;

public partial class TrayMenuTriggerSettingsControl : TriggerSettingsControlBase<TrayMenuTriggerSettings>
{
    public TrayMenuTriggerSettingsControl()
    {
        InitializeComponent();
    }
}