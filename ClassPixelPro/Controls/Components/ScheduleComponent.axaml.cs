using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Avalonia.Controls.Metadata;
using Avalonia.Data.Converters;
using Avalonia.Interactivity;
using ClassPixelPro.Core.Abstractions.Controls;
using ClassPixelPro.Core.Abstractions.Services;
using ClassPixelPro.Core.Attributes;
using ClassPixelPro.Models;
using ClassPixelPro.Services;
using ClassPixelPro.Core.Models.AttachedSettings;
using ClassPixelPro.Models.ComponentSettings;
using ClassPixelPro.Shared.Abstraction.Models;
using ClassPixelPro.Shared.Enums;
using ClassPixelPro.Shared.Models.Profile;
using ReactiveUI;

namespace ClassPixelPro.Controls.Components;

/// <summary>
/// ScheduleComponent.xaml 的交互逻辑
/// </summary>
[MigrateFrom("E7831603-61A0-4180-B51B-54AD75B1A4D3")]  // 课程表（旧）
[ComponentInfo("1DB2017D-E374-4BC6-9D57-0B4ADF03A6B8", "课程表", "\ue751", "显示当前的课程表信息。")]
[PseudoClasses(":show-tomorrow-schedule", ":show-tomorrow-schedule-after-school", ":show-tomorrow-schedule-on-empty", ":show-tomorrow-schedule-always")]
public partial class ScheduleComponent : ComponentBase<LessonControlSettings>, INotifyPropertyChanged
{
    private bool _hideFinishedClass;

    public static readonly DirectProperty<ScheduleComponent, bool> HideFinishedClassProperty = AvaloniaProperty.RegisterDirect<ScheduleComponent, bool>(
        nameof(HideFinishedClass), o => o.HideFinishedClass, (o, v) => o.HideFinishedClass = v);

    public bool HideFinishedClass
    {
        get => _hideFinishedClass;
        set => SetAndRaise(HideFinishedClassProperty, ref _hideFinishedClass, value);
    }

    private bool _showEmptyPlaceholder;

    public static readonly DirectProperty<ScheduleComponent, bool> ShowEmptyPlaceholderProperty = AvaloniaProperty.RegisterDirect<ScheduleComponent, bool>(
        nameof(ShowEmptyPlaceholder), o => o.ShowEmptyPlaceholder, (o, v) => o.ShowEmptyPlaceholder = v);

    public bool ShowEmptyPlaceholder
    {
        get => _showEmptyPlaceholder;
        set => SetAndRaise(ShowEmptyPlaceholderProperty, ref _showEmptyPlaceholder, value);
    }
    
    private bool _showCurrentLessonOnlyOnClass = false;
    private bool _isAfterSchool = false;
    private ClassPlan? _tomorrowClassPlan;
    private ClassPlan? _tomorrowClassPlan1;
    public ILessonsService LessonsService { get; }

    public SettingsService SettingsService { get; }

    public IProfileService ProfileService { get; }
    public IExactTimeService ExactTimeService { get; }

    public static readonly StyledProperty<int> LessonsListBoxSelectedIndexProperty = AvaloniaProperty.Register<ScheduleComponent, int>(
        nameof(LessonsListBoxSelectedIndex));

    private IDisposable? _tomorrowScheduleShowModeObserver;
    private IDisposable? _hideFinishedClassObserver;
    private IDisposable? _showEmptyPlaceholderObserver;
    public int LessonsListBoxSelectedIndex
    {
        get => GetValue(LessonsListBoxSelectedIndexProperty);
        set => SetValue(LessonsListBoxSelectedIndexProperty, value);
    }

    public bool IsAfterSchool
    {
        get => _isAfterSchool;
        set
        {
            if (value == _isAfterSchool) return;
            _isAfterSchool = value;
            OnPropertyChanged();
        }
    }

    public bool ShowCurrentLessonOnlyOnClass
    {
        get => _showCurrentLessonOnlyOnClass;
        set
        {
            if (value == _showCurrentLessonOnlyOnClass) return;
            _showCurrentLessonOnlyOnClass = value;
            OnPropertyChanged();
        }
    }

    public ClassPlan? TomorrowClassPlan
    {
        get => _tomorrowClassPlan1;
        set
        {
            if (Equals(value, _tomorrowClassPlan1)) return;
            _tomorrowClassPlan1 = value;
            OnPropertyChanged();
        }
    }

    public ScheduleComponent(ILessonsService lessonsService, SettingsService settingsService, IProfileService profileService, IExactTimeService exactTimeService)
    {
        LessonsService = lessonsService;
        SettingsService = settingsService;
        ProfileService = profileService;
        ExactTimeService = exactTimeService;
        
        AttachedToVisualTree += (_, _) => LessonsService.PostMainTimerTicked += LessonsServiceOnPostMainTimerTicked;
        AttachedToVisualTree += (_, _) => LessonsService.CurrentTimeStateChanged += OnLessonsServiceOnCurrentTimeStateChanged;
        AttachedToVisualTree += (_, _) => LessonsService.PropertyChanged += LessonsServiceOnPropertyChanged;
        AttachedToVisualTree += OnAttachedToVisualTree;
        DetachedFromVisualTree += (_, _) => LessonsService.PostMainTimerTicked -= LessonsServiceOnPostMainTimerTicked;
        DetachedFromVisualTree += (_, _) => LessonsService.CurrentTimeStateChanged -= OnLessonsServiceOnCurrentTimeStateChanged;
        DetachedFromVisualTree += (_, _) => LessonsService.PropertyChanged -= LessonsServiceOnPropertyChanged;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
        InitializeComponent();
        CurrentTimeStateChanged();
    }

    private bool _examHooked;

    private void OnExamStateChanged()
    {
        ApplyExamVisibility();
    }

    private void ApplyExamVisibility()
    {
        // 考试模式要求隐藏课表：整个组件折叠（「考试中」由角标窗口提示）
        IsVisible = !(ClassPixelPro.Services.Management.ExamModeService.Instance is
                      { IsActive: true, HideSchedule: true });
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs visualTreeAttachmentEventArgs)
    {
        if (_examHooked && ClassPixelPro.Services.Management.ExamModeService.Instance is { } examService)
        {
            examService.StateChanged -= OnExamStateChanged;
            _examHooked = false;
        }
        if (_scaleHost != null)
        {
            _scaleHost.SizeChanged -= ScaleHostOnSizeChanged;
            _scaleHost = null;
        }
        _tomorrowScheduleShowModeObserver?.Dispose();
        _hideFinishedClassObserver?.Dispose();
        _showEmptyPlaceholderObserver?.Dispose();
        
        _tomorrowScheduleShowModeObserver = null;
        _hideFinishedClassObserver = null;
        _showEmptyPlaceholderObserver = null;
    }

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs visualTreeAttachmentEventArgs)
    {
        CurrentTimeStateChanged();
        CheckTomorrowClassShowMode();
        _tomorrowScheduleShowModeObserver ??= Settings
            .ObservableForProperty(x => x.TomorrowScheduleShowMode)
            .Subscribe(_ => CheckTomorrowClassShowMode());
        _hideFinishedClassObserver ??= Settings
            .ObservableForProperty(x => x.HideFinishedClass)
            .Subscribe(_ =>
            {
                HideFinishedClass = Settings.HideFinishedClass;
                UpdateTomorrowVisibility();
            });
        _showEmptyPlaceholderObserver ??= Settings
            .ObservableForProperty(x => x.ShowPlaceholderOnEmptyClassPlan)
            .Subscribe(_ => ShowEmptyPlaceholder = Settings.ShowPlaceholderOnEmptyClassPlan);

        HideFinishedClass = Settings.HideFinishedClass;
        ShowEmptyPlaceholder = Settings.ShowPlaceholderOnEmptyClassPlan;
        MainLessonsListBox.SelectedIndex = LessonsListBoxSelectedIndex;
        UpdateTomorrowVisibility();

        // 进入可视树后先把课程表宽度压到屏幕内（布局完成后再算一次，结果更准）。
        // 只随窗口/缩放变化重算——不挂自身 SizeChanged：当前课的倒计时文本每秒变宽，
        // 会引发「尺寸变化→重设宽度→再布局」的每秒循环，表现为整条岛栏每秒闪烁。
        UpdateScheduleMaxWidth();
        Dispatcher.UIThread.Post(UpdateScheduleMaxWidth);

        // 考试模式：生效且要求隐藏时折叠课程表
        if (ClassPixelPro.Services.Management.ExamModeService.Instance is { } exam)
        {
            if (!_examHooked)
            {
                _examHooked = true;
                exam.StateChanged += OnExamStateChanged;
            }
            ApplyExamVisibility();
        }
    }

    private LayoutTransformControl? _scaleHost;
    private double _lastAppliedMaxWidth;

    private void ScaleHostOnSizeChanged(object? sender, SizeChangedEventArgs e) => UpdateScheduleMaxWidth();

    /// <summary>
    /// 限制课程表可用宽度。缩放过大时整条岛栏（天气+时间+课程表）会超过屏幕宽度，
    /// 默认整条对称溢出、课程表被截断。这里把本组件宽度压到
    /// 「缩放后的可用宽度 − 其余组件宽度」，超出的课程由 LessonsListBox 内部
    /// 横向滚动承接，并自动把当前课滚进可视区域。
    /// </summary>
    private void UpdateScheduleMaxWidth()
    {
        var host = this.GetVisualAncestors().OfType<LayoutTransformControl>().FirstOrDefault();
        var line = this.GetVisualAncestors().OfType<MainWindowLine>().FirstOrDefault();
        if (host == null || line == null) return;
        if (!ReferenceEquals(_scaleHost, host))
        {
            if (_scaleHost != null) _scaleHost.SizeChanged -= ScaleHostOnSizeChanged;
            _scaleHost = host;
            _scaleHost.SizeChanged += ScaleHostOnSizeChanged;
        }
        // 窗口随缩放变化会先改 LayoutTransformControl 的尺寸，从它触发重算
        var window = VisualRoot as Window;
        if (window == null) return;

        // 真正的可用宽度基准是窗口宽度（= 屏幕宽）：
        // host.Bounds 是「缩放后的岛栏宽度」，拿它当基准会自引用（岛越宽基准越大），永远约束不住。
        // 窗口宽是像素；岛栏内部都是缩放前的布局值——按缩放系数换算到同一单位再相减。
        var scale = host.LayoutTransform.Value.M11;
        if (scale <= 0) scale = 1;
        var available = window.Bounds.Width / scale;
        // 其余部分宽度 = 岛栏内容根（GridContentRoot：天气/时间等所有组件）− 本组件，
        // 再加内容根自身的左右 Margin(12 0)。注意不能用 MainWindowLine 的宽度——
        // 行控件在 ItemsControl 里是拉伸到整个窗口宽的，拿它算会得出「剩余≈0」，
        // 课程表被压成一条，只剩当前课可见。
        var islandRoot = line.GetVisualDescendants().OfType<Grid>().FirstOrDefault(g => g.Name == "GridContentRoot");
        if (islandRoot == null) return;

        // 未溢出（岛栏自然宽度没有超过屏幕）时直接移除宽度限制，恢复自然布局：
        // 上限值一旦参与布局，就会和每秒变化的倒计时文本互相影响，造成反复排版闪烁。
        if (islandRoot.Bounds.Width <= available)
        {
            if (_lastAppliedMaxWidth != 0)
            {
                _lastAppliedMaxWidth = 0;
                MainLessonsListBox.MaxWidth = double.PositiveInfinity;
                TomorrowLessonsListBox.MaxWidth = double.PositiveInfinity;
            }
            return;
        }

        var otherWidth = Math.Max(0, islandRoot.Bounds.Width - Bounds.Width) + 24;
        var max = Math.Max(160, available - otherWidth - 12);
        // 迟滞：差异小于 8px 不重设，避免临界状态下来回横跳
        if (_lastAppliedMaxWidth != 0 && Math.Abs(max - _lastAppliedMaxWidth) < 8) return;
        _lastAppliedMaxWidth = max;
        MainLessonsListBox.MaxWidth = max;
        TomorrowLessonsListBox.MaxWidth = max;
    }

    private void CheckTomorrowClassShowMode()
    {
        PseudoClasses.Set(":show-tomorrow-schedule", Settings.TomorrowScheduleShowMode is 1 or 2 or 3);
        PseudoClasses.Set(":show-tomorrow-schedule-after-school", Settings.TomorrowScheduleShowMode is 1);
        PseudoClasses.Set(":show-tomorrow-schedule-always", Settings.TomorrowScheduleShowMode is 2);
        UpdateTomorrowVisibility();
    }

    private void LessonsServiceOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LessonsService.CurrentClassPlan))
        {
            CurrentTimeStateChanged();
        }
    }

    private void OnLessonsServiceOnCurrentTimeStateChanged(object? o, EventArgs eventArgs)
    {
        CurrentTimeStateChanged();
        UpdateTomorrowVisibility();
    }

    private void CurrentTimeStateChanged()
    {
        IsAfterSchool =
            LessonsService.CurrentState == TimeState.AfterSchool ||
            LessonsService.CurrentClassPlan == null;
    }

    public override void OnMigrated(Guid sourceId, object? settings)
    {
        var appSettings = SettingsService.Settings;
        Settings.CountdownSeconds = appSettings.CountdownSeconds;
        Settings.ExtraInfoType = appSettings.ExtraInfoType;
        Settings.IsCountdownEnabled = appSettings.IsCountdownEnabled;
        Settings.ShowExtraInfoOnTimePoint = appSettings.ShowExtraInfoOnTimePoint;
        base.OnMigrated(sourceId, appSettings);
    }

    private void LessonsServiceOnPostMainTimerTicked(object? sender, EventArgs e)
    {
        var settingsSource =
            (ILessonControlSettings?)IAttachedSettingsHostService.GetAttachedSettingsByPriority<LessonControlAttachedSettings>(
                new Guid("58e5b69a-764a-472b-bcf7-003b6a8c7fdf"),
                LessonsService.CurrentSubject,
                LessonsService.CurrentTimeLayoutItem,
                LessonsService.CurrentClassPlan,
                LessonsService.CurrentClassPlan?.TimeLayout) ??
            Settings;
        ShowCurrentLessonOnlyOnClass = settingsSource.ShowCurrentLessonOnlyOnClass;
        TomorrowClassPlan = LessonsService.GetClassPlanByDate(ExactTimeService.GetCurrentLocalDateTime() + TimeSpan.FromDays(1));
        MainLessonsListBox.SelectedIndex = LessonsListBoxSelectedIndex;
        UpdateTomorrowVisibility();
    }

    private void UpdateTomorrowVisibility()
    {
        var showOnEmpty = Settings.TomorrowScheduleShowMode == 3;
        if (!showOnEmpty)
        {
            PseudoClasses.Set(":show-tomorrow-schedule-on-empty", false);
            return;
        }
        var now = ExactTimeService.GetCurrentLocalDateTime().TimeOfDay;
        var selectedItem = LessonsService.CurrentTimeLayoutItem;
        var classPlan = LessonsService.CurrentClassPlan;
        var hasDisplayable = HasDisplayableItems(classPlan, now, selectedItem);
        PseudoClasses.Set(":show-tomorrow-schedule-on-empty", !hasDisplayable);
    }

    private bool HasDisplayableItems(ClassPlan? classPlan, TimeSpan now, TimeLayoutItem? selectedItem)
    {
        var items = classPlan?.ValidTimeLayoutItems;
        if (items == null || items.Count == 0) return false;
        return items.Any(item => ShouldDisplay(item, now, selectedItem, HideFinishedClass, ShowCurrentLessonOnlyOnClass));
    }

    private static bool ShouldDisplay(TimeLayoutItem item, TimeSpan now, TimeLayoutItem? selectedItem, bool hideFinishedClass, bool showCurrentLessonOnlyOnClass)
    {
        if (item.TimeType is 2 or 3) return false;
        if (hideFinishedClass && item.EndTime < now) return false;
        if (item.IsHideDefault && !Equals(selectedItem, item)) return false;
        if (item.TimeType == 1 && !Equals(selectedItem, item)) return false;
        if (showCurrentLessonOnlyOnClass && selectedItem?.TimeType == 0 && !Equals(selectedItem, item)) return false;
        return true;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}

