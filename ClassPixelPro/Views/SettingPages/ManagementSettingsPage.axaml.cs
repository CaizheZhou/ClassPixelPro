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
    /// 服务端返回的选配池配置列表。仅在 <see cref="LoadPoolConfigsAndSyncUI"/> 成功后赋值。
    /// </summary>
    private List<PoolConfigItem>? _poolConfigs;

    /// <summary>
    /// 是否正在加载选配池列表。用于防止重复加载。
    /// </summary>
    private bool _isLoadingPoolConfigs;

    /// <summary>
    /// 是否正在程序化更新配置模式 UI。用于防止 SelectionChanged 事件递归触发。
    /// </summary>
    private bool _isUpdatingConfigMode;

    /// <summary>
    /// 配置模式下拉框的选中索引。绑定到 <see cref="ConfigModeComboBox"/>.SelectedIndex。
    /// getter 从 <see cref="IManagementService.Manifest"/>.ConfigMode 映射，setter 由 SelectionChanged 事件处理。
    /// </summary>
    public int ConfigModeSelectedIndex
    {
        get
        {
            var mode = ManagementService.Manifest?.ConfigMode ?? "global";
            return mode switch
            {
                "pool" => 1,
                "no_classplan" => 2,
                _ => 0
            };
        }
        set
        {
            // 由 SelectionChanged 事件处理，这里不需要做任何事
        }
    }

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

        // 加载选配池列表和当前配置模式
        _ = LoadPoolConfigsAndSyncUI();
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
    /// 加载服务端选配池配置列表，并同步当前配置模式到 UI。
    /// 在 <see cref="ManagementSettingsPage_OnLoaded"/> 中调用。
    /// </summary>
    private async Task LoadPoolConfigsAndSyncUI()
    {
        if (!ManagementService.IsManagementEnabled) return;

        var baseUrl = ManagementService.GetManagementBaseUrl();
        if (string.IsNullOrEmpty(baseUrl)) return;

        var cuid = ManagementService.Persist.ClientUniqueId;

        try
        {
            _isLoadingPoolConfigs = true;
            var url = $"{baseUrl}/api/v1/client/{cuid}/config-pool";
            _poolConfigs = await WebRequestHelper.Default.GetJson<List<PoolConfigItem>>(new Uri(url));

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                // 更新选配池下拉框
                if (PoolConfigComboBox != null)
                {
                    PoolConfigComboBox.Items.Clear();
                    if (_poolConfigs != null)
                    {
                        foreach (var p in _poolConfigs)
                        {
                            PoolConfigComboBox.Items.Add($"{p.Name} (v{p.Version})");
                        }
                    }
                }

                // 同步当前模式到 UI
                SyncConfigModeUI();
            });
        }
        catch (Exception ex)
        {
            // 静默失败，不影响页面加载
            System.Diagnostics.Debug.WriteLine($"加载选配池列表失败: {ex.Message}");
        }
        finally
        {
            _isLoadingPoolConfigs = false;
        }
    }

    /// <summary>
    /// 将 <see cref="IManagementService.Manifest"/>.ConfigMode 同步到配置模式下拉框与选配池面板的可见性。
    /// 使用 <see cref="_isUpdatingConfigMode"/> 防止 SelectionChanged 事件递归触发。
    /// </summary>
    private void SyncConfigModeUI()
    {
        if (_isUpdatingConfigMode) return;
        _isUpdatingConfigMode = true;

        try
        {
            var mode = ManagementService.Manifest?.ConfigMode ?? "global";

            // 同步模式下拉框
            if (ConfigModeComboBox != null)
            {
                ConfigModeComboBox.SelectedIndex = mode switch
                {
                    "pool" => 1,
                    "no_classplan" => 2,
                    _ => 0
                };
            }

            // 显示/隐藏选配池面板
            if (PoolConfigPanel != null)
            {
                PoolConfigPanel.IsVisible = mode == "pool";
            }

            // 如果是 pool 模式，选中当前选配池配置
            if (mode == "pool" && PoolConfigComboBox != null && _poolConfigs != null)
            {
                var currentPoolName = ManagementService.Manifest?.ConfigSourceName;
                // 找到当前选中的配置（通过名称匹配）
                for (int i = 0; i < _poolConfigs.Count; i++)
                {
                    if (_poolConfigs[i].Name == currentPoolName)
                    {
                        PoolConfigComboBox.SelectedIndex = i;
                        break;
                    }
                }
            }
        }
        finally
        {
            _isUpdatingConfigMode = false;
        }
    }

    /// <summary>
    /// 配置模式下拉框选中项变化事件处理。
    /// 根据选中的模式显示/隐藏选配池面板，并调用 API 同步到服务端。
    /// </summary>
    private async void ConfigModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingConfigMode) return;
        if (ConfigModeComboBox == null) return;

        var selectedIndex = ConfigModeComboBox.SelectedIndex;
        var mode = selectedIndex switch
        {
            1 => "pool",
            2 => "no_classplan",
            _ => "global"
        };

        // 显示/隐藏选配池面板
        if (PoolConfigPanel != null)
        {
            PoolConfigPanel.IsVisible = mode == "pool";
        }

        // 如果选了 pool 模式但没选配置，不立即提交
        if (mode == "pool" && (PoolConfigComboBox?.SelectedIndex ?? -1) < 0)
        {
            if (PoolConfigInfo != null)
            {
                PoolConfigInfo.IsVisible = true;
                PoolConfigInfo.Text = "请在下方选择一个选配配置";
            }
            return;
        }

        if (PoolConfigInfo != null)
        {
            PoolConfigInfo.IsVisible = false;
        }

        // 获取选配池配置 ID（pool 模式）
        string? poolConfigId = null;
        if (mode == "pool" && _poolConfigs != null && PoolConfigComboBox != null)
        {
            var poolIndex = PoolConfigComboBox.SelectedIndex;
            if (poolIndex >= 0 && poolIndex < _poolConfigs.Count)
            {
                poolConfigId = _poolConfigs[poolIndex].Id;
            }
        }

        // 调用 API 同步到服务端
        await SetConfigModeAsync(mode, poolConfigId);
    }

    /// <summary>
    /// 选配池配置下拉框选中项变化事件处理。
    /// 显示选中的配置信息，并调用 API 同步到服务端。
    /// </summary>
    private async void PoolConfigComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingConfigMode) return;
        if (PoolConfigComboBox == null || _poolConfigs == null) return;

        var poolIndex = PoolConfigComboBox.SelectedIndex;
        if (poolIndex < 0 || poolIndex >= _poolConfigs.Count) return;

        var poolConfigId = _poolConfigs[poolIndex].Id;

        // 显示选中的配置信息
        if (PoolConfigInfo != null)
        {
            PoolConfigInfo.IsVisible = true;
            var p = _poolConfigs[poolIndex];
            PoolConfigInfo.Text = $"已选择：{p.Name} (v{p.Version}){(string.IsNullOrEmpty(p.Description) ? "" : $" - {p.Description}")}";
        }

        // 调用 API 同步到服务端
        await SetConfigModeAsync("pool", poolConfigId);
    }

    /// <summary>
    /// 调用 PUT /api/v1/client/:cuid/config-mode 接口将配置模式同步到服务端。
    /// 成功后更新本地 <see cref="IManagementService.Manifest"/> 的 ConfigMode 与 ConfigSourceLabel。
    /// </summary>
    private async Task SetConfigModeAsync(string mode, string? poolConfigId)
    {
        var baseUrl = ManagementService.GetManagementBaseUrl();
        if (string.IsNullOrEmpty(baseUrl)) return;

        var cuid = ManagementService.Persist.ClientUniqueId;

        try
        {
            var url = $"{baseUrl}/api/v1/client/{cuid}/config-mode";
            var body = new { configMode = mode, poolConfigId = poolConfigId };
            var json = JsonSerializer.Serialize(body, JsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await HttpClient.PutAsync(url, content);
            if (response.IsSuccessStatusCode)
            {
                // 同步成功，触发 manifest 刷新
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    // 更新本地 Manifest 的 ConfigMode
                    if (ManagementService.Manifest != null)
                    {
                        ManagementService.Manifest.ConfigMode = mode;
                        ManagementService.Manifest.ConfigSourceLabel = mode switch
                        {
                            "pool" => "选配",
                            "no_classplan" => "非课表",
                            _ => "全局"
                        };
                    }

                    // 显示"配置模式已更改"提示和重新拉取按钮
                    // 切换模式后本地配置仍是旧的，需重新拉取才能完全生效
                    if (ConfigModeChangedPanel != null)
                    {
                        ConfigModeChangedPanel.IsVisible = true;
                    }
                });
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"设置配置模式失败: {ex.Message}");
        }
    }

    /// <summary>
    /// "立即重新拉取配置"按钮点击事件。
    /// 调用 <see cref="Services.Management.ManagementService.ReloadManagementAsync"/> 重新拉取所有集控配置，
    /// 完成后刷新选配池列表与 UI，并隐藏切换模式提示面板。
    /// </summary>
    private async void ReloadConfigButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (ReloadConfigButton != null)
        {
            ReloadConfigButton.IsEnabled = false;
            ReloadConfigButton.Content = "🔄 正在重新拉取配置...";
        }

        try
        {
            // IManagementService 接口未暴露 Reload 方法，需通过具体类型调用。
            // 项目中已有此类型转换模式（见 ButtonPullAll_OnClick 内的 concreteManagementService 用法）。
            if (ManagementService is Services.Management.ManagementService concreteManagementService)
            {
                await concreteManagementService.ReloadManagementAsync();
            }

            // 重新加载选配池列表并同步 UI
            await LoadPoolConfigsAndSyncUI();

            // 拉取完成，隐藏提示面板
            if (ConfigModeChangedPanel != null)
            {
                ConfigModeChangedPanel.IsVisible = false;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"重新拉取配置失败: {ex.Message}");
        }
        finally
        {
            if (ReloadConfigButton != null)
            {
                ReloadConfigButton.IsEnabled = true;
                ReloadConfigButton.Content = "🔄 立即重新拉取配置";
            }
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
    /// 预览当前拉取到本地的课表缓存（ClassPlans.json）。
    /// </summary>
    private async void ButtonPreviewClassPlan_OnClick(object sender, RoutedEventArgs e)
    {
        var path = Services.ProfileService.ManagementClassPlanPath;
        if (!File.Exists(path))
        {
            await CommonTaskDialogs.ShowDialog("课表预览", "尚未拉取，请先拉取配置。");
            return;
        }

        try
        {
            var profile = ConfigureFileHelper.LoadConfig<Profile>(path);
            var text = BuildClassPlanPreview(profile);
            await ShowPreviewDialog("课表预览", text);
        }
        catch (Exception ex)
        {
            await CommonTaskDialogs.ShowDialog("课表预览失败", $"读取课表缓存失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 预览当前拉取到本地的时间表缓存（TimeLayouts.json）。
    /// </summary>
    private async void ButtonPreviewTimeLayout_OnClick(object sender, RoutedEventArgs e)
    {
        var path = Services.ProfileService.ManagementTimeLayoutPath;
        if (!File.Exists(path))
        {
            await CommonTaskDialogs.ShowDialog("时间表预览", "尚未拉取，请先拉取配置。");
            return;
        }

        try
        {
            var profile = ConfigureFileHelper.LoadConfig<Profile>(path);
            var text = BuildTimeLayoutPreview(profile);
            await ShowPreviewDialog("时间表预览", text);
        }
        catch (Exception ex)
        {
            await CommonTaskDialogs.ShowDialog("时间表预览失败", $"读取时间表缓存失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 预览当前拉取到本地的科目缓存（Subjects.json）。
    /// </summary>
    private async void ButtonPreviewSubjects_OnClick(object sender, RoutedEventArgs e)
    {
        var path = Services.ProfileService.ManagementSubjectsPath;
        if (!File.Exists(path))
        {
            await CommonTaskDialogs.ShowDialog("科目预览", "尚未拉取，请先拉取配置。");
            return;
        }

        try
        {
            var profile = ConfigureFileHelper.LoadConfig<Profile>(path);
            var text = BuildSubjectsPreview(profile);
            await ShowPreviewDialog("科目预览", text);
        }
        catch (Exception ex)
        {
            await CommonTaskDialogs.ShowDialog("科目预览失败", $"读取科目缓存失败：{ex.Message}");
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

    /// <summary>
    /// 服务端选配池配置项。对应 GET /api/v1/client/:cuid/config-pool 返回的数组元素。
    /// </summary>
    public class PoolConfigItem
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public int Version { get; set; }
    }
}
