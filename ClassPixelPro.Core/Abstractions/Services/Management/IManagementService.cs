using ClassPixelPro.Shared.Abstraction.Services;
using ClassPixelPro.Shared.Enums;
using ClassPixelPro.Shared.Models.Management;

namespace ClassPixelPro.Core.Abstractions.Services.Management;

/// <summary>
/// 集控服务。
/// </summary>
public interface IManagementService
{
    /// <summary>
    /// 是否启用集控
    /// </summary>
    bool IsManagementEnabled { get; set; }
    
    /// <summary>
    /// 集控配置版本
    /// </summary>
    ManagementVersions Versions { get; set; }
    
    /// <summary>
    /// 集控清单
    /// </summary>
    ManagementManifest Manifest { get; set; }
    
    /// <summary>
    /// 集控服务器配置
    /// </summary>
    ManagementSettings Settings { get; }
    
    /// <summary>
    /// 集控策略
    /// </summary>
    ManagementPolicy Policy { get; set; }

    /// <summary>
    /// 集控授权设置
    /// </summary>
    ManagementCredentialConfig CredentialConfig { get; internal set; }

    /// <summary>
    /// 集控下发的帮助信息
    /// </summary>
    ManagementHelpInfo HelpInfo { get; }

    /// <summary>
    /// 集控持久配置
    /// </summary>
    ManagementClientPersistConfig Persist { get; }
    
    /// <summary>
    /// 集控服务器连接
    /// </summary>
    IManagementServerConnection? Connection { get; }
    internal Task<bool> SetupManagement();
    internal void SaveSettings();
    
    /// <summary>
    /// 加入集控服务器。
    /// </summary>
    /// <param name="settings">集控服务器配置</param>
    Task JoinManagementAsync(ManagementSettings settings);

    /// <summary>
    /// 通过 5 位短 ID 解析组织，返回其长 UUID。用于「通过组织 ID 加入集控」。
    /// </summary>
    Task<string> ResolveOrganizationIdAsync(string shortId, string? baseUrl = null);
    
    /// <summary>
    /// 退出集控服务器。
    /// </summary>
    Task ExitManagementAsync();

    /// <summary>
    /// 根据授权等级进行授权。
    /// </summary>
    /// <param name="level">需要的授权等级</param>
    /// <returns>是否通过授权</returns>
    Task<bool> AuthorizeByLevel(AuthorizeLevel level);

    /// <summary>
    /// 上报当前档案中的课表/时间表/科目/课表组配置给集控后端。
    /// <para>
    /// 应在 Profile 加载完毕后调用，以确保上报的是客户端实际使用的课表数据，
    /// 而不是空的默认 Profile。该方法仅在首次调用时执行上报，避免浪费带宽。
    /// </para>
    /// </summary>
    Task ReportCurrentProfileAsync();

    /// <summary>
    /// 集控调课与策略拉取等操作的内存日志列表（最近 <see cref="ManagementLogEntry"/> 条）。
    /// 用于在设置页面向用户展示集控对客户端课表的操作历史。
    /// </summary>
    IReadOnlyList<ManagementLogEntry> ManagementLogs { get; }

    /// <summary>
    /// 从 ManifestUrlTemplate 解析出集控 API base URL。
    /// 用于客户端直接调用服务端 REST API（如选配池、配置模式等）。
    /// </summary>
    string? GetManagementBaseUrl();

    /// <summary>
    /// 从 ManifestUrlTemplate 解析出组织 ID（UUID）。未加入集控或模板非法时返回 null。
    /// </summary>
    string? GetOrganizationId();

    /// <summary>
    /// 应用集控下发的临时调课课表到当前档案的临时层（Overlay）。
    /// <paramref name="downloaded"/> 为 null 时使用本地缓存文件重放（供档案加载完成后恢复启动期间被覆盖的临时课表）。
    /// </summary>
    Task<bool> ApplyTempClassPlanAsync(object? downloaded = null);

    /// <summary>
    /// 列出组织下可选的班级课表，并返回本设备当前选中的班级（null 表示用全局课表）。
    /// </summary>
    Task<(List<ClassPlanOption> List, Guid? Current)> GetClassPlanListAsync();

    /// <summary>
    /// 上报本设备选择的班级课表（classPlanId 为 null 表示改用组织全局课表）。
    /// </summary>
    Task SelectClassPlanAsync(Guid? classPlanId);

    /// <summary>
    /// 上报本次运行会话开始。客户端启动完成后调用，服务端据此记录「何时启动」，
    /// 并为后续的正常/异常退出判定提供基线。
    /// </summary>
    Task ReportSessionStartAsync();

    /// <summary>
    /// 上报本次运行会话结束。
    /// </summary>
    /// <param name="kind">normal（正常退出）/ crash（崩溃）/ abnormal（异常）</param>
    /// <param name="reason">人类可读的退出原因</param>
    /// <param name="detail">可选详情（如异常堆栈），最长 8000 字符</param>
    Task ReportSessionEndAsync(string kind, string reason, string? detail = null);
}

/// <summary>
/// 班级课表选项（对应服务端 class_plans 表）。
/// </summary>
public class ClassPlanOption
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public int Version { get; set; }
}

/// <summary>
/// 集控操作日志条目。记录集控后端对客户端课表/策略等数据的拉取与应用操作。
/// </summary>
public record ManagementLogEntry
{
    /// <summary>
    /// 操作发生的时间（本地时间）。
    /// </summary>
    public DateTime Timestamp { get; init; }

    /// <summary>
    /// 操作类型，例如 "拉取临时课表"、"应用临时课表"、"清除临时课表"、"拉取策略"。
    /// </summary>
    public string Action { get; init; } = "";

    /// <summary>
    /// 操作详情，例如版本号、源课表 ID 等。
    /// </summary>
    public string Detail { get; init; } = "";

    /// <summary>
    /// 操作是否成功。
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// 失败时的错误信息；成功时为 null。
    /// </summary>
    public string? ErrorMessage { get; init; }
}
