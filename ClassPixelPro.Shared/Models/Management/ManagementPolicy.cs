using CommunityToolkit.Mvvm.ComponentModel;
#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

namespace ClassPixelPro.Shared.Models.Management;

/// <summary>
/// 限制策略
/// </summary>
public class ManagementPolicy : ObservableRecipient
{
    private bool _disableProfileClassPlanEditing = false;
    private bool _disableProfileTimeLayoutEditing = false;
    private bool _disableProfileSubjectsEditing = false;
    private bool _disableProfileEditing = false;
    private bool _disableSettingsEditing = false;
    private bool _disableSplashCustomize = false;
    private bool _disableDebugMenu = false;
    private bool _allowExitManagement = true;
    private bool _disableEasterEggs = false;
    private bool _forceRemoteMonitor = false;
    private bool _forceFaceAttendance = false;
    private double? _timeOffsetSeconds;

    public bool DisableProfileClassPlanEditing
    {
        get => _disableProfileClassPlanEditing;
        set => SetProperty(ref _disableProfileClassPlanEditing, value);
    }

    public bool DisableProfileTimeLayoutEditing
    {
        get => _disableProfileTimeLayoutEditing;
        set => SetProperty(ref _disableProfileTimeLayoutEditing, value);
    }

    public bool DisableProfileSubjectsEditing
    {
        get => _disableProfileSubjectsEditing;
        set => SetProperty(ref _disableProfileSubjectsEditing, value);
    }

    public bool DisableProfileEditing
    {
        get => _disableProfileEditing;
        set => SetProperty(ref _disableProfileEditing, value);
    }

    public bool DisableSettingsEditing
    {
        get => _disableSettingsEditing;
        set => SetProperty(ref _disableSettingsEditing, value);
    }

    public bool DisableSplashCustomize
    {
        get => _disableSplashCustomize;
        set => SetProperty(ref _disableSplashCustomize, value);
    }

    public bool DisableDebugMenu
    {
        get => _disableDebugMenu;
        set => SetProperty(ref _disableDebugMenu, value);
    }

    public bool AllowExitManagement
    {
        get => _allowExitManagement;
        set => SetProperty(ref _allowExitManagement, value);
    }

    public bool DisableEasterEggs
    {
        get => _disableEasterEggs;
        set => SetProperty(ref _disableEasterEggs, value);
    }

    /// <summary>
    /// 强制开启远程监控：为 true 时无论本地开关如何，客户端都会启动远程监控服务，
    /// 且本地设置页的开关被禁用。由集控后台策略下发。
    /// </summary>
    public bool ForceRemoteMonitor
    {
        get => _forceRemoteMonitor;
        set => SetProperty(ref _forceRemoteMonitor, value);
    }

    /// <summary>
    /// 强制开启人脸考勤：为 true 时无论本地开关如何，客户端都会启用人脸考勤。
    /// 考勤时间计划仍由本组织统一下发（集控后台「人脸考勤」标签页编辑），
    /// 本地无法修改计划，与 <see cref="ForceRemoteMonitor"/> 同一机制。
    /// </summary>
    public bool ForceFaceAttendance
    {
        get => _forceFaceAttendance;
        set => SetProperty(ref _forceFaceAttendance, value);
    }

    /// <summary>
    /// 集控统一时间偏移（秒），对应客户端「设置 → 时钟 → 时间偏移」。
    /// 为 null 表示集控未配置，保留设备本地设置；配置后下发即覆盖本地值。
    /// </summary>
    public double? TimeOffsetSeconds
    {
        get => _timeOffsetSeconds;
        set => SetProperty(ref _timeOffsetSeconds, value);
    }
}