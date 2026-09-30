using ClassPixelPro.Shared.Enums;

namespace ClassPixelPro.Shared.Models.Management;

/// <summary>
/// 集控清单。
/// </summary>
public class ManagementManifest
{
    /// <summary>
    /// 课表源
    /// </summary>
    public ReVersionString ClassPlanSource { get; set; } = new();

    /// <summary>
    /// 时间表源
    /// </summary>
    public ReVersionString TimeLayoutSource { get; set; } = new();

    /// <summary>
    /// 科目源
    /// </summary>
    public ReVersionString SubjectsSource { get; set; } = new();

    /// <summary>
    /// 默认设置源
    /// </summary>
    public ReVersionString DefaultSettingsSource { get; set; } = new();

    /// <summary>
    /// 策略源
    /// </summary>
    public ReVersionString PolicySource { get; set; } = new();
    
    /// <summary>
    /// 组件设置源
    /// </summary>
    public ReVersionString ComponentsSource { get; set; } = new();
    
    /// <summary>
    /// 凭据设置源
    /// </summary>
    public ReVersionString CredentialSource { get; set; } = new();

    /// <summary>
    /// 应用设置源
    /// </summary>
    public ReVersionString AppSettingsSource { get; set; } = new();

    /// <summary>
    /// 插件配置源
    /// </summary>
    public ReVersionString PluginsSource { get; set; } = new();

    /// <summary>
    /// 主题配置源
    /// </summary>
    public ReVersionString ThemeSource { get; set; } = new();

    /// <summary>
    /// 组件方案源
    /// </summary>
    public ReVersionString? ComponentSchemeSource { get; set; }

    /// <summary>
    /// 临时调课课表源。由集控分发，客户端拉取后应用到当前档案的临时层（Overlay）。
    /// </summary>
    public ReVersionString? TempClassPlanSource { get; set; }

    /// <summary>
    /// 帮助信息源。由集控分发，客户端在"关于"页面展示技术联系人等帮助信息。
    /// </summary>
    public ReVersionString? HelpInfoSource { get; set; }

    /// <summary>
    /// 集控服务器类型
    /// </summary>
    public ManagementServerKind ServerKind { get; set; } = ManagementServerKind.Serverless;

    /// <summary>
    /// 组织名称
    /// </summary>
    public string OrganizationName { get; set; } = "组织名称";

    /// <summary>
    /// 集控信息的核心版本
    /// </summary>
    public Version CoreVersion { get; set; } = new Version(0, 0, 0, 0);

    /// <summary>
    /// 设备配置模式（global/pool/no_classplan），由服务端根据设备设置决定。
    /// </summary>
    public string ConfigMode { get; set; } = "global";

    /// <summary>
    /// 配置来源标签（"全局"/"选配"/"非课表"），用于 UI 显示。
    /// </summary>
    public string ConfigSourceLabel { get; set; } = "全局";

    /// <summary>
    /// 选配池配置名称（仅 pool 模式有值）。
    /// </summary>
    public string? ConfigSourceName { get; set; }

    /// <summary>
    /// 选配池配置版本号（仅 pool 模式有值）。
    /// </summary>
    public int? ConfigSourceVersion { get; set; }

    /// <summary>
    /// 是否存在选配池配置名称（仅 pool 模式为 true）。用于 UI 绑定 IsVisible。
    /// </summary>
    public bool HasConfigSourceName => !string.IsNullOrEmpty(ConfigSourceName);
}