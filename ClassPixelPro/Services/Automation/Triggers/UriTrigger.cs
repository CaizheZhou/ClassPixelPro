using ClassPixelPro.Core.Abstractions.Automation;
using ClassPixelPro.Core.Attributes;
using ClassPixelPro.Models.Automation.Triggers;
using ClassPixelPro.Models.EventArgs;

namespace ClassPixelPro.Services.Automation.Triggers;

[TriggerInfo("classpixelpro.uri", "调用 Uri 时", "\ueab0")]
public class UriTrigger(UriTriggerHandlerService uriTriggerHandlerService) : TriggerBase<UriTriggerSettings>
{
    private UriTriggerHandlerService UriTriggerHandlerService { get; } = uriTriggerHandlerService;

    public override void Loaded()
    {
        UriTriggerHandlerService.HandledRun += UriTriggerHandlerServiceOnHandledRun;
        UriTriggerHandlerService.HandledRevert += UriTriggerHandlerServiceOnHandledRevert;
    }

    private void UriTriggerHandlerServiceOnHandledRevert(object? sender, UriTriggerHandledEventArgs e)
    {
        if (e.Name == Settings.UriSuffix)
        {
            TriggerRevert();
        }
    }

    private void UriTriggerHandlerServiceOnHandledRun(object? sender, UriTriggerHandledEventArgs e)
    {
        if (e.Name == Settings.UriSuffix)
        {
            Trigger();
        }
    }

    public override void UnLoaded()
    {
        UriTriggerHandlerService.HandledRun -= UriTriggerHandlerServiceOnHandledRun;
        UriTriggerHandlerService.HandledRevert -= UriTriggerHandlerServiceOnHandledRevert;
    }
}