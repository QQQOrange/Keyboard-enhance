using System;
using System.Collections.Generic;
using System.Management;
using System.Runtime.InteropServices;

namespace CapsLockPlus.Core.Windows;

/// <summary>亮度调节走的是哪条通道。</summary>
public enum BrightnessBackend
{
    /// <summary>还没探测。</summary>
    Unknown,

    /// <summary>WMI（WmiMonitorBrightnessMethods）—— 笔记本内置屏。</summary>
    Wmi,

    /// <summary>DDC/CI（dxva2.dll，VCP 0x10）—— 台式机外接显示器。</summary>
    Ddc,

    /// <summary>两条都不通。</summary>
    Unsupported,
}

/// <summary>
/// 屏幕亮度调节。两条通道按顺序探测：
///
///   ① WMI（root\wmi → WmiMonitorBrightnessMethods）
///      笔记本内置屏、少数驱动完整的外接屏支持。写入快（一次 30~80ms），
///      不闪屏，是首选。
///
///   ② DDC/CI（dxva2.dll → SetVCPFeature，VCP 0x10）
///      台式机外接显示器走这条：直接通过显示线缆跟显示器固件对话。
///      绝大多数 2010 年后的显示器支持，但**需要在显示器菜单里打开 DDC/CI**
///      （部分型号默认关闭）。代价是慢（一次 100~300ms），个别屏会闪一下。
///
///   两条都不通 → <see cref="Step"/> 返回 null，由上层明确提示"设备不支持"，
///   而不是静默失败 —— 按了没反应会被当成按键坏了。
///
/// 为什么要缓存上次亮度：WMI 查一次 200~500ms、DDC 更要几百毫秒，
/// 长按连续调亮度时每次都读会明显卡顿。写入成功后把值记下来，后续用缓存推算。
/// </summary>
public static class BrightnessService
{
    private const string Scope = @"root\wmi";
    private const string ClassMethods = "WmiMonitorBrightnessMethods";
    private const string ClassState = "WmiMonitorBrightness";

    /// <summary>DDC/CI 里"亮度"的 VCP 代码（VESA MCCS 标准）。</summary>
    private const byte VcpBrightness = 0x10;

    /// <summary>上次成功读写的亮度（-1 = 还不知道）。</summary>
    private static int _last = -1;

    private static BrightnessBackend _backend = BrightnessBackend.Unknown;

    private static readonly object Gate = new();

    /// <summary>当前使用的通道（界面提示 / 自检用）。</summary>
    public static BrightnessBackend Backend
    {
        get { lock (Gate) { ProbeLocked(); return _backend; } }
    }

    /// <summary>通道的中文名，用于日志与提示。</summary>
    public static string BackendName => Backend switch
    {
        BrightnessBackend.Wmi => "WMI（内置屏）",
        BrightnessBackend.Ddc => "DDC/CI（外接屏）",
        BrightnessBackend.Unsupported => "不支持",
        _ => "未探测",
    };

    /// <summary>当前亮度（0~100）；两条通道都不通时返回 null。</summary>
    public static int? Get()
    {
        lock (Gate)
        {
            ProbeLocked();
            return ReadLocked();
        }
    }

    /// <summary>设置绝对亮度（0~100）。成功返回 true。</summary>
    public static bool Set(int percent)
    {
        lock (Gate)
        {
            ProbeLocked();
            percent = Math.Clamp(percent, 0, 100);

            bool ok = _backend switch
            {
                BrightnessBackend.Wmi => WmiSet(percent),
                BrightnessBackend.Ddc => DdcSet(percent),
                _ => false,
            };

            if (ok) _last = percent;
            return ok;
        }
    }

    /// <summary>
    /// 在当前亮度上加减 <paramref name="delta"/> 个百分点。
    /// 返回值：null = 这台设备两条通道都不通；否则是调整后的亮度。
    /// </summary>
    public static int? Step(int delta)
    {
        lock (Gate)
        {
            ProbeLocked();
            if (_backend == BrightnessBackend.Unsupported) return null;

            int cur = _last >= 0 ? _last : (ReadLocked() ?? -1);
            if (cur < 0) return null;

            int target = Math.Clamp(cur + delta, 0, 100);

            // 已经到边界就不必再走一次（0/100 再按只会白等几百毫秒）
            if (target == cur) return cur;

            return Set(target) ? target : null;
        }
    }

    /// <summary>这台机器是否支持软件调亮度（用于界面提示 / 自检）。</summary>
    public static bool IsSupported()
    {
        lock (Gate)
        {
            ProbeLocked();
            return _backend != BrightnessBackend.Unsupported;
        }
    }

    /// <summary>
    /// 丢弃缓存、重新探测。切换显示器 / 重接扩展屏后调用。
    /// 同时释放 DDC 物理显示器句柄（那是驱动资源，不释放会累积）。
    /// </summary>
    public static void Reset()
    {
        lock (Gate)
        {
            ReleaseDdcLocked();
            _backend = BrightnessBackend.Unknown;
            _last = -1;
        }
    }

    // ================= 百分比 ↔ VCP 值换算（纯函数，自检直接断言） =================

    /// <summary>
    /// 显示器的原始亮度值 → 百分比。
    /// 不同屏的量程不一样：多数是 0~100，也有 0~255 甚至 0~1000 的，
    /// 所以必须按 max 换算，不能想当然当百分比用。
    /// </summary>
    public static int FromVcp(uint current, uint max)
    {
        if (max == 0) return -1;
        return (int)Math.Clamp(Math.Round(current * 100.0 / max), 0, 100);
    }

    /// <summary>百分比 → 显示器的原始亮度值（同一套量程换算）。</summary>
    public static uint ToVcp(int percent, uint max)
    {
        if (max == 0) return 0;
        percent = Math.Clamp(percent, 0, 100);
        return (uint)Math.Round(percent * (double)max / 100.0);
    }

    // ================= 探测 =================

    private static void ProbeLocked()
    {
        if (_backend != BrightnessBackend.Unknown) return;

        // WMI 读一次比 DDC 快，先试它
        if (WmiGet() >= 0)
        {
            _backend = BrightnessBackend.Wmi;
            CoreLog.Info("[亮度] 使用 WMI 通道");
            return;
        }

        if (DdcEnsureLocked() && DdcReadLocked() != null)
        {
            _backend = BrightnessBackend.Ddc;
            CoreLog.Info($"[亮度] 使用 DDC/CI 通道（{_ddc?.Count ?? 0} 台显示器）");
            return;
        }

        _backend = BrightnessBackend.Unsupported;
        CoreLog.Warn("[亮度] 两条通道都不可用（设备不支持或未开启 DDC/CI）");
    }

    private static int? ReadLocked() => _backend switch
    {
        BrightnessBackend.Wmi => WmiGet() is int w && w >= 0 ? w : null,
        BrightnessBackend.Ddc => DdcReadLocked(),
        _ => null,
    };

    // ================= 通道 ①：WMI =================

    private static int WmiGet()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(Scope,
                $"SELECT CurrentBrightness FROM {ClassState}");
            foreach (ManagementObject item in searcher.Get())
            {
                using (item)
                {
                    object? v = item["CurrentBrightness"];
                    if (v != null)
                    {
                        int val = Convert.ToInt32(v);
                        _last = val;
                        return val;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            CoreLog.Warn("[亮度] WMI 读取失败：" + ex.Message);
        }
        return -1;
    }

    private static bool WmiSet(int percent)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(Scope,
                $"SELECT * FROM {ClassMethods}");

            bool any = false;
            foreach (ManagementObject item in searcher.Get())
            {
                using (item)
                {
                    try
                    {
                        // WmiSetBrightness(TimeoutSeconds, Brightness)
                        item.InvokeMethod("WmiSetBrightness", new object[] { 1u, (byte)percent });
                        any = true;
                    }
                    catch (Exception ex)
                    {
                        CoreLog.Warn("[亮度] WMI 写入失败：" + ex.Message);
                    }
                }
            }
            return any;
        }
        catch (Exception ex)
        {
            CoreLog.Warn("[亮度] WMI 不可用：" + ex.Message);
            return false;
        }
    }

    // ================= 通道 ②：DDC/CI =================

    private sealed class DdcMonitor
    {
        public IntPtr Handle;
        public uint Max;
    }

    private static List<DdcMonitor>? _ddc;

    /// <summary>物理显示器句柄开出来就一直拿着：每次重新枚举要 100ms 以上。</summary>
    private static bool DdcEnsureLocked()
    {
        if (_ddc != null) return _ddc.Count > 0;

        _ddc = new List<DdcMonitor>();
        try
        {
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr hdc, IntPtr rect, IntPtr data) =>
            {
                if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hMon, out uint count) || count == 0)
                    return true;

                /* PHYSICAL_MONITOR 是 { HANDLE; WCHAR szDescription[128]; }。
                 * 不用 Marshal 的结构体数组：里面有字符串字段，marshaler 需要
                 * 预先分配好的缓冲区，默认的 null 字符串会直接崩。
                 * 手工按偏移取更稳，x64 是 8+256、x86 是 4+256。 */
                int stride = IntPtr.Size + 128 * 2;
                IntPtr buf = Marshal.AllocHGlobal(stride * (int)count);
                try
                {
                    if (GetPhysicalMonitorsFromHMONITOR(hMon, count, buf))
                    {
                        for (int i = 0; i < count; i++)
                        {
                            IntPtr h = Marshal.ReadIntPtr(buf, i * stride);
                            if (h == IntPtr.Zero) continue;

                            // 读一次亮度：能读出 max 才说明这台屏支持 VCP 0x10
                            if (GetVCPFeatureAndVCPFeatureReply(h, VcpBrightness, out _, out _, out uint max)
                                && max > 0)
                            {
                                _ddc.Add(new DdcMonitor { Handle = h, Max = max });
                            }
                            else
                            {
                                DestroyPhysicalMonitor(h);   // 不支持亮度，立刻还回去
                            }
                        }
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buf);
                }
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            CoreLog.Warn("[亮度] 枚举显示器失败：" + ex.Message);
        }

        return _ddc.Count > 0;
    }

    private static int? DdcReadLocked()
    {
        if (!DdcEnsureLocked()) return null;
        var monitors = _ddc!;

        // 取第一台能读的：多屏时按显示器顺序，通常主屏在前
        foreach (var m in monitors)
        {
            if (GetVCPFeatureAndVCPFeatureReply(m.Handle, VcpBrightness, out _, out uint cur, out uint max)
                && max > 0)
            {
                int pct = FromVcp(cur, max);
                if (pct >= 0) { _last = pct; return pct; }
            }
        }
        return null;
    }

    private static bool DdcSet(int percent)
    {
        if (!DdcEnsureLocked()) return false;
        var monitors = _ddc!;

        bool any = false;
        foreach (var m in monitors)
        {
            try
            {
                if (SetVCPFeature(m.Handle, VcpBrightness, ToVcp(percent, m.Max))) any = true;
            }
            catch (Exception ex)
            {
                CoreLog.Warn("[亮度] DDC 写入失败：" + ex.Message);
            }
        }
        return any;
    }

    private static void ReleaseDdcLocked()
    {
        if (_ddc == null) return;
        foreach (var m in _ddc)
        {
            try { DestroyPhysicalMonitor(m.Handle); } catch { }
        }
        _ddc = null;
    }

    // ================= dxva2 / user32 互操作 =================

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip,
        MonitorEnumProc proc, IntPtr data);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint number);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint arraySize, IntPtr monitors);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr hMonitor, byte vcpCode,
        out int vcpType, out uint current, out uint max);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetVCPFeature(IntPtr hMonitor, byte vcpCode, uint value);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyPhysicalMonitor(IntPtr hMonitor);
}
