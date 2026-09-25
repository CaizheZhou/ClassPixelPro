using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using ClassPixelPro.Core;
using ClassPixelPro.Core.Abstractions.Services;
using ClassPixelPro.Core.Abstractions.Services.Management;
using ClassPixelPro.Core.Controls;
using ClassPixelPro.Core.Enums;
using ClassPixelPro.Core.Models.Components;
using ClassPixelPro.Shared.Abstraction.Services;
using ClassPixelPro.Shared.Enums;
using ClassPixelPro.Shared.Models.Management;
using ClassPixelPro.Shared.Models.Profile;
using ClassPixelPro.Shared.Protobuf.Enum;
using ClassPixelPro.Helpers;
using ClassPixelPro.Models;
using ClassPixelPro.Models.Authorize;
using ClassPixelPro.Services;
using ClassPixelPro.Shared;
using ClassPixelPro.Shared.Protobuf.AuditEvent;
using ClassPixelPro.Shared.Protobuf.Client;
using ClassPixelPro.Shared.Protobuf.Service;
using CommunityToolkit.Mvvm.ComponentModel;
using FluentAvalonia.UI.Controls;
using Microsoft.Extensions.Logging;
using Avalonia.Threading;

using static ClassPixelPro.Shared.Helpers.ConfigureFileHelper;

namespace ClassPixelPro.Services.Management;

public class ManagementService : ObservableRecipient, IManagementService
{
    static ManagementService()
    {
    }

    public static void InitManagement()
    {
    }

    public static ManagementService? Instance { get; private set; }

    public static readonly string ManagementPresetPath = Path.Combine(CommonDirectories.AppRootFolderPath, "./ManagementPreset.json");
    public static readonly string ManagementConfigureFolderPath =
        Path.Combine(CommonDirectories.AppDataFolderPath, "Management");
    public static readonly string LocalManagementConfigureFolderPath =
        Path.Combine(CommonDirectories.AppConfigPath, "Management");

    public static readonly string ManagementPersistConfigPath =
        Path.Combine(ManagementConfigureFolderPath, "Persist.json");


    public static readonly string ManagementManifestPath = Path.Combine(ManagementConfigureFolderPath, "Manifest.json");
    public static readonly string ManagementVersionsPath = Path.Combine(ManagementConfigureFolderPath, "Versions.json");
    public static readonly string ManagementSettingsPath = Path.Combine(ManagementConfigureFolderPath, "Settings.json");
    public static readonly string ManagementPolicyPath = Path.Combine(ManagementConfigureFolderPath, "Policy.json");
    public static readonly string ManagementCredentialsPath = Path.Combine(ManagementConfigureFolderPath, "Credentials.json");
    public static readonly string ManagementComponentsPath = Path.Combine(ManagementConfigureFolderPath, "Components.json");
    public static readonly string ManagementAppSettingsPath =
        Path.Combine(ManagementConfigureFolderPath, "AppSettings.json");
    public static readonly string ManagementPluginsPath =
        Path.Combine(ManagementConfigureFolderPath, "Plugins.json");
    public static readonly string ManagementThemePath =
        Path.Combine(ManagementConfigureFolderPath, "Theme.json");
    public static readonly string ManagementComponentSchemePath =
        Path.Combine(CommonDirectories.AppConfigPath, "ManagementComponentScheme.json");
    public static readonly string ManagementTempClassPlanPath =
        Path.Combine(CommonDirectories.AppConfigPath, "ManagementTempClassPlan.json");
    public static readonly string ManagementHelpInfoPath =
        Path.Combine(ManagementConfigureFolderPath, "HelpInfo.json");

    /// <summary>
    /// 帮助信息公开 API 的默认 base URL。未加入集控时使用此地址拉取全局默认帮助信息。
    /// </summary>
    private const string DefaultHelpInfoApiBaseUrl = "https://class.imzcz.cn";

    public static readonly string LocalManagementPolicyPath =
        Path.Combine(LocalManagementConfigureFolderPath, "Policy.json");
    public static readonly string LocalManagementCredentialsPath =
        Path.Combine(LocalManagementConfigureFolderPath, "Credentials.json");

    public bool IsManagementEnabled { get; set; }

    public ManagementVersions Versions { get; set; } = new();

    public ManagementManifest Manifest { get; set; } = new();

    public ManagementSettings Settings { get; }

    private ManagementPolicy _policy = new();
    public ManagementPolicy Policy
    {
        get => _policy;
        set => SetProperty(ref _policy, value);
    }

    private ManagementCredentialConfig _credentialConfig = new();
    public ManagementCredentialConfig CredentialConfig
    {
        get => _credentialConfig;
        set => SetProperty(ref _credentialConfig, value);
    }

    private ManagementHelpInfo _helpInfo = new();
    /// <summary>
    /// 集控下发的帮助信息。客户端"关于"页面据此展示技术联系人等联系方式。
    /// </summary>
    public ManagementHelpInfo HelpInfo
    {
        get => _helpInfo;
        set => SetProperty(ref _helpInfo, value);
    }

    public ManagementClientPersistConfig Persist { get;}

    /// <summary>
    /// 是否已向集控后端上报过当前课表/时间表/科目。
    /// 仅在首次连接成功后上报一次，避免每次轮询都上报浪费带宽。
    /// </summary>
    private bool _hasReported = false;

    /// <summary>
    /// 集控调课与策略拉取等操作的内存日志队列。保留最近 <see cref="MaxLogEntries"/> 条。
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentQueue<ManagementLogEntry> _managementLogs = new();
    private const int MaxLogEntries = 100;

    /// <inheritdoc />
    public IReadOnlyList<ManagementLogEntry> ManagementLogs => _managementLogs.ToList();

    private ILogger<ManagementService> Logger { get; }
    public IAuthorizeService AuthorizeService { get; }

    public IManagementServerConnection? Connection { get; }

    public ManagementService(ILogger<ManagementService> logger, IAuthorizeService authorizeService)
    {
        Instance = this;
        Logger = logger;
        AuthorizeService = authorizeService;
        Persist = LoadConfig<ManagementClientPersistConfig>(ManagementPersistConfigPath);
        Settings = LoadConfig<ManagementSettings>(ManagementSettingsPath);
#if DEBUG
        if (App.ApplicationCommand.DisableManagement)
        {
            Logger.LogInformation("集控已被命令行禁用。");
            return;
        }
#endif
        IsManagementEnabled = Settings.IsManagementEnabled;
        if (!IsManagementEnabled)
        {
            LoadLocalHelpInfo();
            // 未加入集控时，异步拉取全局默认帮助信息
            _ = Task.Run(() => FetchHelpInfoFromApiAsync(null));
            return;
        }

        // 加载本地已保存的帮助信息（集控刷新前先展示缓存）
        LoadLocalHelpInfo();

        // 连接集控服务器
        try
        {
            switch (Settings.ManagementServerKind)
            {
                case ManagementServerKind.Serverless:
                    Connection = new ServerlessConnection(Persist.ClientUniqueId, Settings.ClassIdentity ?? "", Settings.ManifestUrlTemplate);
                    break;
                case ManagementServerKind.ManagementServer:
                    Connection = new ManagementServerConnection(Settings, Persist.ClientUniqueId, false);
                    break;
                default:
                    throw new ArgumentOutOfRangeException("", "无效的集控服务器类型。");
            }
            Connection.CommandReceived += ConnectionOnCommandReceived;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "连接集控服务器失败。");
        }
    }

    private void ConnectionOnCommandReceived(object? sender, ClientCommandEventArgs e)
    {
        switch (e.Type)
        {
            case CommandTypes.RestartApp:
            {
                AppBase.Current.Restart(true);
                break;
            }
            case CommandTypes.DataUpdated:
                Logger.LogInformation("Received DataUpdated command.");
                _ = ReloadManagementAsync();
            break;
        }
    }

    private void SetupLocalManagement()
    {
        Policy = LoadConfig<ManagementPolicy>(LocalManagementPolicyPath);
        CredentialConfig = LoadConfig<ManagementCredentialConfig>(LocalManagementCredentialsPath);

        Policy.PropertyChanged += (sender, args) => SaveConfig(LocalManagementPolicyPath, Policy);
        CredentialConfig.PropertyChanged += (sender, args) => SaveConfig(LocalManagementCredentialsPath, CredentialConfig);
    }

    /// <summary>
    /// 加载本地已保存的帮助信息（集控未启用或拉取失败时使用缓存）。
    /// </summary>
    private void LoadLocalHelpInfo()
    {
        try
        {
            if (File.Exists(ManagementHelpInfoPath))
            {
                HelpInfo = LoadConfig<ManagementHelpInfo>(ManagementHelpInfoPath);
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "加载本地帮助信息失败");
        }
    }

    /// <summary>
    /// 从 /api/help 公开接口拉取帮助信息。
    /// 带 orgId 时后端先查组织自定义，没有则回退到全局默认；不带 orgId 时直接返回全局默认。
    /// 拉取成功后写入本地缓存文件并更新 HelpInfo 属性。
    /// </summary>
    /// <param name="orgId">组织 ID。为 null 时拉取全局默认。</param>
    /// <param name="baseUrl">API base URL。为 null 时使用 <see cref="DefaultHelpInfoApiBaseUrl"/>。</param>
    private async Task FetchHelpInfoFromApiAsync(string? orgId = null, string? baseUrl = null)
    {
        var apiBase = string.IsNullOrWhiteSpace(baseUrl) ? DefaultHelpInfoApiBaseUrl : baseUrl.TrimEnd('/');
        var url = string.IsNullOrEmpty(orgId)
            ? $"{apiBase}/api/help"
            : $"{apiBase}/api/help?orgId={orgId}";
        try
        {
            var info = await WebRequestHelper.Default.GetJson<ManagementHelpInfo>(new Uri(url), retries: 1);
            if (info != null)
            {
                HelpInfo = info;
                try
                {
                    SaveConfig(ManagementHelpInfoPath, info);
                }
                catch (Exception exSave)
                {
                    Logger.LogWarning(exSave, "保存帮助信息到本地失败");
                }
                Logger.LogInformation("已拉取帮助信息（orgId={OrgId}）", orgId ?? "default");
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "拉取帮助信息失败（orgId={OrgId}）", orgId ?? "default");
        }
    }

    /// <summary>
    /// 从 ManifestUrlTemplate 解析出集控 API base URL。
    /// 模板形如：https://example.com/api/mgmt/{orgId}/serverless/manifest?cuid={cuid}&amp;id={id}
    /// </summary>
    private string? GetManagementBaseUrl()
    {
        var template = Settings.ManifestUrlTemplate;
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

    /// <summary>
    /// 从 ManifestUrlTemplate 解析出组织 ID。
    /// 模板形如：https://example.com/api/mgmt/{orgId}/serverless/manifest?...
    /// </summary>
    private string? GetOrgIdFromManifestTemplate()
    {
        var template = Settings.ManifestUrlTemplate;
        if (string.IsNullOrWhiteSpace(template)) return null;
        var match = System.Text.RegularExpressions.Regex.Match(
            template,
            @"/api/mgmt/([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})/",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }

    public async Task<bool> SetupManagement()
    {
        if (!IsManagementEnabled)
        {
            SetupLocalManagement();
            return true;
        }

        Logger.LogInformation("正在初始化集控");

        // 启动定时刷新（每 5 秒，同时上报设备状态）
        _ = Task.Run(async () =>
        {
            while (IsManagementEnabled)
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
                if (!IsManagementEnabled) break;
                try
                {
                    await ReloadManagementAsync();
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "定时刷新集控配置失败");
                }
                // 上报设备当前状态（当前课程/课间、时间段、剩余时间）
                try
                {
                    await ReportDeviceStatusAsync();
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "定时上报设备状态失败");
                }
            }
        });

        return await ReloadManagementAsync();
    }

    public async Task<bool> ReloadManagementAsync()
    {
        Logger.LogInformation("正在重载集控配置");
        if (Connection == null)
        {
            return true;
        }

        //Load old config
        ManagementManifest oldManifest = Manifest;
        ManagementPolicy oldPolicy = Policy;
        ManagementCredentialConfig oldCredential = CredentialConfig;
        ManagementVersions oldVersions = Versions;

        try
        {
            Manifest = LoadConfig<ManagementManifest>(ManagementManifestPath);
            Policy = LoadConfig<ManagementPolicy>(ManagementPolicyPath);
            CredentialConfig = LoadConfig<ManagementCredentialConfig>(ManagementCredentialsPath);
            Versions = LoadConfig<ManagementVersions>(ManagementVersionsPath);
            Manifest = await Connection.GetManifest();
            if (Manifest.CoreVersion.Major != IAppHost.CoreVersion.Major)
            {
                await CommonTaskDialogs.ShowDialog("集控核心版本不兼容",
                    $"您正在核心版本主版本号（{IAppHost.CoreVersion}）与集控数据核心版本主版本号（{Manifest.CoreVersion}）不同的 ClassPixelPro 加入集控，这可能会导致您的数据损坏。请运行兼容贵组织集控服务的 ClassPixelPro 版本，或者联系您的管理员了解详细信息。");
                return false;
            }
            SaveConfig(ManagementManifestPath, Manifest);
            // 拉取策略
            if (Manifest.PolicySource.IsNewerAndNotNull(Versions.PolicyVersion))
            {
                Policy = await Connection.SaveJsonAsync<ManagementPolicy>(Manifest.PolicySource.Value!, ManagementPolicyPath);
                Versions.PolicyVersion = Manifest.PolicySource.Version;
            }
            // 拉取凭据
            if (Manifest.CredentialSource.IsNewerAndNotNull(Versions.CredentialVersion))
            {
                CredentialConfig = await Connection.SaveJsonAsync<ManagementCredentialConfig>(Manifest.CredentialSource.Value!, ManagementCredentialsPath);
                Versions.CredentialVersion = Manifest.CredentialSource.Version;
            }
            // 拉取应用设置
            if (Manifest.AppSettingsSource.IsNewerAndNotNull(Versions.AppSettingsVersion))
            {
                Logger.LogInformation("拉取集控应用设置");
                await Connection.SaveJsonAsync<Settings>(Manifest.AppSettingsSource.Value!, ManagementAppSettingsPath);
                Versions.AppSettingsVersion = Manifest.AppSettingsSource.Version;
                // 应用设置到 SettingsService
                try
                {
                    var settingsService = IAppHost.GetService<SettingsService>();
                    var appSettings = LoadConfig<Settings>(ManagementAppSettingsPath);
                    settingsService.Settings = appSettings;
                    Logger.LogTrace("应用集控设置成功");
                }
                catch (Exception exAppSettings)
                {
                    Logger.LogError(exAppSettings, "应用集控设置失败");
                }
            }
            // 拉取插件配置
            if (Manifest.PluginsSource.IsNewerAndNotNull(Versions.PluginsVersion))
            {
                Logger.LogInformation("拉取集控插件配置");
                await Connection.SaveJsonAsync<object>(Manifest.PluginsSource.Value!, ManagementPluginsPath);
                Versions.PluginsVersion = Manifest.PluginsSource.Version;
            }
            // 拉取主题配置
            if (Manifest.ThemeSource.IsNewerAndNotNull(Versions.ThemeVersion))
            {
                Logger.LogInformation("拉取集控主题配置");
                await Connection.SaveJsonAsync<object>(Manifest.ThemeSource.Value!, ManagementThemePath);
                Versions.ThemeVersion = Manifest.ThemeSource.Version;
            }
            // 拉取组件方案
            if (Manifest.ComponentSchemeSource.HasValue &&
                Manifest.ComponentSchemeSource.Value.IsNewerAndNotNull(Versions.ComponentSchemeVersion))
            {
                Logger.LogInformation("拉取集控组件方案");
                await Connection.SaveJsonAsync<object>(Manifest.ComponentSchemeSource.Value.Value!, ManagementComponentSchemePath);
                Versions.ComponentSchemeVersion = Manifest.ComponentSchemeSource.Value.Version;
                // 应用组件方案
                try
                {
                    var componentsService = IAppHost.GetService<IComponentsService>();
                    // 读取保存的组件方案配置（ComponentProfile JSON）
                    var scheme = LoadConfig<ComponentProfile>(ManagementComponentSchemePath);
                    // 将集控组件方案写入组件配置目录，使用固定名称 "Management"，
                    // 便于用户在编辑模式下查看与切换；随后刷新方案列表并立即应用。
                    const string managementSchemeName = "Management";
                    var schemePath = Path.Combine(ComponentsService.ComponentSettingsPath,
                        managementSchemeName + ".json");
                    SaveConfig(schemePath, scheme);
                    componentsService.RefreshConfigs();
                    componentsService.CurrentComponents = scheme;
                    Logger.LogTrace("应用集控组件方案成功");
                }
                catch (Exception exComponentScheme)
                {
                    Logger.LogError(exComponentScheme, "应用集控组件方案失败");
                }
            }
            // 拉取临时调课课表
            if (Manifest.TempClassPlanSource.HasValue &&
                Manifest.TempClassPlanSource.Value.IsNewerAndNotNull(Versions.TempClassPlanVersion))
            {
                Logger.LogInformation("拉取集控临时调课课表");
                try
                {
                    await Connection.SaveJsonAsync<object>(Manifest.TempClassPlanSource.Value.Value!, ManagementTempClassPlanPath);
                    Versions.TempClassPlanVersion = Manifest.TempClassPlanSource.Value.Version;
                    // 应用临时课表到当前档案的临时层
                    var applied = await ApplyTempClassPlanAsync();
                    if (applied)
                    {
                        Logger.LogInformation("已拉取并应用集控临时调课课表，版本 {Version}", Versions.TempClassPlanVersion);
                    }
                }
                catch (Exception exTempClassPlan)
                {
                    Logger.LogError(exTempClassPlan, "应用集控临时调课课表失败");
                }
            }
            else if (Manifest.TempClassPlanSource.HasValue &&
                     Manifest.TempClassPlanSource.Value.Version == 0 &&
                     Versions.TempClassPlanVersion > 0)
            {
                // 班主任端已删除临时课表（后端 version 回到 0），清除本地 overlay 恢复原课表
                Logger.LogInformation("集控临时调课课表已被删除（manifest version=0），清除本地 overlay");
                await ClearTempClassPlanOverlayAsync();
                Versions.TempClassPlanVersion = 0;
            }
            // 拉取帮助信息
            if (Manifest.HelpInfoSource.HasValue &&
                Manifest.HelpInfoSource.Value.IsNewerAndNotNull(Versions.HelpInfoVersion))
            {
                Logger.LogInformation("拉取集控帮助信息");
                try
                {
                    HelpInfo = await Connection.SaveJsonAsync<ManagementHelpInfo>(Manifest.HelpInfoSource.Value.Value!, ManagementHelpInfoPath);
                    Versions.HelpInfoVersion = Manifest.HelpInfoSource.Value.Version;
                    Logger.LogInformation("已拉取集控帮助信息，版本 {Version}", Versions.HelpInfoVersion);
                }
                catch (Exception exHelpInfo)
                {
                    Logger.LogError(exHelpInfo, "拉取集控帮助信息失败");
                }
            }
            // 拉取课表/时间表/科目（classplan/timelayout/subjects）
            // 这些内容类型由 ProfileService.MergeManagementProfileAsync 负责合并到当前档案。
            // 首次启动时在 LoadProfileAsync 中调用一次；此处为定时刷新场景：
            // 班主任端修改课表后 version 升级，定时刷新检测到 IsNewerAndNotNull 即触发合并。
            if (Manifest.ClassPlanSource.IsNewerAndNotNull(Versions.ClassPlanVersion) ||
                Manifest.TimeLayoutSource.IsNewerAndNotNull(Versions.TimeLayoutVersion) ||
                Manifest.SubjectsSource.IsNewerAndNotNull(Versions.SubjectsVersion))
            {
                Logger.LogInformation("检测到集控课表/时间表/科目有更新，触发档案合并");
                try
                {
                    var profileService = IAppHost.TryGetService<IProfileService>();
                    if (profileService != null)
                    {
                        await profileService.MergeManagementProfileAsync();
                    }
                    else
                    {
                        Logger.LogWarning("ProfileService 不可用，跳过集控课表/时间表/科目合并");
                    }
                }
                catch (Exception exProfileMerge)
                {
                    Logger.LogError(exProfileMerge, "集控课表/时间表/科目合并失败");
                }
            }
            // 保存版本号
            SaveSettings();

            // 注意：ReportCurrentProfileAsync 不在此处调用。
            // 因为 SetupManagement() 在 LoadProfileAsync() 之前执行，此时 ProfileService.Profile
            // 还是空的 new Profile()，上报的会是空数据。改为在 ProfileService.LoadProfileAsync()
            // 末尾调用，以确保上报的是客户端实际加载的课表数据。
        }
        catch (Exception e)
        {
            // 检查是否因为设备被删除导致 manifest 获取失败（404）
            if (IsDeviceDeletedException(e))
            {
                Logger.LogWarning("设备已被管理员删除，自动退出集控");
                Settings.IsManagementEnabled = false;
                SaveConfig(ManagementSettingsPath, Settings);
                IsManagementEnabled = false;
                await CommonTaskDialogs.ShowDialog("已退出集控", "您已被管理员移出集控管理。应用将重启以应用更改。");
                AppBase.Current.Restart();
                return false;
            }
            Manifest = oldManifest;
            Policy = oldPolicy;
            CredentialConfig = oldCredential;
            Versions = oldVersions;
            Logger.LogError(e, "拉取集控清单与策略失败");
        }

        return true;
    }

    /// <summary>
    /// 检查异常是否表示设备已被删除（HTTP 404）。
    /// GetManifest() 底层调用 WebRequestHelper.GetJson，在 404 时会抛出
    /// Exception(Message, innerException) 其中 innerException 为 HttpRequestException。
    /// 因此需要遍历异常链查找带 404 状态码的 HttpRequestException。
    /// </summary>
    private static bool IsDeviceDeletedException(Exception ex)
    {
        for (Exception? current = ex; current != null; current = current.InnerException)
        {
            if (current is HttpRequestException httpEx && (int)(httpEx.StatusCode ?? 0) == 404)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 上报当前档案中的课表/时间表/科目/课表组配置给集控后端。
    /// <para>
    /// 应在 <see cref="ProfileService.LoadProfileAsync"/> 完成后调用，以确保
    /// <see cref="IProfileService.Profile"/> 已加载实际课表数据（包含集控下发与
    /// 用户手动修改的合并结果），而不是空的默认 Profile。
    /// 仅在首次成功上报后通过 <see cref="_hasReported"/> 标志去重，避免浪费带宽。
    /// 失败时仅记录警告，不影响集控主流程。
    /// </para>
    /// </summary>
    public async Task ReportCurrentProfileAsync()
    {
        if (_hasReported)
        {
            return;
        }

        try
        {
            var profileService = IAppHost.TryGetService<IProfileService>();
            if (profileService == null)
            {
                Logger.LogWarning("ProfileService 不可用，跳过上报课表/时间表/科目");
                return;
            }

            var profile = profileService.Profile;
            // 上报完整的当前档案数据：课表、时间表、科目、课表组、当前选中的组。
            // 注意：profile.ClassPlans 是客户端实际使用的课表（集控下发 + 用户手动修改的合并结果），
            // 而不是集控全局课表。班主任端通过 /api/teacher/classplan/:cuid 拉取 per-device 记录，
            // 即可看到客户端当前实际使用的课表。
            // 客户端时间偏移（秒），班主任端编辑课表时需要据此显示与客户端一致的时间
            var settingsService = IAppHost.TryGetService<SettingsService>();
            var reportBody = new
            {
                classplan = new
                {
                    ClassPlans = profile.ClassPlans,
                    ClassPlanGroups = profile.ClassPlanGroups,
                    SelectedClassPlanGroupId = profile.SelectedClassPlanGroupId,
                    // 内嵌 Subjects 和 TimeLayouts，便于后端 fallback 提取
                    Subjects = profile.Subjects,
                    TimeLayouts = profile.TimeLayouts,
                    Name = profile.Name,
                },
                timeLayout = new
                {
                    TimeLayouts = profile.TimeLayouts,
                    Name = profile.Name,
                },
                subjects = new
                {
                    Subjects = profile.Subjects,
                    Name = profile.Name,
                },
                timeOffsetSeconds = settingsService?.Settings.TimeOffsetSeconds ?? 0
            };
            var reportJson = System.Text.Json.JsonSerializer.Serialize(reportBody);

            // 从 PolicySource 中提取 base URL（与 ExitManagementAsync 中的模式一致）
            var baseUrl = Manifest.PolicySource.Value?.Split("/serverless/")[0] ?? "";
            if (string.IsNullOrEmpty(baseUrl))
            {
                Logger.LogWarning("无法从 Manifest.PolicySource 中提取 base URL，跳过上报课表/时间表/科目");
                return;
            }

            var reportUri = new Uri($"{baseUrl}/serverless/report?cuid={Persist.ClientUniqueId}");
            using var httpClient = new HttpClient();
            using var content = new StringContent(reportJson, System.Text.Encoding.UTF8, "application/json");
            var response = await httpClient.PostAsync(reportUri, content);
            response.EnsureSuccessStatusCode();
            _hasReported = true;
            Logger.LogInformation("已上报当前课表/时间表/科目给后端（ClassPlans={ClassPlanCount}, Subjects={SubjectCount}, TimeLayouts={TimeLayoutCount}）",
                profile.ClassPlans.Count, profile.Subjects.Count, profile.TimeLayouts.Count);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "上报课表/时间表失败（非致命）");
        }
    }

    /// <summary>
    /// 应用集控下发的临时调课课表到当前档案的临时层（Overlay）。
    /// <para>
    /// 支持两种载荷格式：
    /// <list type="bullet">
    /// <item>旧格式 <c>{ ClassPlan: ClassPlan, SourceClassPlanId, EnableDate }</c>：单个 ClassPlan 包装。</item>
    /// <item>新格式（班主任端 saveTemp 保存的整个课表档案）：含 <c>ClassPlans</c> 字典、<c>Subjects</c>、<c>TimeLayouts</c> 等顶层字段。
    /// 从中提取"今天"对应的 ClassPlan 作为 overlay，同时同步 Subjects/TimeLayouts 到当前 Profile。</item>
    /// </list>
    /// </para>
    /// </summary>
    public async Task<bool> ApplyTempClassPlanAsync()
    {
        try
        {
            if (!File.Exists(ManagementTempClassPlanPath))
            {
                return false;
            }

            var json = await File.ReadAllTextAsync(ManagementTempClassPlanPath);
            if (string.IsNullOrWhiteSpace(json))
            {
                Logger.LogWarning("集控临时调课课表文件为空，跳过应用");
                return false;
            }

            var profileService = IAppHost.TryGetService<IProfileService>();
            if (profileService == null)
            {
                Logger.LogWarning("ProfileService 不可用，无法应用集控临时调课课表");
                return false;
            }

            var profile = profileService.Profile;
            ClassPlan? overlayCp = null;
            Guid? sourceClassPlanId = null;

            // 用 JsonDocument 判断载荷格式，兼容旧包装格式与班主任端整个档案格式
            using (var doc = System.Text.Json.JsonDocument.Parse(json))
            {
                if (doc.RootElement.TryGetProperty("ClassPlan", out _))
                {
                    // 旧格式：{ ClassPlan: ClassPlan, SourceClassPlanId: ..., EnableDate: ... }
                    var payload = System.Text.Json.JsonSerializer.Deserialize<TempClassPlanPayload>(json, JsonHelperOptions);
                    if (payload?.ClassPlan != null)
                    {
                        overlayCp = payload.ClassPlan;
                        sourceClassPlanId = payload.SourceClassPlanId;
                    }
                }
                else if (doc.RootElement.TryGetProperty("ClassPlans", out _))
                {
                    // 新格式：班主任端 saveTemp 保存的整个课表档案
                    var archive = System.Text.Json.JsonSerializer.Deserialize<TempClassPlanArchivePayload>(json, JsonHelperOptions);
                    if (archive?.ClassPlans != null && archive.ClassPlans.Count > 0)
                    {
                        // 计算今天星期几（1=周一，7=周日，与 TimeRule.WeekDay 一致）
                        var todayWeekDay = DateTime.Today.DayOfWeek == DayOfWeek.Sunday
                            ? 7
                            : (int)DateTime.Today.DayOfWeek;

                        // 优先找 TimeRule.WeekDay == 今天 的 ClassPlan
                        foreach (var kvp in archive.ClassPlans)
                        {
                            if (kvp.Value.TimeRule != null && kvp.Value.TimeRule.WeekDay == todayWeekDay)
                            {
                                overlayCp = kvp.Value;
                                sourceClassPlanId = kvp.Key;
                                break;
                            }
                        }

                        // 如果没找到今天的，取档案中第一个非空 ClassPlan
                        if (overlayCp == null)
                        {
                            var first = archive.ClassPlans.FirstOrDefault(k => k.Value != null);
                            overlayCp = first.Value;
                            sourceClassPlanId = first.Key;
                        }

                        // 同步档案中的 Subjects 和 TimeLayouts 到当前 Profile
                        // （班主任端可能同时修改了科目/时间表，overlay ClassPlan 依赖这些引用）
                        // 这些 ObservableDictionary 的修改会触发 CollectionChanged → UI 刷新，必须在 UI 线程执行
                        if (archive.TimeLayouts != null)
                        {
                            var timeLayoutsToSync = archive.TimeLayouts;
                            var timeLayoutsCount = timeLayoutsToSync.Count;
                            await Dispatcher.UIThread.InvokeAsync(() =>
                            {
                                foreach (var t in timeLayoutsToSync)
                                {
                                    profile.TimeLayouts[t.Key] = t.Value;
                                }
                            });
                            Logger.LogInformation("已同步临时课表档案中的 {Count} 个时间表", timeLayoutsCount);
                        }
                        if (archive.Subjects != null)
                        {
                            var subjectsToSync = archive.Subjects;
                            var subjectsCount = subjectsToSync.Count;
                            await Dispatcher.UIThread.InvokeAsync(() =>
                            {
                                foreach (var s in subjectsToSync)
                                {
                                    profile.Subjects[s.Key] = s.Value;
                                }
                            });
                            Logger.LogInformation("已同步临时课表档案中的 {Count} 个科目", subjectsCount);
                        }
                    }
                }
            }

            if (overlayCp == null)
            {
                Logger.LogWarning("集控临时调课课表载荷无效或未找到可用的 ClassPlan，跳过应用");
                AddLog("拉取临时课表", $"版本 {Versions.TempClassPlanVersion}", false, "未找到可用的 ClassPlan");
                return false;
            }

            AddLog("拉取临时课表", $"版本 {Versions.TempClassPlanVersion}", true);

            var today = DateTime.Today;

            // 标记为临时层课表
            overlayCp.IsOverlay = true;
            if (overlayCp.OverlaySourceId == null)
            {
                overlayCp.OverlaySourceId = sourceClassPlanId;
            }
            overlayCp.OverlaySetupTime = today;

            // 生成新的 GUID 作为临时层课表 ID（若未提供）
            var overlayId = Guid.NewGuid();
            // 生成一个不与现有 ClassPlans 冲突的 overlayId（读取 ClassPlans.Keys 在后台线程是安全的，不触发 CollectionChanged）
            while (profile.ClassPlans.ContainsKey(overlayId))
            {
                overlayId = Guid.NewGuid();
            }

            // 清理已有的临时层课表（避免叠加）并注入新的临时层课表。
            // 这些操作修改 ObservableDictionary/StyledProperty，会触发 CollectionChanged → UI 刷新，
            // 必须在 UI 线程执行，否则会抛出 "Call from invalid thread" 异常。
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                // 清理已有的临时层课表（避免叠加）
                if (profile.OverlayClassPlanId.HasValue &&
                    profile.ClassPlans.ContainsKey(profile.OverlayClassPlanId.Value))
                {
                    var existingOverlay = profile.ClassPlans[profile.OverlayClassPlanId.Value];
                    if (existingOverlay.IsOverlay)
                    {
                        profile.ClassPlans.Remove(profile.OverlayClassPlanId.Value);
                        Logger.LogInformation("已清理旧的临时层课表：{}", profile.OverlayClassPlanId.Value);
                    }
                }

                // 注入新的临时层课表
                profile.ClassPlans[overlayId] = overlayCp;
                profile.IsOverlayClassPlanEnabled = true;
                profile.OverlayClassPlanId = overlayId;
                profile.OrderedSchedules[today] = new OrderedSchedule
                {
                    ClassPlanId = overlayId
                };
            });

            Logger.LogInformation("已应用集控临时调课课表到临时层：{OverlayId}（源课表：{SourceId}）",
                overlayId, overlayCp.OverlaySourceId);
            AddLog("应用临时课表", $"源课表: {overlayCp.OverlaySourceId}", true);
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "应用集控临时调课课表失败");
            AddLog("应用临时课表", "", false, ex.Message);
            return false;
        }
    }

    /// <summary>
    /// 清除当前档案中的临时层课表（Overlay），用于班主任端"恢复原课表"后同步清除客户端 overlay。
    /// <para>
    /// <see cref="ApplyTempClassPlanAsync"/> 在设置 overlay 时会覆盖 <c>OrderedSchedules[today]</c> 指向 overlayId，
    /// 因此清除 overlay 后必须恢复为指向原来的非 overlay 课表，否则客户端将无课表显示。
    /// 恢复策略：优先使用 overlay 的 <see cref="ClassPlan.OverlaySourceId"/>；若不可用，则根据今天星期几
    /// （<see cref="TimeRule.WeekDay"/>）在 <c>profile.ClassPlans</c> 中查找匹配的非 overlay ClassPlan。
    /// </para>
    /// </summary>
    private async Task ClearTempClassPlanOverlayAsync()
    {
        try
        {
            var profileService = IAppHost.TryGetService<IProfileService>();
            if (profileService == null) return;
            var profile = profileService.Profile;

            var oldOverlayId = profile.OverlayClassPlanId;
            var today = DateTime.Today;

            // 修改 ObservableDictionary/StyledProperty 会触发 CollectionChanged → UI 刷新，
            // 必须在 UI 线程执行，否则会抛出 "Call from invalid thread" 异常。
            // 恢复 OrderedSchedules 需要查找 profile.ClassPlans，一并放到 UI 线程里执行以确保安全。
            Guid? restoreClassPlanId = null;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                // 在删除 overlay 之前，先尝试确定要恢复的原课表 ID。
                // 优先使用 overlay 的 OverlaySourceId（如果它在 profile.ClassPlans 中存在且非 overlay）；
                // 否则根据今天的 WeekDay 在 profile.ClassPlans 中查找匹配的非 overlay ClassPlan。
                if (oldOverlayId.HasValue &&
                    profile.ClassPlans.TryGetValue(oldOverlayId.Value, out var overlayCpToRemove))
                {
                    if (overlayCpToRemove.OverlaySourceId.HasValue &&
                        profile.ClassPlans.TryGetValue(overlayCpToRemove.OverlaySourceId.Value, out var sourceCp) &&
                        !sourceCp.IsOverlay)
                    {
                        restoreClassPlanId = overlayCpToRemove.OverlaySourceId;
                    }
                }

                if (restoreClassPlanId == null)
                {
                    var todayWeekDay = today.DayOfWeek == DayOfWeek.Sunday
                        ? 7
                        : (int)today.DayOfWeek;
                    foreach (var kvp in profile.ClassPlans)
                    {
                        if (!kvp.Value.IsOverlay &&
                            kvp.Value.TimeRule != null &&
                            kvp.Value.TimeRule.WeekDay == todayWeekDay)
                        {
                            restoreClassPlanId = kvp.Key;
                            break;
                        }
                    }
                }

                // 判断今天的 OrderedSchedule 是否被 overlay 覆盖（只有被覆盖时才需要恢复）。
                var hadOverlayToday = oldOverlayId.HasValue &&
                    profile.OrderedSchedules.ContainsKey(today) &&
                    profile.OrderedSchedules[today].ClassPlanId == oldOverlayId.Value;

                // 清除 overlay 课表
                if (oldOverlayId.HasValue &&
                    profile.ClassPlans.ContainsKey(oldOverlayId.Value))
                {
                    var existingOverlay = profile.ClassPlans[oldOverlayId.Value];
                    if (existingOverlay.IsOverlay)
                    {
                        profile.ClassPlans.Remove(oldOverlayId.Value);
                        Logger.LogInformation("已清除临时层课表：{}", oldOverlayId.Value);
                    }
                }
                profile.IsOverlayClassPlanEnabled = false;
                profile.OverlayClassPlanId = null;

                // 恢复今天的 OrderedSchedule 指向原课表（而非保留指向已删除的 overlay 或直接删除）。
                if (hadOverlayToday)
                {
                    if (restoreClassPlanId.HasValue)
                    {
                        profile.OrderedSchedules[today] = new OrderedSchedule
                        {
                            ClassPlanId = restoreClassPlanId.Value
                        };
                        Logger.LogInformation("已恢复今天的 OrderedSchedule 指向原课表：{RestoreId}", restoreClassPlanId.Value);
                    }
                    else
                    {
                        // 找不到可恢复的原课表，移除今天的 OrderedSchedule 以避免指向已删除的 overlay
                        profile.OrderedSchedules.Remove(today);
                        Logger.LogWarning("未找到可恢复的原课表，已移除今天的 OrderedSchedule");
                    }
                }
            });

            // 删除本地缓存文件（文件 IO 不需要在 UI 线程执行）
            if (File.Exists(ManagementTempClassPlanPath))
            {
                File.Delete(ManagementTempClassPlanPath);
            }

            AddLog("清除临时课表", $"overlay: {oldOverlayId}, 恢复: {restoreClassPlanId}", true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "清除临时层课表失败");
            AddLog("清除临时课表", "", false, ex.Message);
        }
    }

    /// <summary>
    /// 向 <see cref="_managementLogs"/> 追加一条操作日志，并裁剪到 <see cref="MaxLogEntries"/> 条以内。
    /// </summary>
    private void AddLog(string action, string detail, bool success, string? error = null)
    {
        _managementLogs.Enqueue(new ManagementLogEntry
        {
            Timestamp = DateTime.Now,
            Action = action,
            Detail = detail,
            Success = success,
            ErrorMessage = error
        });
        while (_managementLogs.Count > MaxLogEntries)
        {
            _managementLogs.TryDequeue(out _);
        }
    }

    /// <summary>
    /// 上报客户端当前状态（当前课程/课间、时间段、剩余时间）到集控后端。
    /// <para>
    /// 由定时刷新任务每 5 秒调用一次。失败时仅记录警告，不影响集控主流程。
    /// </para>
    /// </summary>
    private async Task ReportDeviceStatusAsync()
    {
        try
        {
            if (!IsManagementEnabled) return;

            var profileService = IAppHost.TryGetService<IProfileService>();
            if (profileService == null) return;

            var profile = profileService.Profile;
            var now = DateTime.Now;

            // 获取当前课程状态
            var currentLesson = GetCurrentLessonInfo(profile, now);

            // 客户端时间偏移（秒）：每5秒随设备状态上报，确保班主任端能及时看到最新值
            // （ReportCurrentProfileAsync 仅在 _hasReported 为 false 时上报一次，无法反映后续修改）
            var settingsServiceForOffset = IAppHost.TryGetService<SettingsService>();
            var status = new
            {
                cuid = Persist.ClientUniqueId.ToString(),
                currentActivity = currentLesson?.ActivityName ?? "空闲",
                activityType = currentLesson?.ActivityType ?? "idle", // "class"=上课, "break"=课间, "idle"=空闲
                subjectName = currentLesson?.SubjectName ?? "",
                startTime = currentLesson?.StartTime?.ToString("O"),
                endTime = currentLesson?.EndTime?.ToString("O"),
                remainingSeconds = currentLesson?.RemainingSeconds ?? 0,
                currentTime = now.ToString("O"),
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                timeOffsetSeconds = settingsServiceForOffset?.Settings.TimeOffsetSeconds ?? 0
            };

            var json = System.Text.Json.JsonSerializer.Serialize(status, JsonHelperOptions);
            var baseUrl = GetManagementBaseUrl();
            if (string.IsNullOrEmpty(baseUrl))
            {
                return;
            }

            var url = $"{baseUrl}/api/teacher/device-status/{Persist.ClientUniqueId}";
            using var httpClient = new HttpClient();
            using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            var response = await httpClient.PutAsync(url, content);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "上报设备状态失败");
        }
    }

    /// <summary>
    /// 从 Profile 中根据当前时间计算正在进行的课程或课间状态。
    /// <para>
    /// 课表选取优先级：临时层课表（overlay）→ 今天的 OrderedSchedule → 按 TimeRule.WeekDay 匹配今天。
    /// 时间段判断：遍历 TimeLayout.Layouts，找到当前时间落在 [StartTime, EndTime) 内的 item。
    /// </para>
    /// </summary>
    /// <returns>当前课程/课间信息；不在任何时间段时返回 null（表示空闲）。</returns>
    private CurrentLessonInfo? GetCurrentLessonInfo(Profile profile, DateTime now)
    {
        // 1. 确定当前激活的 ClassPlan
        ClassPlan? activePlan = null;

        // 优先使用临时层课表（overlay）
        if (profile.IsOverlayClassPlanEnabled &&
            profile.OverlayClassPlanId.HasValue &&
            profile.ClassPlans.TryGetValue(profile.OverlayClassPlanId.Value, out var overlayPlan))
        {
            activePlan = overlayPlan;
        }

        // 其次查找今天的 OrderedSchedule
        if (activePlan == null &&
            profile.OrderedSchedules.TryGetValue(now.Date, out var ordered) &&
            profile.ClassPlans.TryGetValue(ordered.ClassPlanId, out var orderedPlan))
        {
            activePlan = orderedPlan;
        }

        // 最后按 TimeRule.WeekDay 匹配今天（1=周一，7=周日）
        if (activePlan == null)
        {
            var todayWeekDay = now.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)now.DayOfWeek;
            foreach (var kvp in profile.ClassPlans)
            {
                if (kvp.Value.TimeRule != null && kvp.Value.TimeRule.WeekDay == todayWeekDay)
                {
                    activePlan = kvp.Value;
                    break;
                }
            }
        }

        if (activePlan == null) return null;

        var timeLayout = activePlan.TimeLayout;
        if (timeLayout == null) return null;

        var nowTimeOfDay = now.TimeOfDay;

        // 2. 遍历 Layouts 找到当前时间段
        TimeLayoutItem? currentItem = null;
        foreach (var item in timeLayout.Layouts)
        {
            if (nowTimeOfDay >= item.StartTime && nowTimeOfDay < item.EndTime)
            {
                currentItem = item;
                break;
            }
        }

        if (currentItem == null)
        {
            // 不在任何时间段内，返回空闲
            return null;
        }

        var today = now.Date;
        var startTime = today + currentItem.StartTime;
        var endTime = today + currentItem.EndTime;
        var remainingSeconds = (int)(endTime - now).TotalSeconds;

        // 3. 根据时间段类型返回相应信息
        if (currentItem.TimeType == 0)
        {
            // 上课：从 ClassPlan.Classes 中找到对应的 ClassInfo，再查 Subject.Name
            var subjectName = GetSubjectNameForTimeItem(profile, activePlan, timeLayout, currentItem);
            return new CurrentLessonInfo
            {
                ActivityName = string.IsNullOrEmpty(subjectName) ? "课程" : subjectName,
                ActivityType = "class",
                SubjectName = subjectName,
                StartTime = startTime,
                EndTime = endTime,
                RemainingSeconds = remainingSeconds
            };
        }

        if (currentItem.TimeType == 1)
        {
            // 课间：找下节课的科目名
            var nextClassItem = timeLayout.Layouts
                .Where(x => x.TimeType == 0 && x.StartTime >= currentItem.EndTime)
                .OrderBy(x => x.StartTime)
                .FirstOrDefault();

            var nextSubjectName = nextClassItem != null
                ? GetSubjectNameForTimeItem(profile, activePlan, timeLayout, nextClassItem)
                : "";

            return new CurrentLessonInfo
            {
                ActivityName = "课间",
                ActivityType = "break",
                SubjectName = nextSubjectName,
                StartTime = startTime,
                EndTime = endTime,
                RemainingSeconds = remainingSeconds
            };
        }

        // 其他类型（分割线=2、行动=3）视为空闲
        return null;
    }

    /// <summary>
    /// 根据 TimeLayoutItem 在 ClassPlan.Classes 中的索引查找对应的科目名称。
    /// </summary>
    private static string GetSubjectNameForTimeItem(Profile profile, ClassPlan classPlan, TimeLayout timeLayout, TimeLayoutItem timeItem)
    {
        var validItems = timeLayout.Layouts.Where(x => x.TimeType == 0).ToList();
        var classIndex = validItems.IndexOf(timeItem);
        if (classIndex < 0 || classIndex >= classPlan.Classes.Count) return "";
        var classInfo = classPlan.Classes[classIndex];
        return profile.Subjects.TryGetValue(classInfo.SubjectId, out var subject) ? subject.Name : "";
    }

    /// <summary>
    /// 当前课程/课间状态信息（用于设备状态上报）。
    /// </summary>
    private record CurrentLessonInfo
    {
        /// <summary>活动名称（课程名/"课间"/"空闲"）。</summary>
        public string ActivityName { get; init; } = "";
        /// <summary>活动类型："class"=上课, "break"=课间, "idle"=空闲。</summary>
        public string ActivityType { get; init; } = "idle";
        /// <summary>科目名称（课间时为下节课的科目名）。</summary>
        public string SubjectName { get; init; } = "";
        /// <summary>时间段开始时间（本地时间）。</summary>
        public DateTime? StartTime { get; init; }
        /// <summary>时间段结束时间（本地时间）。</summary>
        public DateTime? EndTime { get; init; }
        /// <summary>时间段剩余秒数。</summary>
        public int? RemainingSeconds { get; init; }
    }

    private static readonly System.Text.Json.JsonSerializerOptions JsonHelperOptions = new(System.Text.Json.JsonSerializerDefaults.Web);

    /// <summary>
    /// 集控下发的临时调课课表载荷（旧包装格式）。
    /// </summary>
    private class TempClassPlanPayload
    {
        /// <summary>
        /// 临时层课表对象。
        /// </summary>
        public ClassPlan? ClassPlan { get; set; }

        /// <summary>
        /// 源课表 ID（临时层所基于的原始课表）。
        /// </summary>
        public Guid? SourceClassPlanId { get; set; }

        /// <summary>
        /// 启用日期（可选，默认为今天）。
        /// </summary>
        public DateTime? EnableDate { get; set; }
    }

    /// <summary>
    /// 班主任端 saveTemp 保存的整个课表档案载荷（新格式）。
    /// <para>
    /// 班主任端 ClassPlanEdit.vue 的 saveTemp 直接将 editData（整个课表档案）保存为
    /// tempclassplan content，顶层字段为 ClassPlans/Subjects/TimeLayouts 等字典，
    /// 而非旧格式的单个 ClassPlan 包装。客户端需从中提取"今天"对应的 ClassPlan 作为 overlay。
    /// </para>
    /// </summary>
    private class TempClassPlanArchivePayload
    {
        /// <summary>课表字典，键为 GUID。</summary>
        public Dictionary<Guid, ClassPlan>? ClassPlans { get; set; }

        /// <summary>科目字典，键为 GUID。</summary>
        public Dictionary<Guid, Subject>? Subjects { get; set; }

        /// <summary>时间表字典，键为 GUID。</summary>
        public Dictionary<Guid, TimeLayout>? TimeLayouts { get; set; }

        /// <summary>课表组字典，键为 GUID。</summary>
        public Dictionary<Guid, ClassPlanGroup>? ClassPlanGroups { get; set; }

        /// <summary>当前选中的课表组 ID。</summary>
        public Guid? SelectedClassPlanGroupId { get; set; }

        /// <summary>档案名称。</summary>
        public string? Name { get; set; }
    }

    public void SaveSettings()
    {
        Logger.LogInformation("保存集控配置");
        SaveConfig(ManagementVersionsPath, Versions);
    }

    public async Task JoinManagementAsync(ManagementSettings settings)
    {
        if (IsManagementEnabled)
            throw new Exception("无法在已加入管理后再次加入管理。");
        var mf = new ManagementManifest();
        switch (settings.ManagementServerKind)
        {
            case ManagementServerKind.Serverless:
                var manifestUrl = settings.ManifestUrlTemplate
                    .Replace("{cuid}", Persist.ClientUniqueId.ToString())
                    .Replace("{id}", settings.ClassIdentity ?? "");
                mf = await WebRequestHelper.Default.GetJson<ManagementManifest>(new Uri(manifestUrl));
                break;
            case ManagementServerKind.ManagementServer:
                var connection = new ManagementServerConnection(settings, Persist.ClientUniqueId, true);
                mf = await connection.RegisterAsync();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(settings.ManagementServerKind), "无效的服务器类型。");
        }

        if (mf.CoreVersion.Major != IAppHost.CoreVersion.Major)
        {
            throw new InvalidOperationException($"集控服务核心版本（{mf.CoreVersion}）与应用当前核心版本不兼容（{IAppHost.CoreVersion}）");
        }

        var dialog = new TaskDialog
        {
            Title = "ClassPixelPro",
            SubHeader = "加入集控",
            Content = $"确定要加入组织 {mf.OrganizationName} 的管理吗？",
            Buttons =
            {
                TaskDialogButton.CancelButton,
                new TaskDialogButton("加入", true)
                {
                    IsDefault = true
                }
            },
            XamlRoot = AppBase.Current.GetRootWindow(),
        };

        var result = await dialog.ShowAsync();
        if (result?.Equals(true) != true)
            return;

        var w = CopyObject(settings);
        w.IsManagementEnabled = true;
        // 清空旧的配置
        foreach (var i in new List<string>([ManagementManifestPath, ManagementPolicyPath, ManagementVersionsPath, ProfileService.ManagementClassPlanPath, ProfileService.ManagementSubjectsPath, ProfileService.ManagementTimeLayoutPath, Path.Combine(CommonDirectories.AppRootFolderPath, "./Profiles/_management-profile.json"), ManagementCredentialsPath]).Where(File.Exists))
        {
            File.Delete(i);
            if (File.Exists(i + ".bak"))
            {
                File.Delete(i + ".bak");
            }
        }
        SaveConfig(ManagementSettingsPath, w);
        await CommonTaskDialogs.ShowDialog("已加入集控", $"已加入组织 {mf.OrganizationName} 的管理。应用将重启以应用更改。");
        await SetupManagement();

        AppBase.Current.Restart();
    }

    public async Task ExitManagementAsync()
    {
        if (!IsManagementEnabled)
            throw new Exception("无法在没有加入集控的情况下退出集控。");
        var authResult = await AuthorizeByLevel(CredentialConfig.ExitManagementAuthorizeLevel);
        if (!authResult)
        {
            throw new Exception("认证失败。");
        }
        if (!Policy.AllowExitManagement)
            throw new Exception("您的组织不允许您退出集控。");
        
        var dialog = new TaskDialog
        {
            Title = "ClassPixelPro",
            SubHeader = "退出集控",
            Content = $"确定要退出组织 {Manifest.OrganizationName} 的管理吗？",
            Buttons =
            {
                TaskDialogButton.CancelButton,
                new TaskDialogButton("退出", true)
                {
                    IsDefault = true
                }
            },
            XamlRoot = AppBase.Current.GetRootWindow(),
        };

        var result = await dialog.ShowAsync();
        if (result?.Equals(true) != true)
            return;

        // 通知后端设备离线
        try
        {
            using var httpClient = new HttpClient();
            var baseUrl = Manifest.PolicySource.Value?.Split("/serverless/")[0] ?? "";
            var offlineUri = new Uri($"{baseUrl}/serverless/offline?cuid={Persist.ClientUniqueId}&id={Settings.ClassIdentity ?? ""}");
            _ = await httpClient.PostAsync(offlineUri, null);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "发送离线通知失败（不阻塞退出流程）");
        }

        Settings.IsManagementEnabled = false;
        SaveConfig(ManagementSettingsPath, Settings);

        await CommonTaskDialogs.ShowDialog("已退出集控", $"已退出组织 {Manifest.OrganizationName} 的管理。应用将重启以应用更改。");

        AppBase.Current.Restart();
    }

    public async Task<bool> AuthorizeByLevel(AuthorizeLevel level)
    {
        if (string.IsNullOrWhiteSpace(CredentialConfig.AdminCredential) &&
            string.IsNullOrWhiteSpace(CredentialConfig.UserCredential))  // 没有设置任何认证方式
        {
            return true;
        }

        var fallbackCredential =
            new List<string>([CredentialConfig.AdminCredential, CredentialConfig.UserCredential]).First(x =>
                !string.IsNullOrWhiteSpace(x));
        var result = level switch
        {
            AuthorizeLevel.None => true,
            AuthorizeLevel.User => await AuthorizeService.AuthenticateAsync(Fallback(CredentialConfig.UserCredential)),
            AuthorizeLevel.Admin => await AuthorizeService.AuthenticateAsync(Fallback(CredentialConfig.AdminCredential)),
            _ => throw new ArgumentOutOfRangeException(nameof(level), level, null)
        };

        if (level != AuthorizeLevel.None && IsManagementEnabled && Connection is ManagementServerConnection connection)
        {
            connection.LogAuditEvent(result ? AuditEvents.AuthorizeSuccess : AuditEvents.AuthorizeFailed, new AuthorizeEvent()
            {
                Level = (int)level
            });
        }

        return result;

        string Fallback(string c) => string.IsNullOrWhiteSpace(c) ? fallbackCredential : c;
    }
}