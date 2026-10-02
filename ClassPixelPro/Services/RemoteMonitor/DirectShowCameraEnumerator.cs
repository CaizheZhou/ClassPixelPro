using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace ClassPixelPro.Services.RemoteMonitor;

/// <summary>
/// Windows 摄像头设备枚举（DirectShow），获取真实设备名。
/// <para>
/// OpenCV 的 <c>VideoCapture(index)</c> 只能按索引打开，拿不到设备友好名
/// （如「Integrated Camera」「USB2.0 HD UVC WebCam」），只能拼出 "Camera 0" 这种占位名。
/// 这里通过 DirectShow 的设备枚举接口拿到友好名；<b>枚举顺序与 DSHOW 后端的索引顺序一致</b>，
/// 因此第 N 个名字正好对应 <c>VideoCapture(N, DSHOW)</c>，不会出现"名字对不上号"的问题。
/// </para>
/// <para>枚举失败（非 Windows、无 DirectShow、被策略禁用）时返回空列表，由调用方回退到索引占位名。</para>
/// </summary>
public static class DirectShowCameraEnumerator
{
    [DllImport("ole32.dll")]
    private static extern int CreateBindCtx(uint reserved, out IBindCtx ppbc);

    [ComImport, Guid("29840822-5B84-11D0-BD3B-00A0C911CE86"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirectShowDeviceEnumerator
    {
        [PreserveSig]
        int BindToMoniker([MarshalAs(UnmanagedType.LPWStr)] string szDeviceName, out IMoniker ppMoniker);

        [PreserveSig]
        int EnumDisplayDevices([MarshalAs(UnmanagedType.LPWStr)] string lpszDevice, uint iMoniker,
            [MarshalAs(UnmanagedType.LPArray)] IMoniker[] ppszDeviceMonikers, uint cFetch, out uint pcFetched);
    }

    [ComImport, Guid("00000001-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IBindCtx
    {
        [PreserveSig]
        int EnumMoniker(out IEnumMoniker monikers);

        [PreserveSig]
        int EnumObjects(out IEnumMoniker monikers, IntPtr lpszReserved);

        [PreserveSig]
        int GetRunningObjectTable(out IntPtr prot);

        [PreserveSig]
        int SetObjectMoniker(IMoniker moniker, IMoniker pmkToLeft);

        [PreserveSig]
        int GetObjectMoniker(out IMoniker moniker);

        [PreserveSig]
        int GetRunningObjectTableByName([MarshalAs(UnmanagedType.LPWStr)] string lpszName, out IntPtr prot);
    }

    [ComImport, Guid("00000102-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumMoniker
    {
        [PreserveSig]
        int Next(int celt, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] IMoniker[] rgelt, IntPtr pceltFetched);

        [PreserveSig]
        int Skip(int celt);

        [PreserveSig]
        int Reset();

        [PreserveSig]
        int Clone(out IEnumMoniker ppenum);
    }

    [ComImport, Guid("0000000f-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMoniker
    {
        [PreserveSig]
        int GetDisplayName(IBindCtx pbc, IntPtr lpszReserved, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);

        // 其余方法本程序不调用，保留槽位以维持 vtable 顺序
        [PreserveSig] int SetDisplayName(IBindCtx pbc, [MarshalAs(UnmanagedType.LPWStr)] string szDisplayName, IntPtr pchDisplayName, uint cchDisplayName);
        [PreserveSig] int LoadTime([MarshalAs(UnmanagedType.BStr)] string lpszMoniker);
        [PreserveSig] int GetStorage([MarshalAs(UnmanagedType.BStr)] string psz, IntPtr pStg);
        [PreserveSig] int GetClassID(out Guid pClassID);
    }

    /// <summary>
    /// 枚举视频采集设备，返回真实设备名（与 DSHOW 索引顺序一致）。
    /// </summary>
    public static List<string> GetCameraNames()
    {
        var names = new List<string>();
        if (!OperatingSystem.IsWindows()) return names;

        IBindCtx? bindCtx = null;
        IMoniker? enumerator = null;
        IEnumMoniker? enumMoniker = null;
        try
        {
            if (CreateBindCtx(0, out bindCtx) != 0 || bindCtx == null) return names;
            if (bindCtx.EnumMoniker(out enumMoniker) != 0 || enumMoniker == null) return names;

            var monikers = new IMoniker[1];
            while (enumMoniker.Next(1, monikers, IntPtr.Zero) == 0)
            {
                var moniker = monikers[0];
                if (moniker == null) break;
                try
                {
                    if (moniker.GetDisplayName(bindCtx, IntPtr.Zero, out var name) == 0
                        && !string.IsNullOrWhiteSpace(name))
                    {
                        // 过滤非视频采集设备（DirectShow 里还混有音频渲染器等）
                        if (LooksLikeVideoDevice(name))
                        {
                            names.Add(CleanDeviceName(name));
                        }
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(moniker);
                }
            }
        }
        catch
        {
            // 枚举不可用时返回空列表，由调用方回退到索引占位名
        }
        finally
        {
            if (enumMoniker != null) Marshal.ReleaseComObject(enumMoniker);
            if (enumerator != null) Marshal.ReleaseComObject(enumerator);
            if (bindCtx != null) Marshal.ReleaseComObject(bindCtx);
        }

        return names;
    }

    /// <summary>按名称粗筛视频采集设备：排除音频渲染/采集器等无关设备。</summary>
    private static bool LooksLikeVideoDevice(string name)
    {
        string[] skip =
        {
            "audio", "sound", "麦克风", "扬声器", "耳机", "麦克", "audio endpoint",
            "立体声", "midi", "bluetooth", "wave out", "primary sound"
        };
        var lower = name.ToLowerInvariant();
        return !skip.Any(k => lower.Contains(k));
    }

    /// <summary>去掉设备名里 DirectShow 附带的实例后缀，如 "USB2.0 HD UVC WebCam (2) #2"。</summary>
    private static string CleanDeviceName(string name)
    {
        var idx = name.IndexOf('#');
        return (idx > 0 ? name.Substring(0, idx) : name).Trim();
    }
}
