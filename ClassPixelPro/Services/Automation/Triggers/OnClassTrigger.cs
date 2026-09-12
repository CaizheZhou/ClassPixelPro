using System;
using ClassPixelPro.Core.Abstractions.Automation;
using ClassPixelPro.Core.Abstractions.Services;
using ClassPixelPro.Core.Attributes;

namespace ClassPixelPro.Services.Automation.Triggers;

[TriggerInfo("classpixelpro.lessons.onClass", "上课时", "\uE47A")]
public class OnClassTrigger(ILessonsService lessonsService) : TriggerBase
{
    private ILessonsService LessonsService { get; } = lessonsService;

    public override void Loaded()
    {
        LessonsService.OnClass += LessonsServiceOnOnClass;
    }
    public override void UnLoaded()
    {
        LessonsService.OnClass -= LessonsServiceOnOnClass;
    }

    private void LessonsServiceOnOnClass(object? sender, EventArgs e)
    {
        Trigger();
    }
}