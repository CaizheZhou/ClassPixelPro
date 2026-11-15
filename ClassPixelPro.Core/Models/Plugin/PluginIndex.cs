using System.Collections.ObjectModel;
using ClassPixelPro.Shared;
using ClassPixelPro.Shared.ComponentModels;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ClassPixelPro.Core.Models.Plugin;

/// <summary>
/// 插件仓库索引
/// </summary>
public class PluginIndex : ObservableRecipient
{
    /// <summary>
    /// 插件仓库包含的插件列表
    /// </summary>
    public ObservableCollection<PluginIndexItem> Plugins { get; set; } = new();

    /// <summary>
    /// 插件下载镜像列表，键为镜像名，值为镜像根。镜像根在下载时会替换<see cref="PluginInfo"/>中下载链接的{root}模板。
    /// </summary>
    public ObservableDictionary<string, string> DownloadMirrors { get; set; } = new();

    /// <summary>
    /// 服务端推荐的默认镜像名。存量客户端本地保存的镜像选择可能指向已失效/不可达的镜像，
    /// 解析时优先采用服务端推荐，保证插件可下载。
    /// </summary>
    public string? RecommendedMirror { get; set; }
}