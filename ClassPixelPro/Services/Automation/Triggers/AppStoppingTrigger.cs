using System;
using ClassPixelPro.Core;
using ClassPixelPro.Core.Abstractions.Automation;
using ClassPixelPro.Core.Attributes;

namespace ClassPixelPro.Services.Automation.Triggers;

[TriggerInfo("classpixelpro.lifetime.stopping", "应用退出时", "\ue0df")]
public class AppStoppingTrigger : TriggerBase
{
    public override void Loaded()
    {
        AppBase.Current.AppStopping += CurrentOnAppStarted;
    }

    public override void UnLoaded()
    {
        AppBase.Current.AppStopping -= CurrentOnAppStarted;
    }

    private void CurrentOnAppStarted(object? sender, EventArgs e)
    {
        Trigger();
    }
}