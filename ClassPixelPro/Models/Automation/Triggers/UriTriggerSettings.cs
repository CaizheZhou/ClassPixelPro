using CommunityToolkit.Mvvm.ComponentModel;

namespace ClassPixelPro.Models.Automation.Triggers;

public partial class UriTriggerSettings : ObservableObject
{
    [ObservableProperty] private string _uriSuffix = "";
}