namespace ClassPixelPro.Shared.Models.Management;

/// <summary>
/// 集控下发的帮助信息。客户端在"关于"页面展示技术联系人等联系方式。
/// </summary>
public class ManagementHelpInfo
{
    /// <summary>
    /// 技术联系人姓名
    /// </summary>
    public string ContactName { get; set; } = string.Empty;

    /// <summary>
    /// 联系方式类型（QQ / 微信 / 电话 / 邮箱 / Telegram / 其他）
    /// </summary>
    public string ContactMethod { get; set; } = string.Empty;

    /// <summary>
    /// 联系号（QQ 号 / 微信号 / 电话号码 / 邮箱地址等）
    /// </summary>
    public string ContactId { get; set; } = string.Empty;

    /// <summary>
    /// 其他说明（如在线时间、备注等）
    /// </summary>
    public string Notes { get; set; } = string.Empty;

    /// <summary>
    /// 是否包含有效内容（任一字段非空即视为有效）
    /// </summary>
    public bool HasContent =>
        !string.IsNullOrWhiteSpace(ContactName) ||
        !string.IsNullOrWhiteSpace(ContactMethod) ||
        !string.IsNullOrWhiteSpace(ContactId) ||
        !string.IsNullOrWhiteSpace(Notes);

    /// <summary>
    /// 是否有技术联系人
    /// </summary>
    public bool HasContactName => !string.IsNullOrWhiteSpace(ContactName);

    /// <summary>
    /// 是否有联系方式类型
    /// </summary>
    public bool HasContactMethod => !string.IsNullOrWhiteSpace(ContactMethod) || !string.IsNullOrWhiteSpace(ContactId);

    /// <summary>
    /// 是否有其他说明
    /// </summary>
    public bool HasNotes => !string.IsNullOrWhiteSpace(Notes);
}
