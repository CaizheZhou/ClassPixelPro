using System;
using ClassPixelPro.Core.Abstractions.Controls;
using ClassPixelPro.Core.Abstractions.Services;
using ClassPixelPro.Core.Abstractions.Services.Management;
using ClassPixelPro.Core.ComponentModels;
using ClassPixelPro.Models.Rules;
using ClassPixelPro.Services;
using ClassPixelPro.Shared.Models.Profile;
using ClassPixelPro.ViewModels;

namespace ClassPixelPro.Controls.RuleSettingsControls;

/// <summary>
/// CurrentSubjectRuleSettingsControl.xaml 的交互逻辑
/// </summary>
public partial class CurrentSubjectRuleSettingsControl : RuleSettingsControlBase<CurrentSubjectRuleSettings>
{
    public ProfileSettingsViewModel ProfileSettingsViewModel { get; }

    public CurrentSubjectRuleSettingsControl(ProfileSettingsViewModel vm)
    {
        ProfileSettingsViewModel = vm;
        InitializeComponent();
    }
}
