using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace ClassPixelPro.Views.Controls;

/// <summary>
/// 「考试中」角标窗口：考试模式生效期间显示在屏幕顶部中央（岛栏下方），
/// 提示当前教室处于考试状态。由 <see cref="Services.Management.ExamModeService"/> 控制显示与隐藏。
/// </summary>
public partial class ExamBadgeWindow : Window
{
    public ExamBadgeWindow()
    {
        InitializeComponent();
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaChromeHints = Avalonia.Platform.ExtendClientAreaChromeHints.NoChrome;
        ExtendClientAreaTitleBarHeightHint = -1;
    }

    /// <summary>
    /// 显示角标并定位到主屏幕顶部中央（岛栏下方，约 60px）。
    /// </summary>
    public void ShowExam(string text)
    {
        ExamText.Text = text;
        PositionAtTopCenter();
        if (!IsVisible)
        {
            Show();
        }
    }

    private void PositionAtTopCenter()
    {
        var screen = Screens.Primary;
        if (screen == null) return;
        var bounds = screen.Bounds;
        var x = bounds.X + (bounds.Width - Width) / 2;
        // 岛栏高 40 + 上下留白，角标放在其正下方
        Position = new PixelPoint((int)x, (int)(bounds.Y + 60));
    }
}
