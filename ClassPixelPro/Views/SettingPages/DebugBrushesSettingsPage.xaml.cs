#if false
using ClassPixelPro.Core.Abstractions.Controls;
using ClassPixelPro.Core.Attributes;
using ClassPixelPro.Core.Enums.SettingsWindow;

namespace ClassPixelPro.Views.SettingPages;

/// <summary>
/// DebugBrushesSettingsPage.xaml 的交互逻辑
/// </summary>
[SettingsPageInfo("debug_brushes", "笔刷", MaterialIconKind.BrushOutline, MaterialIconKind.Brush, SettingsPageCategory.Debug)]
public partial class DebugBrushesSettingsPage : SettingsPageBase
{
    public DebugBrushesSettingsPage()
    {
        InitializeComponent();
        DataContext = this;
    }
}
#endif
