using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ClassPixelPro.Models.Rules;

public class CurrentSubjectRuleSettings : ObservableRecipient
{
    private Guid _subjectId = Guid.Empty;

    public Guid SubjectId
    {
        get => _subjectId;
        set
        {
            if (value == _subjectId) return;
            _subjectId = value;
            OnPropertyChanged();
        }
    }
}