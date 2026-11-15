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
    private const string MarkerFileName = ".ilcompat";

    /// <summary>
    /// 确保插件目录内的程序集已完成兼容重写（幂等：目录带 .ilcompat 标记时跳过）。
    /// </summary>
    public static void EnsureRewritten(string pluginFolder, PluginManifest manifest)
    {
        try
        {
            var marker = Path.Combine(pluginFolder, MarkerFileName);
            if (File.Exists(marker))
                return;
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
            asm.MainModule.AssemblyReferences.Any(r => r.Name != null && r.Name.StartsWith(OldPrefix, StringComparison.Ordinal));
        if (!needsRewrite)
            return false;

        // 1) 程序集引用：ClassIsland.Core -> ClassPixelPro.Core、ClassIsland -> ClassPixelPro（前缀整体替换）
        foreach (var ar in asm.MainModule.AssemblyReferences.ToList())
        {
            if (ar.Name != null && ar.Name.StartsWith(OldPrefix, StringComparison.Ordinal))
                ar.Name = NewPrefix + ar.Name[OldPrefix.Length..];
        }

        // 2) 类型引用命名空间：ClassIsland.Core.Services.* -> ClassPixelPro.Core.Services.*
        //    （只改 TypeRef 表，不动插件自身的 TypeDef；嵌套类型的 Namespace 即其外层命名空间）
        foreach (var tr in asm.MainModule.GetTypeReferences())
        {
            if (tr.Namespace != null && tr.Namespace.StartsWith(OldPrefix, StringComparison.Ordinal))
                tr.Namespace = NewPrefix + tr.Namespace[OldPrefix.Length..];
        }

        // 3) 写回（不带符号），并移除旧的调试符号文件避免符号不匹配
        var writerParams = new WriterParameters { WriteSymbols = false };
        asm.Write(path, writerParams);
        var pdb = Path.ChangeExtension(path, ".pdb");
        if (File.Exists(pdb))
            File.Delete(pdb);
        return true;
    }
}
