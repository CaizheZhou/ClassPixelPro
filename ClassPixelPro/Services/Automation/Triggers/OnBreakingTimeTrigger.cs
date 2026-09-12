using System;
using ClassPixelPro.Core.Abstractions.Automation;
using ClassPixelPro.Core.Abstractions.Services;
using ClassPixelPro.Core.Attributes;

namespace ClassPixelPro.Services.Automation.Triggers;

[TriggerInfo("classpixelpro.lessons.onBreakingTime", "课间休息时", "\ue4c4")]
public class OnBreakingTimeTrigger(ILessonsService lessonsService) : TriggerBase
{
    private ILessonsService LessonsService { get; } = lessonsService;

    public override void Loaded()
    {
        LessonsService.OnBreakingTime += LessonsServiceOnOnBreakingTime;
    }
    public override void UnLoaded()
    {
        LessonsService.OnBreakingTime -= LessonsServiceOnOnBreakingTime;
    }

    private void LessonsServiceOnOnBreakingTime(object? sender, EventArgs e)
    {
        Trigger();
    }
}