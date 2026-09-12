using System;

namespace ClassPixelPro.Controls.ScheduleDataGrid;

public class CreateClassPlanEventArgs : EventArgs
{
    public required DateTime Date { get; init; }
}