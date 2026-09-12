using System.Collections.ObjectModel;
using ClassPixelPro.Core.Abstractions.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ClassPixelPro.Models.ComponentSettings;

public partial class GroupComponentSettings : ObservableObject, IComponentContainerSettings
{
    [ObservableProperty]
    private ObservableCollection<Core.Models.Components.ComponentSettings> _children = [];
}