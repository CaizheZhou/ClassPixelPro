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

    /// <summary>
    /// 花名册学生列表。录入与导入面板都绑定它。
    /// </summary>
    public ObservableCollection<Student> Students { get; } = new();

    /// <summary>可选识别模型列表。</summary>
    public IReadOnlyList<FaceRecognizerModel> RecognizerModels => FaceModelCatalog.All;

    /// <summary>设置页 Loaded 时调用。</summary>
    public async Task InitializeAsync()
    {
        await RefreshPrerequisitesAsync();
        RefreshRoster();
        RefreshModelStatus();
        SyncAttendanceState();

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
            return;
        }

        var bound = await _teacherBindingGate.IsTeacherBoundAsync();
        IsTeacherBound = bound == true;
        PrerequisiteMessage = bound == true
            ? ""
            : "人脸考勤需要先绑定班主任：请联系班主任在教师端完成本设备的绑定后，再刷新本页。";
        OnPropertyChanged(nameof(CanToggleEnabled));
    }

    /// <summary>刷新花名册统计与学生列表。</summary>
    public void RefreshRoster()
    {
        Students.Clear();
        foreach (var s in _roster.Students.OrderBy(x => x.Name, StringComparer.CurrentCulture))
        {
            Students.Add(s);
        }

        RosterCount = _roster.Students.Count(x => x.Faces.Count > 0);
        SampleCount = _roster.Students.Sum(x => x.Faces.Count);
    }

    /// <summary>刷新模型载入状态。</summary>
    public void RefreshModelStatus()
    {
        ModelReady = _engine.IsReady;
        ModelStatus = _engine.IsReady ? "已载入" : "未载入";
        RecognizerDisplay = _engine.RecognizerDisplayName;
        EffectiveThreshold = _engine.EffectiveThreshold;
        ThresholdNote = _engine.ThresholdNote;
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
            var (ok, message) = await _engine.LoadAsync(null, _cts.Token);
            ActionMessage = message;
            RefreshModelStatus();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "载入人脸模型失败");
            ActionMessage = $"载入失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>摄像头自检：抓一帧并给出分辨率。</summary>
    [RelayCommand]
    private async Task TestCameraAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        ActionMessage = "";
        try
        {
            var (_, message) = await _enrollment.TestCameraAsync(_cts.Token);
            ActionMessage = message;
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

    /// <summary>
    /// 拉取班主任端上传的待录入照片并导入（远程录入）。
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
                _ => Task.FromResult(new SmartDecision()), null, _cts.Token);
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
    [RelayCommand]
    private void AddStudent()
    {
        var student = _roster.AddStudent("新同学");
        RefreshRoster();
        ActionMessage = $"已添加「{student.Name}」，请重命名后为其录入人脸。";
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

    /// <summary>刷新前置条件、模型状态与花名册（页面按钮触发）。</summary>
    [RelayCommand]
    private async Task RefreshAllAsync()
    {
        await RefreshPrerequisitesAsync();
        RefreshModelStatus();
        RefreshRoster();
        ActionMessage = "已刷新绑定状态与花名册。";
    }

    /// <summary>释放资源。</summary>
    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
