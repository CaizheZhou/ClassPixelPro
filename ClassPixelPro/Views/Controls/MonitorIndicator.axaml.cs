using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform;

namespace ClassPixelPro.Views.Controls;

/// <summary>
/// 监控指示器：置顶浮动窗口，显示在屏幕左上角，包含摄像头图标、闪烁红点和"监控中"文本。
/// 支持拖动定位，双击可切换可见性。
/// </summary>
/// <remarks>
/// 由 <see cref="ClassPixelPro.Views.MainWindow"/> 根据
/// <c>Settings.IsRemoteMonitorEnabled &amp;&amp; Settings.IsMonitorIndicatorEnabled &amp;&amp; RemoteMonitorService.IsRunning</c>
/// 控制显示/隐藏。
/// </remarks>
public partial class MonitorIndicator : Window
{
    private bool _isDragging;
    private Point _dragStartPoint;
    private Point _dragStartWindowPosition;

    public MonitorIndicator()
    {
        InitializeComponent();
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaChromeHints = ExtendClientAreaChromeHints.NoChrome;
        ExtendClientAreaTitleBarHeightHint = -1;
    }

    /// <summary>
    /// 将指示器定位到指定屏幕的左上角（带边距）。
    /// </summary>
    /// <param name="screen">目标屏幕。</param>
    /// <param name="margin">距屏幕左上角的边距（像素）。</param>
    public void PositionAtTopLeft(Screen screen, double margin = 12)
    {
        if (screen == null) return;
        var bounds = screen.Bounds;
        Position = new PixelPoint((int)(bounds.X + margin), (int)(bounds.Y + margin));
    }

    private void MonitorIndicator_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // 左键拖动
        if (e.Pointer.IsPrimary)
        {
            _isDragging = true;
            _dragStartPoint = e.GetPosition(this);
            _dragStartWindowPosition = Position.ToPoint(1.0);
            e.Pointer.Capture(this);
            e.Handled = true;
        }
    }

    private void MonitorIndicator_OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isDragging) return;
        var current = e.GetPosition(this);
        var delta = current - _dragStartPoint;
        var newPos = _dragStartWindowPosition + delta;
        Position = new PixelPoint((int)newPos.X, (int)newPos.Y);
        e.Handled = true;
    }

    private void MonitorIndicator_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_isDragging)
        {
            _isDragging = false;
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    private void MonitorIndicator_OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        // 双击切换可见性（隐藏后需通过设置页重新启用指示器才能恢复）
        Hide();
        e.Handled = true;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        PointerMoved += MonitorIndicator_OnPointerMoved;
        PointerReleased += MonitorIndicator_OnPointerReleased;
    }

    protected override void OnClosed(EventArgs e)
    {
        PointerMoved -= MonitorIndicator_OnPointerMoved;
        PointerReleased -= MonitorIndicator_OnPointerReleased;
        base.OnClosed(e);
    }
}