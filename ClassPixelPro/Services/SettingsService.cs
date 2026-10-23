using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;
using ClassPixelPro.Core;
using ClassPixelPro.Core.Abstractions.Services.Management;
using ClassPixelPro.Shared.Helpers;
using ClassPixelPro.Models;
using Microsoft.Extensions.Logging;
using System.Text.Json.Serialization;
using Avalonia.Controls;
using ClassPixelPro.Core.Abstractions.Services.SpeechService;
using ClassPixelPro.Helpers;
using ClassPixelPro.Platforms.Abstraction;
using ClassPixelPro.Services.Automation.Actions;
using ClassPixelPro.Services.Management;
using ClassPixelPro.Shared.Protobuf.AuditEvent;
using ClassPixelPro.Shared.Protobuf.Enum;

namespace ClassPixelPro.Services;

public class SettingsService(ILogger<SettingsService> Logger, IManagementService ManagementService)
    : INotifyPropertyChanged
{
    private Settings _settings = new();

    private bool SkipMigration { get; set; } = false;

    public Settings Settings
    {
        get => _settings;
        set => SetField(ref _settings, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool WillMigrateProfileTrustedState { get; set; } = false;

    public bool WillMigrateInitTutorial { get; set; } = false;

    /// <summary>
    /// Settings.PropertyChanged 的命名处理器。
    /// <para>
    /// 修复 P1-13：原实现使用 lambda 匿名委托订阅 PropertyChanged，
    /// 无法在替换 Settings 对象时取消旧对象的订阅，导致旧对象仍能触发 SettingsChanged → SaveSettings，
    /// 而新对象的修改反而无法落盘。改为命名委托后，可在替换前 -= 取消旧订阅、替换后 += 订阅新对象。
    /// </para>
    /// </summary>
    private void SettingsOnPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        SettingsChanged(args.PropertyName!);
    }

    /// <summary>
    /// 安全替换 Settings 对象：先取消旧对象的 PropertyChanged 订阅，再赋值，再订阅新对象。
    /// <para>
    /// 修复 P1-13：所有外部替换 SettingsService.Settings 的调用方都应改用此方法，
    /// 避免漏挂/重复挂 PropertyChanged 导致设置变更不落盘或重复落盘。
    /// </para>
    /// </summary>
    /// <param name="newSettings">新的 Settings 实例。</param>
    public void ReplaceSettings(Settings newSettings)
    {
        // 取消旧对象订阅（即使旧对象为 null 也不会抛异常，-= 对未订阅的委托是 no-op）
        _settings.PropertyChanged -= SettingsOnPropertyChanged;
        _settings = newSettings;
        _settings.PropertyChanged += SettingsOnPropertyChanged;
        // 触发 SettingsService 自身 PropertyChanged，让绑定到 SettingsService.Settings 的 UI 刷新
        OnPropertyChanged(nameof(Settings));
    }

    /// <summary>
    /// 应用集控下发的应用设置（appsettings / 默认设置）。
    /// <para>
    /// 服务端快照是某台设备上传的完整 Settings，里面带着设备本地的运行时状态。
    /// 直接整份替换会让 <see cref="Settings.AppLastStartedTime"/> 永远停留在快照生成的那天，
    /// 于是每次启动都判定「超过 N 天没启动」，反复弹出「翻新 ClassPixelPro」提示。
    /// 因此替换前保留本机运行时字段，服务端只覆盖配置类字段。
    /// </para>
    /// </summary>
    public void ApplyManagedSettings(Settings incoming)
    {
        incoming.AppLastStartedTime = Settings.AppLastStartedTime;
        incoming.LeftRefreshingToastCounts = Settings.LeftRefreshingToastCounts;
        incoming.RefreshingToastIsOnboardingGuide = Settings.RefreshingToastIsOnboardingGuide;
        incoming.ShowRefreshingToastOnNextStart = Settings.ShowRefreshingToastOnNextStart;
        ReplaceSettings(incoming);
    }

    private async Task LoadManagementSettingsAsync()
    {
        if (!ManagementService.Manifest.DefaultSettingsSource.IsNewerAndNotNull(ManagementService.Versions
                .DefaultSettingsVersion) ||
            ManagementService.Connection == null)
        {
            return;
        }

        Logger.LogInformation("拉取集控默认设置");
        var url = ManagementService.Manifest.DefaultSettingsSource.Value!;
        var settings = await ManagementService.Connection.GetJsonAsync<Settings>(url);
        // 修复 P1-13：使用 ReplaceSettings 统一处理订阅/取消订阅，避免漏挂 PropertyChanged。
        ApplyManagedSettings(settings);
        Logger.LogTrace("拉取集控默认设置成功！");
    }

    public async Task LoadSettingsAsync()
    {
        if (Design.IsDesignMode)
        {
            // 设计时不加载设置
            Logger.LogInformation("检测到应用以设计模式运行，跳过配置加载");
            return;
        }
        try
        {
            if (!File.Exists(Path.Combine(CommonDirectories.AppRootFolderPath, "Settings.json")))
            {
                SkipMigration = true; // 如果是新的配置文件，那么就需要跳过迁移。
                Logger.LogInformation("配置文件不存在，跳过加载。");
            }
            else
            {
                Logger.LogInformation("加载配置文件。");
                var r = ConfigureFileHelper.LoadConfig<Settings>(Path.Combine(CommonDirectories.AppRootFolderPath, "Settings.json"));
                // 修复 P1-13：使用 ReplaceSettings 统一处理订阅/取消订阅。
                ReplaceSettings(r);
            }

            // 当还没有初始化应用且启用集控时，从集控拉取设置。
            if (ManagementService.IsManagementEnabled && !Settings.IsWelcomeWindowShowed)
            {
                await LoadManagementSettingsAsync();
            }

            // 集控下发的应用设置（appsettings）优先级高于本地 Settings.json。
            // ReloadManagementAsync 已经将集控 appsettings 下载到 ManagementAppSettingsPath，
            // 但此处先从本地 Settings.json 加载会覆盖它。这里在集控启用时用 ManagementAppSettingsPath
            // 覆盖本地设置，确保集控下发的应用设置生效。
            if (ManagementService.IsManagementEnabled && File.Exists(Management.ManagementService.ManagementAppSettingsPath))
            {
                try
                {
                    var mgmtAppSettings = ConfigureFileHelper.LoadConfig<Settings>(Management.ManagementService.ManagementAppSettingsPath);
                    // 修复 P1-13：使用 ReplaceSettings 统一处理订阅/取消订阅。
                    ApplyManagedSettings(mgmtAppSettings);
                    Logger.LogInformation("已应用集控下发的应用设置（appsettings）");
                }
                catch (Exception exAppSettings)
                {
                    Logger.LogWarning(exAppSettings, "应用集控下发的应用设置失败，回退到本地设置");
                }
            }

            ISpeechService.GlobalSettings = Settings;
        }
        catch (Exception ex)
        {
            SkipMigration = true;
            Logger.LogError(ex, "配置文件加载失败。");
            // ignored
        }

        if (Settings is { IsSystemSpeechSystemExist: false, SpeechSource: 0 })
        {
            Settings.SpeechSource = 1;
        }

        var requiresRestarting = false;
        if (!SkipMigration)
        {
            MigrateSettings(out requiresRestarting);
        }

        Settings.LastAppVersion = Assembly.GetExecutingAssembly().GetName().Version!;

        if (requiresRestarting)
        {
            AppBase.Current.Restart();
        }
    }

    private T TryGetDictionaryValue<T>(IDictionary<string, object?> dictionary, string key, T? fallbackValue = null)
        where T : class
    {
        var fallback = fallbackValue ?? Activator.CreateInstance<T>();
        var r = Settings.MiniInfoProviderSettings.TryGetValue(key.ToLower(), out var o);
        if (o is JsonElement o1)
        {
            return o1.Deserialize<T>() ?? fallback;
        }

        return (T?)Settings.MiniInfoProviderSettings[key.ToLower()] ?? fallback;
    }

#pragma warning disable CS0612 // 类型或成员已过时
    private void MigrateSettings(out bool requiresRestarting)
    {
        requiresRestarting = false;
        if (Assembly.GetExecutingAssembly().GetName().Version < Version.Parse("1.4.1.0"))
        {
            return;
        }
        
        if (Settings.LastAppVersion < Version.Parse("2.0.0.2") 
            || (Settings.LastAppVersion >= Version.Parse("2.0.1.0") && Settings.LastAppVersion < Version.Parse("2.0.1.2")) )
        {
            if (PlatformServices.DesktopService.IsUrlSchemeRegistered)
            {
                PlatformServices.DesktopService.IsUrlSchemeRegistered = true;
            }
            Logger.LogInformation("成功迁移了 2.0.0.2 以前的设置。");
        }
        
        if (Settings.LastAppVersion < Version.Parse("2.0.3.0") )
        {
            WillMigrateInitTutorial = true;
            Logger.LogInformation("成功迁移了 2.0.3.0 以前的设置。");
        }

        if (Settings.LastAppVersion < Version.Parse("2.0.3.3"))
        {
            if (PlatformServices.DesktopService.IsAutoStartEnabled)
            {
                PlatformServices.DesktopService.IsAutoStartEnabled = false;
                PlatformServices.DesktopService.IsAutoStartEnabled = true;
            }
            Logger.LogInformation("成功迁移了 2.0.3.3 以前的自启动快捷方式。");
        }
        
    }
#pragma warning restore CS0612 // 类型或成员已过时

    public void SaveSettings(string note = "")
    {
        if (Design.IsDesignMode)
        {
            return;
        }
        Logger.LogInformation(note == "" ? "写入配置文件。" : "写入配置文件：{}", note);
        ConfigureFileHelper.SaveConfig(Path.Combine(CommonDirectories.AppRootFolderPath, "Settings.json"), Settings);
    }


    public const BindingFlags SettingsPropertiesFlags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;

    /// <summary>
    /// 添加应用设置叠层。
    /// </summary>
    /// <param name="guid">设置叠层 Guid。</param>
    /// <param name="name">设置属性名称。</param>
    /// <param name="value">要设置的值。</param>
    /// <returns>是否成功。失败会抛出。</returns>
    public bool AddSettingsOverlay(Guid guid, string name, object? value)
    {
        var key = guid.ToString();
        var isPropertyOverlayNotNull = Settings.SettingsOverlays.TryGetValue(name, out var propertyOverlay);
        if (isPropertyOverlayNotNull)
            propertyOverlay!.Remove(key);

        var info = typeof(Settings).GetProperty(name, SettingsPropertiesFlags);
        if (info == null)
        {
            throw new KeyNotFoundException($"应用设置中找不到属性“{name}”。");
            return false;
        }

        if (!isPropertyOverlayNotNull || propertyOverlay!.Contains(key))
        {
            propertyOverlay = [];
            var sourceValue = info.GetValue(Settings);
            if (value == sourceValue)
                return true;
            propertyOverlay["@"] = sourceValue;
        }

        info.SetValue(Settings, value);
        propertyOverlay[key] = value;
        Settings.SettingsOverlays[name] = propertyOverlay;
        return true;
    }

    // PropertyInfo 缓存。
    private static readonly ConcurrentDictionary<string, PropertyInfo?> _propertyCache = new();

    public static PropertyInfo? GetPropertyInfoByName(string name) =>
        string.IsNullOrEmpty(name) ? null :
            _propertyCache.GetOrAdd(name,
                key => typeof(Settings).GetProperty(key, SettingsPropertiesFlags));

    /// <summary>
    /// 删除应用设置叠层。
    /// </summary>
    /// <param name="guid">设置叠层 Guid。</param>
    /// <param name="name">设置属性名。</param>
    /// <returns>是否进行了删除。删除出错会抛出。</returns>
    public bool RemoveSettingsOverlay(Guid guid, string name)
    {
        if (!Settings.SettingsOverlays.TryGetValue(name, out var propertyOverlay))
            return false;

        var lengthBefore = propertyOverlay.Count;
        propertyOverlay.Remove(guid.ToString());
        var length = propertyOverlay.Count;
        if (length == lengthBefore)
            return false;

        var info = typeof(Settings).GetProperty(name, SettingsPropertiesFlags);
        if (info == null)
            return false;

        var last = propertyOverlay[length - 1]; // propertyOverlay 至少存在一项。
        if (last is JsonElement json)
            last = json.Deserialize(info.PropertyType);
        info.SetValue(Settings, last);

        if (length > 1)
            Settings.SettingsOverlays[name] = propertyOverlay;
        else
            Settings.SettingsOverlays.Remove(name);
        return true;
    }

    void SettingsChanged(string propertyName)
    {
        if (propertyName != nameof(Settings.SettingsOverlays))
        {
            Settings.SettingsOverlays.Remove(propertyName);
        }

        if (typeof(Settings).GetProperty(propertyName, SettingsPropertiesFlags)?
                            .GetCustomAttribute<JsonIgnoreAttribute>() != null)
            return;

        SaveSettings(propertyName);
        if (ManagementService is { IsManagementEnabled: true, Connection: ManagementServerConnection connection })
        {
            connection.LogAuditEvent(AuditEvents.AppSettingsUpdated, new AppSettingsUpdated()
            {
                PropertyName = propertyName
            });
        }
    }

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
}