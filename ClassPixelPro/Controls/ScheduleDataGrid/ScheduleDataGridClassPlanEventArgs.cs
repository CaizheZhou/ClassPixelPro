using System;
using Avalonia.Interactivity;
using ClassPixelPro.Shared.Models.Profile;

namespace ClassPixelPro.Controls.ScheduleDataGrid;

public class ScheduleDataGridClassPlanEventArgs(RoutedEvent e) : RoutedEventArgs(e)
{
    public required ClassPlan ClassPlan { get; init; }
    
    public required DateTime Date { get; set; }
}