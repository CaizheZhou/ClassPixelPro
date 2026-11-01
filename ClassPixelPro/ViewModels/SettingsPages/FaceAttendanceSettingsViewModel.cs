using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using ClassPixelPro.Services;
using ClassPixelPro.Services.FaceAttendance;
using ClassPixelPro.Services.Management;
using ClassPixelPro.Services.RemoteMonitor;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace ClassPixelPro.ViewModels.SettingsPages;

/// <summary>
/// 下拉框用的一项：值 + 显示名 + 一句话说明。
/// </summary>
public sealed class NamedOption<T>
{
    public NamedOption(T value, string displayName, string description)
    {
        Value = value;
        DisplayName = displayName;
        Description = description;
    }

    /// <summary>选项对应的值。</summary>
    public T Value { get; }

    /// <summary>界面上显示的名字。</summary>
    public string DisplayName { get; }

    /// <summary>一句话说明，帮用户选。</summary>
    public string Description { get; }
}

/// <summary>
/// 抓拍分辨率档位。
/// </summary>
public sealed class ResolutionOption
{
    public ResolutionOption(int width, int height, string description)
    {
        Width = width;
        Height = height;
        Description = description;
    }

    /// <summary>画面宽度（像素）。</summary>
    public int Width { get; }

    /// <summary>画面高度（像素）。</summary>
    public int Height { get; }

    /// <summary>为什么选这一档，界面上直接显示。</summary>
    public string Description { get; }

    /// <summary>下拉框里显示的文字。</summary>
    public string DisplayText => $"{Width}×{Height}";
}

/// <summary>
/// 花名册里的一条人脸样本，供界面列表绑定。
/// </summary>
public sealed class FaceSampleItem
{
    public FaceSampleItem(Student student, int index, FaceSample sample)
    {
        Student = student;
        Index = index;
        Sample = sample;
    }

    /// <summary>样本所属学生。</summary>
    public Student Student { get; }

    /// <summary>样本在 <see cref="Student.Faces"/> 里的下标。</summary>
    public int Index { get; }

    /// <summary>样本本体。</summary>
    public FaceSample Sample { get; }

    /// <summary>界面上显示的一行文字。</summary>
    public string DisplayText =>
        $"#{Index + 1}　{(string.IsNullOrEmpty(Sample.Source) ? "未知来源" : Sample.Source)}　{SourceLabel(Sample.Source)}";

    private static string SourceLabel(string source) => source switch
    {
        "camera" => "摄像头录入",
        "file" => "照片导入",
        "smart" => "智能导入",
        "" => "未知来源",
        _ => source
    };
}

/// <summary>
/// 重命名请求：把学生与新姓名一起传给命令。
/// </summary>
public sealed class RenameRequest
{
    public RenameRequest(Student student, string newName)
    {
        Student = student;
        NewName = newName;
    }

    /// <summary>要改名的学生。</summary>
    public Student Student { get; }

    /// <summary>新姓名。</summary>
    public string NewName { get; }
}

/// <summary>
/// 人脸考勤设置页 ViewModel。
/// <para>
/// 与远程监控设置页同构：功能必须<strong>已加入集控且已绑定班主任</strong>才能启用，
/// 未满足时开关禁用并给出原因（<see cref="PrerequisiteMessage"/>）。
/// 集控策略 <c>forceFaceAttendance</c> 可以强制开启，此时本地开关禁用。
/// </para>
/// <para>
/// <b>抓拍时间计划在这里只读展示</b>：时间由组织在集控后台统一下发，
/// 每 60 秒由 <see cref="FacePlanSyncService"/> 拉取并覆盖本地设置，本地改了也会被冲掉。
/// </para>
/// </summary>
public partial class FaceAttendanceSettingsViewModel : ObservableRecipient
{
    private readonly SettingsService _settingsService;
    private readonly FaceAttendanceSettings _faceSettings;
    private readonly AttendanceService _attendance;
    private readonly EnrollmentService _enrollment;
    private readonly RosterService _roster;
    private readonly FaceAttendanceEngine _engine;
    private readonly FaceModelProvider _models;
    private readonly ManagementService _managementService;
    private readonly TeacherBindingGate _teacherBindingGate;
    private readonly FacePlanSyncService _planSync;
    private readonly CameraCaptureService _cameraService;
    private readonly ILogger<FaceAttendanceSettingsViewModel> _logger;
    private readonly CancellationTokenSource _cts = new();

    public FaceAttendanceSettingsViewModel(
        SettingsService settingsService,
        FaceAttendanceSettings faceSettings,
        AttendanceService attendance,
        EnrollmentService enrollment,
        RosterService roster,
        FaceAttendanceEngine engine,
        FaceModelProvider models,
        ManagementService managementService,
        TeacherBindingGate teacherBindingGate,
        FacePlanSyncService planSync,
        CameraCaptureService cameraService,
        ILogger<FaceAttendanceSettingsViewModel> logger)
    {
        _settingsService = settingsService;
        _faceSettings = faceSettings;
        _attendance = attendance;
        _enrollment = enrollment;
        _roster = roster;
        _engine = engine;
        _models = models;
        _managementService = managementService;
        _teacherBindingGate = teacherBindingGate;
        _planSync = planSync;
        _cameraService = cameraService;
        _logger = logger;

        _planSync.RunRequested += OnRunRequested;
    }

    public SettingsService SettingsService => _settingsService;
    public FaceAttendanceSettings FaceSettings => _faceSettings;
    public AttendanceService Attendance => _attendance;
    public EnrollmentService Enrollment => _enrollment;
    public RosterService Roster => _roster;
    public FaceAttendanceEngine Engine => _engine;
    public FaceModelProvider Models => _models;
    public FacePlanSyncService PlanSync => _planSync;

    /// <summary>是否已加入集控。未加入时人脸考勤不可用。</summary>
    [ObservableProperty] private bool _isManagementReady;

    /// <summary>是否已绑定班主任。</summary>
    [ObservableProperty] private bool _isTeacherBound;

    /// <summary>前置条件不满足时的提示文案；满足时为空。</summary>
    [ObservableProperty] private string _prerequisiteMessage = "";

    /// <summary>本地开关是否可操作（前置条件满足、不忙、未被策略强制）。</summary>
    public bool CanToggleEnabled => IsManagementReady && IsTeacherBound && !IsBusy
        && !_managementService.Policy.ForceFaceAttendance;

    /// <summary>
    /// 前置条件是否满足（已加入集控且已绑定班主任）。
    /// 不满足时设置页只显示横幅与总开关，全部配置区块隐藏——与远程监控页同样的门禁形态。
    /// </summary>
    public bool PrerequisitesMet => IsManagementReady && IsTeacherBound;

    /// <summary>是否显示模型下载进度条（首次启用或手动载入时）。</summary>
    [ObservableProperty] private bool _modelDownloadVisible;

    /// <summary>模型下载进度 0~100。</summary>
    [ObservableProperty] private double _modelDownloadPercent;

    /// <summary>模型下载当前阶段说明。</summary>
    [ObservableProperty] private string _modelDownloadStage = "";

    /// <summary>模型文件是否还没就绪（未下载且本地找不到）。为真时设置页只显示下载/导入卡片，其余配置隐藏。</summary>
    [ObservableProperty] private bool _modelFilesMissing = true;

    /// <summary>完整配置区是否可见（前置条件满足且模型文件就绪）。</summary>
    public bool ShowFullConfig => PrerequisitesMet && !ModelFilesMissing;

    /// <summary>模型准备卡片是否可见（前置条件满足但模型文件缺失）。</summary>
    public bool ShowModelSetupCard => PrerequisitesMet && ModelFilesMissing;

    partial void OnModelFilesMissingChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowFullConfig));
        OnPropertyChanged(nameof(ShowModelSetupCard));
    }

    /// <summary>是否正在执行识别、录入等耗时操作。</summary>
    [ObservableProperty] private bool _isBusy;

    /// <summary>当前状态文本（由 <see cref="AttendanceService"/> 推进）。</summary>
    [ObservableProperty] private string _statusText = "尚未执行考勤";

    /// <summary>当前阶段说明。</summary>
    [ObservableProperty] private string _stageText = "空闲";

    /// <summary>进度百分比 0~100。</summary>
    [ObservableProperty] private double _progressPercent;

    /// <summary>是否显示进度条。</summary>
    [ObservableProperty] private bool _progressVisible;

    /// <summary>最近一次结果摘要。</summary>
    [ObservableProperty] private string _lastResultSummary = "尚未执行考勤";

    /// <summary>集控下发的计划描述；为空表示组织尚未配置。</summary>
    [ObservableProperty] private string _planDescription = "";

    /// <summary>模型是否已载入。</summary>
    [ObservableProperty] private bool _modelReady;

    /// <summary>模型状态文本。</summary>
    [ObservableProperty] private string _modelStatus = "未载入";

    /// <summary>当前实际使用的识别模型名。</summary>
    [ObservableProperty] private string _recognizerDisplay = "";

    /// <summary>当前生效的阈值。</summary>
    [ObservableProperty] private double _effectiveThreshold;

    /// <summary>阈值说明（出自哪款模型）。</summary>
    [ObservableProperty] private string _thresholdNote = "";

    /// <summary>花名册人数（已录入人脸的）。</summary>
    [ObservableProperty] private int _rosterCount;

    /// <summary>样本总条数。</summary>
    [ObservableProperty] private int _sampleCount;

    /// <summary>一次性操作的结果/错误提示。</summary>
    [ObservableProperty] private string _actionMessage = "";

    /// <summary>摄像头是否可用。</summary>
    [ObservableProperty] private bool _cameraSupported;

    /// <summary>智能导入提问弹窗的宿主：设置页代码在需要用户选人时调用它。</summary>
    public Func<SmartPrompt, Task<SmartDecision>>? SmartPromptHandler { get; set; }

    /// <summary>本地花名册导入文本框内容。</summary>
    [ObservableProperty] private string _rosterImportText = "";

    /// <summary>默认班级名（导入花名册时给没写班级的同学用）。</summary>
    [ObservableProperty] private string _defaultClassName = "";

    /// <summary>可选的抓拍分辨率档位。</summary>
    public IReadOnlyList<ResolutionOption> ResolutionOptions { get; } = new[]
    {
        new ResolutionOption(640, 480, "640×480（不推荐：后排同学基本认不出）"),
        new ResolutionOption(1280, 720, "1280×720（推荐，实测最稳）"),
        new ResolutionOption(1920, 1080, "1920×1080（更清晰，识别更慢）"),
    };

    /// <summary>当前选中的分辨率档位。</summary>
    [ObservableProperty] private ResolutionOption? _selectedResolution;

    /// <summary>可选摄像头列表。</summary>
    public ObservableCollection<CameraInfo> AvailableCameras { get; } = new();

    /// <summary>当前选中的摄像头。</summary>
    [ObservableProperty] private CameraInfo? _selectedCamera;

    /// <summary>模型自检结果文本。</summary>
    [ObservableProperty] private string _selfTestResult = "";

    /// <summary>摄像头自检抓到的画面预览（界面上直接显示，确认镜头对准了人）。</summary>
    [ObservableProperty] private Avalonia.Media.Imaging.Bitmap? _cameraPreview;

    /// <summary>一次导入的逐条结果（导入完成后展示）。</summary>
    public ObservableCollection<ImportItemResult> LastImportItems { get; } = new();

    /// <summary>
    /// 迟到判定方式选项。引擎两种模式都实现了，这里让用户能切。
    /// </summary>
    public IReadOnlyList<NamedOption<LateJudgeMode>> JudgeModeOptions { get; } = new[]
    {
        new NamedOption<LateJudgeMode>(LateJudgeMode.Absence, "教室抓拍 · 缺席判定", "摄像头对着教室，应在场却没被认出来的人算迟到"),
        new NamedOption<LateJudgeMode>(LateJudgeMode.Presence, "门口抓拍 · 出现判定", "摄像头对着门口，在抓拍时刻出现的人算迟到"),
    };

    /// <summary>当前选中的判定方式。</summary>
    public LateJudgeMode JudgeMode
    {
        get => _faceSettings.JudgeMode;
        set
        {
            if (_faceSettings.JudgeMode == value) return;
            _faceSettings.JudgeMode = value;
            OnPropertyChanged(nameof(JudgeMode));
            OnPropertyChanged(nameof(SelectedJudgeMode));
            OnPropertyChanged(nameof(JudgeModeHint));
        }
    }

    /// <summary>下拉框里的判定方式选中项。</summary>
    public NamedOption<LateJudgeMode>? SelectedJudgeMode
    {
        get => JudgeModeOptions.FirstOrDefault(o => o.Value == JudgeMode);
        set
        {
            if (value == null) return;
            JudgeMode = value.Value;
        }
    }

    /// <summary>当前判定方式的一句话说明。</summary>
    public string JudgeModeHint => SelectedJudgeMode?.Description ?? "";

    /// <summary>当前识别模型 Id（auto / sface / arcface_mobilefacenet）。</summary>
    public string RecognizerModelId
    {
        get => _faceSettings.RecognizerModelId;
        set
        {
            if (_faceSettings.RecognizerModelId == value) return;
            _faceSettings.RecognizerModelId = value;
            OnPropertyChanged(nameof(RecognizerModelId));
            OnPropertyChanged(nameof(SelectedRecognizerModel));
        }
    }

    /// <summary>是否允许联网下载模型。学校网络连不上 HuggingFace 时关掉，避免每次载入都等超时。</summary>
    public bool AllowModelDownload
    {
        get => _faceSettings.AllowModelDownload;
        set
        {
            if (_faceSettings.AllowModelDownload == value) return;
            _faceSettings.AllowModelDownload = value;
            OnPropertyChanged(nameof(AllowModelDownload));
        }
    }

    /// <summary>抓拍张数。</summary>
    public int CaptureCount
    {
        get => _faceSettings.CaptureCount;
        set
        {
            if (_faceSettings.CaptureCount == value) return;
            _faceSettings.CaptureCount = Math.Clamp(value, 1, 20);
            OnPropertyChanged(nameof(CaptureCount));
        }
    }

    /// <summary>抓拍间隔（毫秒）。</summary>
    public int CaptureIntervalMs
    {
        get => _faceSettings.CaptureIntervalMs;
        set
        {
            if (_faceSettings.CaptureIntervalMs == value) return;
            _faceSettings.CaptureIntervalMs = Math.Clamp(value, 0, 5000);
            OnPropertyChanged(nameof(CaptureIntervalMs));
        }
    }

    /// <summary>抓拍前预热秒数。</summary>
    public int WarmUpSeconds
    {
        get => _faceSettings.WarmUpSeconds;
        set
        {
            if (_faceSettings.WarmUpSeconds == value) return;
            _faceSettings.WarmUpSeconds = Math.Clamp(value, 0, 10);
            OnPropertyChanged(nameof(WarmUpSeconds));
        }
    }

    /// <summary>最少命中张数（多帧投票）。</summary>
    public int MinHits
    {
        get => _faceSettings.MinHits;
        set
        {
            if (_faceSettings.MinHits == value) return;
            _faceSettings.MinHits = Math.Clamp(value, 1, 20);
            OnPropertyChanged(nameof(MinHits));
        }
    }

    /// <summary>是否保留抓拍照片。</summary>
    public bool SaveSnapshots
    {
        get => _faceSettings.SaveSnapshots;
        set
        {
            if (_faceSettings.SaveSnapshots == value) return;
            _faceSettings.SaveSnapshots = value;
            OnPropertyChanged(nameof(SaveSnapshots));
        }
    }

    /// <summary>抓拍照片保留天数。</summary>
    public int RetentionDays
    {
        get => _faceSettings.RetentionDays;
        set
        {
            if (_faceSettings.RetentionDays == value) return;
            _faceSettings.RetentionDays = Math.Clamp(value, 1, 365);
            OnPropertyChanged(nameof(RetentionDays));
        }
    }

    /// <summary>当前生效阈值（0~1）。</summary>
    public double Threshold
    {
        get => _engine.EffectiveThreshold;
        set
        {
            var clamped = Math.Clamp(value, 0.05, 0.99);
            // 阈值必须按当前模型分别记住，切换模型时不会把上一款的值带过来。
            _faceSettings.SetThresholdForModel(CurrentModelId(), clamped);
            // 阈值是每次读设置算出来的，不用额外刷新引擎。
            OnPropertyChanged(nameof(Threshold));
            OnPropertyChanged(nameof(IsThresholdCustomized));
            RefreshModelStatus();
        }
    }

    /// <summary>当前模型的推荐阈值。</summary>
    public double RecommendedThreshold => _engine.RecommendedThreshold;

    /// <summary>用户是否改过当前模型的阈值（改了才显示「恢复推荐值」）。</summary>
    public bool IsThresholdCustomized =>
        Math.Abs(_engine.EffectiveThreshold - _engine.RecommendedThreshold) > 1e-6;

    /// <summary>当前实际载入的模型 Id（用于阈值按模型分别保存）。</summary>
    private string CurrentModelId()
    {
        var path = _engine.RecognizerPath;
        return FaceModelCatalog.MatchByPath(path)?.Id
               ?? FaceModelCatalog.Find(_faceSettings.RecognizerModelId)?.Id
               ?? FaceModelCatalog.SFaceId;
    }

    /// <summary>
    /// 花名册学生列表。录入与导入面板都绑定它。
    /// </summary>
    public ObservableCollection<Student> Students { get; } = new();

    /// <summary>当前选中学生的人脸样本列表（界面删除单条样本用）。</summary>
    public ObservableCollection<FaceSampleItem> SelectedStudentSamples { get; } = new();

    /// <summary>当前选中学生（样本列表跟着它变）。</summary>
    [ObservableProperty] private Student? _selectedStudent;

    partial void OnSelectedStudentChanged(Student? value)
    {
        SelectedStudentSamples.Clear();
        if (value == null) return;
        for (var i = 0; i < value.Faces.Count; i++)
        {
            SelectedStudentSamples.Add(new FaceSampleItem(value, i, value.Faces[i]));
        }
    }

    /// <summary>可选识别模型列表。</summary>
    public IReadOnlyList<FaceRecognizerModel> RecognizerModels => FaceModelCatalog.All;

    /// <summary>
    /// 下拉框里的识别模型选中项。设置里存的可能是 <c>auto</c>，
    /// 这时按当前实际载入的模型显示，避免下拉框空着。
    /// </summary>
    public FaceRecognizerModel? SelectedRecognizerModel
    {
        get => FaceModelCatalog.Find(RecognizerModelId)
               ?? FaceModelCatalog.MatchByPath(_engine.RecognizerPath)
               ?? RecognizerModels.FirstOrDefault();
        set
        {
            if (value == null || value.Id == RecognizerModelId) return;
            RecognizerModelId = value.Id;
            OnPropertyChanged();
            ActionMessage = $"已选择「{value.DisplayName}」，点「载入模型」后生效。";
        }
    }

    /// <summary>设置页 Loaded 时调用。</summary>
    public async Task InitializeAsync()
    {
        await RefreshPrerequisitesAsync();
        RefreshRoster();
        RefreshModelStatus();
        SyncAttendanceState();

        // 模型还没就绪时主动开始准备（下载+载入），设置页会显示实时进度条；
        // 用户不点任何按钮也能看到下载进度，而不是面对一堆不可用的配置。
        if (PrerequisitesMet && _faceSettings.AllowModelDownload && !_engine.IsReady)
        {
            _ = EnsureModelsWithProgressAsync();
        }

        SelectedResolution = ResolutionOptions.FirstOrDefault(o =>
            o.Width == _faceSettings.CaptureWidth && o.Height == _faceSettings.CaptureHeight)
            ?? ResolutionOptions.FirstOrDefault(o => o.Width == 1280);

        _attendance.PropertyChanged += OnAttendancePropertyChanged;
        _roster.RosterChanged += OnRosterChanged;
    }

    /// <summary>设置页 Unloaded 时调用。</summary>
    public void Deinitialize()
    {
        _attendance.PropertyChanged -= OnAttendancePropertyChanged;
        _roster.RosterChanged -= OnRosterChanged;
        _planSync.RunRequested -= OnRunRequested;
    }

    private void OnAttendancePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            StatusText = _attendance.StatusText;
            StageText = _attendance.StageText;
            ProgressPercent = _attendance.ProgressPercent;
            ProgressVisible = _attendance.ProgressVisible;
            LastResultSummary = _attendance.LastResultSummary;
        });
    }

    private void OnRosterChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(RefreshRoster);
    }

    private void OnRunRequested(DateTime commandAt)
    {
        Dispatcher.UIThread.Post(() =>
        {
            ActionMessage = $"班主任端在 {commandAt:HH:mm:ss} 请求立即识别一次，正在执行…";
        });
    }

    private void SyncAttendanceState()
    {
        StatusText = _attendance.StatusText;
        StageText = _attendance.StageText;
        ProgressPercent = _attendance.ProgressPercent;
        ProgressVisible = _attendance.ProgressVisible;
        LastResultSummary = _attendance.LastResultSummary;
        PlanDescription = _planSync.LastPlanDescription;
        CameraSupported = _cameraService.IsSupported;
    }

    /// <summary>
    /// 刷新前置条件：已加入集控 + 已绑定班主任，并生成提示文案。
    /// </summary>
    public async Task RefreshPrerequisitesAsync()
    {
        IsManagementReady = _managementService.IsManagementEnabled;
        IsTeacherBound = false;

        if (!IsManagementReady)
        {
            PrerequisiteMessage = "人脸考勤仅在加入集控并绑定班主任后可用：请先在「集控」设置中加入集控，并由班主任完成设备绑定。";
            OnPropertyChanged(nameof(CanToggleEnabled));
            OnPropertyChanged(nameof(PrerequisitesMet));
        OnPropertyChanged(nameof(ShowFullConfig));
        OnPropertyChanged(nameof(ShowModelSetupCard));
            return;
        }

        var bound = await _teacherBindingGate.IsTeacherBoundAsync();
        IsTeacherBound = bound == true;
        PrerequisiteMessage = bound == true
            ? ""
            : "人脸考勤需要先绑定班主任：请联系班主任在教师端完成本设备的绑定后，再刷新本页。";
        OnPropertyChanged(nameof(CanToggleEnabled));
        OnPropertyChanged(nameof(PrerequisitesMet));
        OnPropertyChanged(nameof(ShowFullConfig));
        OnPropertyChanged(nameof(ShowModelSetupCard));
    }

    /// <summary>刷新花名册统计与学生列表。</summary>
    public void RefreshRoster()
    {
        var keepId = SelectedStudent?.Id;
        Students.Clear();
        foreach (var s in _roster.Students.OrderBy(x => x.Name, StringComparer.CurrentCulture))
        {
            Students.Add(s);
        }

        RosterCount = _roster.Students.Count(x => x.Faces.Count > 0);
        SampleCount = _roster.Students.Sum(x => x.Faces.Count);

        // 重建列表会把列表控件的选中项清掉，这里按 Id 把原来选中的同学找回来，
        // 否则删完一条样本就得重新点一次才能继续删下一条。
        SelectedStudent = keepId == null ? null : Students.FirstOrDefault(x => x.Id == keepId);
    }

    /// <summary>刷新模型载入状态。</summary>
    public void RefreshModelStatus()
    {
        ModelReady = _engine.IsReady;
        ModelFilesMissing = !_engine.AreModelFilesPresent();
        ModelStatus = _engine.IsReady ? "已载入" : "未载入";
        RecognizerDisplay = _engine.RecognizerDisplayName;
        EffectiveThreshold = _engine.EffectiveThreshold;
        ThresholdNote = _engine.ThresholdNote;
        OnPropertyChanged(nameof(Threshold));
        OnPropertyChanged(nameof(RecommendedThreshold));
        OnPropertyChanged(nameof(IsThresholdCustomized));
        OnPropertyChanged(nameof(RecognizerModelId));
        OnPropertyChanged(nameof(SelectedRecognizerModel));
        OnPropertyChanged(nameof(JudgeMode));
        OnPropertyChanged(nameof(SelectedJudgeMode));
        OnPropertyChanged(nameof(JudgeModeHint));
    }

    /// <summary>
    /// 切换功能总开关。开启前实时复核前置条件，不满足则回滚。
    /// </summary>
    [RelayCommand]
    private async Task ToggleEnabledAsync()
    {
        // 集控策略强制开启时禁止本地关闭
        if (!_faceSettings.IsEnabled && _managementService.Policy.ForceFaceAttendance)
        {
            _faceSettings.IsEnabled = true;
            OnPropertyChanged(nameof(CanToggleEnabled));
            _logger.LogInformation("人脸考勤被集控策略强制开启，已忽略本地关闭操作");
            return;
        }

        if (_faceSettings.IsEnabled)
        {
            await RefreshPrerequisitesAsync();
            if (!(IsManagementReady && IsTeacherBound))
            {
                _faceSettings.IsEnabled = false;
                _logger.LogWarning("人脸考勤开启被拦截：前置条件不满足（集控={Mgmt}，绑定={Bound}）",
                    IsManagementReady, IsTeacherBound);
                OnPropertyChanged(nameof(CanToggleEnabled));
                return;
            }
        }

        _logger.LogInformation("人脸考勤本地开关已设为 {Enabled}", _faceSettings.IsEnabled);

        // 首次启用时模型多半还没下载：后台拉起下载/载入并把进度条显示在设置页上，
        // 不阻塞开关本身，也打断点的地方不会重复触发（_modelEnsureRunning 去重）。
        if (_faceSettings.IsEnabled && !_engine.IsReady)
        {
            _ = EnsureModelsWithProgressAsync();
        }
    }

    private bool _modelEnsureRunning;

    /// <summary>
    /// 确保模型就绪：需要下载时把进度条（ModelDownloadVisible/Percent/Stage）展示出来。
    /// 首次启用功能、手动点「载入模型」共用同一条进度显示。
    /// </summary>
    private async Task EnsureModelsWithProgressAsync()
    {
        if (_modelEnsureRunning) return;
        _modelEnsureRunning = true;
        ModelDownloadVisible = true;
        ModelDownloadPercent = 0;
        ModelDownloadStage = "正在准备模型…";
        var ok = false;
        var message = "";
        try
        {
            var (okResult, messageResult) = await _engine.LoadAsync(
                new Progress<string>(s => ModelDownloadStage = s),
                new Progress<double>(p => ModelDownloadPercent = p),
                _cts.Token).ConfigureAwait(true);
            ok = okResult;
            message = messageResult;
            ModelDownloadStage = ok ? "模型已就绪" : messageResult;
            if (ok) ModelDownloadPercent = 100;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "启用人脸考勤后的模型下载/载入失败");
            ModelDownloadStage = $"模型准备失败：{ex.Message}";
            message = ex.Message;
        }
        finally
        {
            _modelEnsureRunning = false;
            ActionMessage = ok ? "" : $"模型准备失败：{message}";
            RefreshModelStatus();
            // 成功后进度条停留 2 秒再收起，失败多留几秒让用户看清原因。
            await Task.Delay(ok ? 2000 : 8000).ConfigureAwait(true);
            if (!_modelEnsureRunning) ModelDownloadVisible = false;
        }
    }

    /// <summary>立即执行一次考勤。</summary>
    [RelayCommand]
    private async Task RunOnceAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        ActionMessage = "";
        try
        {
            var record = await _attendance.RunCheckAsync(true, _cts.Token);
            ActionMessage = record.Message;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "手动执行人脸考勤失败");
            ActionMessage = $"执行失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
            RefreshRoster();
            RefreshModelStatus();
        }
    }

    /// <summary>
    /// 载入识别模型（YuNet + SFace/ArcFace）。模型体积较大，首次载入需要几秒。
    /// </summary>
    [RelayCommand]
    private async Task LoadModelsAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        ActionMessage = "";
        try
        {
            // 模型缺失但禁用了联网下载时，用户显式点「下载/载入」视为同意联网下载。
            if (!_faceSettings.AllowModelDownload && !_engine.AreModelFilesPresent())
            {
                AllowModelDownload = true;
            }

            // 与首次启用共用同一条进度条：需要下载时用户能看到进度而不是干等。
            ModelDownloadVisible = true;
            ModelDownloadPercent = 0;
            ModelDownloadStage = "正在准备模型…";
            var (ok, message) = await _engine.LoadAsync(
                new Progress<string>(s => ModelDownloadStage = s),
                new Progress<double>(p => ModelDownloadPercent = p),
                _cts.Token);
            ActionMessage = message;
            ModelDownloadStage = ok ? "模型已就绪" : message;
            if (ok) ModelDownloadPercent = 100;
            RefreshModelStatus();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "载入人脸模型失败");
            ActionMessage = $"载入失败：{ex.Message}";
            ModelDownloadStage = $"载入失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
            await Task.Delay(2000);
            ModelDownloadVisible = false;
        }
    }

    /// <summary>摄像头自检：抓一帧并给出分辨率。抓到的画面也留着给界面预览。</summary>
    [RelayCommand]
    private async Task TestCameraAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        ActionMessage = "";
        try
        {
            var (jpeg, message) = await _enrollment.TestCameraAsync(_cts.Token);
            ActionMessage = message;
            CameraPreview = ToBitmap(jpeg);
        }
        catch (Exception ex)
        {
            ActionMessage = $"自检失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>取主窗口的剪贴板；拿不到时返回 null（命令行环境或窗口尚未创建）。</summary>
    private static Avalonia.Input.Platform.IClipboard? GetClipboard()
        => (Avalonia.Application.Current?.ApplicationLifetime
            as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)
           ?.MainWindow?.Clipboard;

    /// <summary>把 JPEG 字节转成界面能显示的位图；失败或为空时返回 null。</summary>
    private static Avalonia.Media.Imaging.Bitmap? ToBitmap(byte[]? jpeg)
    {
        if (jpeg == null || jpeg.Length == 0) return null;
        try
        {
            using var ms = new MemoryStream(jpeg);
            return new Avalonia.Media.Imaging.Bitmap(ms);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 拉取班主任端上传的待录入照片并导入（远程录入）。
    /// <para>
    /// 多脸照片一样要走 <see cref="AskSmartAsync"/>：班主任上传的可能是合影，
    /// 不弹窗问就会把第一个人的脸录到花名册第一个人名下。
    /// </para>
    /// </summary>
    [RelayCommand]
    private async Task DownloadAndImportAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        ActionMessage = "";
        try
        {
            var (_, summary, message) = await _enrollment.DownloadAndImportAsync(
                AskSmartAsync, null, _cts.Token);
            ActionMessage = summary.Items.Count == 0
                ? message
                : $"已处理 {summary.OkCount} 张照片，新增 {summary.AddedCount} 条样本。";
            RefreshRoster();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "拉取远程录入照片失败");
            ActionMessage = $"拉取失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>新增一位学生到花名册。</summary>
    /// <param name="name">学生姓名；为空时用占位名「新同学」，由界面弹改名框。</param>
    [RelayCommand]
    private void AddStudent(string? name = null)
    {
        var finalName = string.IsNullOrWhiteSpace(name) ? "新同学" : name.Trim();
        if (_roster.FindByName(finalName) != null)
        {
            ActionMessage = $"花名册里已有「{finalName}」，请换个名字。";
            return;
        }

        var student = _roster.AddStudent(finalName);
        SelectedStudent = Students.FirstOrDefault(x => x.Id == student.Id);
        RefreshRoster();
        SelectedStudent = Students.FirstOrDefault(x => x.Id == student.Id);
        ActionMessage = string.IsNullOrWhiteSpace(name)
            ? $"已添加「{finalName}」，请改为真实姓名后为其录入人脸。"
            : $"已添加「{finalName}」，接下来可以为他录入人脸。";
    }

    /// <summary>删除一位学生及其全部人脸样本。</summary>
    [RelayCommand]
    private void RemoveStudent(Student? student)
    {
        if (student == null) return;
        _roster.RemoveStudent(student);
        RefreshRoster();
        ActionMessage = $"已删除「{student.Name}」及其人脸样本。";
    }

    /// <summary>刷新集控下发的计划描述。</summary>
    [RelayCommand]
    private async Task RefreshPlanAsync()
    {
        try
        {
            await _planSync.RefreshAsync(_cts.Token);
            PlanDescription = _planSync.LastPlanDescription;
            ActionMessage = string.IsNullOrEmpty(PlanDescription)
                ? "组织尚未下发考勤时间，请在集控后台「人脸考勤」里配置。"
                : $"当前计划：{PlanDescription}";
        }
        catch (Exception ex)
        {
            ActionMessage = $"刷新计划失败：{ex.Message}";
        }
    }

    // ---------------- 花名册导入 / 导出 ----------------

    /// <summary>
    /// 从文本框导入花名册。每行一位同学，支持「姓名」「姓名,班级」「姓名 班级」。
    /// </summary>
    [RelayCommand]
    private void ImportRosterFromText()
    {
        var text = RosterImportText ?? "";
        if (text.Trim().Length == 0)
        {
            ActionMessage = "请先粘贴花名册文本，每行一位同学。";
            return;
        }

        var before = _roster.Students.Count;
        var added = _roster.ImportFromText(text, DefaultClassName ?? "");
        RefreshRoster();
        ActionMessage = added == 0
            ? before == 0 ? "没有导入任何同学，请检查格式。" : "名单里的同学都已在花名册中，没有新增。"
            : $"已导入 {added} 位同学（共 {_roster.Students.Count} 人）。接下来请为他们录入人脸。";
    }

    /// <summary>导出花名册为 CSV（复制到剪贴板）。</summary>
    [RelayCommand]
    private async Task ExportRosterCsvAsync()
    {
        try
        {
            var csv = _roster.ExportToCsv();
            var clipboard = GetClipboard();
            if (clipboard == null)
            {
                ActionMessage = "当前环境不支持剪贴板，已改为写入文件。";
                return;
            }

            await clipboard.SetTextAsync(csv);
            ActionMessage = $"花名册 CSV 已复制到剪贴板（{_roster.Students.Count} 人）。";
        }
        catch (Exception ex)
        {
            ActionMessage = $"导出失败：{ex.Message}";
        }
    }

    /// <summary>打开人脸样本目录，看看录进去的人脸小图长什么样。</summary>
    [RelayCommand]
    private void OpenFacesFolder()
    {
        try
        {
            var dir = FaceAttendancePaths.EnsureDirectory(FaceAttendancePaths.FacesFolder);
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true
            };
            System.Diagnostics.Process.Start(psi);
            ActionMessage = $"已打开人脸样本目录：{dir}";
        }
        catch (Exception ex)
        {
            ActionMessage = $"打开目录失败：{ex.Message}";
        }
    }

    /// <summary>把剪贴板里的名单直接导入（先从剪贴板读回文本框，让用户看见导入了什么）。</summary>
    [RelayCommand]
    private async Task ImportRosterFromClipboardAsync()
    {
        try
        {
            var clipboard = GetClipboard();
            var text = clipboard == null ? "" : await clipboard.GetTextAsync();
            if (string.IsNullOrWhiteSpace(text))
            {
                ActionMessage = "剪贴板里没有文本。请先复制名单（每行一位同学）。";
                return;
            }

            RosterImportText = text;
            ImportRosterFromText();
        }
        catch (Exception ex)
        {
            ActionMessage = $"读取剪贴板失败：{ex.Message}";
        }
    }

    // ---------------- 人脸录入 ----------------

    /// <summary>
    /// 用摄像头为选中的同学连拍录入人脸。
    /// </summary>
    [RelayCommand]
    private async Task EnrollFromCameraAsync(Student? student)
    {
        if (student == null) return;
        if (IsBusy) return;
        IsBusy = true;
        ActionMessage = "";
        try
        {
            var (added, message) = await _enrollment.EnrollFromCameraAsync(
                student, Math.Clamp(_faceSettings.CaptureCount, 1, 20), null, _cts.Token);
            ActionMessage = $"「{student.Name}」：{message}（现有 {student.Faces.Count} 条样本）";
            RefreshRoster();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "摄像头录入人脸失败");
            ActionMessage = $"录入失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 拿用于文件选择的窗口。本应用的主窗口只是 Show 出来，桌面生命周期的
    /// MainWindow 从未被赋值（一直是 null），所以要回退到 Application.MainWindow，
    /// 否则所有文件选择都会误报「当前环境不支持文件选择」。
    /// </summary>
    private static Avalonia.Controls.Window? GetPickerWindow()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime
            is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            return desktop.MainWindow ?? (Avalonia.Application.Current as ClassPixelPro.Core.AppBase)?.MainWindow;
        }
        return (Avalonia.Application.Current as ClassPixelPro.Core.AppBase)?.MainWindow;
    }

    /// <summary>
    /// 从照片文件批量导入。文件名里带人名的自动匹配，认不出的走智能导入提问。
    /// </summary>
    [RelayCommand]
    private async Task ImportFromFilesAsync()
    {
        if (IsBusy) return;
        var topLevel = GetPickerWindow();
        if (topLevel?.StorageProvider == null)
        {
            ActionMessage = "当前环境不支持文件选择。";
            return;
        }

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "选择学生照片",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new Avalonia.Platform.Storage.FilePickerFileType("图片")
                {
                    Patterns = ["*.jpg", "*.jpeg", "*.png", "*.bmp"]
                }
            ]
        });

        if (files == null || files.Count == 0) return;

        var paths = files.Select(f => f.Path.LocalPath).Where(p => !string.IsNullOrEmpty(p)).ToList();
        if (paths.Count == 0) return;

        await RunSmartImportAsync(paths);
    }

    /// <summary>
    /// 打开人脸样本目录里某位同学的照片，按文件名自动匹配导入。
    /// </summary>
    [RelayCommand]
    private async Task ImportFromFacesFolderAsync()
    {
        if (IsBusy) return;
        var dir = FaceAttendancePaths.EnsureDirectory(FaceAttendancePaths.RootFolder, "inbox");
        var topLevel = GetPickerWindow();
        if (topLevel?.StorageProvider == null)
        {
            ActionMessage = "当前环境不支持文件选择。";
            return;
        }

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "选择照片（可多选）",
            SuggestedStartLocation = await topLevel.StorageProvider.TryGetFolderFromPathAsync(new Uri(dir)),
            AllowMultiple = true,
            FileTypeFilter =
            [
                new Avalonia.Platform.Storage.FilePickerFileType("图片")
                {
                    Patterns = ["*.jpg", "*.jpeg", "*.png", "*.bmp"]
                }
            ]
        });

        if (files == null || files.Count == 0) return;

        var paths = files.Select(f => f.Path.LocalPath).Where(p => !string.IsNullOrEmpty(p)).ToList();
        if (paths.Count == 0) return;

        await RunSmartImportAsync(paths);
    }

    /// <summary>
    /// 跑一遍智能导入，多脸照片通过 <see cref="SmartPromptHandler"/> 抛给设置页弹窗问用户。
    /// </summary>
    private async Task RunSmartImportAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;

        IsBusy = true;
        ActionMessage = "";
        LastImportItems.Clear();
        try
        {
            var progress = new Progress<(int Done, int Total, string Current)>(p =>
            {
                ActionMessage = $"正在导入 {p.Done}/{p.Total}：{p.Current}";
            });

            var summary = await _enrollment.SmartImportAsync(paths, AskSmartAsync, progress, _cts.Token);
            foreach (var item in summary.Items)
            {
                LastImportItems.Add(item);
            }

            ActionMessage = summary.FatalError.Length > 0
                ? summary.FatalError
                : summary.Cancelled
                    ? $"已取消。已处理 {summary.OkCount} 张，新增 {summary.AddedCount} 条样本。"
                    : $"处理完成：成功 {summary.OkCount} 张，跳过 {summary.SkippedCount} 张，新增 {summary.AddedCount} 条样本。";
            RefreshRoster();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "智能导入人脸失败");
            ActionMessage = $"导入失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 智能导入的提问回调。设置页在 <see cref="SmartPromptHandler"/> 里挂上弹窗；
    /// 没挂时按「跳过」处理，绝不静默把第一张脸录到别人名下。
    /// </summary>
    private Task<SmartDecision> AskSmartAsync(SmartPrompt prompt)
    {
        if (SmartPromptHandler == null)
        {
            return Task.FromResult(new SmartDecision { Skip = true });
        }

        return SmartPromptHandler(prompt);
    }

    /// <summary>清空某位同学的全部人脸样本。</summary>
    [RelayCommand]
    private void ClearStudentFaces(Student? student)
    {
        if (student == null) return;
        if (student.Faces.Count == 0)
        {
            ActionMessage = $"「{student.Name}」还没有人脸样本。";
            return;
        }

        var count = student.Faces.Count;
        _roster.ClearFaces(student);
        RefreshRoster();
        ActionMessage = $"已清空「{student.Name}」的 {count} 条人脸样本。";
    }

    /// <summary>删除某位同学的一条人脸样本。</summary>
    [RelayCommand]
    private void RemoveFaceSample(FaceSampleItem? sample)
    {
        if (sample?.Student == null) return;
        if (_roster.RemoveFaceSample(sample.Student, sample.Index))
        {
            RefreshRoster();
            ActionMessage = $"已删除「{sample.Student.Name}」的一条样本。";
        }
    }

    /// <summary>重命名一位同学。</summary>
    [RelayCommand]
    private void RenameStudent(RenameRequest? request)
    {
        if (request?.Student == null) return;
        var name = (request.NewName ?? "").Trim();
        if (name.Length == 0)
        {
            ActionMessage = "姓名不能为空。";
            return;
        }

        if (_roster.RenameStudent(request.Student, name))
        {
            RefreshRoster();
            ActionMessage = $"已改名为「{name}」。";
        }
        else
        {
            ActionMessage = $"改名失败：花名册里已有同名同班的同学。";
        }
    }

    /// <summary>把当前选中的同学改名为文本框里的名字。</summary>
    [RelayCommand]
    private void RenameSelected(string? newName)
    {
        if (SelectedStudent == null)
        {
            ActionMessage = "请先在花名册里选中一位同学。";
            return;
        }

        RenameStudent(new RenameRequest(SelectedStudent, newName ?? ""));
    }

    // ---------------- 模型与阈值 ----------------

    /// <summary>模型自检：真建一次检测器、真跑一次推理，把「文件都在但跑不起来」暴露出来。</summary>
    [RelayCommand]
    private async Task SelfTestAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        SelfTestResult = "正在自检…";
        try
        {
            var (ok, message) = await _engine.SelfTestAsync();
            SelfTestResult = (ok ? "✔ " : "✘ ") + message;
            RefreshModelStatus();
        }
        catch (Exception ex)
        {
            SelfTestResult = "✘ 自检过程出错：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>把当前模型的阈值恢复成官方推荐值。</summary>
    [RelayCommand]
    private void ResetThreshold()
    {
        _faceSettings.SetThresholdForModel(CurrentModelId(), _engine.RecommendedThreshold);
        OnPropertyChanged(nameof(Threshold));
        OnPropertyChanged(nameof(IsThresholdCustomized));
        RefreshModelStatus();
        ActionMessage = $"阈值已恢复为当前模型的推荐值 {_engine.RecommendedThreshold:F3}。";
    }

    /// <summary>从本地文件导入识别模型（学校网络连不上 HuggingFace 时用 U 盘拷）。</summary>
    [RelayCommand]
    private async Task ImportModelFileAsync()
    {
        if (IsBusy) return;
        var topLevel = GetPickerWindow();
        if (topLevel?.StorageProvider == null)
        {
            ActionMessage = "当前环境不支持文件选择。";
            return;
        }

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "选择识别模型文件（.onnx）",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new Avalonia.Platform.Storage.FilePickerFileType("ONNX 模型") { Patterns = ["*.onnx"] }
            ]
        });

        var file = files?.FirstOrDefault();
        if (file == null) return;

        IsBusy = true;
        ActionMessage = "";
        try
        {
            var (ok, message) = await _models.ImportAsync(file.Path.LocalPath, false);
            ActionMessage = message;
            if (ok)
            {
                RefreshModelStatus();
                OnPropertyChanged(nameof(Threshold));
            }
        }
        catch (Exception ex)
        {
            ActionMessage = $"导入失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>刷新摄像头列表与当前选中项。</summary>
    [RelayCommand]
    private async Task RefreshCamerasAsync()
    {
        try
        {
            var cameras = await Task.Run(() => _cameraService.GetCameras());
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                AvailableCameras.Clear();
                foreach (var c in cameras)
                {
                    AvailableCameras.Add(c);
                }

                var want = _faceSettings.CameraIndex;
                SelectedCamera = AvailableCameras.FirstOrDefault(c => c.Index == want)
                                 ?? AvailableCameras.FirstOrDefault(c => c.IsDefault)
                                 ?? AvailableCameras.FirstOrDefault();
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "刷新摄像头列表失败");
            ActionMessage = $"读取摄像头列表失败：{ex.Message}";
        }
    }

    partial void OnSelectedCameraChanged(CameraInfo? value)
    {
        if (value == null) return;
        _faceSettings.CameraIndex = value.Index;
        try
        {
            // 与远程监控共用同一路设备：这里改了，监控与录制也跟着用这一路。
            _settingsService.Settings.RemoteMonitorSelectedCameraIndex = value.Index;
            _cameraService.SelectCamera(value.Index.ToString());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "切换摄像头失败");
        }
    }

    partial void OnSelectedResolutionChanged(ResolutionOption? value)
    {
        if (value == null) return;
        _faceSettings.CaptureWidth = value.Width;
        _faceSettings.CaptureHeight = value.Height;
    }

    /// <summary>刷新前置条件、模型状态与花名册（页面按钮触发）。</summary>
    [RelayCommand]
    private async Task RefreshAllAsync()
    {
        await RefreshPrerequisitesAsync();
        RefreshModelStatus();
        RefreshRoster();
        await RefreshCamerasAsync();
        ActionMessage = "已刷新绑定状态、花名册与摄像头列表。";
    }

    /// <summary>释放资源。</summary>
    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
