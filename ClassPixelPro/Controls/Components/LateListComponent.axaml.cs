using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Threading;
using ClassPixelPro.Core.Abstractions.Controls;
using ClassPixelPro.Core.Attributes;
using ClassPixelPro.Models.ComponentSettings;
using ClassPixelPro.Services.FaceAttendance;

namespace ClassPixelPro.Controls.Components;

/// <summary>
/// 主界面组件：显示最近一次人脸考勤识别出的迟到同学。
/// <para>
/// 与 ClassIsland 插件「早读迟到名单」同构：数据源是本机
/// <see cref="AttendanceService.LastResult"/>，迟到名单与统计数字只在主界面展示，
/// 上送到集控的是同一份姓名与统计，链路上不出现人脸特征。</para>
/// </summary>
[PseudoClasses(":empty")]
[ComponentInfo("8C7E1B5A-3F4D-4E2A-9B6C-1D0F7A2E5C31", "早读迟到名单", "\uE8D4",
    "显示最近一次人脸考勤识别出的迟到同学；没有迟到时自动隐藏。")]
public partial class LateListComponent : ComponentBase<LateListComponentSettings>
{
    private readonly AttendanceService _attendance;

    private bool _subscribed;
    private bool _settingsHooked;

    public LateListComponent(AttendanceService attendance)
    {
        _attendance = attendance;
        InitializeComponent();
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Subscribe();
        Refresh();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Unsubscribe();
        base.OnDetachedFromVisualTree(e);
    }

    private void Subscribe()
    {
        if (_subscribed) return;
        _attendance.ResultChanged += OnChanged;
        _subscribed = true;
    }

    private void Unsubscribe()
    {
        if (!_subscribed) return;
        _attendance.ResultChanged -= OnChanged;
        _subscribed = false;
    }

    private void OnChanged(object? sender, EventArgs e)
    {
        // 结果通知本身已在 UI 线程上抛出（AttendanceService.Notify），这里直接刷新即可。
        Dispatcher.UIThread.Post(Refresh);
    }

    private void HookSettings()
    {
        if (_settingsHooked || Settings == null) return;
        Settings.PropertyChanged += (_, _) => Dispatcher.UIThread.Post(Refresh);
        _settingsHooked = true;
    }

    private void Refresh()
    {
        HookSettings();
        if (Settings == null) return;

        var record = _attendance.LastResult;

        TitleText.Text = string.IsNullOrWhiteSpace(Settings.Title) ? "早读迟到" : Settings.Title;
        TitleText.FontSize = Settings.FontSize + 1;

        var names = "";
        var lateCount = 0;
        if (record is { Success: true } && record.Late.Count > 0)
        {
            lateCount = record.Late.Count;
            var max = Settings.MaxDisplayNames;
            if (max > 0 && lateCount > max)
            {
                names = string.Join("、", record.Late.Take(max).Select(x => x.Name)) + $" 等 {lateCount} 人";
            }
            else
            {
                names = string.Join("、", record.Late.Select(x => x.Name));
            }
        }

        NamesText.Text = names;
        NamesText.FontSize = Settings.FontSize;
        NamesText.IsVisible = names.Length > 0;

        CountBadge.IsVisible = lateCount > 0;
        CountText.Text = $"{lateCount} 人";
        PseudoClasses.Set(":empty", lateCount == 0);

        // 状态行：空名单时显示一句安抚文案，避免整块组件只剩标题
        StatusText.Text = record is { Success: true }
            ? (lateCount == 0 ? "本次无人迟到" : $"识别完成，迟到 {lateCount} 人")
            : _attendance.StatusText;
        StatusText.IsVisible = Settings.ShowStatusLine;

        TimeText.Text = record is { Success: true }
            ? $"识别时间 {record.CaptureTime:HH:mm:ss} · 抓拍 {record.PhotoCount} 张 · 检测到 {record.FaceCount} 张脸 · 耗时 {record.ElapsedSeconds:F1} 秒"
            : "";
        TimeText.IsVisible = Settings.ShowDetectTime && record is { Success: true };

        RootCard.IsVisible = !(Settings.HideWhenEmpty && lateCount == 0);
    }
}
