using CommunityToolkit.Mvvm.ComponentModel;

namespace ClassPixelPro.ViewModels.SettingsPages;

public partial class ErrorSettingsViewModel : ObservableRecipient
{
    [ObservableProperty] private bool _isError = false;
}