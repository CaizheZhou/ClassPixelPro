using System;
using System.Collections.ObjectModel;
using ClassPixelPro.Core.Abstractions.Services;

namespace ClassPixelPro.Services;

public class AttachedSettingsHostService : IAttachedSettingsHostService
{
    public ObservableCollection<Type> TimePointSettingsAttachedSettingsControls { get; } = new();
    public ObservableCollection<Type> TimeLayoutSettingsAttachedSettingsControls { get; } = new();
    public ObservableCollection<Type> ClassPlanSettingsAttachedSettingsControls { get; } = new();
    public ObservableCollection<Type> SubjectSettingsAttachedSettingsControls { get; } = new();
}