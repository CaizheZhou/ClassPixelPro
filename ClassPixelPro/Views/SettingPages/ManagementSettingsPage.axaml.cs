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
using Avalonia.Controls.Primitives;
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
using ClassPixelPro.Helpers;
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

    /// <summary>
    /// 服务端返回的班级课表列表。用于「选择班级」下拉。
    /// </summary>
    private List<ClassPlanOption>? _classPlans;
    private bool _isUpdatingClassPlan;

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

        // 加载班级课表列表并同步当前选班
        _ = LoadClassPlansAndSyncUI();
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
    /// 加载组织下可选的班级课表，并同步当前选班到 UI。
    /// </summary>
    private async Task LoadClassPlansAndSyncUI()
    {
        if (!ManagementService.IsManagementEnabled) return;
        try
        {
            var (list, current) = await ManagementService.GetClassPlanListAsync();
            _classPlans = list;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (ClassPlanComboBox == null) return;
                ClassPlanComboBox.Items.Clear();
                ClassPlanComboBox.Items.Add("使用全局课表");
                if (_classPlans != null)
                {
                    foreach (var p in _classPlans)
                    {
                        ClassPlanComboBox.Items.Add($"{p.Name} (v{p.Version})");
                    }
                }
                SyncClassPlanUI(current);
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"加载班级课表列表失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 将当前设备的选班同步到下拉框（current 为服务端返回的 class_plan_id）。
    /// </summary>
    private void SyncClassPlanUI(Guid? current)
    {
        if (_isUpdatingClassPlan) return;
        _isUpdatingClassPlan = true;
        try
        {
            var idx = 0;
            if (current != null && _classPlans != null)
            {
                var i = _classPlans.FindIndex(p => p.Id == current);
                if (i >= 0) idx = i + 1;
            }
            ClassPlanComboBox.SelectedIndex = idx;
        }
        finally
        {
            _isUpdatingClassPlan = false;
        }
    }

    /// <summary>
    /// 班级课表下拉选中项变化：上报服务端，选班后课表自动拉取。
    /// </summary>
    private async void ClassPlanComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingClassPlan) return;
        if (ClassPlanComboBox == null || _classPlans == null) return;

        var idx = ClassPlanComboBox.SelectedIndex;
        Guid? classPlanId = null;
        string? className = null;
        if (idx > 0 && idx - 1 < _classPlans.Count)
        {
            classPlanId = _classPlans[idx - 1].Id;
            className = _classPlans[idx - 1].Name;
        }

        if (ClassPlanInfo != null)
        {
            ClassPlanInfo.IsVisible = true;
            ClassPlanInfo.Text = className != null ? $"已选择班级：{className}，课表将自动拉取" : "已切换为使用全局课表";
        }

        try
        {
            await ManagementService.SelectClassPlanAsync(classPlanId);
            // 选班后立即重载集控，让新课表马上生效（不等 5 秒轮询）。
            // 后端已用 class_plan_epoch 保证切班后 classplan 版本单调递增，这里直接触发一次拉取即可。
            if (ManagementService is Services.Management.ManagementService concrete)
            {
                _ = concrete.ReloadManagementAsync();
            }
            // 提示：新课表已自动生效，同时给出强制重拉 + 重启的入口。
            // 部分配置（组件方案、主题等）需重启进程后才完全生效，故保留该出口。
            if (ClassPlanChangedPanel != null)
            {
                ClassPlanChangedPanel.IsVisible = true;
            }
        }
        catch (Exception ex)
        {
            if (ClassPlanInfo != null)
            {
                ClassPlanInfo.Text = "选班失败：" + ex.Message;
            }
        }
    }

    /// <summary>
    /// "重新拉取并重启"按钮点击事件。
    /// 部分配置（如组件方案、主题等）需要重启进程才能完全生效。
    /// 此处先重置所有本地版本号为 0 以强制重新拉取所有配置，拉取完成后再重启进程。
    /// </summary>
    private async void ReloadConfigButton_OnClick(object sender, RoutedEventArgs e)
    {
        // 弹出确认对话框：CommonTaskDialogs.ShowDialog 只有"确定"按钮，无法取消，
        // 这里使用 TaskDialog 自定义"取消/确定"两个按钮，与 PullAndOverwriteAsync 中的确认对话框风格一致。
        var confirmDialog = new TaskDialog
        {
            Title = "ClassPixelPro",
            SubHeader = "确认拉取并重启",
            Content = "将重新从服务器拉取所有集控配置，然后重启应用。确定要继续吗？",
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
            // 第一步：重置所有本地版本号为 0，强制下次 Reload 拉取所有配置。
            // 切换模式后服务端返回的新模式版本号可能小于本地缓存的版本号
            // （如 global classplan v3 → pool v2，IsNewerAndNotNull(2 > 3) 为 false 不拉取），
            // 故需先重置版本号才能强制拉取新配置。
            var versions = ManagementService.Versions;
            versions.ClassPlanVersion = 0;
            versions.TimeLayoutVersion = 0;
            versions.SubjectsVersion = 0;
            versions.DefaultSettingsVersion = 0;
            versions.PolicyVersion = 0;
            versions.CredentialVersion = 0;
            versions.ComponentsVersion = 0;
            versions.AppSettingsVersion = 0;
            versions.PluginsVersion = 0;
            versions.ThemeVersion = 0;
            versions.ComponentSchemeVersion = 0;
            versions.TempClassPlanVersion = 0;
            versions.HelpInfoVersion = 0;

            // 第二步：重新拉取所有配置
            // ReloadManagementAsync 定义在实现类 Services.Management.ManagementService 上，
            // 接口 IManagementService 未暴露该方法，故需先做类型转换，与 PullAndOverwriteAsync 中的处理一致。
            if (ManagementService is not Services.Management.ManagementService concreteManagementService)
            {
                await CommonTaskDialogs.ShowDialog("拉取失败", "无法获取集控服务实例。");
                return;
            }
            await concreteManagementService.ReloadManagementAsync();

            // 第三步：重启应用
            var currentProcess = System.Diagnostics.Process.GetCurrentProcess();
            var executablePath = currentProcess.MainModule?.FileName;
            if (string.IsNullOrEmpty(executablePath))
            {
                await CommonTaskDialogs.ShowDialog("重启失败", "无法确定当前可执行文件路径。");
                return;
            }

            // 启动新进程
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = true
            });

            // 退出当前进程
            System.Environment.Exit(0);
        }
        catch (Exception ex)
        {
            await CommonTaskDialogs.ShowDialog("操作失败", $"拉取配置或重启应用失败：{ex.Message}");
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
    /// <para>
    /// 修复 P1-17：自动路径（ReloadManagementAsync）走合并语义（MergeManagementProfileAsync），
    /// 此手动路径走覆盖语义（Clear+Add）。两套语义有意为之：
    /// <list type="bullet">
    /// <item>自动路径：定时轮询，需保留用户手动修改，故用合并。</item>
    /// <item>手动路径：用户在 UI 上明确点击"拉取并覆盖"，确认对话框已警告"覆盖本地设置"，
    /// 故用覆盖以符合用户预期。</item>
    /// </list>
    /// 为确保两套路径的版本号不互相踩，此方法在每次拉取后立即更新对应版本号并最终 SaveSettings()，
    /// 与自动路径的版本号更新策略一致。下次自动轮询会因版本号已等于 manifest 而跳过，不会重复拉取。
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
                // 修复：把拉取到的 cpNew 落盘到 ManagementClassPlanPath，
                // 作为后续 MergeManagementProfileAsync 中 MergeDictionary 的 diffBase，
                // 否则 diffBase 永远为空，集控合并无法识别"被删除的键"。
                ConfigureFileHelper.SaveConfig(
                    Services.ProfileService.ManagementClassPlanPath, cpNew);
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
                // 修复：同步落盘 tlNew 到 ManagementTimeLayoutPath，理由同上。
                ConfigureFileHelper.SaveConfig(
                    Services.ProfileService.ManagementTimeLayoutPath, tlNew);
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
                // 修复：同步落盘 subjectNew 到 ManagementSubjectsPath，理由同上。
                ConfigureFileHelper.SaveConfig(
                    Services.ProfileService.ManagementSubjectsPath, subjectNew);
                ManagementService.Versions.SubjectsVersion = manifest.SubjectsSource.Version;
            }

            // 保存档案到本地（仅在修改了课表/时间表/科目时）。
            // 集控模式下 CurrentProfilePath 即为 "_management-profile.json"（见 ProfileService.LoadProfileAsync）。
            if (profileService != null && (options.ClassPlan || options.TimeLayout || options.Subjects))
            {
                profileService.SaveProfile("_management-profile.json");
            }

            // 修复：Clear + Add 只触发 CollectionChanged，不触发 ObservableDictionary 所在
            // Profile 对象的 PropertyChanged，绑定到整个集合（而非集合项）的 UI 不会刷新。
            // 用 CopyObject 重建实例并重新赋值，借助 setter 触发 PropertyChanged。
            // 同时调用 RefreshTimeLayouts() 重建 ClassPlan → TimeLayout 引用缓存，
            // 否则覆盖后 ClassPlan.TimeLayoutId 指向的 TimeLayout 可能尚未在 UI 端生效。
            if (profile != null && (options.ClassPlan || options.TimeLayout || options.Subjects))
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    profile.ClassPlans = ConfigureFileHelper.CopyObject(profile.ClassPlans);
                    profile.ClassPlanGroups = ConfigureFileHelper.CopyObject(profile.ClassPlanGroups);
                    profile.TimeLayouts = ConfigureFileHelper.CopyObject(profile.TimeLayouts);
                    profile.Subjects = ConfigureFileHelper.CopyObject(profile.Subjects);
                    profile.RefreshTimeLayouts();
                });
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
                    // 修复 P1-13：使用 ReplaceSettings 统一处理 PropertyChanged 订阅/取消订阅，
                    // 避免旧对象仍能触发 SettingsChanged 而新对象修改不落盘。
                    settingsService.ReplaceSettings(appSettings);
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

    #region 配置预览

    /// <summary>
    /// 预览当前实际使用的课表。
    /// 从 <see cref="IProfileService.Profile"/> 读取（合并了集控配置和本地修改），
    /// 这样能正确显示关联时间表名称和科目名称。
    /// </summary>
    private async void ButtonPreviewClassPlan_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var profileService = IAppHost.TryGetService<IProfileService>();
            var profile = profileService?.Profile;
            if (profile == null || profile.ClassPlans.Count == 0)
            {
                await CommonTaskDialogs.ShowDialog("课表预览", "当前没有课表数据。");
                return;
            }

            var text = BuildClassPlanPreview(profile);
            await ShowPreviewDialog("当前课表预览", text);
        }
        catch (Exception ex)
        {
            await CommonTaskDialogs.ShowDialog("课表预览失败", $"读取课表失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 预览当前实际使用的时间表。
    /// 从 <see cref="IProfileService.Profile"/> 读取，确保展示的是当前生效的配置。
    /// </summary>
    private async void ButtonPreviewTimeLayout_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var profileService = IAppHost.TryGetService<IProfileService>();
            var profile = profileService?.Profile;
            if (profile == null || profile.TimeLayouts.Count == 0)
            {
                await CommonTaskDialogs.ShowDialog("时间表预览", "当前没有时间表数据。");
                return;
            }

            var text = BuildTimeLayoutPreview(profile);
            await ShowPreviewDialog("当前时间表预览", text);
        }
        catch (Exception ex)
        {
            await CommonTaskDialogs.ShowDialog("时间表预览失败", $"读取时间表失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 预览当前实际使用的科目。
    /// 从 <see cref="IProfileService.Profile"/> 读取，确保展示的是当前生效的配置。
    /// </summary>
    private async void ButtonPreviewSubjects_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var profileService = IAppHost.TryGetService<IProfileService>();
            var profile = profileService?.Profile;
            if (profile == null || profile.Subjects.Count == 0)
            {
                await CommonTaskDialogs.ShowDialog("科目预览", "当前没有科目数据。");
                return;
            }

            var text = BuildSubjectsPreview(profile);
            await ShowPreviewDialog("当前科目预览", text);
        }
        catch (Exception ex)
        {
            await CommonTaskDialogs.ShowDialog("科目预览失败", $"读取科目失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 预览当前拉取到本地的应用设置缓存（AppSettings.json）。
    /// </summary>
    private async void ButtonPreviewAppSettings_OnClick(object sender, RoutedEventArgs e)
    {
        var path = Services.Management.ManagementService.ManagementAppSettingsPath;
        if (!File.Exists(path))
        {
            await CommonTaskDialogs.ShowDialog("应用设置预览", "尚未拉取，请先拉取配置。");
            return;
        }

        try
        {
            var settings = ConfigureFileHelper.LoadConfig<Settings>(path);
            var text = BuildAppSettingsPreview(settings);
            await ShowPreviewDialog("应用设置预览", text);
        }
        catch (Exception ex)
        {
            await CommonTaskDialogs.ShowDialog("应用设置预览失败", $"读取应用设置缓存失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 预览当前拉取到本地的组件方案缓存（ManagementComponentScheme.json）。
    /// </summary>
    private async void ButtonPreviewComponentScheme_OnClick(object sender, RoutedEventArgs e)
    {
        var path = Services.Management.ManagementService.ManagementComponentSchemePath;
        if (!File.Exists(path))
        {
            await CommonTaskDialogs.ShowDialog("组件方案预览", "尚未拉取，请先拉取配置。");
            return;
        }

        try
        {
            var scheme = ConfigureFileHelper.LoadConfig<ComponentProfile>(path);
            var text = BuildComponentSchemePreview(scheme);
            await ShowPreviewDialog("组件方案预览", text);
        }
        catch (Exception ex)
        {
            await CommonTaskDialogs.ShowDialog("组件方案预览失败", $"读取组件方案缓存失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 以可滚动的等宽文本对话框展示预览内容。
    /// </summary>
    private static async Task ShowPreviewDialog(string title, string content)
    {
        var textBlock = new TextBlock
        {
            Text = content,
            FontFamily = new FontFamily("Consolas"),
            TextWrapping = TextWrapping.NoWrap,
            FontSize = 13
        };
        var scrollViewer = new ScrollViewer
        {
            Content = textBlock,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 500,
            MaxWidth = 700
        };
        var dialog = new TaskDialog
        {
            Header = title,
            Content = scrollViewer,
            XamlRoot = AppBase.Current.GetRootWindow(),
            Buttons =
            {
                new TaskDialogButton("关闭", true)
                {
                    IsDefault = true
                }
            }
        };
        await dialog.ShowAsync();
    }

    /// <summary>
    /// 生成课表预览文本。展示每个课表的名称、触发星期、关联时间表与课程列表（科目简称）。
    /// </summary>
    private static string BuildClassPlanPreview(Profile profile)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"课表数量：{profile.ClassPlans.Count}");
        if (profile.ClassPlanGroups.Count > 0)
        {
            sb.AppendLine($"课表群数量：{profile.ClassPlanGroups.Count}");
        }
        sb.AppendLine(new string('─', 40));

        var weekDayNames = new[] { "周日", "周一", "周二", "周三", "周四", "周五", "周六" };

        foreach (var (id, plan) in profile.ClassPlans)
        {
            sb.AppendLine($"【{plan.Name}】");
            sb.AppendLine($"  ID：{id}");
            if (profile.TimeLayouts.TryGetValue(plan.TimeLayoutId, out var tl))
            {
                sb.AppendLine($"  关联时间表：{tl.Name}");
            }
            else
            {
                sb.AppendLine($"  关联时间表ID：{plan.TimeLayoutId}（未在缓存中找到）");
            }
            var weekDay = plan.TimeRule.WeekDay;
            var weekDayText = weekDay >= 0 && weekDay < weekDayNames.Length
                ? weekDayNames[weekDay]
                : $"第{weekDay}天";
            sb.AppendLine($"  触发星期：{weekDayText}");
            if (plan.TimeRule.WeekCountDiv != 0)
            {
                sb.AppendLine($"  轮换：第{plan.TimeRule.WeekCountDiv}周 / 共{plan.TimeRule.WeekCountDivTotal}周");
            }
            sb.AppendLine($"  课程列表（共{plan.Classes.Count}节）：");
            for (var i = 0; i < plan.Classes.Count; i++)
            {
                var cls = plan.Classes[i];
                var subjectName = "（空）";
                if (cls.SubjectId != Guid.Empty && profile.Subjects.TryGetValue(cls.SubjectId, out var subject))
                {
                    subjectName = string.IsNullOrEmpty(subject.Initial) ? subject.Name : subject.Initial;
                }
                var enabledText = cls.IsEnabled ? "" : "（已禁用）";
                sb.AppendLine($"    {i + 1}. {subjectName}{enabledText}");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// 生成时间表预览文本。展示每个时间表的名称与时间点列表（开始-结束 + 类型）。
    /// </summary>
    private static string BuildTimeLayoutPreview(Profile profile)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"时间表数量：{profile.TimeLayouts.Count}");
        sb.AppendLine(new string('─', 40));

        foreach (var (id, layout) in profile.TimeLayouts)
        {
            sb.AppendLine($"【{layout.Name}】");
            sb.AppendLine($"  ID：{id}");
            sb.AppendLine($"  时间点（共{layout.Layouts.Count}个）：");
            for (var i = 0; i < layout.Layouts.Count; i++)
            {
                var item = layout.Layouts[i];
                var typeText = item.TimeType switch
                {
                    0 => "上课",
                    1 => "课间",
                    2 => "分割线",
                    3 => "行动",
                    _ => "未知"
                };
                string timeRange;
                if (item.TimeType == 1 && !string.IsNullOrEmpty(item.BreakName))
                {
                    timeRange = $"{FormatTimeSpan(item.StartTime)}-{FormatTimeSpan(item.EndTime)} {item.BreakName}";
                }
                else
                {
                    timeRange = $"{FormatTimeSpan(item.StartTime)}-{FormatTimeSpan(item.EndTime)} {typeText}";
                }
                sb.AppendLine($"    {i + 1}. {timeRange}");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// 生成科目预览文本。展示每个科目的简称、全名、教师与是否户外。
    /// </summary>
    private static string BuildSubjectsPreview(Profile profile)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"科目数量：{profile.Subjects.Count}");
        sb.AppendLine(new string('─', 40));

        var idx = 0;
        foreach (var (id, subject) in profile.Subjects)
        {
            idx++;
            sb.AppendLine($"【科目 {idx}】");
            sb.AppendLine($"  ID：{id}");
            sb.AppendLine($"  简称：{subject.Initial}");
            sb.AppendLine($"  全名：{subject.Name}");
            sb.AppendLine($"  教师：{subject.TeacherName}");
            sb.AppendLine($"  户外：{(subject.IsOutDoor ? "是" : "否")}");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// 生成应用设置预览文本。展示主题、颜色、字体、通知等关键设置项。
    /// </summary>
    private static string BuildAppSettingsPreview(Settings settings)
    {
        var sb = new StringBuilder();
        sb.AppendLine("【应用设置】");
        sb.AppendLine(new string('─', 40));

        var themeText = settings.Theme switch
        {
            0 => "跟随系统",
            1 => "明亮",
            2 => "黑暗",
            _ => $"未知({settings.Theme})"
        };
        sb.AppendLine($"  主题：{themeText}");
        sb.AppendLine($"  强调色：{ToHexColor(settings.PrimaryColor)}");
        sb.AppendLine($"  次强调色：{ToHexColor(settings.SecondaryColor)}");
        sb.AppendLine($"  背景色：{ToHexColor(settings.BackgroundColor)}");
        sb.AppendLine($"  自定义背景色启用：{(settings.IsCustomBackgroundColorEnabled ? "是" : "否")}");
        sb.AppendLine($"  透明度：{settings.Opacity}");
        sb.AppendLine($"  缩放：{settings.Scale}");
        sb.AppendLine($"  主窗口字体：{settings.MainWindowFont}");
        sb.AppendLine($"  字体粗细：{settings.MainWindowFontWeight2}");
        sb.AppendLine($"  圆角X：{settings.RadiusX}");
        sb.AppendLine($"  圆角Y：{settings.RadiusY}");
        sb.AppendLine($"  次要字号：{settings.MainWindowSecondaryFontSize}");
        sb.AppendLine($"  正文字号：{settings.MainWindowBodyFontSize}");
        sb.AppendLine($"  强调字号：{settings.MainWindowEmphasizedFontSize}");
        sb.AppendLine($"  大字号：{settings.MainWindowLargeFontSize}");
        sb.AppendLine($"  自定义前景色启用：{(settings.IsCustomForegroundColorEnabled ? "是" : "否")}");
        sb.AppendLine($"  自定义前景色：{ToHexColor(settings.CustomForegroundColor)}");
        sb.AppendLine($"  行垂直间距：{settings.MainWindowLineVerticalMargin}");
        sb.AppendLine($"  调度间距：{settings.ScheduleSpacing}");
        sb.AppendLine($"  当前档案：{settings.SelectedProfile}");
        sb.AppendLine($"  当前组件方案：{settings.CurrentComponentConfig}");
        sb.AppendLine($"  Island 分离：{(settings.IsIslandSeperated ? "是" : "否")}");
        sb.AppendLine($"  主窗口可见：{(settings.IsMainWindowVisible ? "是" : "否")}");
        sb.AppendLine($"  颜色来源：{settings.ColorSource}");
        sb.AppendLine($"  选中调色板索引：{settings.SelectedPlatteIndex}");
        sb.AppendLine($"  壁纸类名：{settings.WallpaperClassName}");
        sb.AppendLine($"  壁纸自动更新：{(settings.IsWallpaperAutoUpdateEnabled ? "是" : "否")}");
        sb.AppendLine($"  壁纸更新间隔(秒)：{settings.WallpaperAutoUpdateIntervalSeconds}");
        sb.AppendLine($"  回退模式启用：{(settings.IsFallbackModeEnabled ? "是" : "否")}");
        sb.AppendLine($"  目标亮度值：{settings.TargetLightValue}");
        sb.AppendLine($"  显示日期：{(settings.ShowDate ? "是" : "否")}");
        sb.AppendLine($"  上课时隐藏：{(settings.HideOnClass ? "是" : "否")}");
        sb.AppendLine($"  全屏时隐藏：{(settings.HideOnFullscreen ? "是" : "否")}");
        sb.AppendLine($"  最大窗口时隐藏：{(settings.HideOnMaxWindow ? "是" : "否")}");
        sb.AppendLine($"  提前通知秒数：{settings.ClassPrepareNotifySeconds}");
        sb.AppendLine($"  课前通知启用：{(settings.IsClassPrepareNotificationEnabled ? "是" : "否")}");
        sb.AppendLine($"  换课通知启用：{(settings.IsClassChangingNotificationEnabled ? "是" : "否")}");
        sb.AppendLine($"  下课通知启用：{(settings.IsClassOffNotificationEnabled ? "是" : "否")}");
        sb.AppendLine($"  通知启用：{(settings.IsNotificationEnabled ? "是" : "否")}");
        sb.AppendLine($"  启动画面启用：{(settings.IsSplashEnabled ? "是" : "否")}");
        sb.AppendLine($"  启动画面文字：{settings.SplashCustomText}");
        sb.AppendLine($"  精确时间启用：{(settings.IsExactTimeEnabled ? "是" : "否")}");
        sb.AppendLine($"  时间偏移秒数：{settings.TimeOffsetSeconds}");
        sb.AppendLine($"  动画等级：{settings.AnimationLevel}");
        sb.AppendLine($"  报告启用：{(settings.IsReportingEnabled ? "是" : "否")}");
        sb.AppendLine($"  Sentry 启用：{(settings.IsSentryEnabled ? "是" : "否")}");

        return sb.ToString();
    }

    /// <summary>
    /// 生成组件方案预览文本。展示行/组件树形结构（缩进展示子组件）。
    /// </summary>
    private static string BuildComponentSchemePreview(ComponentProfile scheme)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"行数：{scheme.Lines.Count}");
        sb.AppendLine(new string('─', 40));

        for (var i = 0; i < scheme.Lines.Count; i++)
        {
            var line = scheme.Lines[i];
            var mainLineText = line.IsMainLine ? "（主行）" : "";
            sb.AppendLine($"[行 {i + 1}]{mainLineText}");
            sb.AppendLine($"  可见：{(line.IsVisible ? "是" : "否")}");
            sb.AppendLine($"  提醒启用：{(line.IsNotificationEnabled ? "是" : "否")}");
            sb.AppendLine($"  组件数：{line.Children.Count}");
            for (var j = 0; j < line.Children.Count; j++)
            {
                BuildComponentNodePreview(sb, line.Children[j], 1, j + 1);
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// 递归生成组件节点预览文本。Children 为 [JsonIgnore] 计算属性，
    /// 从缓存加载时若组件 Settings 实现了 IComponentContainerSettings 则 Children 有值，否则为 null。
    /// </summary>
    private static void BuildComponentNodePreview(StringBuilder sb, ComponentSettings comp, int indent, int index)
    {
        var indentStr = new string(' ', indent * 2);
        var name = string.IsNullOrEmpty(comp.NameCache) ? comp.Id : comp.NameCache;
        sb.AppendLine($"{indentStr}{index}. {name}（ID: {comp.Id}）");
        var children = comp.Children;
        if (children != null)
        {
            for (var k = 0; k < children.Count; k++)
            {
                BuildComponentNodePreview(sb, children[k], indent + 1, k + 1);
            }
        }
    }

    /// <summary>
    /// 将 TimeSpan 格式化为 HH:mm 字符串。
    /// </summary>
    private static string FormatTimeSpan(TimeSpan time)
    {
        return $"{(int)time.TotalHours:D2}:{time.Minutes:D2}";
    }

    /// <summary>
    /// 将 Avalonia.Media.Color 格式化为 #RRGGBBAA 十六进制字符串。
    /// </summary>
    private static string ToHexColor(Color color)
    {
        return $"#{color.R:X2}{color.G:X2}{color.B:X2}{color.A:X2}";
    }

    #endregion

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
