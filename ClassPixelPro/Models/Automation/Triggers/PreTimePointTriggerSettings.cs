using ClassPixelPro.Core.Attributes;
using ClassPixelPro.Shared.Enums;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ClassPixelPro.Models.Automation.Triggers;

public partial class PreTimePointTriggerSettings : ObservableObject
{
    [ObservableProperty] private TimeState _targetState = TimeState.OnClass;

    [ObservableProperty] private double _timeSeconds = 60;
}