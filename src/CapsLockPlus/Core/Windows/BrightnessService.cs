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

    /// <summary>
    /// 相邻两次 DDC/CI 写入的最小间隔（毫秒）。
    ///
    /// DDC/CI 走 I²C，显示器固件处理一条命令要几十毫秒，间隔太密会被**直接丢弃**，
    /// 表现就是"连按几下只有第一下生效"。Twinkle Tray 也是按 VCP 码做节流的。
    /// 60ms 是"不丢命令"和"连按跟得上"之间的折中。
    /// </summary>
    private const int DdcMinGapMs = 60;

    private static long _lastDdcWriteTick;

    /// <summary>上次成功读写的亮度（-1 = 还不知道）。</summary>
    private static int _last = -1;

    /// <summary>上次同步 <see cref="_last"/> 的时刻，用于判断缓存还新不新鲜。</summary>
    private static long _lastSyncTick;

    /// <summary>记下已知亮度，并盖上时间戳。</summary>
    private static void SetLast(int value)
    {
        _last = value;
        _lastSyncTick = Environment.TickCount64;
    }

    private static BrightnessBackend _backend = BrightnessBackend.Unknown;

    /// <summary>上次探测失败的时刻（用来决定什么时候再试一次）。</summary>
    private static long _probeFailTick;

    private static readonly object Gate = new();

    /* ---- 目标值 + 后台平滑推进 ----
     *
     * DDC/CI 写一次 50~150ms、读一次 50~100ms。在按键路径上同步做，
     * 一个动作就是 250ms —— 用户体感就是"按了没反应"。
     *
     * 现在按键只改这个**目标值**并立刻刷横条，硬件由一条后台线程
     * 一点点追上去。效果和 Windows 音量横条一样：按键立刻有反馈，
     * 屏幕亮度在后面平滑地爬。
     */
    private static int _target = -1;
    private static Thread? _pump;

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

            /* 主通道失败时切到另一条再试一次。
             *
             * 为什么需要兜底：WMI 那套接口存在"读得到、写不进"的机型
             * （驱动暴露了 CurrentBrightness 但 WmiSetBrightness 无效果），
             * 反过来 DDC/CI 也会在显示器从休眠唤醒后短暂失效。
             * 只试一条就放弃，用户看到的就是"按了没反应"。 */
            bool ok = _backend switch
            {
                BrightnessBackend.Wmi => WmiSet(percent) || DdcTrySet(percent),
                BrightnessBackend.Ddc => DdcTrySet(percent) || WmiSet(percent),
                _ => false,
            };

            if (ok) SetLast(percent);
            else CoreLog.Warn($"[亮度] 写入 {percent}% 失败（通道 {BackendName}）");
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

            /* 先读真实值，读不到才用缓存。
             *
             * 为什么不像音量那样直接用缓存：显示器上通常还有自己的实体按键，
             * 用户很可能刚用显示器菜单调过亮度。缓存这时是过期的，
             * 按"亮度 +"会跳到一个和眼前画面无关的值。
             * 读一次 DDC 约 50~100ms，单次按键完全承受得住。
             *
             * 但**连按**时不能再读：一次读 + 一次写 + 一次回读已经 250ms 左右，
             * 每下都先读一遍会让"按五下"变成一秒多才走完，手感发黏。
             * 1.5 秒内的连续按键认为没人来得及去按显示器实体键，直接用缓存推算。 */
            bool fresh = _last >= 0 && Environment.TickCount64 - _lastSyncTick < 1500;
            int cur = fresh ? _last : (ReadLocked() ?? (_last >= 0 ? _last : -1));
            if (cur < 0) return null;

            int target = Math.Clamp(cur + delta, 0, 100);

            // 已经到边界就不必再走一次（0/100 再按只会白等几百毫秒）
            if (target == cur) return cur;

            return Set(target) ? target : null;
        }
    }

    /// <summary>当前目标亮度（-1 表示还没有建立）。</summary>
    public static int Target
    {
        get { lock (Gate) return _target; }
    }

    /// <summary>
    /// 在**目标亮度**上加减，立刻返回新目标（不等硬件）。
    ///
    /// 这是给按键用的：调用方拿到返回值就能刷横条，
    /// 真正的硬件写入交给后台线程。
    /// </summary>
    public static int? AddTarget(int delta)
    {
        lock (Gate)
        {
            ProbeLocked();
            if (_backend == BrightnessBackend.Unsupported) return null;

            if (_target < 0)
            {
                int? cur = ReadLocked() ?? (_last >= 0 ? _last : -1);
                if (cur == null || cur < 0) return null;
                _target = cur.Value;
            }

            _target = Math.Clamp(_target + delta, 0, 100);
            EnsurePumpLocked();
            return _target;
        }
    }

    /// <summary>把目标直接设成某个绝对亮度（界面拖动 / 恢复时用）。</summary>
    public static bool SetTarget(int percent)
    {
        lock (Gate)
        {
            ProbeLocked();
            if (_backend == BrightnessBackend.Unsupported) return false;
            _target = Math.Clamp(percent, 0, 100);
            EnsurePumpLocked();
            return true;
        }
    }

    private static void EnsurePumpLocked()
    {
        if (_pump != null) return;
        _pump = new Thread(PumpLoop)
        {
            IsBackground = true,
            Name = "brightness-pump",
            Priority = ThreadPriority.BelowNormal,
        };
        _pump.Start();
    }

    /// <summary>
    /// 后台把实际亮度一步步推向目标。
    ///
    /// 每步最多 4%：太小追不上按键速度（按住时目标每秒涨 22%），
    /// 太大看起来就是一跳。所以取值让"每步 / 步耗时 ≈ 30%/秒"，
    /// 略快于按键增速，长按时不会落后。
    ///
    /// 这里不额外读硬件 —— <c>Set</c> 内部已经维护 <c>_last</c>，
    /// 每步再读一次会把速度砍一半。
    /// </summary>
    private static void PumpLoop()
    {
        const int StepSize = 4;

        while (true)
        {
            try
            {
                int target;
                lock (Gate)
                {
                    if (_backend == BrightnessBackend.Unsupported) return;
                    target = _target;
                }

                if (target < 0) { Thread.Sleep(30); continue; }

                int cur;
                lock (Gate) cur = _last;

                if (cur < 0)
                {
                    int? read = Get();
                    if (read == null) { Thread.Sleep(80); continue; }
                    cur = read.Value;
                }

                if (cur == target) { Thread.Sleep(20); continue; }

                int diff = target - cur;
                int step = Math.Sign(diff) * Math.Min(StepSize, Math.Abs(diff));
                Set(cur + step);      // 内部会更新 _last
                Thread.Sleep(6);
            }
            catch (Exception e)
            {
                CoreLog.Warn("[亮度] 平滑推进出错：" + e.Message);
                Thread.Sleep(250);
            }
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
    /// 调不动时给一句**能照着做**的说明。
    ///
    /// 泛泛一句"不支持"没有用：用户不知道是设备不行、设置没开，还是线材问题。
    /// 这里按当前探测到的事实分类回答 —— 没找到屏 / 找到屏但没响应 / 命令被丢。
    /// </summary>
    public static string Diagnose()
    {
        lock (Gate)
        {
            ProbeLocked();

            if (_backend == BrightnessBackend.Unsupported)
                return "没找到能调亮度的显示器：外接屏要在显示器菜单里把 DDC/CI 打开（有的型号叫「显示器控制」），笔记本内置屏需要显卡驱动正常";

            if (_backend == BrightnessBackend.Ddc)
            {
                int n = _ddc?.Count ?? 0;
                if (n == 0) return "没找到支持 DDC/CI 的显示器，请确认显示器菜单里的 DDC/CI 已开启";
                return $"显示器（{_ddc![0].Description}）没有响应亮度命令 —— 常见于 HDMI 转接器 / KVM / 扩展坞不透传 DDC，换直连试试";
            }

            return "亮度命令没有生效，请把日志里的「[亮度]」几行发出来";
        }
    }

    /// <summary>
    /// 列出探测到的通道与显示器，供 <c>--hwtest</c> 输出与问题排查。
    /// 用户报"亮度调不了"时，这几行能直接说明是没找到屏、还是找到了写不进。
    /// </summary>
    public static IReadOnlyList<string> Describe()
    {
        lock (Gate)
        {
            ProbeLocked();
            var list = new List<string> { "通道：" + BackendName };
            if (_backend == BrightnessBackend.Wmi) list.Add("WMI：内置屏");

            if (_ddc != null)
            {
                for (int i = 0; i < _ddc.Count; i++)
                    list.Add($"DDC 屏{i + 1}：{_ddc[i].Description}，量程 0~{_ddc[i].Max}");
            }
            return list;
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
            _target = -1;
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
        /* 「不支持」这个结论**不能永久缓存**。
         *
         * 探测失败常常是暂时的：程序随开机自启时显示器还没初始化完、
         * 显示器正在休眠、刚切完输入源、显卡驱动还没起来。
         * 旧写法一旦探测失败就把 _backend 钉死在 Unsupported，
         * 之后整个会话都返回"这台显示器不支持" —— 用户看到的就是
         * "亮度功能压根没实现"，只有重启程序才可能好。
         *
         * 现在隔 5 秒允许重探一次，睡眠唤醒 / 重新插线之后能自己恢复。 */
        if (_backend == BrightnessBackend.Unsupported)
        {
            if (Environment.TickCount64 - _probeFailTick < 5000) return;
            ReleaseDdcLocked();
            _backend = BrightnessBackend.Unknown;
        }

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
        _probeFailTick = Environment.TickCount64;
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
                        SetLast(val);
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
        public string Description = "";
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

                            /* 描述紧跟在句柄后面，是 128 个 wchar 的定长字段。
                             *
                             * 必须按长度读再手工截到第一个 \0：PtrToStringUni 带长度
                             * 的版本不会在 \0 处停下，会把字段后面的堆垃圾一起读进来，
                             * 日志里就变成「Generic PnP Monitor 紈ጲ翽…」这种乱码。 */
                            string desc = "";
                            try
                            {
                                string raw = Marshal.PtrToStringUni(
                                    IntPtr.Add(buf, i * stride + IntPtr.Size), 128) ?? "";
                                int z = raw.IndexOf('\0');
                                desc = (z >= 0 ? raw[..z] : raw).Trim();
                            }
                            catch { }

                            // 读一次亮度：能读出 max 才说明这台屏支持 VCP 0x10
                            if (GetVCPFeatureAndVCPFeatureReply(h, VcpBrightness, out _, out uint cur, out uint max)
                                && max > 0)
                            {
                                _ddc.Add(new DdcMonitor { Handle = h, Max = max, Description = desc });
                                CoreLog.Info($"[亮度] 发现 DDC 屏：{desc}，量程 0~{max}，当前 {cur}（{FromVcp(cur, max)}%）");
                            }
                            else
                            {
                                CoreLog.Info($"[亮度] 跳过 {desc}：不支持 VCP 0x10 亮度");
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
                if (pct >= 0) { SetLast(pct); return pct; }
            }
        }
        return null;
    }

    /// <summary>
    /// 写 DDC，并且**回读确认**，失败时重建句柄再试一次。
    ///
    /// 为什么要回读：<c>SetVCPFeature</c> 返回 TRUE 只代表命令交给了驱动，
    /// 显示器固件完全可能把这条命令丢掉（I²C 忙、两条命令间隔太密、
    /// 显示器刚从休眠醒来）。这时程序"以为成功了"，用户看到的却是屏幕
    /// 一点没变 —— 这是亮度调节最典型的假成功。所以写完必须读回来，
    /// 值真的变了才算数，没变就再写一次（最多 3 次）。
    ///
    /// 句柄什么时候会失效：显示器休眠/唤醒、切输入源、拔插线缆、
    /// 显卡驱动重启。表现为之后所有写入都返回 false —— 而用户那边的感受
    /// 只是"亮度键忽然不管用了"。重建一次就能恢复，不必重启程序。
    /// </summary>
    private static bool DdcTrySet(int percent)
    {
        if (!DdcEnsureLocked()) return false;
        if (DdcSet(percent)) return true;

        CoreLog.Warn("[亮度] DDC 写入未生效，重新枚举显示器后重试一次");
        ReleaseDdcLocked();
        return DdcEnsureLocked() && DdcSet(percent);
    }

    private static bool DdcSet(int percent)
    {
        if (!DdcEnsureLocked()) return false;
        var monitors = _ddc!;

        int before = DdcReadLocked() ?? -1;

        /* 已经就是这个亮度：不必再写。
         * 少了这个短路，回读值不会变，会被下面的逻辑误判成"写入被丢弃"，
         * 白重试三次还报失败。 */
        if (before == percent) { SetLast(percent); return true; }

        for (int attempt = 1; attempt <= 3; attempt++)
        {
            // 相邻命令留出间隔，否则显示器固件会丢掉后面几条
            ThrottleDdcLocked();

            bool any = false;
            for (int i = 0; i < monitors.Count; i++)
            {
                var m = monitors[i];
                try
                {
                    uint value = ToVcp(percent, m.Max);
                    bool ok = SetVCPFeature(m.Handle, VcpBrightness, value);
                    if (ok) any = true;
                    CoreLog.Info($"[亮度] DDC 屏{i + 1}（{m.Description}）写 {percent}%，" +
                                 $"VCP={value}/{m.Max} → {(ok ? "已发送" : "失败")}");
                }
                catch (Exception ex)
                {
                    CoreLog.Warn("[亮度] DDC 写入失败：" + ex.Message);
                }
            }
            if (!any) return false;

            // 显示器处理命令要时间，读太早会读到旧值
            System.Threading.Thread.Sleep(60);

            int after = DdcReadLocked() ?? -1;
            if (after >= 0 && after != before)
            {
                SetLast(after);
                if (attempt > 1) CoreLog.Info($"[亮度] 第 {attempt} 次写入才真正生效");
                return true;
            }

            CoreLog.Warn($"[亮度] 写完回读仍是 {after}%，命令被显示器丢弃，重试 {attempt}/3");
        }

        return false;
    }

    /// <summary>把两次 DDC 写入拉开到 <see cref="DdcMinGapMs"/> 以上。</summary>
    private static void ThrottleDdcLocked()
    {
        if (_lastDdcWriteTick == 0) { _lastDdcWriteTick = Environment.TickCount64; return; }

        long gap = Environment.TickCount64 - _lastDdcWriteTick;
        if (gap < DdcMinGapMs) System.Threading.Thread.Sleep((int)(DdcMinGapMs - gap));
        _lastDdcWriteTick = Environment.TickCount64;
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
