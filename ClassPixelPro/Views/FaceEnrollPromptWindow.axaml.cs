using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Markup.Xaml;
using ClassPixelPro.Core.Controls;
using ClassPixelPro.Services.FaceAttendance;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ClassPixelPro.Views;

/// <summary>
/// 智能导入提问弹窗的 ViewModel：把 <see cref="SmartPrompt"/> 摊成可绑定的属性，
/// 用户的选择回填到 <see cref="SmartDecision"/>。
/// </summary>
public partial class FaceEnrollPromptViewModel : ObservableObject
{
    /// <summary>「新建同学…」这一项在候选下拉里的固定标记。</summary>
    public const string NewStudentMarker = "\u2731 新建同学…";

    private readonly SmartPrompt _prompt;

    [ObservableProperty] private int _selectedFaceIndex;
    [ObservableProperty] private CandidateItem? _selectedOwner;
    [ObservableProperty] private string _newName = "";
    [ObservableProperty] private string _hintText = "";
    [ObservableProperty] private Bitmap? _previewImage;

    /// <summary>用户最终做出的选择；窗口被直接关掉时按「跳过」处理。</summary>
    public SmartDecision Decision { get; private set; } = new() { Skip = true };

    /// <summary>下拉框候选：新建同学 + 花名册里的人。</summary>
    public ObservableCollection<CandidateItem> CandidateItems { get; } = new();

    /// <summary>照片里检测到的每一张脸。</summary>
    public ObservableCollection<SmartFaceOption> FaceItems { get; } = new();

    /// <summary>归属同学；选了「新建同学…」时为 null。</summary>
    public Student? SelectedStudent => SelectedOwner?.Student;

    /// <summary>照片里检测到几张脸。</summary>
    public int FaceCount => _prompt.FaceCount;

    /// <summary>是否允许点「确定」：选了脸，且要么选了同学、要么填了新名字。</summary>
    public bool CanConfirm => SelectedFaceIndex > 0
        && (SelectedOwner?.Student != null || !string.IsNullOrWhiteSpace(NewName));

    public FaceEnrollPromptViewModel()
    {
        _prompt = new SmartPrompt();
    }

    public FaceEnrollPromptViewModel(SmartPrompt prompt, IReadOnlyList<Student> candidates) : this()
    {
        _prompt = prompt ?? throw new ArgumentNullException(nameof(prompt));
        var roster = candidates ?? Array.Empty<Student>();

        CandidateItems.Add(CandidateItem.NewStudent());
        foreach (var s in roster)
        {
            CandidateItems.Add(CandidateItem.For(s));
        }

        foreach (var f in prompt.Faces)
        {
            FaceItems.Add(f);
        }

        // 服务层按相似度给出的最佳猜测直接预填；相似度没把握时改用文件名匹配结果。
        var guess = prompt.Best ?? prompt.FileNameMatch;
        SelectedOwner = guess == null
            ? CandidateItems[0]
            : CandidateItems.FirstOrDefault(x => x.Student?.Id == guess.Id) ?? CandidateItems[0];

        // 默认选中服务层建议的那张脸，省用户一次点击。
        var suggested = prompt.Faces.FirstOrDefault(x => x.Best != null) ?? prompt.Faces.FirstOrDefault();
        SelectedFaceIndex = suggested?.Index ?? 0;

        RenderPreview();
        UpdateHint();
    }

    partial void OnSelectedOwnerChanged(CandidateItem? value)
    {
        OnPropertyChanged(nameof(SelectedStudent));
        OnPropertyChanged(nameof(CanConfirm));
    }

    partial void OnSelectedFaceIndexChanged(int value)
    {
        OnPropertyChanged(nameof(CanConfirm));
    }

    partial void OnNewNameChanged(string value)
    {
        OnPropertyChanged(nameof(CanConfirm));
    }

    /// <summary>把服务层给的 JPEG 预览图渲染出来（优先带编号方框的整图）。</summary>
    private void RenderPreview()
    {
        var bytes = _prompt.AnnotatedPreview ?? _prompt.Thumbnail;
        if (bytes == null || bytes.Length == 0) return;
        try
        {
            using var ms = new MemoryStream(bytes);
            PreviewImage = new Bitmap(ms);
        }
        catch
        {
            // 预览图渲染失败不影响选择本身，用户仍可按文字信息挑
        }
    }

    private void UpdateHint()
    {
        HintText = _prompt.FileNameAmbiguous
            ? $"文件名「{_prompt.FileName}」能同时对上多位同学，请手动指定。"
            : _prompt.FileNameMatch != null
                ? $"文件名「{_prompt.FileName}」看起来是「{_prompt.FileNameMatch.Name}」。"
                : $"照片「{_prompt.FileName}」里检测到 {_prompt.FaceCount} 张脸，请选一张并指定归属同学。";
    }

    /// <summary>用户点了「确定」。</summary>
    public void Confirm()
    {
        Decision = new SmartDecision
        {
            FaceIndex = SelectedFaceIndex,
            Target = SelectedStudent,
            NewName = SelectedStudent == null ? NewName.Trim() : ""
        };
    }

    /// <summary>
    /// 下拉框的一项：要么是「新建同学…」，要么是花名册里的一位同学。
    /// </summary>
    public sealed class CandidateItem
    {
        /// <summary>对应的同学；「新建同学…」项为 null。</summary>
        public Student? Student { get; init; }

        /// <summary>是不是「新建同学…」这一项。</summary>
        public bool IsNewStudent => Student == null;

        /// <summary>下拉框里显示的文字。</summary>
        public string DisplayText => Student?.Name ?? NewStudentMarker;

        /// <summary>构造「新建同学…」项。</summary>
        public static CandidateItem NewStudent() => new();

        /// <summary>构造一位同学的项。</summary>
        public static CandidateItem For(Student student) => new() { Student = student };
    }
}

/// <summary>
/// 智能导入的提问弹窗：照片里检测到多张脸时，让用户挑一张、指定归属同学。
/// <para>
/// 界面刻意做成「一张脸一行的列表」而不是只显示一张大图：照片里出现多个人时
/// （合影、班集体照），必须让用户明确点选，否则会把 A 的脸录到 B 名下。
/// </para>
/// </summary>
public partial class FaceEnrollPromptWindow : MyWindow
{
    /// <summary>本窗口的 ViewModel。</summary>
    public FaceEnrollPromptViewModel Vm { get; }

    public FaceEnrollPromptWindow()
    {
        Vm = new FaceEnrollPromptViewModel();
        // XAML 里所有绑定都带 Vm. 前缀，DataContext 必须是窗口本身；
        // 之前错设成 Vm，导致整个弹窗的预览、脸列表、归属下拉全部绑定失败显示为空。
        DataContext = this;
        InitializeComponent();
    }

    public FaceEnrollPromptWindow(SmartPrompt prompt, IReadOnlyList<Student> candidates) : this()
    {
        Vm = new FaceEnrollPromptViewModel(prompt, candidates);
        DataContext = this;
        Title = $"这张照片是谁？（{prompt.Index}/{prompt.Total}）";
    }

    /// <summary>用户最终做出的选择。</summary>
    public SmartDecision Decision => Vm.Decision;

    /// <summary>窗口打开后把建议选中的脸在列表里高亮出来。</summary>
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        var face = FaceList.Items?.OfType<SmartFaceOption>().FirstOrDefault(x => x.Index == Vm.SelectedFaceIndex);
        if (face != null)
        {
            FaceList.SelectedItem = face;
        }
    }

    private void FaceList_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        Vm.SelectedFaceIndex = (FaceList.SelectedItem as SmartFaceOption)?.Index ?? 0;
    }

    private void ButtonConfirm_OnClick(object? sender, RoutedEventArgs e)
    {
        Vm.Confirm();
        Close(Vm.Decision);
    }

    private void ButtonSkip_OnClick(object? sender, RoutedEventArgs e)
    {
        Close(new SmartDecision { Skip = true });
    }

    private void ButtonCancel_OnClick(object? sender, RoutedEventArgs e)
    {
        Close(new SmartDecision { Cancel = true });
    }
}
