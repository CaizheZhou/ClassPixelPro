using System.Text.Json.Serialization;

namespace ClassPixelPro.Core.Models.Plugin;

/// <summary>
/// 代表一个插件的索引信息。
/// </summary>
public class PluginIndexItem : PluginInfo
{
    /// <summary>
    /// 插件最新版本下载链接
    /// </summary>
    public string DownloadUrl { get; set; } = "";

    /// <summary>
    /// 插件最新版本下载MD5
    /// </summary>
    public string DownloadMd5 { get; set; } = "";

    /// <summary>
    /// 插件市场标记：此插件已知与当前宿主架构不兼容（由索引下发）。
    /// </summary>
    public bool Incompatible { get; set; }

    /// <summary>
    /// 不兼容原因，用于市场列表展示。
    /// </summary>
    public string IncompatibleReason { get; set; } = "";
}