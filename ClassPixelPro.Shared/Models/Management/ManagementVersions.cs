namespace ClassPixelPro.Shared.Models.Management;

/// <summary>
/// 集控本地信息版本
/// </summary>
public class ManagementVersions
{
    /// <summary>
    /// 课表版本
    /// </summary>
    public int ClassPlanVersion { get; set; } = 0;

    /// <summary>
    /// 时间表版本
    /// </summary>
    public int TimeLayoutVersion { get; set; } = 0;

    /// <summary>
    /// 科目版本
    /// </summary>
    public int SubjectsVersion { get; set; } = 0;

    /// <summary>
    /// 默认设置版本
    /// </summary>
    public int DefaultSettingsVersion { get; set; } = 0;

    /// <summary>
    /// 策略版本
    /// </summary>
    public int PolicyVersion { get; set; } = 0;
    
    /// <summary>
    /// 凭据版本
    /// </summary>
    public int CredentialVersion { get; set; } = 0;
    
    /// <summary>
    /// 组件版本
    /// </summary>
    public int ComponentsVersion { get; set; } = 0;

    /// <summary>
    /// 应用设置版本
    /// </summary>
    public int AppSettingsVersion { get; set; } = 0;

    /// <summary>
    /// 插件配置版本
    /// </summary>
    public int PluginsVersion { get; set; } = 0;

    /// <summary>
    /// 主题配置版本
    /// </summary>
    public int ThemeVersion { get; set; } = 0;

    /// <summary>
    /// 组件方案版本
    /// </summary>
    public int ComponentSchemeVersion { get; set; } = 0;

    /// <summary>
    /// 临时调课课表版本
    /// </summary>
    public int TempClassPlanVersion { get; set; } = 0;

    /// <summary>
    /// 帮助信息版本
    /// </summary>
    public int HelpInfoVersion { get; set; } = 0;
}