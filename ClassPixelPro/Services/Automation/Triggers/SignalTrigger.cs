using ClassPixelPro.Core.Abstractions.Automation;
using ClassPixelPro.Core.Attributes;
using ClassPixelPro.Models.Automation.Triggers;
using ClassPixelPro.Models.EventArgs;

namespace ClassPixelPro.Services.Automation.Triggers;

[TriggerInfo("classpixelpro.signal", "收到信号时", "\ue40e")]
public class SignalTrigger(SignalTriggerHandlerService signalTriggerHandlerService) : TriggerBase<SignalTriggerSettings>
{
    public SignalTriggerHandlerService SignalTriggerHandlerService { get; } = signalTriggerHandlerService;

    public override void Loaded()
    {
        SignalTriggerHandlerService.Handled += SignalTriggerHandlerServiceOnHandled;
    }


    public override void UnLoaded()
    {
        SignalTriggerHandlerService.Handled -= SignalTriggerHandlerServiceOnHandled;
    }

    private void SignalTriggerHandlerServiceOnHandled(object? sender, SignalTriggerEventArgs e)
    {
        if (e.SignalName != Settings.SignalName) return;

        if (e.Revert)
        {
            TriggerRevert();
        }
        else
        {
            Trigger();
        }
    }
}