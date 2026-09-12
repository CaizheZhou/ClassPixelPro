using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using ClassPixelPro.Shared.Models.Automation;

namespace ClassPixelPro.Controls;

public class WorkflowProgressIndicatorItemControl : TemplatedControl
{
    public static readonly StyledProperty<ActionItem> ActionProperty = AvaloniaProperty.Register<WorkflowProgressIndicatorItemControl, ActionItem>(
        nameof(Action));

    public ActionItem Action
    {
        get => GetValue(ActionProperty);
        set => SetValue(ActionProperty, value);
    }
}