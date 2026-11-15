using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClassPixelPro.Core.Models.Plugin;
using Mono.Cecil;

namespace ClassPixelPro.Helpers;

/// <summary>
/// 插件程序集兼容重写器。
/// <para/>
/// ClassPixelPro 是 ClassIsland 的二次开发版本，宿主程序集与命名空间已由 ClassIsland.* 统一改名为
/// ClassPixelPro.*；而插件市场上游的社区插件是针对 ClassIsland.* 程序集与命名空间编译的。
/// 插件加载时（<see cref="PluginLoadContext"/>）引用 ClassIsland.* 程序集会解析失败，
/// 导致所有上游插件「安装成功但无法加载」。
/// <para/>
/// 本类在插件包解压安装后，用 Mono.Cecil 就地重写插件目录内程序集的
/// AssemblyRef（ClassIsland.* → ClassPixelPro.*）与 TypeRef 命名空间（ClassIsland.* → ClassPixelPro.*），
/// 使上游插件绑定到本宿主的对应程序集与类型。
/// </summary>
public static class PluginAssemblyCompat
{
    private const string OldPrefix = "ClassIsland";
    private const string NewPrefix = "ClassPixelPro";
    /// <summary>兼容重写标记。v1（".ilcompat"）是 2.3.6 的产物——当时存在 TypeRef 未改写的顺序
    /// bug，其标记下的插件程序集是半改写残次品，因此 2.3.6.2 起改用 v2 标记并强制重写所有 v1 产物。</summary>
    private const string MarkerFileName = ".ilcompat-v2";
    private const string LegacyMarkerFileName = ".ilcompat";

    /// <summary>
    /// 宿主程序集的精确映射白名单（ClassIsland.* → ClassPixelPro.*）。
    /// 只重写命中的引用——插件自身的程序集/命名空间（如 ClassIsland.AISmartClass 这种以
    /// ClassIsland 开头的插件命名空间）、插件之间的引用一律不动，避免错误改写指向不存在的类型。
    /// </summary>
    private static readonly Dictionary<string, string> HostAssemblyMap = new(StringComparer.Ordinal)
    {
        ["ClassIsland.Core"] = "ClassPixelPro.Core",
        ["ClassIsland.Shared"] = "ClassPixelPro.Shared",
        ["ClassIsland.Shared.IPC"] = "ClassPixelPro.Shared.IPC",
        ["ClassIsland.Desktop"] = "ClassPixelPro.Desktop",
        ["ClassIsland"] = "ClassPixelPro",
        ["ClassIsland.Platforms.Windows"] = "ClassPixelPro.Platforms.Windows",
        ["ClassIsland.Platforms.Linux"] = "ClassPixelPro.Platforms.Linux",
        ["ClassIsland.Platforms.MacOs"] = "ClassPixelPro.Platforms.MacOs",
        ["ClassIsland.Platforms.Abstractions"] = "ClassPixelPro.Platforms.Abstractions",
        ["ClassIsland.PluginSdk"] = "ClassPixelPro.PluginSdk",
    };

    /// <summary>
    /// 确保插件目录内的程序集已完成兼容重写（幂等：目录带 .ilcompat 标记时跳过）。
    /// </summary>
    public static void EnsureRewritten(string pluginFolder, PluginManifest manifest)
    {
        try
        {
            var marker = Path.Combine(pluginFolder, MarkerFileName);
            var legacyMarker = Path.Combine(pluginFolder, LegacyMarkerFileName);
            if (File.Exists(marker))
                return;
            // 2.3.6 的 v1 标记产物是半改写残次品（TypeRef 未改写），删除旧标记强制重新重写
            if (File.Exists(legacyMarker))
                File.Delete(legacyMarker);
            var (rewritten, failed) = RewriteFolder(pluginFolder);
            File.WriteAllText(marker,
                $"ClassIsland->ClassPixelPro IL 兼容重写：成功 {rewritten} 个程序集，失败 {failed} 个（{DateTime.Now:yyyy-MM-dd HH:mm:ss}）");
            if (rewritten > 0)
                Console.WriteLine($"插件兼容重写完成: {manifest.Name}({manifest.Id})，重写 {rewritten} 个程序集。");
        }
        catch (Exception e)
        {
            // 重写失败不阻断插件加载流程：针对 ClassPixelPro SDK 编写的插件无需重写
            Console.WriteLine($"插件兼容重写失败（忽略）: {manifest.Id} - {e.Message}");
        }
    }

    /// <summary>
    /// 重写目录下的全部托管程序集。返回 (成功数, 失败数)。
    /// </summary>
    private static (int rewritten, int failed) RewriteFolder(string folder)
    {
        int rewritten = 0, failed = 0;
        foreach (var dll in Directory.EnumerateFiles(folder, "*.dll", SearchOption.AllDirectories))
        {
            // 非托管本机库目录无需处理
            if (dll.Contains($"{Path.DirectorySeparatorChar}runtimes{Path.DirectorySeparatorChar}") &&
                dll.Contains($"{Path.DirectorySeparatorChar}native{Path.DirectorySeparatorChar}"))
                continue;
            try
            {
                if (RewriteAssembly(dll))
                    rewritten++;
            }
            catch (Exception)
            {
                failed++;
            }
        }
        return (rewritten, failed);
    }

    /// <summary>
    /// 重写单个程序集。若其中不存在任何 ClassIsland 引用则不做任何修改（返回 false）。
    /// </summary>
    private static bool RewriteAssembly(string path)
    {
        // 读入内存：Cecil 从文件读取后直接写回同一路径会因文件流未释放而 IOException
        var readerParams = new ReaderParameters { ReadSymbols = false };
        using var asm = AssemblyDefinition.ReadAssembly(new MemoryStream(File.ReadAllBytes(path)), readerParams);

        var needsRewrite =
            asm.MainModule.AssemblyReferences.Any(r => r.Name != null && HostAssemblyMap.ContainsKey(r.Name));
        if (!needsRewrite)
            return false;

        // 1) 类型引用：必须在程序集引用改名**之前**处理——TypeRef 的 ResolutionScope 此刻还是
        //    旧名（ClassIsland.Core），用白名单旧名匹配 scope。若先改程序集名再查 scope，
        //    白名单（旧名键）永不命中，命名空间将全部残留导致类型解析失败（2.3.6 的顺序 bug）。
        //    仅重写 ResolutionScope 指向宿主白名单程序集的 TypeRef；插件自身命名空间
        //    （namespace ClassIsland.* 开头的插件代码）与插件间引用一律不动。
        foreach (var tr in asm.MainModule.GetTypeReferences())
        {
            if (tr.Namespace == null || !tr.Namespace.StartsWith(OldPrefix, StringComparison.Ordinal))
                continue;
            var scopeName = ResolveScopeAssemblyName(tr);
            if (scopeName != null && HostAssemblyMap.ContainsKey(scopeName))
                tr.Namespace = NewPrefix + tr.Namespace[OldPrefix.Length..];
        }

        // 2) 程序集引用：精确命中白名单才改（ClassIsland.Core -> ClassPixelPro.Core 等）。
        //    插件自身程序集（如 ClassIsland.AISmartClass）与第三方引用不在白名单，保持原样。
        foreach (var ar in asm.MainModule.AssemblyReferences.ToList())
        {
            if (ar.Name != null && HostAssemblyMap.TryGetValue(ar.Name, out var mapped))
                ar.Name = mapped;
        }

        // 3) 写回（不带符号），并移除旧的调试符号文件避免符号不匹配
        var writerParams = new WriterParameters { WriteSymbols = false };
        asm.Write(path, writerParams);
        var pdb = Path.ChangeExtension(path, ".pdb");
        if (File.Exists(pdb))
            File.Delete(pdb);
        return true;
    }

    /// <summary>
    /// 解析 TypeRef 最终指向的程序集名（嵌套类型沿 DeclaringType 递归向上）。
    /// 返回 null 表示指向当前模块自身（插件程序集内部的引用）。
    /// </summary>
    private static string? ResolveScopeAssemblyName(TypeReference tr)
    {
        TypeReference current = tr;
        for (int i = 0; i < 32 && current != null; i++)
        {
            switch (current.Scope)
            {
                case AssemblyNameReference anr:
                    return anr.Name;
                case TypeReference outer:
                    current = outer;   // 嵌套类型：继续向上
                    continue;
                default:
                    return null;       // ModuleReference / null（自身模块）
            }
        }
        return null;
    }
}
