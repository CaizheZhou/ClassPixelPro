using System.Collections.ObjectModel;
using ClassPixelPro.Core.Abstractions.Services;
using ClassPixelPro.Core.Models.Tutorial;
using ClassPixelPro.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using FluentAvalonia.UI.Controls;

namespace ClassPixelPro.ViewModels;

public partial class TutorialCenterViewModel(SettingsService settingsService, ITutorialService tutorialService) : ObservableObject
{
   public SettingsService SettingsService { get; } = settingsService;
   public ITutorialService TutorialService { get; } = tutorialService;

   [ObservableProperty] private TutorialGroup? _selectedTutorialGroup;
   [ObservableProperty] private Tutorial? _selectedTutorial;
   
   public ObservableCollection<object> NavigationViewItems { get; } = [];
}