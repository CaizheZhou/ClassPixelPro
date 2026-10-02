using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace ClassPixelPro.Services;

/// <summary>
/// Windows 防火墙入站规则维护（仅 Windows）。
/// <para>
/// 远程监控的 HTTP/WS 服务监听 0.0.0.0，局域网其他设备（教师手机）要直连必须有一条
/// 允许入站的防火墙规则。而 Windows 在首次弹窗放行时，是按「当时的可执行文件完整路径」
/// 记录的规则；自动更新会把应用解包到新的 app-&lt;版本&gt; 目录，路径一变规则立刻失效——
/// 表现为「只有开着软件的这台电脑能用浏览器连，手机打开就退回 Tunnel 中转」，
/// 且不��何报错。这里在每次启动 HTTP 服务时用当前可执行文件路径重建规则。
/// </para>
/// <para>需要管理员权限；无权限时静默失败并在日志中给出人工处理指引，不影响功能（可退回 Tunnel）。</para>
/// </summary>
public static class FirewallHelper
{
    private const string RuleName = "ClassPixelPro 远程监控";

    /// <summary>
    /// 为当前可执行文件路径确保存在一条允许入站的 TCP 规则，并清理指向旧版本目录的失效规则。
    /// </summary>
    public static void EnsureInboundRule(int port, Action<string>? log = null)
    {
        if (!OperatingSystem.IsWindows()) return;

        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath)) return;

        try
        {
            // 1) 清理指向其它路径的同名旧规则（多版本共存时只保留当前版本的）
            Run("netsh", "advfirewall firewall delete rule name=\"" + RuleName + "\"");
            // 2) 为当前 exe 路径建立入站放行规则（所有配置文件类型，机房/教室网络常被标为「公用」）
            var (exitCode, output) = Run("netsh", "advfirewall firewall add rule name=\"" + RuleName + "\" " +
                         "dir=in action=allow protocol=TCP localport=" + port + " " +
                         "profile=any program=\"" + exePath + "\" enable=yes");
            // netsh 权限不足时返回非 0 退出码且不抛异常，必须显式检查，否则会误报成功
            if (exitCode == 0)
            {
                log?.Invoke($"已更新防火墙入站规则：{exePath} 端口 {port}");
            }
            else
            {
                log?.Invoke($"防火墙入站规则更新失败（netsh 退出码 {exitCode}）：{output.Trim()}。" +
                            "局域网直连可能被拦截，远程监控将退回 Tunnel 中转。" +
                            "请以管理员身份运行一次本程序，或手动执行：" +
                            $"netsh advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow " +
                            $"protocol=TCP localport={port} profile=any");
            }
        }
        catch (Exception ex)
        {
            log?.Invoke("更新防火墙规则失败（需要管理员权限）：" + ex.Message +
                        "。局域网直连可能不可用，可在大屏电脑上以管理员身份执行：" +
                        $"netsh advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow protocol=TCP localport={port} profile=any");
        }
    }

    /// <summary>
    /// 版本更新后首次启动时自动确保放行规则存在。
    /// <para>
    /// 规则按「程序」放行而不是按端口：监控服务每次启动都随机换端口，写死端口的规则
    /// 下一次启动就失效。检测到当前 exe 路径没有放行规则时自动重建——已提权则静默执行，
    /// 未提原则弹一次 UAC；每个版本只提示一次，避免反复打扰。
    /// </para>
    /// </summary>
    public static bool EnsureInboundRuleOnUpdate(Action<string>? log = null)
    {
        if (!OperatingSystem.IsWindows()) return true;

        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath)) return false;

        // 已有覆盖当前 exe 的规则 → 无需任何操作（绝大多数启动都走这里，不打扰用户）
        if (HasRuleForPath(exePath)) return true;

        // 本版本已询问过（用户拒绝或已自行处理）→ 不再重复弹 UAC
        if (WasPromptedForCurrentVersion())
        {
            log?.Invoke("本次更新尚未放行防火墙（此前已询问过），局域网直连可能被拦截；" +
                        "可在「设置 → 远程监控 → 放行防火墙」中手动处理。");
            return false;
        }
        MarkPromptedForCurrentVersion();

        if (IsElevated())
        {
            // 已以管理员身份运行（如机房常用的管理员启动方式）→ 静默完成，无需打扰
            var (exitCode, _) = ApplyRule(exePath);
            if (exitCode == 0)
            {
                log?.Invoke("检测到版本更新，已自动重建防火墙入站放行规则。");
                return true;
            }
        }

        // 未提权：自动弹一次 UAC 完成放行
        var ok = EnsureInboundRuleElevated(0, out var message);
        log?.Invoke(ok ? $"检测到版本更新，已自动放行防火墙。{message}" : $"自动放行防火墙未完成：{message}");
        return ok;
    }

    /// <summary>当前 exe 路径是否已有入站放行规则（读取 netsh 规则无需管理员）。</summary>
    private static bool HasRuleForPath(string exePath)
    {
        var (_, output) = Run("netsh", "advfirewall firewall show rule name=\"" + RuleName + "\"");
        return output.Contains(exePath, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsElevated()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(identity)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>以程序（不限端口）为范围重建入站放行规则，返回 (退出码, 输出)。</summary>
    private static (int ExitCode, string Output) ApplyRule(string exePath)
    {
        Run("netsh", "advfirewall firewall delete rule name=\"" + RuleName + "\"");
        return Run("netsh", "advfirewall firewall add rule name=\"" + RuleName + "\" " +
                            "dir=in action=allow program=\"" + exePath + "\" enable=yes profile=any");
    }

    // 标记文件：记录「本次版本已询问过防火墙放行」，避免每次启动都弹 UAC
    private static string PromptMarkerPath => Path.Combine(
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory,
        "..", "..", "data", "Cache", "firewall-prompted.txt");

    private static bool WasPromptedForCurrentVersion()
    {
        try
        {
            var marker = PromptMarkerPath;
            return File.Exists(marker) &&
                   File.ReadAllText(marker).Trim() == CurrentVersionTag();
        }
        catch
        {
            return false;
        }
    }

    private static void MarkPromptedForCurrentVersion()
    {
        try
        {
            var marker = PromptMarkerPath;
            var dir = Path.GetDirectoryName(marker);
            if (dir != null) Directory.CreateDirectory(dir);
            File.WriteAllText(marker, CurrentVersionTag());
        }
        catch
        {
            // 标记失败只会导致下次多问一次，不影响功能
        }
    }

    private static string CurrentVersionTag()
    {
        try
        {
            return typeof(FirewallHelper).Assembly.GetName().Version?.ToString() ?? "unknown";
        }
        catch
        {
            return "unknown";
        }
    }

    /// <summary>
    /// 以管理员权限重建入站规则（触发一次 UAC 提示）。
    /// 供设置页「放行防火墙」按钮在用户明确点击时调用。
    /// </summary>
    public static bool EnsureInboundRuleElevated(int port, out string message)
    {
        if (!OperatingSystem.IsWindows())
        {
            message = "仅 Windows 需要该操作";
            return false;
        }

        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
        {
            message = "无法确定当前程序路径";
            return false;
        }

        // port <= 0 时按程序放行（不限端口）：监控端口每次启动随机变化，按端口的规则下次就失效
        var portPart = port > 0 ? $"protocol=TCP localport={port} " : "";
        var args = "netsh advfirewall firewall delete rule name=\"" + RuleName + "\" & "
                   + "netsh advfirewall firewall add rule name=\"" + RuleName + "\" dir=in action=allow "
                   + $"{portPart}profile=any program=\"{exePath}\" enable=yes";
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", "/c " + args)
            {
                UseShellExecute = true,
                Verb = "runas",
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null)
            {
                message = "无法启动提权进程";
                return false;
            }
            proc.WaitForExit(30000);
            message = proc.ExitCode == 0
                ? "防火墙放行规则已更新，局域网直连应可用"
                : "操作未完成（可能取消了 UAC 提示）";
            return proc.ExitCode == 0;
        }
        catch (Exception ex)
        {
            message = "提权失败：" + ex.Message;
            return false;
        }
    }

    /// <summary>执行外部命令，返回 (退出码, 合并输出)。</summary>
    private static (int ExitCode, string Output) Run(string fileName, string arguments)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var proc = Process.Start(psi);
        if (proc == null) return (-1, "无法启动 " + fileName);
        // 必须读干输出，否则进程可能卡在缓冲区满
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(15000);
        return (proc.ExitCode, (stdout + " " + stderr).Trim());
    }
}
