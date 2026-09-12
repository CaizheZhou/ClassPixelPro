using System.Windows;
using Avalonia.Interactivity;
using ClassPixelPro.Controls;
using ClassPixelPro.Controls.TimeLine;
using ClassPixelPro.Shared.Models.Profile;

namespace ClassPixelPro.Models.EventArgs;

public class SeparatorLikeTimePointMovedEventArgs(TimeLayoutItem item) : RoutedEventArgs(TimeLineListItemSeparatorAdornerControl.SeparatorLikeTimePointMovedEvent)
{
    public TimeLayoutItem Item { get; } = item;
}