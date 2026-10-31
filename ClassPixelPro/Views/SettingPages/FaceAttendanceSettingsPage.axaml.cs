using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ClassPixelPro.Core.Abstractions.Controls;
using ClassPixelPro.Core.Attributes;
using ClassPixelPro.Core.Enums.SettingsWindow;
using ClassPixelPro.Core.Helpers.UI;
using ClassPixelPro.Services.FaceAttendance;
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
        // 智能导入遇到多脸照片时要问用户「这张脸归谁」，这里把弹窗交给 ViewModel。
        // 不挂的话 AskSmartAsync 一律返回「跳过」，合影一张也录不进去。
        ViewModel.SmartPromptHandler = ShowSmartPromptAsync;
        await ViewModel.InitializeAsync();
    }

    /// <summary>
    /// 弹出「这张照片是谁？」让用户挑脸与归属同学。窗口被直接关掉时按「跳过」处理。
    /// </summary>
    /// <remarks>
    /// SmartImportAsync 在 Task.Run(ConfigureAwait(false)) 之后回调本方法，调用线程是
    /// 线程池线程；Avalonia 窗口只能在 UI 线程构造（否则 Call from invalid thread），
    /// 所以先 marshal 回 UI 线程再弹窗。
    /// </remarks>
    private async Task<SmartDecision> ShowSmartPromptAsync(SmartPrompt prompt)
    {
        if (!Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            return await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(
                () => ShowSmartPromptAsync(prompt));
        }

        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner == null) return new SmartDecision { Skip = true };

        var window = new FaceEnrollPromptWindow(prompt, ViewModel.Roster.Students);
        var result = await window.ShowDialog<SmartDecision?>(owner);
        return result ?? new SmartDecision { Skip = true };
    }

    /// <summary>
    /// 同学列表里的「参与考勤 / 今日请假」开关。Student 是纯数据类，
    /// 绑定只会改内存里的值，这里补一次落盘。
    /// </summary>
    private void StudentFlag_OnChanged(object? sender, RoutedEventArgs e)
    {
        ViewModel.Roster.Save();
    }

    private async void ButtonImportModel_OnClick(object sender, RoutedEventArgs e)
    {
        await ViewModel.ImportModelFileCommand.ExecuteAsync(null);
        if (!string.IsNullOrEmpty(ViewModel.ActionMessage))
        {
            this.ShowToast(ViewModel.ActionMessage);
        }
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
