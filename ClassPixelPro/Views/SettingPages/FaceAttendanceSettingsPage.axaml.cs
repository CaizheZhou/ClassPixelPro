using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ClassPixelPro.Core.Abstractions.Controls;
using ClassPixelPro.Core.Attributes;
using ClassPixelPro.Core.Enums.SettingsWindow;
using ClassPixelPro.Core.Helpers.UI;
using ClassPixelPro.Shared;
using ClassPixelPro.ViewModels.SettingsPages;

namespace ClassPixelPro.Views.SettingPages;

/// <summary>
/// 人脸考勤设置页的交互逻辑。
/// <para>
/// 这一页只做三件事：把 <see cref="FaceAttendanceSettingsViewModel"/> 交给界面、
/// 在 Loaded/Unloaded 时挂卸事件、把「一次性动作」按钮转发给 ViewModel 的命令。
/// 开关的状态本身双向绑定到设置，页面不自己存状态。
/// </para>
/// </summary>
[SettingsPageInfo("face-attendance", "人脸考勤", "\ue4e8", "\ue4e8", SettingsPageCategory.Internal)]
public partial class FaceAttendanceSettingsPage : SettingsPageBase
{
    public FaceAttendanceSettingsViewModel ViewModel { get; } = IAppHost.GetService<FaceAttendanceSettingsViewModel>();

    public FaceAttendanceSettingsPage()
    {
        InitializeComponent();
        DataContext = this;
    }

    private async void FaceAttendanceSettingsPage_OnLoaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.InitializeAsync();
    }

    private void FaceAttendanceSettingsPage_OnUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.Deinitialize();
    }

    /// <summary>
    /// 总开关切换时交给 ViewModel 复核前置条件。
    /// </summary>
    private async void ToggleEnabled_OnIsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        await ViewModel.ToggleEnabledCommand.ExecuteAsync(null);
    }

    private async void ButtonRunOnce_OnClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsBusy)
        {
            this.ShowWarningToast("已有操作正在进行中");
            return;
        }
        this.ShowToast("正在执行考勤…");
        await ViewModel.RunOnceCommand.ExecuteAsync(null);
        if (!string.IsNullOrEmpty(ViewModel.ActionMessage))
        {
            this.ShowToast(ViewModel.ActionMessage);
        }
    }

    private async void ButtonLoadModels_OnClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsBusy)
        {
            this.ShowWarningToast("已有操作正在进行中");
            return;
        }
        this.ShowToast("正在载入模型…");
        await ViewModel.LoadModelsCommand.ExecuteAsync(null);
        if (!string.IsNullOrEmpty(ViewModel.ActionMessage))
        {
            this.ShowToast(ViewModel.ActionMessage);
        }
    }

    private async void ButtonTestCamera_OnClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsBusy)
        {
            this.ShowWarningToast("已有操作正在进行中");
            return;
        }
        this.ShowToast("正在自检摄像头…");
        await ViewModel.TestCameraCommand.ExecuteAsync(null);
        if (!string.IsNullOrEmpty(ViewModel.ActionMessage))
        {
            this.ShowToast(ViewModel.ActionMessage);
        }
    }

    private async void ButtonRemoteEnroll_OnClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsBusy)
        {
            this.ShowWarningToast("已有操作正在进行中");
            return;
        }
        if (!ViewModel.IsTeacherBound)
        {
            this.ShowWarningToast("设备尚未绑定班主任，无法拉取远程录入照片");
            return;
        }
        this.ShowToast("正在拉取班主任上传的照片…");
        await ViewModel.DownloadAndImportCommand.ExecuteAsync(null);
        if (!string.IsNullOrEmpty(ViewModel.ActionMessage))
        {
            this.ShowToast(ViewModel.ActionMessage);
        }
    }

    private void ButtonAddStudent_OnClick(object sender, RoutedEventArgs e)
    {
        ViewModel.AddStudentCommand.Execute(null);
        this.ShowSuccessToast(ViewModel.ActionMessage);
    }

    private async void ButtonRefreshPlan_OnClick(object sender, RoutedEventArgs e)
    {
        await ViewModel.RefreshPlanCommand.ExecuteAsync(null);
        if (!string.IsNullOrEmpty(ViewModel.ActionMessage))
        {
            this.ShowToast(ViewModel.ActionMessage);
        }
    }

    private async void ButtonRefreshRoster_OnClick(object sender, RoutedEventArgs e)
    {
        await ViewModel.RefreshAllCommand.ExecuteAsync(null);
        this.ShowSuccessToast(ViewModel.ActionMessage);
    }
}
