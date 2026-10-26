using CommunityToolkit.Mvvm.ComponentModel;

namespace ClassPixelPro.Models.ComponentSettings;

/// <summary>
/// 「早读迟到名单」主界面组件的设置项。
/// </summary>
public class LateListComponentSettings : ObservableRecipient
{
    private string _title = "早读迟到";
    private int _maxDisplayNames = 8;
    private bool _hideWhenEmpty = true;
    private bool _showStatusLine = true;
    private bool _showDetectTime = true;
    private int _fontSize = 15;

    /// <summary>组件标题。</summary>
    public string Title
    {
        get => _title;
        set
        {
            if (value == null) return;
            if (value.Equals(_title)) return;
            _title = value;
            OnPropertyChanged();
        }
    }

    /// <summary>名单最多显示几人，超出折叠成「等 N 人」。0 表示不限制。</summary>
    public int MaxDisplayNames
    {
        get => _maxDisplayNames;
        set
        {
            if (value == _maxDisplayNames) return;
            _maxDisplayNames = value < 0 ? 0 : value;
            OnPropertyChanged();
        }
    }

    /// <summary>没有迟到时隐藏整块组件。</summary>
    public bool HideWhenEmpty
    {
        get => _hideWhenEmpty;
        set
        {
            if (value == _hideWhenEmpty) return;
            _hideWhenEmpty = value;
            OnPropertyChanged();
        }
    }

    /// <summary>显示当前状态行（如「正在抓拍并识别…」）。</summary>
    public bool ShowStatusLine
    {
        get => _showStatusLine;
        set
        {
            if (value == _showStatusLine) return;
            _showStatusLine = value;
            OnPropertyChanged();
        }
    }

    /// <summary>显示识别时间、抓拍张数、耗时等明细行。</summary>
    public bool ShowDetectTime
    {
        get => _showDetectTime;
        set
        {
            if (value == _showDetectTime) return;
            _showDetectTime = value;
            OnPropertyChanged();
        }
    }

    /// <summary>名单姓名字号。</summary>
    public int FontSize
    {
        get => _fontSize;
        set
        {
            if (value == _fontSize) return;
            _fontSize = value;
            OnPropertyChanged();
        }
    }
}
