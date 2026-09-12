using System;
using Avalonia.Interactivity;
using ClassPixelPro.Shared.Models.Profile;

namespace ClassPixelPro.Controls.ScheduleDataGrid;

public class ScheduleDataGridSelectionChangedEventArgs(RoutedEvent e) : RoutedEventArgs(e)
{
    public required ClassInfo ClassInfo { get; init; }
    
    public required DateTime Date { get; init; }
}