using ClassPixelPro.Core.Abstractions.Controls;
using ClassPixelPro.Models.Automation.Triggers;

namespace ClassPixelPro.Controls.TriggerSettingsControls;

/// <summary>
/// CronTriggerSettingsControl.axaml 的交互逻辑
/// </summary>
public partial class CronTriggerSettingsControl : TriggerSettingsControlBase<CronTriggerSettings>
{
    public CronTriggerSettingsControl()
    {
        InitializeComponent();
    }
}