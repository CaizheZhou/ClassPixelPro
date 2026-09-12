using ClassPixelPro.Platforms.Abstraction.Services;

namespace ClassPixelPro.Platforms.Abstraction.Stubs.Services;

/// <inheritdoc />
public class SystemEventsServiceStub : ISystemEventsService
{
    /// <inheritdoc />
    public event EventHandler? TimeChanged;
}