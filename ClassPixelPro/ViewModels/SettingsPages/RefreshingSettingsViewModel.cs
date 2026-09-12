using ClassPixelPro.Core.Abstractions.Services;
using ClassPixelPro.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ClassPixelPro.ViewModels.SettingsPages;

public partial class RefreshingSettingsViewModel(
    SettingsService settingsService, 
    ITutorialService tutorialService,
    IRefreshingService refreshingService) : ObservableObject
{
    public SettingsService SettingsService { get; } = settingsService;
    public ITutorialService TutorialService { get; } = tutorialService;
    public IRefreshingService RefreshingService { get; } = refreshingService;
}