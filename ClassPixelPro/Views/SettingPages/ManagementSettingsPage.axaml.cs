using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using ClassPixelPro.Controls;
using ClassPixelPro.Core;
using ClassPixelPro.Core.Abstractions.Controls;
using ClassPixelPro.Core.Abstractions.Services;
using ClassPixelPro.Core.Abstractions.Services.Management;
using ClassPixelPro.Core.Attributes;
using ClassPixelPro.Core.Controls;
using ClassPixelPro.Core.Enums.SettingsWindow;
using ClassPixelPro.Core.Models.Components;
using ClassPixelPro.Models;
using ClassPixelPro.Shared;
using ClassPixelPro.Shared.Enums;
using ClassPixelPro.Shared.Helpers;
using ClassPixelPro.Shared.Models.Management;
using ClassPixelPro.Shared.Models.Profile;
using ClassPixelPro.ViewModels.SettingsPages;
using FluentAvalonia.UI.Controls;
using Net.Codecrete.QrCodeGenerator;

namespace ClassPixelPro.Views.SettingPages;

/// <summary>
/// ManagementSettingsPage.xaml 的交互逻辑
/// </summary>
[SettingsPageInfo("management", "集控", true, SettingsPageCategory.About)]
public partial class ManagementSettingsPage : SettingsPageBase
{
    public IManagementService ManagementService { get; }

    public ManagementSettingsViewModel ViewModel { get; } = new();

    private static readonly HttpClient HttpClient = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private CancellationTokenSource? _bindingPollCts;

    /// <summary>
    /// 用于定时从 <see cref="IManagementService.ManagementLogs"/> 同步日志到 UI 的定时器。
    /// <see cref="IManagementService.ManagementLogs"/> 返回的是快照副本，不会主动通知变化，
    /// 因此需要定时拉取以刷新绑定。
    /// </summary>
    private DispatcherTimer? _logRefreshTimer;

    public ManagementSettingsPage(IManagementService managementService)
    {
        ManagementService = managementService;
        DataContext = this;
        InitializeComponent();
    }

    private void ButtonJoinManagement_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new JoinManagementDialog();
        dialog.ShowDialog((TopLevel.GetTopLevel(this) as Window)!);
    }

    private void ManagementSettingsPage_OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!ManagementService.IsManagementEnabled)
        {
            return;
        }

        var qrcode = QrCode.EncodeText(ManagementService.Persist.ClientUniqueId.ToString(), QrCode.Ecc.Medium);
        ViewModel.CuidQrCodePath = Geometry.Parse(qrcode.ToGraphicsPath());

        // 初始化时查询一次绑定状态
        _ = CheckBindingStatusAsync();

        // 启动日志刷新定时器：每 2 秒从 ManagementService.ManagementLogs 同步到 ViewModel
        RefreshManagementLogs();
        _logRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _logRefreshTimer.Tick += LogRefreshTimer_OnTick;
        _logRefreshTimer.Start();
    }

    private void ManagementSettingsPage_OnUnloaded(object sender, RoutedEventArgs e)
    {
        _logRefreshTimer?.Stop();
        _logRefreshTimer = null;
        StopPollingBindingStatus();
    }

    private void LogRefreshTimer_OnTick(object? sender, EventArgs e)
    {
        RefreshManagementLogs();
    }

    /// <summary>
    /// 将 <see cref="IManagementService.ManagementLogs"/> 的最新快照同步到
    /// <see cref="ManagementSettingsViewModel.ManagementLogs"/>，以驱动 UI 刷新。
    /// 使用逐项比较以避免无谓的清空+重建造成的闪烁。
    /// </summary>
    private void RefreshManagementLogs()
    {
        var latest = ManagementService.ManagementLogs;
        var current = ViewModel.ManagementLogs;

        // 快速路径：数量相同且首末元素一致时假定无变化
        if (current.Count == latest.Count &&
            (current.Count == 0 ||
             (ReferenceEquals(current[0], latest[0]) && ReferenceEquals(current[^1], latest[^1]))))
        {
            return;
        }

        // 数量不同或边界不同，整体替换
        current.Clear();
        // 日志按时间倒序显示（最新在最上）
        for (var i = latest.Count - 1; i >= 0; i--)
        {
            current.Add(latest[i]);
        }
    }

    private async void ButtonCopyCuid_OnClick(object sender, RoutedEventArgs e)
    {
        var clipboard = (TopLevel.GetTopLevel(this) as Window)?.Clipboard;
        if (clipboard != null)
        {
            await clipboard.SetTextAsync(ManagementService.Persist.ClientUniqueId.ToString());
        }
    }

    /// <summary>
    /// 获取集控服务器的 baseUrl，用于拼接教师绑定 API。
    /// </summary>
    private string? GetManagementBaseUrl()
    {
        var settings = ManagementService.Settings;
        switch (settings.ManagementServerKind)
        {
            case ManagementServerKind.ManagementServer:
                return string.IsNullOrWhiteSpace(settings.ManagementServer) ? null : settings.ManagementServer.TrimEnd('/');
            case ManagementServerKind.Serverless:
                // 从 ManifestUrlTemplate 中提取 baseUrl
                // 模板形如：https://example.com/serverless/manifest?cuid={cuid}&id={id}
                var template = settings.ManifestUrlTemplate;
                if (string.IsNullOrWhiteSpace(template)) return null;
                try
                {
                    var uri = new Uri(template);
                    var builder = new UriBuilder(uri.Scheme, uri.Host, uri.Port);
                    return builder.ToString().TrimEnd('/');
                }
                catch
                {
                    return null;
                }
            default:
                return null;
        }
    }


    private async void ButtonGenerateBindingQr_OnClick(object sender, RoutedEventArgs e)
    {
        await GenerateBindingQrCodeAsync();
    }

    /// <summary>
    /// 调用后端 API 生成绑定令牌，并显示二维码。
    /// </summary>
    private async Task GenerateBindingQrCodeAsync()
    {
        ViewModel.IsBindingQrLoading = true;
        ViewModel.IsBindingErrorVisible = false;
        ViewModel.BindingQrCodePath = null;

        try
        {
            var baseUrl = GetManagementBaseUrl();
            if (string.IsNullOrEmpty(baseUrl))
            {
                ViewModel.BindingErrorMessage = "无法确定集控服务器地址。";
                ViewModel.IsBindingErrorVisible = true;
                return;
            }

            var cuid = ManagementService.Persist.ClientUniqueId.ToString();
            var classIdentity = ManagementService.Settings.ClassIdentity ?? "";

            // 调用 POST /api/teacher/bindings/token
            var tokenUri = new Uri($"{baseUrl}/api/teacher/bindings/token");
            var requestBody = new { cuid, classIdentity };
            var requestJson = JsonSerializer.Serialize(requestBody, JsonOptions);
            using var content = new StringContent(requestJson, Encoding.UTF8, "application/json");
            using var response = await HttpClient.PostAsync(tokenUri, content);
            response.EnsureSuccessStatusCode();

            var responseJson = await response.Content.ReadAsStringAsync();
            var tokenResponse = JsonSerializer.Deserialize<TeacherBindingTokenResponse>(responseJson, JsonOptions);

            if (tokenResponse == null || string.IsNullOrWhiteSpace(tokenResponse.QrUrl))
            {
                ViewModel.BindingErrorMessage = "服务器返回的绑定信息无效。";
                ViewModel.IsBindingErrorVisible = true;
                return;
            }

            // 生成二维码（使用 QrCodeGenerator 本地生成，无需联网）
            var qrCode = QrCode.EncodeText(tokenResponse.QrUrl, QrCode.Ecc.Medium);
            ViewModel.BindingQrCodePath = Geometry.Parse(qrCode.ToGraphicsPath());

            // 开始轮询绑定状态
            StartPollingBindingStatus();
        }
        catch (Exception ex)
        {
            ViewModel.BindingErrorMessage = $"生成绑定二维码失败：{ex.Message}";
            ViewModel.IsBindingErrorVisible = true;
        }
        finally
        {
            ViewModel.IsBindingQrLoading = false;
        }
    }

    /// <summary>
    /// 开始轮询绑定状态，每 5 秒查询一次。
    /// </summary>
    private void StartPollingBindingStatus()
    {
        _bindingPollCts?.Cancel();
        _bindingPollCts = new CancellationTokenSource();
        ViewModel.IsPollingBindingStatus = true;
        _ = PollBindingStatusAsync(_bindingPollCts.Token);
    }

    /// <summary>
    /// 停止轮询绑定状态。
    /// </summary>
    private void StopPollingBindingStatus()
    {
        _bindingPollCts?.Cancel();
        _bindingPollCts = null;
        ViewModel.IsPollingBindingStatus = false;
    }

    private async Task PollBindingStatusAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && !ViewModel.IsTeacherBound)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                if (cancellationToken.IsCancellationRequested) break;

                var bound = await CheckBindingStatusAsync();
                if (bound)
                {
                    StopPollingBindingStatus();
                    break;
                }
            }
        }
        catch (TaskCanceledException)
        {
            // 正常取消
        }
        catch (Exception ex)
        {
            ViewModel.BindingErrorMessage = $"轮询绑定状态失败：{ex.Message}";
            ViewModel.IsBindingErrorVisible = true;
            ViewModel.IsPollingBindingStatus = false;
        }
    }

    /// <summary>
    /// 查询一次绑定状态。
    /// </summary>
    private async Task<bool> CheckBindingStatusAsync()
    {
        try
        {
            var baseUrl = GetManagementBaseUrl();
            if (string.IsNullOrEmpty(baseUrl)) return false;

            var cuid = ManagementService.Persist.ClientUniqueId.ToString();
            var statusUri = new Uri($"{baseUrl}/api/teacher/bindings/status?cuid={Uri.EscapeDataString(cuid)}");
            var responseJson = await HttpClient.GetStringAsync(statusUri);
            var status = JsonSerializer.Deserialize<TeacherBindingStatusResponse>(responseJson, JsonOptions);

            if (status == null) return false;

            if (status.Bound)
            {
                ViewModel.IsTeacherBound = true;
                ViewModel.BoundTeacherName = status.TeacherName;
                return true;
            }
            else
            {
                ViewModel.IsTeacherBound = false;
                ViewModel.BoundTeacherName = null;
                return false;
            }
        }
        catch
        {
            // 查询失败时静默处理（初始化时可能网络未就绪）
            return false;
        }
    }

    /// <summary>
    /// 拉取选中的集控配置并覆盖本地设置。
    /// 根据复选框的选择，只拉取并覆盖选定的配置项。
    /// </summary>
    private async void ButtonPullSelected_OnClick(object? sender, RoutedEventArgs e)
    {
        var options = GetPullOptions();
        if (!options.HasAny)
        {
            await CommonTaskDialogs.ShowDialog("未选择配置", "请至少选择一项要拉取的配置。");
            return;
        }
        await PullAndOverwriteAsync(options);
    }

    /// <summary>
    /// 全部拉取并覆盖本地设置（等同于之前的一键拉取）。
    /// </summary>
    private async void ButtonPullAll_OnClick(object? sender, RoutedEventArgs e)
    {
        await PullAndOverwriteAsync(PullOptions.All);
    }

    /// <summary>
    /// 从 UI 复选框读取当前选择。
    /// </summary>
    private PullOptions GetPullOptions()
    {
        return new PullOptions
        {
            ClassPlan = PullClassPlanCheckBox.IsChecked == true,
            TimeLayout = PullTimeLayoutCheckBox.IsChecked == true,
            Subjects = PullSubjectsCheckBox.IsChecked == true,
            AppSettings = PullAppSettingsCheckBox.IsChecked == true,
            ComponentScheme = PullComponentSchemeCheckBox.IsChecked == true,
            Theme = PullThemeCheckBox.IsChecked == true,
            TempClassPlan = PullTempClassPlanCheckBox.IsChecked == true,
        };
    }

    /// <summary>
    /// 从服务器强制拉取选定的集控内容并覆盖本地设置。
    /// <para>
    /// 与 <see cref="Services.Management.ManagementService.ReloadManagementAsync"/> 不同，此方法以"覆盖"方式
    /// （而非合并）应用课表/时间表/科目，且只处理选中的配置项，未选中的项保持不变。
    /// </para>
    /// </summary>
    private async Task PullAndOverwriteAsync(PullOptions options)
    {
        // 1. 弹出确认对话框，警告用户这将覆盖本地设置
        var confirmDialog = new TaskDialog
        {
            Title = "ClassPixelPro",
            SubHeader = "确认拉取并覆盖",
            Content = options.IsAll
                ? "此操作将从服务器强制拉取所有集控内容（课表、科目、时间表、应用设置等）并覆盖本地设置。\n\n" +
                  "如果服务器同时下发了全局课表和本设备的专属课表，将优先使用专属课表。\n\n" +
                  "确定要继续吗？"
                : "此操作将从服务器强制拉取以下选定的集控内容并覆盖本地设置：\n\n" +
                  options.GetDescription() + "\n\n" +
                  "确定要继续吗？",
            Buttons =
            {
                TaskDialogButton.CancelButton,
                new TaskDialogButton("确定", true)
                {
                    IsDefault = true
                }
            },
            XamlRoot = AppBase.Current.GetRootWindow(),
        };

        var confirmResult = await confirmDialog.ShowAsync();
        if (confirmResult?.Equals(true) != true) return;

        try
        {
            var connection = ManagementService.Connection;
            if (connection == null)
            {
                await CommonTaskDialogs.ShowDialog("拉取失败", "集控服务器连接未建立。");
                return;
            }

            if (ManagementService is not Services.Management.ManagementService concreteManagementService)
            {
                await CommonTaskDialogs.ShowDialog("拉取失败", "无法获取集控服务实例。");
                return;
            }

            // 2. 获取最新的 manifest，确保各 source 的 URL 与版本是最新的。
            var manifest = await connection.GetManifest();
            if (manifest.CoreVersion.Major != IAppHost.CoreVersion.Major)
            {
                await CommonTaskDialogs.ShowDialog("集控核心版本不兼容",
                    $"您正在核心版本主版本号（{IAppHost.CoreVersion}）与集控数据核心版本主版本号（{manifest.CoreVersion}）不同的 ClassPixelPro 加入集控，这可能会导致您的数据损坏。请运行兼容贵组织集控服务的 ClassPixelPro 版本，或者联系您的管理员了解详细信息。");
                return;
            }
            ManagementService.Manifest = manifest;

            var profileService = IAppHost.TryGetService<IProfileService>();
            var profile = profileService?.Profile;

            // 3. 拉取并覆盖课表（ClassPlans + ClassPlanGroups）
            //    Manifest.ClassPlanSource 由服务器根据设备 cuid 返回，
            //    可能是全局课表或本设备的专属课表，服务器端已处理优先级。
            if (options.ClassPlan && !string.IsNullOrWhiteSpace(manifest.ClassPlanSource.Value))
            {
                var cpNew = await connection.GetJsonAsync<Profile>(manifest.ClassPlanSource.Value!);
                if (profile != null)
                {
                    // 用 clear + add 代替替换整个 ObservableDictionary。
                    // 直接赋值会丢失 ProfileService.LoadProfileAsync 中注册的审计 CollectionChanged 处理器，
                    // 且不触发 CollectionChanged 事件，导致绑定到集合项的 UI 无法实时刷新。
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        profile.ClassPlans.Clear();
                        foreach (var kvp in cpNew.ClassPlans)
                        {
                            profile.ClassPlans.Add(kvp.Key, kvp.Value);
                        }
                        profile.ClassPlanGroups.Clear();
                        foreach (var kvp in cpNew.ClassPlanGroups)
                        {
                            profile.ClassPlanGroups.Add(kvp.Key, kvp.Value);
                        }
                    });
                }
                ManagementService.Versions.ClassPlanVersion = manifest.ClassPlanSource.Version;
            }

            // 4. 拉取并覆盖时间表
            if (options.TimeLayout && !string.IsNullOrWhiteSpace(manifest.TimeLayoutSource.Value))
            {
                var tlNew = await connection.GetJsonAsync<Profile>(manifest.TimeLayoutSource.Value!);
                if (profile != null)
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        profile.TimeLayouts.Clear();
                        foreach (var kvp in tlNew.TimeLayouts)
                        {
                            profile.TimeLayouts.Add(kvp.Key, kvp.Value);
                        }
                    });
                }
                ManagementService.Versions.TimeLayoutVersion = manifest.TimeLayoutSource.Version;
            }

            // 5. 拉取并覆盖科目
            if (options.Subjects && !string.IsNullOrWhiteSpace(manifest.SubjectsSource.Value))
            {
                var subjectNew = await connection.GetJsonAsync<Profile>(manifest.SubjectsSource.Value!);
                if (profile != null)
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        profile.Subjects.Clear();
                        foreach (var kvp in subjectNew.Subjects)
                        {
                            profile.Subjects.Add(kvp.Key, kvp.Value);
                        }
                    });
                }
                ManagementService.Versions.SubjectsVersion = manifest.SubjectsSource.Version;
            }

            // 保存档案到本地（仅在修改了课表/时间表/科目时）。
            // 集控模式下 CurrentProfilePath 即为 "_management-profile.json"（见 ProfileService.LoadProfileAsync）。
            if (profileService != null && (options.ClassPlan || options.TimeLayout || options.Subjects))
            {
                profileService.SaveProfile("_management-profile.json");
            }

            // 6. 拉取并应用应用设置
            if (options.AppSettings && !string.IsNullOrWhiteSpace(manifest.AppSettingsSource.Value))
            {
                await connection.SaveJsonAsync<Settings>(
                    manifest.AppSettingsSource.Value!,
                    Services.Management.ManagementService.ManagementAppSettingsPath);
                var appSettings = ConfigureFileHelper.LoadConfig<Settings>(
                    Services.Management.ManagementService.ManagementAppSettingsPath);
                var settingsService = IAppHost.TryGetService<Services.SettingsService>();
                if (settingsService != null)
                {
                    settingsService.Settings = appSettings;
                }
                ManagementService.Versions.AppSettingsVersion = manifest.AppSettingsSource.Version;
            }

            // 7. 拉取并应用组件方案
            if (options.ComponentScheme && manifest.ComponentSchemeSource.HasValue &&
                !string.IsNullOrWhiteSpace(manifest.ComponentSchemeSource.Value.Value))
            {
                await connection.SaveJsonAsync<object>(
                    manifest.ComponentSchemeSource.Value.Value!,
                    Services.Management.ManagementService.ManagementComponentSchemePath);
                var componentsService = IAppHost.TryGetService<IComponentsService>();
                if (componentsService != null)
                {
                    var scheme = ConfigureFileHelper.LoadConfig<ComponentProfile>(
                        Services.Management.ManagementService.ManagementComponentSchemePath);
                    // 将集控组件方案写入组件配置目录，使用固定名称 "Management"，
                    // 便于用户在编辑模式下查看与切换；随后刷新方案列表并立即应用。
                    const string managementSchemeName = "Management";
                    var schemePath = Path.Combine(
                        Services.ComponentsService.ComponentSettingsPath,
                        managementSchemeName + ".json");
                    ConfigureFileHelper.SaveConfig(schemePath, scheme);
                    componentsService.RefreshConfigs();
                    componentsService.CurrentComponents = scheme;
                }
                ManagementService.Versions.ComponentSchemeVersion = manifest.ComponentSchemeSource.Value.Version;
            }

            // 8. 拉取主题配置
            if (options.Theme && !string.IsNullOrWhiteSpace(manifest.ThemeSource.Value))
            {
                await connection.SaveJsonAsync<object>(
                    manifest.ThemeSource.Value!,
                    Services.Management.ManagementService.ManagementThemePath);
                ManagementService.Versions.ThemeVersion = manifest.ThemeSource.Version;
            }

            // 9. 拉取并应用临时调课课表
            if (options.TempClassPlan && manifest.TempClassPlanSource.HasValue &&
                !string.IsNullOrWhiteSpace(manifest.TempClassPlanSource.Value.Value))
            {
                await connection.SaveJsonAsync<object>(
                    manifest.TempClassPlanSource.Value.Value!,
                    Services.Management.ManagementService.ManagementTempClassPlanPath);
                await concreteManagementService.ApplyTempClassPlanAsync();
                ManagementService.Versions.TempClassPlanVersion = manifest.TempClassPlanSource.Value.Version;
            }

            // 10. 保存版本号
            concreteManagementService.SaveSettings();

            await CommonTaskDialogs.ShowDialog("拉取成功",
                options.IsAll
                    ? "集控策略已成功拉取并覆盖本地设置。"
                    : $"已拉取并覆盖以下配置：{options.GetDescription()}。");
        }
        catch (Exception ex)
        {
            await CommonTaskDialogs.ShowDialog("拉取失败", $"拉取集控策略失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 集控配置拉取选项。用于指定要拉取并覆盖的配置类型。
    /// </summary>
    private class PullOptions
    {
        /// <summary>课表（ClassPlan + ClassPlanGroups）</summary>
        public bool ClassPlan;

        /// <summary>时间表（TimeLayouts）</summary>
        public bool TimeLayout;

        /// <summary>科目（Subjects）</summary>
        public bool Subjects;

        /// <summary>应用设置（AppSettings）</summary>
        public bool AppSettings;

        /// <summary>组件方案（ComponentScheme）</summary>
        public bool ComponentScheme;

        /// <summary>主题（Theme）</summary>
        public bool Theme;

        /// <summary>临时调课（TempClassPlan）</summary>
        public bool TempClassPlan;

        /// <summary>是否至少选择了一项。</summary>
        public bool HasAny => ClassPlan || TimeLayout || Subjects || AppSettings ||
                              ComponentScheme || Theme || TempClassPlan;

        /// <summary>是否全选。</summary>
        public bool IsAll => ClassPlan && TimeLayout && Subjects && AppSettings &&
                             ComponentScheme && Theme && TempClassPlan;

        /// <summary>全选选项。</summary>
        public static PullOptions All => new()
        {
            ClassPlan = true,
            TimeLayout = true,
            Subjects = true,
            AppSettings = true,
            ComponentScheme = true,
            Theme = true,
            TempClassPlan = true,
        };

        /// <summary>获取选中项的中文描述，用于确认对话框展示。</summary>
        public string GetDescription()
        {
            var parts = new List<string>();
            if (ClassPlan) parts.Add("课表");
            if (TimeLayout) parts.Add("时间表");
            if (Subjects) parts.Add("科目");
            if (AppSettings) parts.Add("应用设置");
            if (ComponentScheme) parts.Add("组件方案");
            if (Theme) parts.Add("主题");
            if (TempClassPlan) parts.Add("临时调课");
            return string.Join("、", parts);
        }
    }

    private async void ButtonUnbindTeacher_OnClick(object sender, RoutedEventArgs e)
    {
        // 解绑逻辑：调用后端解绑 API（如果可用），或仅本地清除状态
        var dialog = new TaskDialog
        {
            Title = "ClassPixelPro",
            SubHeader = "解绑教师",
            Content = "确定要解绑当前教师吗？解绑后本设备将不再与班主任账户关联。",
            Buttons =
            {
                TaskDialogButton.CancelButton,
                new TaskDialogButton("解绑", true)
                {
                    IsDefault = true
                }
            },
            XamlRoot = AppBase.Current.GetRootWindow(),
        };

        var result = await dialog.ShowAsync();
        if (result?.Equals(true) != true) return;

        try
        {
            var baseUrl = GetManagementBaseUrl();
            if (!string.IsNullOrEmpty(baseUrl))
            {
                var cuid = ManagementService.Persist.ClientUniqueId.ToString();
                var unbindUri = new Uri($"{baseUrl}/api/teacher/bindings/unbind?cuid={Uri.EscapeDataString(cuid)}");
                using var response = await HttpClient.PostAsync(unbindUri, null);
                // 不检查响应状态，即使失败也清除本地状态
            }
        }
        catch
        {
            // 忽略网络错误，仍清除本地状态
        }

        ViewModel.IsTeacherBound = false;
        ViewModel.BoundTeacherName = null;
        ViewModel.BindingQrCodePath = null;
    }

    /// <summary>
    /// 教师绑定令牌响应。
    /// </summary>
    private class TeacherBindingTokenResponse
    {
        public string? Token { get; set; }
        public string? QrUrl { get; set; }
    }

    /// <summary>
    /// 教师绑定状态响应。
    /// </summary>
    private class TeacherBindingStatusResponse
    {
        public bool Bound { get; set; }
        public string? TeacherName { get; set; }
    }
}
