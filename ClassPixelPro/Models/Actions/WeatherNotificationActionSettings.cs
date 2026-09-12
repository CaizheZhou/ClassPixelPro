using CommunityToolkit.Mvvm.ComponentModel;

namespace ClassPixelPro.Models.Actions;

public partial class WeatherNotificationActionSettings : ObservableObject
{
    [ObservableProperty] private int _notificationKind = 0;
}