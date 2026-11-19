using System.Diagnostics;
using System.Reflection;

#if Platforms_Windows
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;
#endif

#if Platforms_Linux
using Mono.Unix;
#endif

// 获取对应平台的可执行文件路径
string executableName;
if (OperatingSystem.IsWindows())
{
    executableName = "ClassPixelPro.Desktop.exe";
} else if (OperatingSystem.IsLinux())
{
    executableName = "ClassPixelPro.Desktop";
}
else
{
    ShowError("ClassPixelPro 正在不支持的平台上运行，无法继续。");
    return 1;
}

var root = Path.GetFullPath(Path.GetDirectoryName(Environment.ProcessPath) ?? "");
var installation = Directory.GetDirectories(root)
    .Where(x => Path.GetFileName(x).StartsWith("app") &&
                // app-current 是 Launcher 维护的固定路径 junction，不是真实版本目录
                !Path.GetFileName(x).Equals("app-current", StringComparison.OrdinalIgnoreCase) &&
                !(File.Exists(Path.Combine(x, ".destroy")) || File.Exists(Path.Combine(x, ".partial"))) &&
                File.Exists(Path.Combine(x, executableName)))
    // 必须优先选带 .current 标记的目录：更新后会同时保留一个 .prev 旧版本目录
    // 供恢复模式回滚，若这里按升序取「没有 .current 的」，就会启动成旧版本。
    .OrderByDescending(x => File.Exists(Path.Combine(x, ".current")) ? 1 : 0)
    .ThenByDescending(x =>
    {
        var filename = Path.GetFileName(x);
        var split = filename.Split('-');
        if (split.Length <= 1)
        {
            return new Version();
        }

        return Version.TryParse(split[1], out var version) ? version : new Version();
    })
    .ThenByDescending(x =>
    {
        var filename = Path.GetFileName(x);
        var split = filename.Split('-');
        if (split.Length <= 2)
        {
            return 0;
        }

        return int.TryParse(split[2], out var n) ? n : 0;
    })
    .FirstOrDefault();

if (installation == null)
{
    ShowError("找不到有效的 ClassPixelPro 版本，可能是安装已损坏。请在 https://class.imzcz.cn/#/download 重新下载并安装 ClassPixelPro。");
    return 1;
}

var exePath = Path.Combine(Path.Combine(installation, executableName));

// Windows 防火墙按程序文件路径绑定规则：直接启动 app-{版本}-{n}/ 下的主程序，
// 每次更新后目录名变化 → 防火墙规则失效 → 每次都要重新放行弹窗。
// 这里维护一个固定路径 junction（app-current → 当前版本目录），主程序路径恒定，
// 防火墙只需放行一次，后续更新不再弹窗。junction 是目录联接，普通用户无需管理员即可创建。
#if Platforms_Windows
if (OperatingSystem.IsWindows())
{
    var junction = Path.Combine(root, "app-current");
    try
    {
        // 删除旧的联接（只删链接本身，不影响目标目录）
        if (Directory.Exists(junction) || File.Exists(junction))
        {
            var attr = File.GetAttributes(junction);
            Directory.Delete(junction, false);
        }
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c mklink /J \"{junction}\" \"{installation}\"",
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            UseShellExecute = false
        };
        using var mk = Process.Start(psi);
        mk?.WaitForExit(10000);
        if (Directory.Exists(Path.Combine(junction, executableName)))
        {
            exePath = Path.Combine(junction, executableName);
        }
    }
    catch (Exception e)
    {
        Console.Error.WriteLine($"无法创建 app-current 联接，改用版本目录直接启动（不影响运行，仅防火墙每次更新需重新放行）：{e.Message}");
    }
}
#endif

// 自动添加可执行权限，防止出现无法运行的问题 https://github.com/ClassPixelPro/ClassPixelPro/issues/1212
#if Platforms_Linux
try
{
    var unixFileInfo = new UnixFileInfo(exePath);
    unixFileInfo.FileAccessPermissions |= FileAccessPermissions.UserExecute | FileAccessPermissions.GroupExecute |
                                      FileAccessPermissions.OtherExecute;
}
catch (Exception e)
{
    Console.Error.WriteLine($"无法设置可执行文件 {exePath} 的执行权限，可能导致应用无法启动：{e}");
}

#endif

var startInfo = new ProcessStartInfo()
{
    FileName = exePath,
    WorkingDirectory = root
};
foreach (var i in args)
{
    startInfo.ArgumentList.Add(i);
}
startInfo.EnvironmentVariables["ClassPixelPro_PackageRoot"] = root;  // 防止因环境变量已设置导致启动器崩溃
Process.Start(startInfo);

return 0;

void ShowError(string message)
{
#if Platforms_Windows
    PInvoke.MessageBox(HWND.Null, message,
        "ClassPixelPro", MESSAGEBOX_STYLE.MB_APPLMODAL | MESSAGEBOX_STYLE.MB_ICONSTOP);
#else
    Console.Error.WriteLine(message);
#endif
}