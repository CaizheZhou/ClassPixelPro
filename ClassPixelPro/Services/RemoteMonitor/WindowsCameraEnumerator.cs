using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;

namespace ClassPixelPro.Services.RemoteMonitor;

/// <summary>
/// 摄像头设备枚举：取系统中的真实摄像头数量与友好名称。
/// <para>
/// 选型说明（均为本项目目标机实测结论）：
/// DirectShow 的设备枚举器依赖 COM 类注册，在该机上返回 REGDB_E_CLASSNOTREG(0x80040154)，
/// 完全不可用；SetupAPI 枚举 Camera 设备类在该机返回 0 个。WMI 查询 Win32_PnPEntity
/// 则稳定返回真实结果（如 1 个 "USB2.0 HD UVC WebCam"），故采用 WMI。
/// </para>
/// <para>
/// 另外，OpenCV 的 DSHOW 后端在本机打开不存在的索引时不会失败而是回落到默认设备，
/// 导致「按索引探测」把 1 个摄像头报成 9 个——所以列表数量必须来自系统枚举而非探测。
/// </para>
/// </summary>
public static class WindowsCameraEnumerator
{
    /// <summary>
    /// 枚举系统中当前存在的摄像头友好名称；失败返回空列表，调用方需回退。
    /// </summary>
    public static List<string> GetCameraNames()
    {
        var names = new List<string>();
        if (!OperatingSystem.IsWindows()) return names;

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name FROM Win32_PnPEntity WHERE PNPClass='Camera'");
            foreach (var obj in searcher.Get())
            {
                var name = obj["Name"]?.ToString()?.Trim();
                if (!string.IsNullOrWhiteSpace(name) && !names.Contains(name))
                {
                    names.Add(name);
                }
            }
        }
        catch
        {
            // WMI 不可用（服务被禁用等）时返回空列表，由调用方回退
        }

        return names;
    }
}
