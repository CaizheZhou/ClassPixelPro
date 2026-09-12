using CommunityToolkit.Mvvm.ComponentModel;

namespace ClassPixelPro.ViewModels.SettingsPages;

public partial class ManagementCredentialsSettingsViewModel : ObservableObject
{
    [ObservableProperty] private bool _isLocked = true;
}