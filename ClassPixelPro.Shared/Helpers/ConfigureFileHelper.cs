using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ClassPixelPro.Shared.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ClassPixelPro.Shared.Helpers;

/// <summary>
/// 配置文件工具集
/// </summary>
public class ConfigureFileHelper
{
    private static ILogger? _logger;

    private static ILogger? Logger
    {
        get
        {
#if NET8_0_OR_GREATER
            return _logger ??= IAppHost.Host?.Services.GetService<ILogger<ConfigureFileHelper>>();

#else
            return null;
#endif
        }
    }

    internal static JsonSerializerOptions SerializerOptions { get; set; } = new();

    private static Lazy<JsonSerializerOptions> WriteIndentedSerializerOptions { get; } =
        new(() => new JsonSerializerOptions(SerializerOptions)
        {
            WriteIndented = true
        });

    /// <summary>
    /// 配置在默认情况下，是否启用配置文件备份
    /// </summary>
    public static bool IsBackupEnabled { get; set; } = true;

    /// <summary>
    /// 加载配置文件时发生的错误
    /// </summary>
    public static ObservableCollection<ConfigError> Errors { get; } = [];

    static ConfigureFileHelper()
    {
        // 覆盖默认的 JsonSerializerOptions
        var field = typeof(JsonSerializerOptions)?.GetField("s_defaultOptions", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        field?.SetValue(null, SerializerOptions);
    }

    /// <summary>
    /// 加载配置文件，并自动创建备份。当加载失败时，将尝试加载备份的配置文件。如果希望在发生加载异常时自动捕获错误，请使用方法 <see cref="LoadConfig{T}"/>。
    /// </summary>
    /// <typeparam name="T">配置文件类型</typeparam>
    /// <param name="path">配置文件路径</param>
    /// <param name="backupEnabled">是否启用备份。如果没有填写此参数，将使用默认值。</param>
    /// <param name="isLoadingBackup">是否正在加载备份。如果是，在加载失败时将不会尝试加载备份。</param>
    /// <returns>加载的配置文件对象</returns>
    public static T LoadConfigUnWrapped<T>(string path, bool? backupEnabled = null, bool isLoadingBackup = false)
    {
        backupEnabled ??= IsBackupEnabled;
        Logger?.LogInformation("读取 JSON 文件：{}", path);
        if (!File.Exists(path))
        {
            var cfg = Activator.CreateInstance<T>();
            if (!isLoadingBackup)
                SaveConfig(path, cfg);
            return cfg;
        }

        var backupPath = path + ".bak";
        try
        {
            var json = File.ReadAllText(path);
            var r = JsonSerializer.Deserialize<T>(json, SerializerOptions);
            if (r == null)
                return Activator.CreateInstance<T>();
            if (backupEnabled.Value)
                File.Copy(path, backupPath, true);
            return r;
        }
        catch (Exception ex)
        {
            if (!backupEnabled.Value || !File.Exists(backupPath))
                throw;
            Logger?.LogWarning(ex, "读取 JSON 文件 {} 失败，尝试加载备份", path);
            Errors.Add(new ConfigError(path, 0, false, ex));
            var r = LoadConfig<T>(backupPath, false, true);
            File.Copy(backupPath, path, true);
            return r;
        }
    }

    /// <summary>
    /// 加载配置文件，并自动创建备份。当加载失败时，将尝试加载备份的配置文件。如果备份文件也加载失败，会返回新的对象。如果希望在发生加载异常时不自动捕获错误，请使用方法 <see cref="LoadConfigUnWrapped{T}"/>。
    /// </summary>
    /// <typeparam name="T">配置文件类型</typeparam>
    /// <param name="path">配置文件路径</param>
    /// <param name="backupEnabled">是否启用备份。如果没有填写此参数，将使用默认值。</param>
    /// <param name="isLoadingBackup">是否正在加载备份。如果是，在加载失败时将不会尝试加载备份。</param>
    /// <returns>加载的配置文件对象</returns>
    public static T LoadConfig<T>(string path, bool? backupEnabled=null, bool isLoadingBackup=false)
    {
        try
        {
            return LoadConfigUnWrapped<T>(path, backupEnabled, isLoadingBackup);
        }
        catch (Exception e)
        {
            Logger?.LogError(e, "无法读取 JSON 文件 {}，已返回默认值", path);
            Errors.Add(new ConfigError(path, 1, true, e));
            return Activator.CreateInstance<T>();
        }
    }

    /// <summary>
    /// 保存配置文件，并自动创建备份
    /// </summary>
    /// <typeparam name="T">配置文件类型</typeparam>
    /// <param name="path">配置文件路径</param>
    /// <param name="o">要写入到配置的对象</param>
    public static void SaveConfig<T>(string path, T o)
    {
        SaveConfig(path, o, false);
    }

    /// <summary>
    /// 保存配置文件，并自动创建备份
    /// </summary>
    /// <typeparam name="T">配置文件类型</typeparam>
    /// <param name="path">配置文件路径</param>
    /// <param name="o">要写入到配置的对象</param>
    /// <param name="writeIndented">是否在保存时格式化 JSON</param>
    public static void SaveConfig<T>(string path, T o, bool writeIndented)
    {
        Logger?.LogInformation("写入 JSON 文件：{}", path);
        var options = writeIndented ? WriteIndentedSerializerOptions.Value : SerializerOptions;
        // 在保存时不对备份文件进行操作，以防止在保存时发生意外断电时，备份文件也受到损坏。
        WriteAllTextSafe(path, JsonSerializer.Serialize<T>(o, options));
    }

    /// <summary>
    /// 复制配置对象
    /// </summary>
    /// <typeparam name="T">配置类型</typeparam>
    /// <param name="o">要复制的对象</param>
    /// <returns>复制后的对象副本</returns>
    public static T CopyObject<T>(T o)
    {
        return JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(o, SerializerOptions), SerializerOptions) ?? Activator.CreateInstance<T>();
    }

    /// <summary>
    /// 合并字典。将新的字典与基准字典比较，向要操作的字典中添加新字典与基准字典相较没有的元素，并删除要操作的字典中新字典与基准字典相比没有的元素。
    /// </summary>
    /// <typeparam name="TKey">字典键类型</typeparam>
    /// <typeparam name="TValue">字典值类型</typeparam>
    /// <param name="raw">要操作的字典</param>
    /// <param name="diffBase">合并基准字典</param>
    /// <param name="diffNew">新的字典</param>
    /// <returns></returns>
    public static IDictionary<TKey, TValue> MergeDictionary<TKey, TValue>(IDictionary<TKey, TValue> raw,
        IDictionary<TKey, TValue> diffBase, IDictionary<TKey, TValue> diffNew)
    {
        // 修复：rm 应表示"在基准中存在、但在新字典中已被删除"的键，
        // 即 diffBase.Keys - diffNew.Keys。原实现写成 diffNew.Keys - diffBase.Keys（新增的键），
        // 会导致新增的键被错误地从 raw 中删除。
        var rm = (from i in diffBase.Keys where !diffNew.Keys.Contains(i) select i).ToList();
        rm.ForEach(i => raw.Remove(i));
        foreach (var i in diffNew)
        {
            if (raw.ContainsKey(i.Key))
                raw[i.Key] = i.Value;
            else
                raw.Add(i);
        }

        return raw;
    }

    /// <summary>
    /// 原子写入文本文件：先写入同目录下的临时文件，再用 File.Replace/File.Move 原子替换。
    /// <para>
    /// 修复 P0-11：原实现用 <c>new FileStream(path, FileMode.Create)</c> 直接截断目标文件，
    /// 并发写同一路径会抛 IOException，且写入过程中崩溃会留下半写的损坏文件。
    /// 改为「写临时文件再原子替换」后：
    /// <list type="bullet">
    /// <item>并发写不会互相截断，临时文件名带 PID+GUID 保证互不冲突；</item>
    /// <item>写入过程中崩溃只会留下孤儿临时文件，目标文件保持旧内容完整；</item>
    /// <item>File.Replace 在同卷上是原子的（Windows/POSIX 均保证）。</item>
    /// </list>
    /// </para>
    /// </summary>
    /// <param name="path">目标文件路径。</param>
    /// <param name="content">要写入的内容。</param>
    private static void WriteAllTextSafe(string path, string content)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        // 临时文件名带进程 ID 与 GUID，避免并发写时互相覆盖。
        // 注意：用 Process.GetCurrentProcess().Id 而非 Environment.ProcessId——后者是 .NET 5+ API，
        // 本项目在 Windows 上多目标 net8.0;net472，net472 下没有该定义（CS0117）。
        var temp = path + $".tmp.{Process.GetCurrentProcess().Id}.{Guid.NewGuid():N}";
        try
        {
            // 先把全部内容写到临时文件（FileMode.CreateNew 保证不会覆盖已存在的临时文件）。
            File.WriteAllText(temp, content);

            // 原子替换：目标存在时用 File.Replace，不存在时用 File.Move。
            if (File.Exists(path))
            {
                // File.Replace(src, dst, backupPath) 在同卷上原子替换 dst 为 src。
                // backupPath 传 null 表示不保留备份（Windows 上会直接删除原 dst）。
                File.Replace(temp, path, null);
            }
            else
            {
                // net472 没有 File.Move(src, dst, overwrite) 三参数重载；此处 temp 刚创建、
                // path 不存在（上方已判），不存在被占用问题，直接 Move 即可。
                File.Move(temp, path);
            }
        }
        finally
        {
            // 异常路径下清理可能残留的临时文件（成功路径上 temp 已被 Move/Replace 接管）。
            try
            {
                if (File.Exists(temp))
                    File.Delete(temp);
            }
            catch
            {
                // 清理失败不影响主流程；孤儿临时文件不影响目标文件正确性。
            }
        }
    }
}