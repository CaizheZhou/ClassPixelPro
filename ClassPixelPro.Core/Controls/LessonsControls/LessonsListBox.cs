using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ClassPixelPro.Core.Abstractions.Services;
using ClassPixelPro.Shared;
using ClassPixelPro.Shared.Abstraction.Models;
using ClassPixelPro.Shared.ComponentModels;
using ClassPixelPro.Shared.Models.Profile;

namespace ClassPixelPro.Core.Controls.LessonsControls;

/// <summary>
/// 用于显示课表的列表控件。 
/// </summary>
public class LessonsListBox : ListBox
{
    public static string MinimizedLessonControlResourceKey { get; } = "MinimizedLessonControl";
    public static string ExpandedLessonControlResourceKey { get; } = "ExpandedLessonControl";
    public static string SeparatorLessonControlResourceKey { get; } = "SeparatorLessonControl";
    public static string NormalItemContainerStyleKey { get; } = "NormalItemContainer";
    public static string BlankItemContainerStyleKey { get; } = "BlankItemContainer";


    /// <summary>
    /// 属性<see cref="ClassPlan"/>的依赖属性。
    /// </summary>
    public static readonly StyledProperty<ClassPlan> ClassPlanProperty =
        AvaloniaProperty.Register<LessonsListBox, ClassPlan>(
            nameof(ClassPlan));

    public ClassPlan ClassPlan
    {
        get => GetValue(ClassPlanProperty);
        set => SetValue(ClassPlanProperty, value);
    }

    public static readonly AttachedProperty<ObservableDictionary<Guid, Subject>> SubjectsProperty =
        AvaloniaProperty.RegisterAttached<LessonsListBox, Control, ObservableDictionary<Guid, Subject>>("Subjects", inherits:true);

    public static void SetSubjects(Control obj, ObservableDictionary<Guid, Subject> value) => obj.SetValue(SubjectsProperty, value);
    public static ObservableDictionary<Guid, Subject> GetSubjects(Control obj) => obj.GetValue(SubjectsProperty);

    public static readonly AttachedProperty<bool> IsLiveUpdatingEnabledProperty =
        AvaloniaProperty.RegisterAttached<LessonsListBox, Control, bool>("IsLiveUpdatingEnabled", false, true);

    public static void SetIsLiveUpdatingEnabled(Control obj, bool value) => obj.SetValue(IsLiveUpdatingEnabledProperty, value);
    public static bool GetIsLiveUpdatingEnabled(Control obj) => obj.GetValue(IsLiveUpdatingEnabledProperty);
    

    public static readonly StyledProperty<bool> HighlightChangedClassProperty =
        AvaloniaProperty.Register<LessonsListBox, bool>(
            nameof(HighlightChangedClass));

    public bool HighlightChangedClass
    {
        get => GetValue(HighlightChangedClassProperty);
        set => SetValue(HighlightChangedClassProperty, value);
    }

    public static readonly StyledProperty<ILessonControlSettings> LessonControlSettingsProperty =
        AvaloniaProperty.Register<LessonsListBox, ILessonControlSettings>(
            nameof(LessonControlSettings));

    public ILessonControlSettings LessonControlSettings
    {
        get => GetValue(LessonControlSettingsProperty);
        set => SetValue(LessonControlSettingsProperty, value);
    }

    public static readonly StyledProperty<bool> DiscardHidingDefaultProperty =
        AvaloniaProperty.Register<LessonsListBox, bool>(
            nameof(DiscardHidingDefault));

    public bool DiscardHidingDefault
    {
        get => GetValue(DiscardHidingDefaultProperty);
        set => SetValue(DiscardHidingDefaultProperty, value);
    }

    public static readonly StyledProperty<bool> ShowCurrentTimeLayoutItemOnlyOnClassProperty =
        AvaloniaProperty.Register<LessonsListBox, bool>(
            nameof(ShowCurrentTimeLayoutItemOnlyOnClass));

    public bool ShowCurrentTimeLayoutItemOnlyOnClass
    {
        get => GetValue(ShowCurrentTimeLayoutItemOnlyOnClassProperty);
        set => SetValue(ShowCurrentTimeLayoutItemOnlyOnClassProperty, value);
    }

    public static readonly StyledProperty<bool> HideFinishedClassProperty =
        AvaloniaProperty.Register<LessonsListBox, bool>(
            nameof(HideFinishedClass));

    public bool HideFinishedClass
    {
        get => GetValue(HideFinishedClassProperty);
        set => SetValue(HideFinishedClassProperty, value);
    }

    public static readonly StyledProperty<bool> FadeCompletedClassesProperty = AvaloniaProperty.Register<LessonsListBox, bool>(
        nameof(FadeCompletedClasses));

    public bool FadeCompletedClasses
    {
        get => GetValue(FadeCompletedClassesProperty);
        set => SetValue(FadeCompletedClassesProperty, value);
    }


    /// <inheritdoc />
    public LessonsListBox()
    {
        //ItemContainerStyleSelector = new LessonsListBoxItemContainerStyleSelector(this);
        //ItemTemplateSelector = new LessonsListBoxItemTemplateSelector(this);

        // 课程表横向滚动：滚轮把垂直增量转成水平滚动
        PointerWheelChanged += OnPointerWheelChanged;
        // 缩放/宽度变化后，把当前课重新滚回可视区
        SizeChanged += (_, _) => ScrollCurrentLessonIntoView();
    }

    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        var sv = this.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (sv == null || sv.Extent.Width <= sv.Viewport.Width) return;
        var maxOffset = sv.Extent.Width - sv.Viewport.Width;
        sv.Offset = new Vector(Math.Clamp(sv.Offset.X - e.Delta.Y * 48, 0, maxOffset), 0);
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        // 当前课切换（含换课/换节）时，等布局把容器排好再把新当前课滚进可视区
        if (e.Property == SelectedItemProperty || e.Property == SelectedIndexProperty)
        {
            Dispatcher.UIThread.Post(ScrollCurrentLessonIntoView);
        }
    }

    /// <summary>
    /// 把当前选中的课程滚进可视区域并尽量居中。
    /// 缩放过大导致课程表超出可视宽度时，保证「正在进行的课」始终可见。
    /// </summary>
    public void ScrollCurrentLessonIntoView()
    {
        if (SelectedIndex < 0) return;
        var container = ContainerFromIndex(SelectedIndex);
        if (container is not Visual visual) return;
        var sv = this.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (sv == null || sv.Extent.Width <= sv.Viewport.Width) return;
        if (sv.Content is not Visual content) return;
        var t = visual.TransformToVisual(content);
        if (t == null) return;
        var itemCenter = t.Value.Transform(new Point(visual.Bounds.Width / 2, 0)).X;
        var maxOffset = sv.Extent.Width - sv.Viewport.Width;
        var target = Math.Clamp(itemCenter - sv.Viewport.Width / 2, 0, maxOffset);
        sv.Offset = new Vector(target, 0);
    }
}