using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace ClassPixelPro.Services.RemoteMonitor;

/// <summary>
/// 摄像头设备枚举（SetupAPI 设备枚举接口），获取系统中的真实摄像头与友好名称。
/// <para>
/// 为什么不用 DirectShow：DirectShow 的设备枚举器依赖 COM 类注册，在部分系统
/// （精简版/未安装多媒体组件的镜像、容器、部分虚拟机）上会返回
/// REGDB_E_CLASSNOTREG（0x80040154）而完全不可用——实测本项目的目标机就是这样，
/// 导致摄像头名称只能退化成 "Camera 0" 占位符。
/// </para>
/// <para>
/// SetupAPI 直接枚举系统的 Camera 设备类（GUID_DEVCLASS_CAMERA），是纯 Win32 调用，
/// 不依赖任何 COM 注册，在任何 Windows 上都可用；返回的数量就是真实摄像头数量
/// （此前按索引逐个探测 DSHOW 时，打开失败会回落到默认设备，导致 1 个摄像头被报成 9 个）。
/// </para>
/// </summary>
public static class WindowsCameraEnumerator
{
    // GUID_DEVCLASS_CAMERA = {CA3E8AB1-B4E3-4AE2-8255-61F13A0FB9F9}
    private static Guid DevClassCamera = new("CA3E8AB1-B4E3-4AE2-8255-61F13A0FB9F9");
    // DEVPKEY_Device_FriendlyName = {A45C254E-DF1C-4EFD-8020-67D146A850E0} 14
    private static Guid DevKeyFriendlyName = new("A45C254E-DF1C-4EFD-8020-67D146A850E0}");
    private const int DevPropTypeString = 31;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public int cbSize;
        public IntPtr ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr devInfoSet, int memberIndex, ref SP_DEVINFO_DATA devInfo);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceRegistryProperty(IntPtr devInfoSet, ref SP_DEVINFO_DATA devInfo,
        ref Guid property, ref uint propertyRegDataType, IntPtr propertyBuffer, uint propertyBufferSize, out uint requiredSize);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr devInfoSet);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, IntPtr enumerator, IntPtr hwnd, uint flags);

    private const uint DIGCF_PRESENT = 0x02;
    private const uint DIGCF_DEVICEINTERFACE = 0x10;
    private const int ERROR_INSUFFICIENT_BUFFER = 122;
    private const int ERROR_NO_MORE_ITEMS = 259;

    /// <summary>
    /// 枚举系统中当前存在的摄像头，返回友好名称（按系统枚举顺序）。
    /// 失败返回空列表，调用方需回退到索引占位名。
    /// </summary>
    public static List<string> GetCameraNames()
    {
        var names = new List<string>();
        if (!OperatingSystem.IsWindows()) return names;

        IntPtr devInfoSet = IntPtr.Zero;
        try
        {
            // 同时按"存在"和"有设备接口"两种方式查询，覆盖未启动驱动/未打开应用的摄像头
            foreach (var flags in new[] { DIGCF_PRESENT, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE })
            {
                devInfoSet = SetupDiGetClassDevsW(ref DevClassCamera, IntPtr.Zero, IntPtr.Zero, flags);
                if (devInfoSet == IntPtr.Zero || devInfoSet == new IntPtr(-1)) continue;

                var index = 0;
                while (true)
                {
                    var info = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
                    if (!SetupDiEnumDeviceInfo(devInfoSet, index, ref info))
                    {
                        var err = Marshal.GetLastWin32Error();
                        if (err == ERROR_NO_MORE_ITEMS) break;
                        break;
                    }
                    index++;

                    var name = ReadFriendlyName(devInfoSet, ref info);
                    if (!string.IsNullOrWhiteSpace(name) && !names.Contains(name))
                    {
                        names.Add(name);
                    }
                }
                if (names.Count > 0) break;
            }
        }
        catch
        {
            // 枚举失败返回空列表，由调用方回退
        }
        finally
        {
            if (devInfoSet != IntPtr.Zero && devInfoSet != new IntPtr(-1))
            {
                SetupDiDestroyDeviceInfoList(devInfoSet);
            }
        }

        return names;
    }

    private static string? ReadFriendlyName(IntPtr devInfoSet, ref SP_DEVINFO_DATA devInfo)
    {
        uint propType = 0, required = 0;
        var prop = DevKeyFriendlyName;
        // 先取所需长度
        SetupDiGetDeviceRegistryProperty(devInfoSet, ref devInfo, ref prop, ref propType, IntPtr.Zero, 0, out required);
        if (required == 0) return null;

        var buffer = Marshal.AllocHGlobal((int)required);
        try
        {
            if (!SetupDiGetDeviceRegistryProperty(devInfoSet, ref devInfo, ref prop, ref propType,
                    buffer, required, out required))
            {
                return null;
            }
            return Marshal.PtrToStringUni(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
