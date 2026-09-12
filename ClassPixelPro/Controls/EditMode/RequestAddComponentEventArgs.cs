using System.Collections.Generic;
using Avalonia.Interactivity;
using ClassPixelPro.Core.Models.Components;

namespace ClassPixelPro.Controls.EditMode;

public class RequestAddComponentEventArgs(RoutedEvent e) : RoutedEventArgs(e)
{
    public required IList<ComponentSettings> ComponentList { get; init; }
}