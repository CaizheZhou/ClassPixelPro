using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace ClassPixelPro.Views;

/// <summary>
/// 班主任通知的全屏展示窗口：全屏、置顶，正文随屏幕尺寸和文字长度自适应放大。
/// 窗口为单例复用，重复通知会替换内容并重新计时，不叠加多个窗口。
/// </summary>
public partial class FullscreenNotificationWindow : Window
{
    private readonly DispatcherTimer _timer = new();

    public FullscreenNotificationWindow()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => Dismiss();
    }

    /// <summary>
    /// 展示一条全屏通知。<paramref name="duration"/> 为自动关闭时长。
    /// </summary>
    public void ShowNotification(string text, TimeSpan duration)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        MessageText.Text = text;

        // Viewbox 以 120px 为基准按可用空间缩放：短文本会放大到铺满屏幕，长文本自动缩小。
        // 固定宽度让 TextBlock 先换行，否则 Viewbox 会给它无限宽度、永远不换行。
        MessageText.FontSize = 120;
        var screen = Screens.Primary ?? Screens.All.FirstOrDefault();
        var scaling = screen?.Scaling ?? 1.0;
        var widthDip = (screen?.WorkingArea.Width ?? 1920) / scaling;
        MessageText.Width = Math.Max(320, widthDip * 0.72);

        if (!IsVisible)
        {
            if (screen != null)
            {
                Position = new PixelPoint(screen.Bounds.X, screen.Bounds.Y);
            }
            WindowState = WindowState.FullScreen;
            Show();
        }

        WindowState = WindowState.FullScreen;
        Topmost = true;

        _timer.Interval = duration > TimeSpan.Zero ? duration : TimeSpan.FromSeconds(12);
        _timer.Stop();
        _timer.Start();
    }

    private void Dismiss()
    {
        _timer.Stop();
        Hide();
    }

    private void OnDismiss(object? sender, PointerPressedEventArgs e) => Dismiss();

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Dismiss();
        }
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (e.CloseReason is WindowCloseReason.OSShutdown or WindowCloseReason.ApplicationShutdown)
        {
            return;
        }
        e.Cancel = true;
        Dismiss();
    }
}
