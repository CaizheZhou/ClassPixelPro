using System;
using ClassPixelPro.Core.Abstractions.Services;
using ClassPixelPro.Models.Automation.Triggers;
using ClassPixelPro.Models.EventArgs;

namespace ClassPixelPro.Services.Automation.Triggers;

public class SignalTriggerHandlerService
{
    public event EventHandler<SignalTriggerEventArgs>? Handled;

    public void EmitSignal(string name, bool revert)
    {
        Handled?.Invoke(this, new SignalTriggerEventArgs(name, revert));
    }

    public SignalTriggerHandlerService(IActionService actionService)
    {
        actionService.RegisterActionHandler("classpixelpro.broadcastSignal", (o, guid) =>
        {
            if (o is SignalTriggerSettings settings)
            {
                EmitSignal(settings.SignalName, settings.IsRevert);
            }
        });
        actionService.RegisterRevertHandler("classpixelpro.broadcastSignal", (o, guid) =>
        {
            if (o is SignalTriggerSettings settings)
            {
                EmitSignal(settings.SignalName, !settings.IsRevert);
            }
        });
    }
}