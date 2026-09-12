using CommunityToolkit.Mvvm.ComponentModel;

namespace ClassPixelPro.ViewModels;

public partial class RecoveryViewModel : ObservableObject
{
    [ObservableProperty] private bool _canGoBack;
}