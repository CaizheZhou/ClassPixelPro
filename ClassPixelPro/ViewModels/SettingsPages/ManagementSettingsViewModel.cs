using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ClassPixelPro.ViewModels.SettingsPages;

public partial class ManagementSettingsViewModel : ObservableObject
{
    [ObservableProperty] private Geometry? _cuidQrCodePath;

    /// <summary>
    /// 教师绑定二维码的几何路径。
    /// </summary>
    [ObservableProperty] private Geometry? _bindingQrCodePath;

    /// <summary>
    /// 是否正在生成绑定二维码。
    /// </summary>
    [ObservableProperty] private bool _isBindingQrLoading;

    /// <summary>
    /// 是否已绑定教师。
    /// </summary>
    [ObservableProperty] private bool _isTeacherBound;

    /// <summary>
    /// 已绑定的教师姓名。
    /// </summary>
    [ObservableProperty] private string? _boundTeacherName;

    /// <summary>
    /// 是否正在轮询绑定状态。
    /// </summary>
    [ObservableProperty] private bool _isPollingBindingStatus;

    /// <summary>
    /// 绑定二维码区域的错误信息。
    /// </summary>
    [ObservableProperty] private string? _bindingErrorMessage;

    /// <summary>
    /// 绑定二维码区域是否显示错误。
    /// </summary>
    [ObservableProperty] private bool _isBindingErrorVisible;
}
