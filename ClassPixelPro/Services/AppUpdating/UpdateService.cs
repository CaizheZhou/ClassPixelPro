using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using ClassPixelPro.Core;
using ClassPixelPro.Core.Abstractions.Services;
using ClassPixelPro.Core.Helpers;
using ClassPixelPro.Core.Helpers.Native;
using ClassPixelPro.Core.Models;
using ClassPixelPro.Core.Models.Updating;
using ClassPixelPro.Enums.AppUpdating;
using ClassPixelPro.Helpers;
using ClassPixelPro.Models;
using ClassPixelPro.Models.AppUpdating;
using ClassPixelPro.Platforms.Abstraction;
using ClassPixelPro.Shared;
using ClassPixelPro.Shared.Enums;
using ClassPixelPro.Shared.Helpers;
using ClassPixelPro.Views;
using Downloader;
using DynamicData;
using ICSharpCode.SharpZipLib.GZip;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PhainonDistributionCenter.Shared.Helpers;
using PhainonDistributionCenter.Shared.Models.Api.Responses.Distribution;
using PhainonDistributionCenter.Shared.Models.Client;
using PhainonDistributionCenter.Shared.Models.FileMap;
using Sentry;
using Tmds.DBus.Protocol;
using DownloadProgressChangedEventArgs = Downloader.DownloadProgressChangedEventArgs;
using File = System.IO.File;

namespace ClassPixelPro.Services.AppUpdating;

public class UpdateService : IHostedService, INotifyPropertyChanged
{
    private UpdateWorkingStatus _currentWorkingStatus = UpdateWorkingStatus.Idle;
    public IDownload? Downloader;
    private bool _isCanceled = false;
    private Exception? _networkErrorException;
    private TimeSpan _downloadEtcSeconds = TimeSpan.Zero;
    private DistributionInfoClient _distributionInfo;
    private string _currentWorkingMessage = "";

    private const string PhainonRootUrl = "https://class.imzcz.cn";

    // FileMap JSON 使用 camelCase 属性名，需要匹配的 JsonSerializerOptions
    private static readonly JsonSerializerOptions FileMapJsonOptions = new(JsonSerializerDefaults.Web);

    internal static string UpdateCachePath { get; } = Path.Combine(CommonDirectories.AppCacheFolderPath, "Update");

    public static string UpdateTempPath => Path.Combine(CommonDirectories.AppTempFolderPath, "Updating");

    private static string UpdateDistributionInfoPath { get; } = Path.Combine(UpdateCachePath, "DistributionInfo.json");
    private static string UpdateDistributionMetadataPath { get; } = Path.Combine(UpdateCachePath, "DistributionMetadata.json");
    
    public static readonly string[] AllowedPackageTypes = ["folder", "folderClassic", "installer"];

    private CancellationTokenSource? _downloadCancellationTokenSource;
    private TaskCompletionSource? _downloadCompletion;
    private int _downloadedCount = 0;
    private int _downloadingCountTotal = 0;
    private bool _isDownloadingProgressIndeterminate = false;
    private Exception? _deployErrorException;

    public ObservableCollection<DownloadTaskInfo> DownloadTasks { get; } = [];

    public DistributionInfoClient DistributionInfo
    {
        get => _distributionInfo;
        set => SetField(ref _distributionInfo, value);
    }

    public DistributionMetadata DistributionMetadata
    {
        get;
        set;
    }

    public UpdateWorkingStatus CurrentWorkingStatus
    {
        get => _currentWorkingStatus;
        set => SetField(ref _currentWorkingStatus, value);
    }

    private SettingsService SettingsService
    {
        get;
    }

    public string CurrentWorkingMessage
    {
        get => _currentWorkingMessage;
        set => SetField(ref _currentWorkingMessage, value);
    }

    public bool IsDownloadingProgressIndeterminate
    {
        get => _isDownloadingProgressIndeterminate;
        set => SetField(ref _isDownloadingProgressIndeterminate, value);
    }

    private Settings Settings => SettingsService.Settings;

    private ITaskBarIconService TaskBarIconService
    {
        get;
    }

    private ISplashService SplashService { get; }

    private ILogger<UpdateService> Logger { get; }

    private string MetadataPublisherPublicKey { get; }

    public event EventHandler? UpdateInfoUpdated;
    
    private WebRequestHelper RequestHelper { get; }

    public int DownloadingCountTotal
    {
        get => _downloadingCountTotal;
        set
        {
            if (value == _downloadingCountTotal) return;
            _downloadingCountTotal = value;
            OnPropertyChanged();
        }
    }

    public int DownloadedCount
    {
        get => _downloadedCount;
        set
        {
            if (value == _downloadedCount) return;
            _downloadedCount = value;
            OnPropertyChanged();
        }
    }

    public UpdateService(SettingsService settingsService, ITaskBarIconService taskBarIconService, IHostApplicationLifetime lifetime,
        ISplashService splashService, ILogger<UpdateService> logger)
    {
        SettingsService = settingsService;
        TaskBarIconService = taskBarIconService;
        SplashService = splashService;
        Logger = logger;

        var keyStream = AssetLoader.Open(new Uri("avares://ClassPixelPro/Assets/TrustedPublicKeys/ClassPixelPro.MetadataPublisher.asc", UriKind.RelativeOrAbsolute));
        MetadataPublisherPublicKey = new StreamReader(keyStream).ReadToEnd();

        RequestHelper = new WebRequestHelper(GetPhainonBaseUri(), true);
        _distributionInfo = ConfigureFileHelper.LoadConfig<DistributionInfoClient>(UpdateDistributionInfoPath);
        DistributionMetadata = ConfigureFileHelper.LoadConfig<DistributionMetadata>(UpdateDistributionMetadataPath);
    }

    /// <summary>
    /// 计算 Phainon 服务器的 base URI，支持开发构建时通过 DebugPhainonRootUrlOverride 覆盖。
    /// </summary>
    private Uri GetPhainonBaseUri()
    {
        return AppBase.Current.IsDevelopmentBuild
               && !string.IsNullOrWhiteSpace(Settings.DebugPhainonRootUrlOverride)
               && Uri.TryCreate(Settings.DebugPhainonRootUrlOverride, UriKind.Absolute, out var u1)
            ? u1
            : new Uri(PhainonRootUrl);
    }

    /// <summary>
    /// 从服务器获取所有版本的更新日志。
    /// 该端点返回直接 JSON 数组（非 Phainon Result 包装），因此需要使用 non-phainon 模式的请求 helper。
    /// </summary>
    public async Task<List<ChangeLogEntry>> GetChangeLogsAsync()
    {
        // changelogs 端点返回直接 JSON 数组，不是 Result<T> 包装，需要 phainon: false
        var nonPhainonHelper = new WebRequestHelper(GetPhainonBaseUri(), false);
        return await nonPhainonHelper.GetJson<List<ChangeLogEntry>>(
            new Uri("api/v1/public/distributions/changelogs", UriKind.Relative));
    }

    public bool IsCanceled
    {
        get => _isCanceled;
        set => SetField(ref _isCanceled, value);
    }

    public Exception? NetworkErrorException
    {
        get => _networkErrorException;
        set => SetField(ref _networkErrorException, value);
    }

    public Exception? DeployErrorException
    {
        get => _deployErrorException;
        set => SetField(ref _deployErrorException, value);
    }

    public async Task<bool> AppStartup()
    {
        _ = AppStartupBackground();
        return false;
    }

    private async Task AppStartupBackground()
    {
        // 修复：旧版本目录堆积。CleanupPrevDeployments 原先只在「更新后首次启动且
        // LastUpdateStatus == UpdateDeployed」这唯一一次机会被调用；而那一刻旧进程
        // 往往尚未完全退出、文件仍被占用，删除失败后异常被捕获仅记日志，
        // 状态随即转为 UpToDate，此后永远不会重试——表现为 app-<旧版本> 目录
        // 每次 更新 后都留下 一个，永不清理。
        // 该方法只删除带 .destroy 标记的目录（该标记由 MarkCurrentAsPrevious 在
        // 清理更早的 .prev 时写入，不可能是当前正在运行的版本），因此每次启动调用一次是安全的：
        // 本次因文件占用删不掉的目录，下次启动会自动补删。
        //
        // 注意顺序：必须先清扫孤儿 cloudflared 进程再清理目录——孤儿进程持有旧版本
        // app-* 目录的句柄，不先杀掉它们，下面的目录删除必然失败。
        Services.RemoteMonitor.CloudflaredManager.SweepOrphanProcesses();
        CleanupPrevDeployments();

        if (Settings.LastUpdateStatus == UpdateStatus.UpdateDeployed)
        {
            await PlatformServices.DesktopToastService.ShowToastAsync("更新成功",
                $"应用已更新到 {AppBase.AppVersion}，点击以查看详细信息。", UpdateNotificationClickedCallback);
            Settings.LastUpdateStatus = UpdateStatus.UpToDate;
        }
        
        if (Settings.UpdateMode < 1)
        {
            return;
        }
        
        await CheckUpdateAsync();

        if (Settings.UpdateMode < 2)
        {
            return;
        }

        if (Settings.LastUpdateStatus == UpdateStatus.UpdateAvailable)
        {
            await DownloadUpdateAsync();
        }

        if (Settings.UpdateMode < 3)
        {
            return;
        }

        if (Settings.LastUpdateStatus == UpdateStatus.UpdateDownloaded)
        {
            await ExtractUpdateAsync();
        }
    }

    public static void RemoveUpdateTemporary(string target)
    {
        if (File.Exists(target))
        {
            NativeWindowHelper.WaitForFile(target);
            File.Delete(target);
        }
        try
        {
            Directory.Delete(UpdateTempPath, true);
        }
        catch (Exception e)
        {
            // ignored
            Console.WriteLine(e);
        }
    }


    public async Task CheckUpdateAsync(bool isForce=false, bool isCancel=false)
    {
        if (!AllowedPackageTypes.Contains(AppBase.Current.PackagingType))
        {
            return;
        }
        var transaction = SentrySdk.StartTransaction("Get Update Info", "appUpdating.getMetadata");
        if (!isCancel)
        {
            NetworkErrorException = null;
            DeployErrorException = null;
        }
        try
        {
            var spanGetIndex = transaction.StartChild("getIndex");
            var subChannel = GetCurrentSubChannel();
            CurrentWorkingStatus = UpdateWorkingStatus.CheckingUpdates;
            DistributionMetadata = await RequestHelper.SaveJson<DistributionMetadata>(
                new Uri("api/v1/public/distributions/metadata", UriKind.Relative), UpdateDistributionMetadataPath);
            if (!DistributionMetadata.Channels.ContainsKey(Settings.SelectedUpdateChannelV3))
            {
                Settings.SelectedUpdateChannelV3 = DistributionMetadata.DefaultChannelId;
            }
            var latest = await RequestHelper.GetJson<LatestDistributionInfoMinResponse>(
                new Uri($"api/v1/public/distributions/latest/{Settings.SelectedUpdateChannelV3}?appVersion={AppBase.AppVersion}", UriKind.Relative));
            spanGetIndex.Finish(SpanStatus.Ok);
            if (!IsNewerVersion(isForce, isCancel, Version.Parse(latest.Version)))
            {
                Settings.LastUpdateStatus = UpdateStatus.UpToDate;
                transaction.Finish(SpanStatus.Ok);
                return;
            }

            var spanGetDetail = transaction.StartChild("getDetail");
            DistributionInfo = await RequestHelper.SaveJson<DistributionInfoClient>(
                new Uri($"api/v1/public/distributions/{latest.DistributionId}/{subChannel}", UriKind.Relative),
                UpdateDistributionInfoPath);
            Settings.LastUpdateStatus = UpdateStatus.UpdateAvailable;
            await PlatformServices.DesktopToastService.ShowToastAsync("发现新版本",
                $"{Assembly.GetExecutingAssembly().GetName().Version} -> {latest.Version}" +Environment.NewLine+
                "点击以查看详细信息。", UpdateNotificationClickedCallback);

            Settings.LastUpdateStatus = UpdateStatus.UpdateAvailable;
            spanGetDetail.Finish(SpanStatus.Ok);
            transaction.Finish(SpanStatus.Ok);
        }
        catch (Exception ex)
        {
            Settings.LastUpdateStatus = UpdateStatus.UpToDate;
            NetworkErrorException = ex;
            transaction.GetLastActiveSpan()?.Finish(ex, SpanStatus.InternalError);
            transaction.Finish(ex, SpanStatus.InternalError);
            Logger.LogError(ex, "检查应用更新失败。");
        }
        finally
        {
            Settings.LastCheckUpdateTime = DateTime.Now;
            UpdateInfoUpdated?.Invoke(this, EventArgs.Empty);
            CurrentWorkingStatus = UpdateWorkingStatus.Idle;
        }
    }

    private string GetCurrentSubChannel()
    {
        return AppBase.Current.IsDevelopmentBuild && !string.IsNullOrWhiteSpace(Settings.DebugSubChannelOverride)
            ? Settings.DebugSubChannelOverride
            : AppBase.Current.AppSubChannel;
    }

    private void UpdateNotificationClickedCallback()
    {
        IAppHost.GetService<IUriNavigationService>().NavigateWrapped(new Uri("classpixelpro://app/settings/update"));
    }

    private bool IsNewerVersion(bool isForce, bool isCancel, Version verCode)
    {
        return (verCode > Assembly.GetExecutingAssembly().GetName().Version &&
                (Settings.LastUpdateStatus != UpdateStatus.UpdateDownloaded || isCancel)) // 正常更新
               || isForce;
    }

    public async Task DownloadUpdateAsync()
    {
        IsDownloadingProgressIndeterminate = true;
        if (!AllowedPackageTypes.Contains(AppBase.Current.PackagingType))
        {
            return;
        }
        if (Design.IsDesignMode)
        {
            return;
        }
        _downloadCompletion = new TaskCompletionSource();
        var transaction = SentrySdk.StartTransaction("Download Update", "appUpdating.download");
        var spanDeletePreviousFile = transaction.StartChild("deletePreviousFile");
        try
        {
            if (Directory.Exists(UpdateTempPath))
            {
                Directory.Delete(UpdateTempPath, true);
            }
            spanDeletePreviousFile.Finish(SpanStatus.Ok);
        }
        catch (Exception ex)
        {
            spanDeletePreviousFile.Finish(ex, SpanStatus.InternalError);
            Logger.LogError(ex, "移除下载临时文件失败。");
        }

        var spanDownload = transaction.StartChild("download");
        if (_downloadCancellationTokenSource != null)
        {
            await _downloadCancellationTokenSource.CancelAsync();
        }

        try
        {
            var publicKey =
                AppBase.Current.IsDevelopmentBuild && !string.IsNullOrWhiteSpace(Settings.DebugPublicKeyOverride)
                    ? Settings.DebugPublicKeyOverride
                    : MetadataPublisherPublicKey;
            var valid = DetachedSignatureProcessor.VerifyDetachedSignature(DistributionInfo.FileMapJson,
                Encoding.UTF8.GetBytes(DistributionInfo.FileMapSignature), publicKey);
            if (!valid)
            {
                throw new InvalidOperationException("文件图签名校验不通过");
            }

            var fileMap = JsonSerializer.Deserialize<FileMap>(DistributionInfo.FileMapJson, FileMapJsonOptions);
            if (fileMap == null)
            {
                throw new InvalidOperationException("文件图解析失败");
            }

            // 整包下载模式：当 files 为空且 ArchiveUrl 不为空时
            var hasFiles = fileMap.Components.Values.Any(c => c.Files.Count > 0);
            if (!hasFiles && !string.IsNullOrEmpty(fileMap.ArchiveUrl))
            {
                Logger.LogInformation("检测到整包下载模式，ArchiveUrl: {Url}", fileMap.ArchiveUrl);
                CurrentWorkingStatus = UpdateWorkingStatus.DownloadingUpdates;
                var ctsArchive = _downloadCancellationTokenSource = new CancellationTokenSource();
                var cancellationTokenArchive = ctsArchive.Token;

                var archivePath = Path.Combine(UpdateTempPath, "archive.zip");
                if (!Directory.Exists(UpdateTempPath))
                {
                    Directory.CreateDirectory(UpdateTempPath);
                }

                // 下载整包 zip
                using var httpClient = new HttpClient();
                httpClient.Timeout = TimeSpan.FromMinutes(30);
                using var response = await httpClient.GetAsync(fileMap.ArchiveUrl, HttpCompletionOption.ResponseHeadersRead, cancellationTokenArchive);
                response.EnsureSuccessStatusCode();

                // 进度以「字节」为单位直接喂给 DownloadingCountTotal/DownloadedCount
                // （UI 的 ProgressBar 就是绑这两个值）。经 gh-proxy 之类的加速代理时
                // 响应可能是 chunked 而没有 Content-Length，此时退化为不确定进度条，
                // 而不是像以前那样 Maximum=1、Value 只在开头 0 和结尾 1 之间跳变。
                var contentLength = response.Content.Headers.ContentLength;
                var hasTotal = contentLength is > 0;
                DownloadingCountTotal = hasTotal ? (int)Math.Min(contentLength!.Value, int.MaxValue) : 1;
                DownloadedCount = 0;
                IsDownloadingProgressIndeterminate = !hasTotal;
                Logger.LogInformation("开始下载整包更新：{Url}，总大小 {Total} 字节", fileMap.ArchiveUrl, contentLength ?? 0);

                // 整包下载也往「查看下载状态」抽屉里放一个任务条目。此前该抽屉只显示
                // 逐文件下载的任务列表，整包模式下是空的，用户完全看不到下载进度。
                DownloadTasks.Clear();
                var archiveTask = new DownloadTaskInfo()
                {
                    FileName = fileMap.ArchiveUrl.Split('/').LastOrDefault() ?? "archive.zip",
                    Key = "archive",
                    State = DownloadState.Downloading
                };
                DownloadTasks.Add(archiveTask);

                await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationTokenArchive);
                await using var fileStream = File.Create(archivePath);

                var buffer = new byte[81920];
                int bytesRead;
                long totalDownloaded = 0;
                var progressStopwatch = Stopwatch.StartNew();
                var downloadStopwatch = Stopwatch.StartNew();
                while ((bytesRead = await contentStream.ReadAsync(buffer, cancellationTokenArchive)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationTokenArchive);
                    totalDownloaded += bytesRead;
                    if (!hasTotal || progressStopwatch.ElapsedMilliseconds < 250)
                    {
                        continue;
                    }
                    progressStopwatch.Restart();
                    var downloaded = (int)Math.Min(totalDownloaded, DownloadingCountTotal);
                    var speed = downloadStopwatch.Elapsed.TotalSeconds > 1
                        ? totalDownloaded / downloadStopwatch.Elapsed.TotalSeconds
                        : 0;
                    var eta = TimeSpanHelper.FromSecondsSafe(hasTotal && speed > 0
                        ? (long)((contentLength!.Value - totalDownloaded) / speed)
                        : 0);
                    Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        DownloadedCount = downloaded;
                        archiveTask.DownloadedSize = totalDownloaded;
                        archiveTask.FileSize = hasTotal ? contentLength!.Value : 0;
                        archiveTask.DownloadSpeed = speed;
                        archiveTask.TimeToComplete = eta;
                    });
                }

                DownloadedCount = DownloadingCountTotal;
                archiveTask.State = DownloadState.Completed;
                archiveTask.DownloadedSize = totalDownloaded;
                Logger.LogInformation("整包下载完成，大小: {Size} 字节", totalDownloaded);

                // 保存 FileMap 和签名
                await File.WriteAllTextAsync(Path.Combine(UpdateTempPath, "FileMap.json"), DistributionInfo.FileMapJson, cancellationTokenArchive);
                await File.WriteAllTextAsync(Path.Combine(UpdateTempPath, "FileMap.json.sig"), DistributionInfo.FileMapSignature, cancellationTokenArchive);

                var archiveDeploymentLock = new DeploymentLock()
                {
                    SubChannel = GetCurrentSubChannel(),
                    FileMapSha512 = SHA512.HashData(Encoding.UTF8.GetBytes(DistributionInfo.FileMapJson))
                };
                await File.WriteAllTextAsync(Path.Combine(UpdateTempPath, "Deployment.lock"),
                    JsonSerializer.Serialize(archiveDeploymentLock), cancellationTokenArchive);

                Settings.LastUpdateStatus = UpdateStatus.UpdateDownloaded;
                return;
            }

            CurrentWorkingStatus = UpdateWorkingStatus.DownloadingUpdates;
            var cts = _downloadCancellationTokenSource = new CancellationTokenSource();
            var cancellationToken = cts.Token;

            var options = new DownloadConfiguration()
            {
                ChunkCount = 4,
                ParallelCount = 4,
                ParallelDownload = true,
                BlockTimeout = 60_000,
                HttpClientTimeout = 60_000
            };
            // key 是 hash（HEX）
            Dictionary<string, (string Name, FileMapFile FileInfo)> filesHashed = [];
            var deploymentLock = new DeploymentLock()
            {
                SubChannel = GetCurrentSubChannel(),
                FileMapSha512 = SHA512.HashData(Encoding.UTF8.GetBytes(DistributionInfo.FileMapJson))
            };

            Logger.LogTrace("正在计算要下载的文件");
            var prevFileMapPath = Path.Combine(Environment.CurrentDirectory, "files.json");
            var prevFileMapUserVarsPath = Path.Combine(Environment.CurrentDirectory, "files.uvars.json");
            var prevFileMap = new FileMap();
            var prevUserVars = new Dictionary<string, string>();
            var prevFileMapUserVarsLoadSuccess = false;
            if (File.Exists(prevFileMapPath))
            {
                try
                {
                    prevFileMap = ConfigureFileHelper.LoadConfigUnWrapped<FileMap>(prevFileMapPath, false);
                }
                catch (Exception e)
                {
                    Logger.LogWarning(e, "无法加载当前版本的文件图 {}", prevFileMapPath);
                }
            }
            if (File.Exists(prevFileMapUserVarsPath))
            {
                try
                {
                    prevUserVars = ConfigureFileHelper.LoadConfigUnWrapped<Dictionary<string, string>>(prevFileMapUserVarsPath, false);
                    prevFileMapUserVarsLoadSuccess = true;
                }
                catch (Exception e)
                {
                    Logger.LogWarning(e, "无法加载当前版本的部署用户变量 {}", prevFileMapUserVarsPath);
                }
            }
            var vars = ConfigureFileHelper.CopyObject(prevFileMap.Variables);
            if (prevFileMapUserVarsLoadSuccess)
            {
                foreach (var (k, v) in prevUserVars)
                {
                    vars[k] = v;
                }
            }

            var root = CommonDirectories.AppPackageRoot;
            foreach (var (id, component) in fileMap.Components)
            {
                var prevComp = prevFileMap.Components.GetValueOrDefault(id);
                var existedFiles = new List<string>();
                deploymentLock.ExistedFiles[id] = existedFiles;

                string? prevCompRoot = null;
                var prevExpandedRoot = VariableStringHelpers.ExpandString(component.Root, vars);
                if (prevFileMapUserVarsLoadSuccess)
                {
                    prevCompRoot = Path.GetFullPath(Path.Combine(root, prevExpandedRoot));
                    if (!PathHelpers.IsSafePath(root, prevCompRoot))
                    {
                        throw new InvalidOperationException("文件图组件根目录无效");
                    }
                    deploymentLock.ComponentRoots[id] = prevCompRoot;
                }

                foreach (var (path, fileInfo) in component.Files)
                {
                    var prevPath = Path.Combine(DeployBase(path, prevCompRoot ?? root, prevExpandedRoot), path);
                    if (prevFileMapUserVarsLoadSuccess && prevCompRoot != null &&
                        Path.Exists(prevPath) &&
                        component.AllowDiffUpdate &&
                        prevComp?.Files.GetValueOrDefault(path)?.FileSha512.SequenceEqual(fileInfo.FileSha512) == true)
                    {
                        await using var file = File.OpenRead(prevPath);
                        var hash = await SHA512.HashDataAsync(file, cancellationToken);
                        if (hash.SequenceEqual(fileInfo.FileSha512))
                        {
                            existedFiles.Add(path);
                            Logger.LogTrace("SKIP {}/{}", id, path);
                            continue;
                        }
                    }

                    Logger.LogTrace("ADD {}/{}", id, path);
                    filesHashed.TryAdd(Convert.ToHexString(fileInfo.FileSha512), (Path.GetFileName(path), fileInfo));
                }
            }

            var dlRoot = UpdateTempPath;
            if (!Directory.Exists(dlRoot))
            {
                Directory.CreateDirectory(dlRoot);
            }

            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = 8,
                CancellationToken = cancellationToken
            };
            DownloadedCount = 0;
            DownloadingCountTotal = filesHashed.Count;
            IsDownloadingProgressIndeterminate = false;
            Logger.LogInformation("开始下载更新，要下载 {} 个文件", filesHashed.Count);
            var downloadTasksMap = new Dictionary<string, DownloadTaskInfo>();
            DownloadTasks.Clear();
            DownloadTasks.AddRange(filesHashed.Select(x => new DownloadTaskInfo()
            {
                FileName = x.Value.Name,
                Key = x.Key
            }));
            foreach (var info in DownloadTasks)
            {
                downloadTasksMap[info.Key] = info;
            }

            await Parallel.ForEachAsync(filesHashed, parallelOptions, async (pair, token) =>
            {
                var (hashHex, (fileName, file)) = pair;
                var updateStopwatch = Stopwatch.StartNew();
                Logger.LogInformation("开始下载 {}({})", fileName, file.ArchiveDownloadUrl);
                var info = downloadTasksMap.GetValueOrDefault(hashHex) ?? DownloadTaskInfo.CreateEmpty();
                info.State = DownloadState.Downloading;
                await using var downloader = DownloadBuilder.New()
                    .WithConfiguration(options)
                    .WithUrl(file.ArchiveDownloadUrl)
                    .WithFileLocation(Path.Combine(dlRoot, hashHex[..2], hashHex))
                    .Build();
                var taskCompletionSource = new TaskCompletionSource();
                downloader.DownloadFileCompleted += (_, args) =>
                {
                    if (args.Error != null)
                    {
                        taskCompletionSource.SetException(args.Error);
                        return;
                    }

                    Dispatcher.UIThread.InvokeAsync(() => { DownloadedCount++; });
                    taskCompletionSource.SetResult();
                };
                downloader.DownloadProgressChanged += (_, e) =>
                {
                    if (updateStopwatch.ElapsedMilliseconds < 250)
                        return;
                    updateStopwatch.Restart();
                    var totalSize = e.TotalBytesToReceive;
                    var downloadedSize = e.ReceivedBytesSize;
                    var downloadSpeed = e.BytesPerSecondSpeed;

                    var eta = TimeSpanHelper.FromSecondsSafe(downloadSpeed == 0
                        ? 0
                        : (long)((totalSize - downloadedSize) / downloadSpeed));
                    Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        info.FileSize = totalSize;
                        info.DownloadedSize = downloadedSize;
                        info.DownloadSpeed = downloadSpeed;
                        info.TimeToComplete = eta;
                    });
                };
                token.Register(() => { taskCompletionSource.SetCanceled(token); });
                await downloader.StartAsync(token);
                await taskCompletionSource.Task;
                info.State = DownloadState.Completed;
                // DownloadTasks.Remove(info);
                Logger.LogInformation("下载完成 {}({})", fileName, file.ArchiveDownloadUrl);
            });

            await File.WriteAllTextAsync(Path.Combine(UpdateTempPath, "FileMap.json"), DistributionInfo.FileMapJson,
                cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(UpdateTempPath, "FileMap.json.sig"),
                DistributionInfo.FileMapSignature, cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(UpdateTempPath, "Deployment.lock"),
                JsonSerializer.Serialize(deploymentLock), cancellationToken);
            Logger.LogInformation("全部下载完成！");
            Settings.LastUpdateStatus = UpdateStatus.UpdateDownloaded;
        }
        catch (TaskCanceledException)
        {
            transaction.Finish(SpanStatus.Cancelled);
            Logger.LogInformation("已取消下载更新");
        }
        catch (Exception ex)
        {
            NetworkErrorException = ex;
            transaction.Finish(ex, SpanStatus.InternalError);
            Logger.LogError(ex, "下载应用更新失败");
            await RemoveDownloadedFiles(true);
        }
        finally
        {
            if (CurrentWorkingStatus == UpdateWorkingStatus.DownloadingUpdates)
            {
                CurrentWorkingStatus = UpdateWorkingStatus.Idle;
            }
            _downloadCancellationTokenSource = null;
            _downloadCompletion?.TrySetResult();
        }
    }

    public async Task StopDownloading()
    {
        Logger.LogInformation("应用更新下载停止。");
        IsCanceled = true;
        if (_downloadCancellationTokenSource != null)
        {
            try
            {
                await _downloadCancellationTokenSource.CancelAsync();
            }
            catch (Exception e)
            {
                // ignored
            }
        }

        // 等待下载循环真正退出并释放文件句柄，再删除临时目录，
        // 否则会因 archive.zip 仍被占用而抛 IOException（此前取消下载的报错来源）
        if (_downloadCompletion != null)
        {
            await _downloadCompletion.Task;
        }

        _downloadCancellationTokenSource = null;
        Settings.LastUpdateStatus = UpdateStatus.UpToDate;
        CurrentWorkingStatus = UpdateWorkingStatus.Idle;
        await RemoveDownloadedFiles(true);
    }

    public async Task RemoveDownloadedFiles(bool isCancel)
    {
        try
        {
            Directory.Delete(UpdateTempPath, true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "移除下载临时文件失败。");
        }

        if (!isCancel)
        {
            return;
        }
        await CheckUpdateAsync(isCancel:true);
    }
    

    public async Task ExtractUpdateAsync()
    {
        DeployErrorException = null;
        if (!AllowedPackageTypes.Contains(AppBase.Current.PackagingType))
        {
            return;
        }
        CurrentWorkingStatus = UpdateWorkingStatus.ExtractingUpdates;
        try
        {
            Logger.LogInformation("正在部署应用更新");
            var fileMapJson = await File.ReadAllTextAsync(Path.Combine(UpdateTempPath, "FileMap.json"));
            var fileMapSig = await File.ReadAllTextAsync(Path.Combine(UpdateTempPath, "FileMap.json.sig"));
            var deploymentLock = ConfigureFileHelper.LoadConfigUnWrapped<DeploymentLock>(Path.Combine(UpdateTempPath, "Deployment.lock"), false);
            if (!deploymentLock.FileMapSha512.SequenceEqual(SHA512.HashData(Encoding.UTF8.GetBytes(fileMapJson))))
            {
                throw new InvalidOperationException("文件图哈希与下载时不符合，可能已经损坏");
            }
            if (deploymentLock.SubChannel != GetCurrentSubChannel())
            {
                throw new InvalidOperationException("下载的更新不适用于当前子频道的 ClassPixelPro");
            }
            var publicKey =
                AppBase.Current.IsDevelopmentBuild && !string.IsNullOrWhiteSpace(Settings.DebugPublicKeyOverride)
                    ? Settings.DebugPublicKeyOverride
                    : MetadataPublisherPublicKey;
            var valid = DetachedSignatureProcessor.VerifyDetachedSignature(fileMapJson,
                Encoding.UTF8.GetBytes(fileMapSig), publicKey);
            if (!valid)
            {
                throw new InvalidOperationException("文件图签名校验不通过");
            }

            var fileMap = JsonSerializer.Deserialize<FileMap>(fileMapJson, FileMapJsonOptions);
            if (fileMap == null)
            {
                throw new InvalidOperationException("文件图解析失败");
            }

            // 整包解压模式：当 files 为空且 ArchiveUrl 不为空时
            var hasFiles = fileMap.Components.Values.Any(c => c.Files.Count > 0);
            if (!hasFiles && !string.IsNullOrEmpty(fileMap.ArchiveUrl))
            {
                Logger.LogInformation("检测到整包解压模式");
                var archivePath = Path.Combine(UpdateTempPath, "archive.zip");
                if (!File.Exists(archivePath))
                {
                    throw new InvalidOperationException("整包 zip 文件不存在");
                }

                // 计算新部署目录
                var rootArchive = CommonDirectories.AppPackageRoot;
                var numArchive = 0;
                fileMap.Variables["number"] = numArchive.ToString();
                while (numArchive <= 255 && Directory.Exists(Path.Combine(rootArchive,
                           VariableStringHelpers.ExpandString(fileMap.Components["app"].Root, fileMap.Variables))))
                {
                    numArchive++;
                    fileMap.Variables["number"] = numArchive.ToString();
                }

                var archiveAppPath = Path.Combine(rootArchive,
                    VariableStringHelpers.ExpandString(fileMap.Components["app"].Root, fileMap.Variables));
                if (!Directory.Exists(archiveAppPath))
                {
                    Directory.CreateDirectory(archiveAppPath);
                    await File.WriteAllTextAsync(Path.Combine(archiveAppPath, ".partial"), "");
                }

                // 解压 zip 到临时目录，再正确分发
                // zip 包含完整发布结构：启动器(ClassPixelPro.exe) + PackageType + app-* 子目录
                // 需要把 app-* 子目录的内容复制到 archiveAppPath，把启动器和 PackageType 复制到根目录
                Logger.LogInformation("正在解压整包到临时目录");
                var tempExtractPath = Path.Combine(UpdateTempPath, "archive_extracted");
                if (Directory.Exists(tempExtractPath)) Directory.Delete(tempExtractPath, true);
                Directory.CreateDirectory(tempExtractPath);

                using (var archive = ZipFile.OpenRead(archivePath))
                {
                    foreach (var entry in archive.Entries)
                    {
                        if (string.IsNullOrEmpty(entry.Name)) continue; // 跳过目录条目
                        var destPath = Path.Combine(tempExtractPath, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
                        var dir = Path.GetDirectoryName(destPath);
                        if (dir != null && !Directory.Exists(dir))
                        {
                            Directory.CreateDirectory(dir);
                        }
                        entry.ExtractToFile(destPath, true);
                    }
                }

                // 找到 zip 中的 app-* 目录，把内容复制到 archiveAppPath
                var appSourceDir = Directory.GetDirectories(tempExtractPath, "app-*", SearchOption.TopDirectoryOnly).FirstOrDefault();
                if (appSourceDir == null)
                {
                    throw new InvalidOperationException("zip 中未找到 app-* 目录");
                }
                Logger.LogInformation("正在复制应用文件到 {Path}", archiveAppPath);
                foreach (var item in Directory.GetFileSystemEntries(appSourceDir))
                {
                    var itemName = Path.GetFileName(item);
                    var dest = Path.Combine(archiveAppPath, itemName);
                    if (Directory.Exists(item))
                    {
                        CopyDirectoryRecursive(item, dest);
                    }
                    else
                    {
                        File.Copy(item, dest, true);
                    }
                }

                // 把启动器和 PackageType 复制到根目录
                foreach (var rootFile in new[] { "ClassPixelPro.exe", "PackageType" })
                {
                    var srcFile = Path.Combine(tempExtractPath, rootFile);
                    if (File.Exists(srcFile))
                    {
                        File.Copy(srcFile, Path.Combine(rootArchive, rootFile), true);
                        Logger.LogInformation("已复制 {File} 到根目录", rootFile);
                    }
                }

                // 清理临时解压目录
                Directory.Delete(tempExtractPath, true);

                // 复制 FileMap 和用户变量
                File.Copy(Path.Combine(UpdateTempPath, "FileMap.json"), Path.Combine(archiveAppPath, "files.json"), true);
                ConfigureFileHelper.SaveConfig(Path.Combine(archiveAppPath, "files.uvars.json"), fileMap.Variables);

                // 激活新部署
                await File.WriteAllTextAsync(Path.Combine(archiveAppPath, ".current"), "");
                DeploymentManager.MarkCurrentAsPrevious();
                File.Delete(Path.Combine(archiveAppPath, ".partial"));
                foreach (var deployment in Directory.GetDirectories(rootArchive)
                             .Where(x => Path.GetFileName(x).StartsWith("app")
                                         && Path.GetFullPath(Path.Combine(rootArchive, x)) != Path.GetFullPath(Environment.CurrentDirectory)
                                         && File.Exists(Path.Combine(x, ".current"))))
                {
                    File.Delete(Path.Combine(deployment, ".current"));
                }

                Settings.LastUpdateStatus = UpdateStatus.UpdateDeployed;
                Logger.LogInformation("整包部署成功");
                await RemoveDownloadedFiles(false);
                return;
            }

            Logger.LogInformation("正在解压并检验文件完整性");
            
            var root = CommonDirectories.AppPackageRoot;
            
            Logger.LogTrace("DeployRoot = {}", root);
            var extractedPath = Path.Combine(UpdateTempPath, "extracted");
            if (!Directory.Exists(extractedPath))
            {
                Directory.CreateDirectory(extractedPath);
            }
            
            foreach (var (id, component) in fileMap.Components)
            {
                var existedFiles = deploymentLock.ExistedFiles.GetValueOrDefault(id, []);

                foreach (var (path, fileInfo) in component.Files.Where(x =>
                             !component.AllowDiffUpdate || !existedFiles.Contains(x.Key)))
                {
                    var hashHex = Convert.ToHexString(fileInfo.FileSha512);
                    var fullPath = Path.Combine(extractedPath, hashHex[..2], hashHex);
                    if (!Directory.Exists(Path.GetDirectoryName(fullPath)))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? throw new InvalidOperationException("Path is null"));
                    }

                    Logger.LogTrace("正在解压：{}({})", path, fullPath);
                    await using (var file = File.OpenWrite(fullPath))
                    {
                        await using var archive = File.OpenRead(Path.Combine(UpdateTempPath, hashHex[..2], hashHex));
                        archive.Position = 0;
                        await using var gZipStream = new GZipStream(archive, CompressionMode.Decompress);
                        await gZipStream.CopyToAsync(file);
                    }

                    await using (var file = File.OpenRead(fullPath))
                    {
                        var hash = await SHA512.HashDataAsync(file);
                        if (!hash.SequenceEqual(fileInfo.FileSha512))
                        {
                            throw new InvalidOperationException($"文件 {id}/{path} 的 SHA512 校验失败 ");
                        }
                    }
                }
            }
            
            Logger.LogTrace("正在检查文件目录");

            var num = 0;
            fileMap.Variables["number"] = num.ToString();
            while (num <= 255 && Directory.Exists(Path.Combine(root,
                       VariableStringHelpers.ExpandString(fileMap.Components["app"].Root, fileMap.Variables))))
            {
                num++;
                fileMap.Variables["number"] = num.ToString();
            }
            
            foreach (var (id, component) in fileMap.Components)
            {
                var expandedRoot = VariableStringHelpers.ExpandString(component.Root, fileMap.Variables);
                var compRoot = Path.Combine(root, expandedRoot);

                if (!PathHelpers.IsSafePath(root, compRoot) ||
                    component.Files.Any(x =>
                        !PathHelpers.IsSafePath(root, DeployTarget(x.Key, root, expandedRoot))))
                {
                    throw new InvalidOperationException("文件图发现非法文件路径");
                }

                var existedFiles = deploymentLock.ExistedFiles.GetValueOrDefault(id, []);
                var existedFilesRoot = deploymentLock.ComponentRoots.GetValueOrDefault(id);

                foreach (var (path, fileInfo) in component.Files)
                {
                    var targetPath = Path.Combine(DeployBase(path, root, expandedRoot), path);

                    if (component.AllowDiffUpdate && existedFiles.Contains(path) && existedFilesRoot != null)
                    {
                        var existedPath = Path.Combine(DeployBase(path, existedFilesRoot, expandedRoot), path);
                        if (Path.Exists(existedPath))
                        {
                            await using var file = File.OpenRead(existedPath);
                            var hash = await SHA512.HashDataAsync(file);
                            if (hash.SequenceEqual(fileInfo.FileSha512))
                            {
                                Logger.LogTrace("Deploy Check EXISTED {} -> {}", existedPath, targetPath);
                                continue;
                            }
                        }
                    }
                    
                    var hashHex = Convert.ToHexString(fileInfo.FileSha512);
                    var fullPath = Path.Combine(extractedPath, hashHex[..2], hashHex);
                    if (File.Exists(fullPath))
                    {
                        Logger.LogTrace("Deploy EXISTED {} -> {}", fullPath, targetPath);
                        continue;
                    }

                    throw new InvalidOperationException($"文件 {id}/{path} 未找到部署源");
                }
            }
            Logger.LogInformation("Variables: {}", JsonSerializer.Serialize(fileMap.Variables));
            
            Logger.LogInformation("正在准备部署");

            var appPath = Path.Combine(root,
                VariableStringHelpers.ExpandString(fileMap.Components["app"].Root, fileMap.Variables));
            if (!Directory.Exists(appPath))
            {
                Directory.CreateDirectory(appPath);
                await File.WriteAllTextAsync(Path.Combine(appPath, ".partial"), "");
            }
            
            
            foreach (var (id, component) in fileMap.Components)
            {
                var existedFiles = deploymentLock.ExistedFiles.GetValueOrDefault(id, []);
                var existedFilesRoot = deploymentLock.ComponentRoots.GetValueOrDefault(id);
                var deployExpandedRoot = VariableStringHelpers.ExpandString(component.Root, fileMap.Variables);
                var compRoot = Path.Combine(root, deployExpandedRoot);

                await Parallel.ForEachAsync(component.Files, new ParallelOptions()
                {
                    MaxDegreeOfParallelism = 16
                }, async (pair, token) =>
                {
                    var (path, fileInfo) = pair;
                    var targetPath = Path.Combine(DeployBase(path, root, deployExpandedRoot), path);
                    var dir = Path.GetDirectoryName(targetPath);
                    if (dir != null && !Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    if (component.AllowDiffUpdate && existedFiles.Contains(path) && existedFilesRoot != null)
                    {
                        var existedPath = Path.Combine(DeployBase(path, existedFilesRoot, deployExpandedRoot), path);

                        Logger.LogTrace("Deploy Copy EXISTED {} -> {}", existedPath, targetPath);
                        File.Copy(existedPath, targetPath, true);
                        return;
                    }

                    var hashHex = Convert.ToHexString(fileInfo.FileSha512);
                    var fullPath = Path.Combine(extractedPath, hashHex[..2], hashHex);
                    Logger.LogTrace("Deploy Copy {} -> {}", fullPath, targetPath);
                    File.Copy(fullPath, targetPath, true);
                });
            }

            if (OperatingSystem.IsLinux() && AppBase.Current.PackagingType == "folder")
            {
                using var proc = Process.Start(new ProcessStartInfo("chmod",
                [
                    "+x",
                    Path.GetFullPath(Path.Combine(root,
                        "ClassPixelPro"))
                ]));
                var task = proc?.WaitForExitAsync();
                if (task != null)
                {
                    await task;
                }
            }
            File.Copy(Path.Combine(UpdateTempPath, "FileMap.json"), Path.Combine(appPath, "files.json"));
            ConfigureFileHelper.SaveConfig(Path.Combine(appPath, "files.uvars.json"), fileMap.Variables);
            
            Logger.LogInformation("正在激活新的部署");
            
            await File.WriteAllTextAsync(Path.Combine(appPath, ".current"), "");
            DeploymentManager.MarkCurrentAsPrevious();
            File.Delete(Path.Combine(appPath, ".partial"));
            foreach (var deployment in Directory.GetDirectories(root)
                         .Where(x => Path.GetFileName(x).StartsWith("app") 
                                     && Path.GetFullPath(Path.Combine(root, x)) != Path.GetFullPath(Environment.CurrentDirectory)
                                     && File.Exists(Path.Combine(x, ".current"))))
            {
                File.Delete(Path.Combine(deployment, ".current"));
            }

            Settings.LastUpdateStatus = UpdateStatus.UpdateDeployed;
            Logger.LogInformation("部署成功");
            await RemoveDownloadedFiles(false);
        }
        catch (Exception e)
        {
            DeployErrorException = e;
            Logger.LogError(e, "无法部署应用更新");
            await RemoveDownloadedFiles(true);
        }
        finally
        {
            CurrentWorkingStatus = UpdateWorkingStatus.Idle;
        }

        return;

        string GetExistedFileRoot(string id) => id switch
        {
            "app" => Environment.CurrentDirectory,
            _ => throw new ArgumentOutOfRangeException()
        }; 
    }

    public void CleanupPrevDeployments()
    {
        var root = CommonDirectories.AppPackageRoot;
        var hasPrevious = Directory.GetDirectories(root)
            .Any(x => File.Exists(Path.Combine(x, ".prev")));
        foreach (var deployment in Directory.GetDirectories(root)
                     .Where(x => Path.GetFileName(x).StartsWith("app")
                                 && Path.GetFullPath(Path.Combine(root, x)) != Path.GetFullPath(Environment.CurrentDirectory)
                                 && File.Exists(Path.Combine(x, ".destroy"))))
        {
            // 兼容旧版本的部署逻辑：旧代码把上一版本标记为 .destroy 直接删除。既然现在要保留
            // 一个版本用于回滚，就把第一个完整可用的待删目录转成 .prev 留下，让「回滚」在
            // 升级后第一次启动即可用；其余待删目录照旧删除。
            if (!hasPrevious &&
                File.Exists(Path.Combine(deployment, "ClassPixelPro.Desktop" + AppBase.PlatformExecutableExtension)))
            {
                Logger.LogInformation("将先前的部署保留为可回滚版本：{}", deployment);
                try
                {
                    File.Delete(Path.Combine(deployment, ".destroy"));
                    File.WriteAllText(Path.Combine(deployment, ".prev"), "");
                    hasPrevious = true;
                    continue;
                }
                catch (Exception e)
                {
                    Logger.LogError(e, "无法保留先前部署 {} 用于回滚", deployment);
                }
            }

            Logger.LogInformation("正在清理先前的部署：{}", deployment);
            try
            {
                Directory.Delete(Path.Combine(root, deployment), true);
            }
            catch (Exception e)
            {
                Logger.LogError(e, "无法清理部署 {}", deployment);
            }
        }

        // 补充清理：上面的递归删除可能在删完内容后因目录句柄被短暂占用而失败，
        // 只留下空目录——此时 .destroy 标记已随内容一并删除，上面的循环下次不会再尝试，
        // 空目录将永久残留。空的 app-* 目录不可能是有效安装（启动器要求其中存在
        // 可执行文件），因此这里直接移除所有空的非当前 app-* 目录。
        foreach (var deployment in Directory.GetDirectories(root)
                     .Where(x => Path.GetFileName(x).StartsWith("app")
                                 && Path.GetFullPath(Path.Combine(root, x)) != Path.GetFullPath(Environment.CurrentDirectory)
                                 && !Directory.EnumerateFileSystemEntries(x).Any()))
        {
            Logger.LogInformation("正在清理残留的空部署目录：{}", deployment);
            try
            {
                Directory.Delete(deployment);
            }
            catch (Exception e)
            {
                Logger.LogError(e, "无法清理空部署目录 {}", deployment);
            }
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        return;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        return;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    /// <summary>
    /// 判断文件映射里的 path 是否已经带了组件 root 前缀。
    ///
    /// 文件映射的路径是**相对包根目录**的，而不是相对组件 root：组件 root 本身就是
    /// <c>app-$(version)-$(number)/</c>，展开后为 <c>app-2.2.3.4-0/</c>，而映射里的 key
    /// 形如 <c>app-2.2.3.4-0/Foo.dll</c>——前缀已经在里面了。同理 <c>ClassPixelPro.exe</c>
    /// 和 <c>PackageType</c> 没有前缀，因为它们位于包根目录。
    ///
    /// 早期版本无条件用 <c>Path.Combine(compRoot, path)</c>，于是拼出
    /// <c>app-2.2.3.4-0/app-2.2.3.4-0/Foo.dll</c> 这种双层目录。启动器要求版本目录下
    /// 直接存在 <c>ClassPixelPro.Desktop.exe</c>，找不到就判定「安装已损坏」并拒绝启动——
    /// 表现为从 2.2.3.3 升到 2.2.3.4 后无法启动，而全新安装却正常（整包解压的目录结构是平的）。
    /// </summary>
    private static bool PathHasRootPrefix(string path, string expandedRoot)
    {
        var p = path.Replace('\\', '/').TrimStart('/');
        var prefix = expandedRoot.Replace('\\', '/').TrimEnd('/') + "/";
        return p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 计算某个文件映射路径应当落在哪个基准目录下。
    /// 已带 root 前缀的（应用本体文件）以包根为基准，没有前缀的（启动器、PackageType）
    /// 以组件根为基准。两种文件映射写法都能正确落盘。
    /// </summary>
    private static string DeployBase(string path, string baseDir, string expandedRoot)
    {
        return PathHasRootPrefix(path, expandedRoot)
            ? baseDir
            : Path.Combine(baseDir, expandedRoot);
    }

    /// <summary>
    /// 计算文件映射路径的最终落盘位置。
    /// </summary>
    private static string DeployTarget(string path, string baseDir, string expandedRoot)
    {
        return Path.Combine(DeployBase(path, baseDir, expandedRoot), path);
    }

    private static void CopyDirectoryRecursive(string sourceDir, string destDir)
    {
        if (!Directory.Exists(destDir)) Directory.CreateDirectory(destDir);
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), true);
        }
        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            CopyDirectoryRecursive(dir, Path.Combine(destDir, Path.GetFileName(dir)));
        }
    }
}