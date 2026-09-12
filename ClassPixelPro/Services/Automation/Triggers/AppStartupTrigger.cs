using System.Diagnostics;
using System.Linq;
using ClassPixelPro.Core;
using ClassPixelPro.Core.Abstractions.Automation;
using ClassPixelPro.Core.Attributes;
using ClassPixelPro.Core.Enums;

namespace ClassPixelPro.Services.Automation.Triggers;

[TriggerInfo("classpixelpro.lifetime.startup", "应用启动时", "\ue067")]
public class AppStartupTrigger : TriggerBase
{
    public override void Loaded()
    {
        if (AppBase.CurrentLifetime < ApplicationLifetime.Running) {
            Trigger();
        }
    }

    public override void UnLoaded()
    {
    }
}