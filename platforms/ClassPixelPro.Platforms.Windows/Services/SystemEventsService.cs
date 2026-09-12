using ClassPixelPro.Platforms.Abstraction.Services;
using Microsoft.Win32;

namespace ClassPixelPro.Platform.Windows.Services;

public class SystemEventsService : ISystemEventsService
{
    public event EventHandler? TimeChanged;

    public SystemEventsService()
    {
        
        SystemEvents.TimeChanged += (sender, args) => TimeChanged?.Invoke(sender, args);
    }
    
}