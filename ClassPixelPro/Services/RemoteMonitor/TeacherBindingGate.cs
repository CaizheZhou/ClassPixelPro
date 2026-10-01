using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using ClassPixelPro.Services.Management;
using ClassPixelPro.Shared.Enums;
using Microsoft.Extensions.Logging;

namespace ClassPixelPro.Services.RemoteMonitor;

/// <summary>
/// 班主任绑定状态查询门禁。
/// <para>
/// 远程监控会把摄像头/屏幕暴露到公网，开启的前提是设备已加入集控且已绑定班主任
/// （有明确的归属与责任人）。本类封装"查询绑定状态"这一步，供设置页与自启动
/// 路径共用；查询失败一律按未绑定处理（安全优先）。
/// </para>
/// </summary>
public class TeacherBindingGate
{
    private readonly ManagementService _managementService;
    private readonly ILogger<TeacherBindingGate> _logger;
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(10) };

    private static readonly JsonSerializerOptions JsonOptions = new(System.Text.Json.JsonSerializerDefaults.Web);

    public TeacherBindingGate(ManagementService managementService, ILogger<TeacherBindingGate> logger)
    {
        _managementService = managementService;
        _logger = logger;
    }

    /// <summary>
    /// 查询本设备是否已绑定班主任。
    /// 未加入集控时直接返回 false；查询失败（网络等）也返回 false。
    /// </summary>
    public async Task<bool> IsTeacherBoundAsync()
    {
        if (!_managementService.IsManagementEnabled) return false;

        try
        {
            var baseUrl = GetManagementBaseUrl();
            if (string.IsNullOrEmpty(baseUrl)) return false;

            var cuid = _managementService.Persist.ClientUniqueId.ToString();
            var statusUri = new Uri($"{baseUrl}/api/teacher/bindings/status?cuid={Uri.EscapeDataString(cuid)}");
            var responseJson = await _httpClient.GetStringAsync(statusUri);
            var status = JsonSerializer.Deserialize<TeacherBindingStatusResponse>(responseJson, JsonOptions);
            return status?.Bound == true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "查询班主任绑定状态失败（按未绑定处理）");
            return false;
        }
    }

    /// <summary>
    /// 从集控设置解析 API base URL（与 ManagementSettingsPage 的同名逻辑一致）。
    /// </summary>
    private string? GetManagementBaseUrl()
    {
        var settings = _managementService.Settings;
        if (settings.ManagementServerKind == ManagementServerKind.ManagementServer)
        {
            return string.IsNullOrWhiteSpace(settings.ManagementServer) ? null : settings.ManagementServer.TrimEnd('/');
        }
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
    }

    private class TeacherBindingStatusResponse
    {
        public bool Bound { get; set; }
        public string? TeacherName { get; set; }
    }
}
