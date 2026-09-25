using System;
using System.Collections.Generic;
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
using ClassPixelPro.Core.Abstractions.Services.Management;
using ClassPixelPro.Core.Attributes;
using ClassPixelPro.Core.Enums.SettingsWindow;
using ClassPixelPro.Shared.Enums;
using ClassPixelPro.Shared.Models.Management;
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
