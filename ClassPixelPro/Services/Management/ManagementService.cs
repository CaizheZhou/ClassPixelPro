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
using ClassPixelPro.Shared.Abstraction.Services;
using ClassPixelPro.Shared.Enums;
using ClassPixelPro.Shared.Models.Management;
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

    public ManagementClientPersistConfig Persist { get;}

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
            return;

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

    public async Task<bool> SetupManagement()
    {
        if (!IsManagementEnabled)
        {
            SetupLocalManagement();
            return true;
        }

        Logger.LogInformation("正在初始化集控");

        // 启动定时刷新（每 5 分钟）
        _ = Task.Run(async () =>
        {
            while (IsManagementEnabled)
            {
                await Task.Delay(TimeSpan.FromMinutes(5));
                if (!IsManagementEnabled) break;
                try
                {
                    await ReloadManagementAsync();
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "定时刷新集控配置失败");
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
            // 保存版本号
            SaveSettings();
        }
        catch (Exception e)
        {
            Manifest = oldManifest;
            Policy = oldPolicy;
            CredentialConfig = oldCredential;
            Versions = oldVersions;
            Logger.LogError(e, "拉取集控清单与策略失败");
        }

        return true;
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
                mf = await WebRequestHelper.Default.GetJson<ManagementManifest>(new Uri(settings.ManifestUrlTemplate));
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