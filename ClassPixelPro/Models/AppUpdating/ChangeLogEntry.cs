using System;

namespace ClassPixelPro.Models.AppUpdating;

/// <summary>
/// 代表从服务器 /api/v1/public/distributions/changelogs 端点获取的单个版本更新日志条目。
/// 该端点返回直接 JSON 数组（非 Phainon Result 包装），字段使用 camelCase。
/// </summary>
public class ChangeLogEntry
{
    /// <summary>
    /// 具体版本号，如 2.0.0.0
    /// </summary>
    public string Version { get; set; } = "";

    /// <summary>
    /// 友好版本号，如 2.0-Khaslana Release 1
    /// </summary>
    public string FriendlyVersion { get; set; } = "";

    /// <summary>
    /// 友好短版本号，如 2.0-Khaslana R1
    /// </summary>
    public string FriendlyVersionShort { get; set; } = "";

    /// <summary>
    /// 更新日志（Markdown 格式）
    /// </summary>
    public string ChangeLog { get; set; } = "";

    /// <summary>
    /// 该版本的创建时间
    /// </summary>
    public DateTime CreatedAt { get; set; }
}