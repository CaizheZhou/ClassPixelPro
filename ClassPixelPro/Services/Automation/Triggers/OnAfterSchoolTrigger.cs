using System;
using ClassPixelPro.Core.Abstractions.Automation;
using ClassPixelPro.Core.Abstractions.Services;
using ClassPixelPro.Core.Attributes;

namespace ClassPixelPro.Services.Automation.Triggers;

[TriggerInfo("classpixelpro.lessons.onAfterSchool", "放学时", "\ued35")]
public class OnAfterSchoolTrigger(ILessonsService lessonsService) : TriggerBase
{
    private ILessonsService LessonsService { get; } = lessonsService;

    public override void Loaded()
    {
        LessonsService.OnAfterSchool += OnLessonsServiceOnAfterSchool;
    }
    public override void UnLoaded()
    {
        LessonsService.OnAfterSchool -= OnLessonsServiceOnAfterSchool;
    }

    private void OnLessonsServiceOnAfterSchool(object? sender, EventArgs e)
    {
        Trigger();
    }
}