using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using ClassPixelPro.Core.Abstractions.Controls;
using ClassPixelPro.Models.Rules;

namespace ClassPixelPro.Controls.RuleSettingsControls;

public partial class SunRiseSetRuleSettingsControl : RuleSettingsControlBase<SunRiseSetRuleSettings>
{
    public SunRiseSetRuleSettingsControl()
    {
        InitializeComponent();
    }
}
