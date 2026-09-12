using CommunityToolkit.Mvvm.ComponentModel;

namespace ClassPixelPro.ViewModels.SettingsPages;

public partial class ManagementPolicySettingsViewModel : ObservableObject
{
    [ObservableProperty] private bool _isLocked = true;
}