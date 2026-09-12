using System;
using ClassPixelPro.Core.Abstractions.Automation;
using ClassPixelPro.Core.Abstractions.Services;
using ClassPixelPro.Core.Attributes;

namespace ClassPixelPro.Services.Automation.Triggers;

[TriggerInfo("classpixelpro.lessons.currentTimeStateChanged", "当前时间状态变化时", "\ue4d2")]
public class CurrentTimeStateChangedTrigger(ILessonsService lessonsService) : TriggerBase
{
    private ILessonsService LessonsService { get; } = lessonsService;

    public override void Loaded()
    {
        LessonsService.CurrentTimeStateChanged += CurrentLessonsServiceOnTimeStateChanged;
    }
    public override void UnLoaded()
    {
        LessonsService.CurrentTimeStateChanged -= CurrentLessonsServiceOnTimeStateChanged;
    }

    private void CurrentLessonsServiceOnTimeStateChanged(object? sender, EventArgs e)
    {
        Trigger();
    }
}