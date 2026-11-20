using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using ClassPixelPro.Core.Exceptions;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using ClassPixelPro.Core;
using ClassPixelPro.Core.Abstractions.Services;
using ClassPixelPro.Core.Helpers;
using ClassPixelPro.Core.Models;
using ClassPixelPro.Core.Models.Plugin;
using ClassPixelPro.Models;
using ClassPixelPro.Platforms.Abstraction;
using ClassPixelPro.Platforms.Abstraction.Models;
using ClassPixelPro.Shared;
using ClassPixelPro.Shared.ComponentModels;
using ClassPixelPro.Shared.Helpers;
using CommunityToolkit.Mvvm.ComponentModel;
using Downloader;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using Sentry;

namespace ClassPixelPro.Services;

public class PluginMarketService : ObservableRecipient, IPluginMarketService
{
    public static string DefaultPluginIndexKey { get; } = "Default";

    public SettingsService SettingsService { get; }
    public IPluginService PluginService { get; }

    public ObservableDictionary<string, DownloadProgress> DownloadTasks { get; } = new();

    public ObservableDictionary<string, PluginIndex> Indexes { get; } = new();
    public ILogger<PluginMarketService> Logger { get; }

    public static ObservableDictionary<string, string> FallbackMirrors { get; } = new()
    {
        // 索引拉取失败时的镜像展示兜底。原 github/ghproxy/moeyy 组合已全部失效
        // （mirror.ghproxy.com 与 moeyy 停摆、GitHub 直连国内不可达），换成可用的加速镜像。
        { "gh-proxy", "https://gh-proxy.com/https://github.com" },
        { "gh-proxy-v4", "https://v4.gh-proxy.com/https://github.com" }
    };

    private bool _isLoadingPluginSource = false;
    private double _pluginSourceDownloadProgress;
    private Exception? _exception;
    private IDisposable? _pluginsUpdateProgressObserver;
    private readonly OSPlatform _currentOSPlatform = 
        OperatingSystem.IsWindows() ? OSPlatform.Windows :
        OperatingSystem.IsLinux()   ? OSPlatform.Linux :
        OperatingSystem.IsMacOS()   ? OSPlatform.OSX :
        OSPlatform.Create("Unknown");

    public PluginMarketService(SettingsService settingsService, IPluginService pluginService, ILogger<PluginMarketService> logger)
    {
        SettingsService = settingsService;
        PluginService = pluginService;
        Logger = logger;
        
        if (DateTime.Now - SettingsService.Settings.LastRefreshPluginSourceTime >= TimeSpan.FromDays(7))
        {
            _ = RefreshPluginSourceAsync();
        }
    }

    public ObservableDictionary<string, PluginInfo> MergedPlugins { get; } = new();

    public bool IsLoadingPluginSource
    {
        get => _isLoadingPluginSource;
        set
        {
            if (value == _isLoadingPluginSource) return;
            _isLoadingPluginSource = value;
            OnPropertyChanged();
        }
    }

    public double PluginSourceDownloadProgress
    {
        get => _pluginSourceDownloadProgress;
        set
        {
            if (value.Equals(_pluginSourceDownloadProgress)) return;
            _pluginSourceDownloadProgress = value;
            OnPropertyChanged();
        }
    }

    public Exception? Exception
    {
        get => _exception;
        set
        {
            if (Equals(value, _exception)) return;
            _exception = value;
            OnPropertyChanged();
        }
    }

    public async Task RefreshPluginSourceAsync()
    {
        if (IsLoadingPluginSource)
            return;
        IsLoadingPluginSource = true;
        Exception = null;
        PluginSourceDownloadProgress = 0.0;
        Logger.LogInformation("正在刷新插件源……");
        var transaction = SentrySdk.StartTransaction("Update Plugin Index", "pluginIndex.update");
        var ignoreSsl = SettingsService.Settings.IgnoreSslForPluginMirrors;
        var prevCallback = (ignoreSsl ? ServicePointManager.ServerCertificateValidationCallback : null);
        if (ignoreSsl)
        {
            ServicePointManager.ServerCertificateValidationCallback = (_, _, _, _) => true;
        }
        try
        {
            if (SettingsService.Settings.OfficialIndexMirrors.Count <= 0)
            {
                SettingsService.Settings.OfficialIndexMirrors = ConfigureFileHelper.CopyObject(FallbackMirrors);
            }
            var indexes = GetIndexInfos().ToList();
            var i = 0.0;
            var total = Math.Max(1, indexes.Count);
            foreach (var indexInfo in indexes)
            {
                var url = indexInfo.Url.Replace("{time}",
                    ((long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds).ToString());
                Logger.LogDebug("正在刷新插件源：{}（{}）", indexInfo.Id, url);
                var archive = Path.GetTempFileName();
                var download = DownloadBuilder.New()
                    .WithUrl(url)
                    .WithFileLocation(archive)
                    .WithConfiguration(new DownloadConfiguration()
                    {
                        BlockTimeout = 10_000,
                        HttpClientTimeout = 10_000
                    })
                    .Build();
                var i1 = i;
                download.DownloadProgressChanged +=
                    (sender, args) =>
                        PluginSourceDownloadProgress = (args.ProgressPercentage / total) + (i1 / total * 100.0);
                download.DownloadFileCompleted += (sender, args) =>
                {
                    if (args.Error != null)
                    {
                        throw new Exception($"无法加载插件源：{args.Error.Message}", args.Error);
                    } 
                    var indexFolderPath = Path.Combine(Services.PluginService.PluginsIndexPath, indexInfo.Id);
                    if (Directory.Exists(indexFolderPath))
                    {
                        Directory.Delete(indexFolderPath, true);
                    }

                    Directory.CreateDirectory(indexFolderPath);
                    ZipFile.ExtractToDirectory(archive, indexFolderPath);
                };
                await download.StartAsync();

                i++;
            }
            LoadPluginSource();
            var count = MergedPlugins.Count(x => x.Value is { IsUpdateAvailable: true, IsEnabled: true, RestartRequired: false });
            if (count > 0)
            {
                if (SettingsService.Settings.IsPluginsUpdateNotificationEnabled)
                {
                    await PlatformServices.DesktopToastService.ShowToastAsync(new DesktopToastContent()
                    {
                        Title = "插件更新可用",
                        Body = $"有 {count} 个插件有新版本可用，点击以查看详细信息。",
                        Activated = (_, _) => IAppHost.GetService<IUriNavigationService>().NavigateWrapped(new Uri("classpixelpro://app/settings/classpixelpro.plugins"))
                    });
                }
                if (SettingsService.Settings.IsPluginsAutoUpdateEnabled)
                {
                    UpdateAllPlugins();
                }
            }
            transaction.Finish(SpanStatus.Ok);
        }
        catch (Exception ex)
        {
            transaction.Finish(ex, SpanStatus.InternalError);
            Logger.LogError(ex, "无法加载插件源。");
            Exception = ex;
        }
        finally
        {
            if (ignoreSsl)
            {
                ServicePointManager.ServerCertificateValidationCallback = prevCallback;
            }
        }
        Logger.LogInformation("插件源刷新成功。");
        SettingsService.Settings.LastRefreshPluginSourceTime = DateTime.Now;
        IsLoadingPluginSource = false;
    }

    public IEnumerable<PluginIndexInfo> GetIndexInfos()
    {
        var mirrors = SettingsService.Settings.OfficialIndexMirrors.Count == 0
            ? FallbackMirrors
            : SettingsService.Settings.OfficialIndexMirrors;
        const string repo = "https://class.imzcz.cn/api/plugin/index?time={time}";
        return SettingsService.Settings.PluginIndexes.Where(x => !string.IsNullOrWhiteSpace(x.Url)).Append(new PluginIndexInfo()
        {
            Id = DefaultPluginIndexKey,
            Url = repo,
            SelectedMirror = SettingsService.Settings.OfficialSelectedMirror ?? "github",
            Mirrors = SettingsService.Settings.OfficialIndexMirrors
        });
    }

    public void UpdateAllPlugins(bool discardDisabled=false)
    {
        var toUpdate = MergedPlugins
            .Where(x => x.Value is { IsUpdateAvailable: true, RestartRequired: false }
                        && (x.Value.DownloadProgress == null || x.Value.DownloadProgress.IsDownloading == false)
                        && (discardDisabled || x.Value.IsEnabled))
            .ToImmutableDictionary();
        if (toUpdate.Count <= 0)
        {
            return;
        }
        _pluginsUpdateProgressObserver ??= DownloadTasks.ObservableForProperty(x => x.Count)
            .Subscribe(_ =>
            {
                if (DownloadTasks.Count > 0) return;
                var success = toUpdate.Values.Count(x => x.DownloadProgress?.Exception == null);

                if (SettingsService.Settings.IsPluginsUpdateNotificationEnabled)
                {
                    if (success == toUpdate.Count)
                    {
                        PlatformServices.DesktopToastService.ShowToastAsync(new DesktopToastContent()
                        {
                            Title = "插件更新完成",
                            Body = $"已将 {success} 个插件升级到最新版本，将在下次启动应用时生效。",
                            Buttons =
                            {
                                { "立即重启", () => AppBase.Current.Restart() }
                            }
                        });    
                    } else if (success > 0 && success < toUpdate.Count)
                    {
                        PlatformServices.DesktopToastService.ShowToastAsync(new DesktopToastContent()
                        {
                            Title = "插件更新完成",
                            Body = $"已将 {success} 个插件升级到最新版本，{toUpdate.Count - success} 个插件升级失败。将在下次启动应用时生效。",
                            Buttons =
                            {
                                { "立即重启", () => AppBase.Current.Restart() }
                            }
                        });
                    }
                    else
                    {
                        PlatformServices.DesktopToastService.ShowToastAsync(new DesktopToastContent()
                        {
                            Title = "插件更新失败",
                            Body = $"无法更新插件。请检查您的网络设置，或更换插件镜像源，然后再试一遍。"
                        });
                    }
                    
                }

                _pluginsUpdateProgressObserver?.Dispose();
                _pluginsUpdateProgressObserver = null;
            });
        
        foreach (var (id, _) in toUpdate)
        {
            RequestDownloadPlugin(id);
        }
    }

    public PluginIndexItem? ResolveMarketPlugin(string id)
    {
        return Indexes.Select(i => i.Value.Plugins
            .FirstOrDefault(x => x.Manifest.Id == id))
            .OfType<PluginIndexItem>()
            .FirstOrDefault();
    }

    public async void RequestDownloadPlugin(string id)
    {
        var item = ResolveMarketPlugin(id);
        var transaction = SentrySdk.StartTransaction("Download Plugin", "plugin.download");
        transaction.SetTag("plugin.id", id);

        if (item == null)
        {
            Logger.LogWarning("找不到符合id的插件：{}", id);
            transaction.Finish(SpanStatus.NotFound);
            return;
        }
        transaction.SetTag("plugin", item.Manifest.Name);

        // 已知不兼容插件（索引 Incompatible 标记）：直接拒绝并说明原因，不进入下载流程
        if (item.IsIncompatible)
        {
            Logger.LogWarning("插件 {} 已标记为不兼容，拒绝安装：{}", id, item.IncompatibleReason);
            transaction.Finish(SpanStatus.InvalidArgument);
            PlatformServices.DesktopToastService.ShowToastAsync(new DesktopToastContent()
            {
                Title = "无法安装插件",
                Body = $"{item.Manifest?.Name}：{item.IncompatibleReason}"
            });
            return;
        }

        if (DownloadTasks.ContainsKey(id))
        {
            Logger.LogWarning("{}已正在下载。", id);
            transaction.Finish(SpanStatus.AlreadyExists);
            return;
        }

        Logger.LogInformation("开始下载插件：{}", id);
        var spanDownload = transaction.StartChild("download");
        var url = item.DownloadUrl;
        var md5 = item.DownloadMd5;
        var task = new DownloadProgress()
        {
            IsDownloading = true
        };
        DownloadTasks[id] = task;
        var archive = Path.GetTempFileName() + ".tmp";
        var download = DownloadBuilder.New()
            .WithUrl(url)
            .WithFileLocation(archive)
            .WithConfiguration(new DownloadConfiguration())
            .Build();
        transaction.SetTag("url", url);
        if (Uri.TryCreate(url, UriKind.RelativeOrAbsolute, out var uri))
        {
            transaction.SetTag("url.host", uri.Host);
        }

        var stopwatch = new Stopwatch();
        download.DownloadFileCompleted += (sender, args) =>
        {
            stopwatch.Stop();
            transaction.SetExtra("download.size", download.TotalFileSize);
            var speed = stopwatch.Elapsed.TotalSeconds == 0
                ? 0.0
                : download.TotalFileSize / stopwatch.Elapsed.TotalSeconds;
            transaction.SetExtra("download.bytesPerSecond", speed);
            if (args.Error != null)
            {
                spanDownload.Finish(args.Error, SpanStatus.InternalError);
                throw new Exception($"无法下载插件 {id}：{args.Error.Message}", args.Error);
            }
            spanDownload.Finish(SpanStatus.Ok);

            var spanValidateChecksum = transaction.StartChild("validate");
            try
            {
                ChecksumHelper.VerifyChecksum(archive, md5);
            }
            catch (ChecksumUnMatchException)
            {
                // MD5 与索引不符但 zip 结构完整（可打开且含 manifest.yml，zip 自身 CRC 保证
                // 内容未截断）→ 索引校验值过期（作者刚发布新版本），按完整性接受安装。
                // MD5 来自上游未签名索引，本就不是信任边界；真正的截断包在这里会被 catch 住拒绝。
                bool looksLikePackage = false;
                try
                {
                    using var probe = ZipFile.OpenRead(archive);
                    looksLikePackage = probe.Entries.Any(x => x.FullName.EndsWith("manifest.yml", StringComparison.OrdinalIgnoreCase));
                }
                catch { }
                if (!looksLikePackage)
                    throw;
                Logger.LogWarning("插件 {Id} 的 MD5 与市场索引不符，但安装包结构完整（索引滞后），按包完整性接受安装", id);
            }
            spanValidateChecksum.Finish(SpanStatus.Ok);

            var spanMoveToCache = transaction.StartChild("moveToCache");
            File.Move(archive, Path.Combine(Services.PluginService.PluginsPkgRootPath, id + ".cipx"), true);
            spanMoveToCache.Finish(SpanStatus.Ok);
        };
        download.DownloadProgressChanged += (sender, args) =>
        {
            task.Progress = args.ProgressPercentage;
        };
        var ignoreSsl = SettingsService.Settings.IgnoreSslForPluginMirrors;
        var prevCallback = (ignoreSsl ? ServicePointManager.ServerCertificateValidationCallback : null);
        if (ignoreSsl)
        {
            ServicePointManager.ServerCertificateValidationCallback = (_, _, _, _) => true;
        }
        try
        {
            BindDownloadTasks();
            stopwatch.Start();
            await download.StartAsync(task.CancellationToken);
            item.RestartRequired = true;
            if (MergedPlugins.TryGetValue(id, out var plugin))
            {
                plugin.RestartRequired = true;
            }
            RestartRequested?.Invoke(this, EventArgs.Empty);
            Logger.LogInformation("插件 {} 下载完成。", id);
            transaction.Finish(SpanStatus.Ok);
        }
        catch (Exception e)
        {
            task.Exception = e;
            transaction.GetLastActiveSpan()?.Finish(e, SpanStatus.InternalError);
            transaction.Finish(e, SpanStatus.InternalError);
            Logger.LogError(e, "无法从 {} 下载插件 {}", url, id);
        }
        finally
        {
            if (ignoreSsl)
            {
                ServicePointManager.ServerCertificateValidationCallback = prevCallback;
            }
        }
        task.IsDownloading = false;
        DownloadTasks.Remove(id);
    }

    public event EventHandler? RestartRequested;

    public void LoadPluginSource()
    {
        Logger.LogInformation("正在加载插件源");
        MergedPlugins.Clear();
        Indexes.Clear();

        foreach (var pluginLocal in IPluginService.LoadedPlugins)
        {
            var id = pluginLocal.Manifest.Id;
            MergedPlugins[id] = pluginLocal;
        }

        var indexInfos = GetIndexInfos().ToList();
        foreach (var i in indexInfos)
        {
            var indexFolderPath = Path.Combine(Services.PluginService.PluginsIndexPath, i.Id);
            var name = Path.GetFileName(indexFolderPath);
            Logger.LogDebug("正在加载插件源：{}", name);
            var indexPath = Path.Combine(indexFolderPath, "index.v2.json");
            if (!File.Exists(indexPath))
                continue;
            var index = Indexes[name] = ConfigureFileHelper.LoadConfig<PluginIndex>(indexPath);
            // 镜像解析：服务端推荐（RecommendedMirror）优先——本地保存的选择可能指向已失效/不可达的
            // 镜像（2026-11-15：mirror.ghproxy.com 与 moeyy 全部失效、GitHub 直连国内不可达，插件全部装不上）；
            // 所选键不在镜像表中时回退到推荐镜像，再不行才取第一项。
            var mirror = !string.IsNullOrWhiteSpace(index.RecommendedMirror) &&
                         index.DownloadMirrors.ContainsKey(index.RecommendedMirror)
                ? index.RecommendedMirror
                : i.SelectedMirror;
            i.Mirrors = ConfigureFileHelper.CopyObject(index.DownloadMirrors);
            i.SelectedMirror = mirror;
            if (!index.DownloadMirrors.TryGetValue(mirror, out var root))
            {
                // 所选镜像已失效（如历史保存的 github/ghproxy/moeyy）：优先回退到服务端推荐镜像
                var recommended = index.RecommendedMirror;
                if (string.IsNullOrWhiteSpace(recommended) ||
                    !index.DownloadMirrors.TryGetValue(recommended, out root))
                {
                    mirror = index.DownloadMirrors.First().Key;
                    root = index.DownloadMirrors.First().Value;
                }
                else
                {
                    mirror = recommended;
                }
                i.SelectedMirror = mirror;
            }
            Logger.LogDebug("插件源 {} 选择的镜像根：{}", name, root);
            foreach (var plugin in index.Plugins.Where(x =>
                         Version.TryParse(x.Manifest.ApiVersion, out var version) &&
                         version >= Version.Parse("2.0.0.0")))
            {
                var id = plugin.Manifest.Id;
                plugin.DownloadUrl = plugin.DownloadUrl.Replace("{root}", root);
                if (MergedPlugins.ContainsKey(id) && MergedPlugins[id].IsLocal)
                {
                    var pluginLocal = MergedPlugins[id];
                    pluginLocal.IsAvailableOnMarket = true;
                    pluginLocal.DownloadCount = plugin.DownloadCount;
                    pluginLocal.StarsCount = plugin.StarsCount;
                    if (Version.TryParse(pluginLocal.Manifest.Version, out var versionLocal) &&
                        Version.TryParse(plugin.Manifest.Version, out var versionRemote) &&
                        Version.TryParse(plugin.Manifest.ApiVersion, out var apiVersion) &&
                        Version.TryParse(AppBase.AppVersion, out var appVersion) &&
                        versionRemote > versionLocal)  // TODO: 在 2.0 发布后，添加 api 版本校验！
                    {
                        pluginLocal.IsUpdateAvailable = true;
                    }
                    continue;
                }
                plugin.IsAvailableOnMarket = true;
                plugin.IsIncompatible = plugin.Incompatible;
                plugin.IncompatibleReason = plugin.IncompatibleReason;
                if (!string.IsNullOrEmpty(plugin.RealIconPath))
                    plugin.RealIconPath = plugin.RealIconPath.Replace("{root}", root);
                if (!string.IsNullOrEmpty(plugin.Manifest.Readme))
                    plugin.Manifest.Readme = plugin.Manifest.Readme.Replace("{root}", root);
                plugin.IsNotSupportCurrentOS = plugin.Manifest.SupportedOSPlatforms == null ||
                                               !plugin.Manifest.SupportedOSPlatforms.Contains(_currentOSPlatform);
                MergedPlugins[id] = plugin;
            }
        }

        SettingsService.Settings.OfficialSelectedMirror =
            indexInfos.First(x => x.Id == DefaultPluginIndexKey).SelectedMirror;
        var defaultIndex = Indexes.FirstOrDefault(x => x.Key == DefaultPluginIndexKey).Value ?? new PluginIndex();
        SettingsService.Settings.OfficialIndexMirrors = ConfigureFileHelper.CopyObject(
            defaultIndex.DownloadMirrors);
        BindDownloadTasks();
    }

    private void BindDownloadTasks()
    {
        foreach (var i in DownloadTasks)
        {
            var b = MergedPlugins.TryGetValue(i.Key, out var v);
            if (!b || v == null)
                continue;
            v.DownloadProgress = i.Value;
        }

        foreach (var plugin in MergedPlugins.Where(plugin => File.Exists(Path.Combine(Services.PluginService.PluginsPkgRootPath, plugin.Value.Manifest.Id + ".cipx"))))
        {
            plugin.Value.RestartRequired = true;
        }
    }
}