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
            Run("netsh", "advfirewall firewall add rule name=\"" + RuleName + "\" " +
                         "dir=in action=allow protocol=TCP localport=" + port + " " +
                         "profile=any program=\"" + exePath + "\" enable=yes");
            log?.Invoke($"已更新防火墙入站规则：{exePath} 端口 {port}");
        }
        catch (Exception ex)
        {
            log?.Invoke("更新防火墙规则失败（需要管理员权限）：" + ex.Message +
                        "。局域网直连可能不可用，可在大屏电脑上以管理员身份执行：" +
                        $"netsh advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow protocol=TCP localport={port} profile=any");
        }
    }

    private static void Run(string fileName, string arguments)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var proc = Process.Start(psi);
        if (proc == null) return;
        // 必须读干输出，否则进程可能卡在缓冲区满
        proc.StandardOutput.ReadToEnd();
        proc.StandardError.ReadToEnd();
        proc.WaitForExit(15000);
    }
}
