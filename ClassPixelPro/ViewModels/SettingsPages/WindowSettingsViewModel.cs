using System.Collections.ObjectModel;
using Avalonia.Platform;
using ClassPixelPro.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ClassPixelPro.ViewModels.SettingsPages;

public class WindowSettingsViewModel(SettingsService settingsService) : ObservableRecipient
{
    public SettingsService SettingsService { get; } = settingsService;
    private ObservableCollection<Screen> _screens = new();

    public ObservableCollection<Screen> Screens
    {
        get => _screens;
        set
        {
            if (Equals(value, _screens)) return;
            _screens = value;
            OnPropertyChanged();
        }
    }
}