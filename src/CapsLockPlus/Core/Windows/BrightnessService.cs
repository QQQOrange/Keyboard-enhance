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
/// 两条都不通 → <see cref="AddTarget"/> 返回 null，由上层明确提示"设备不支持"，
/// 而不是静默失败 —— 按了没反应会被当成按键坏了。
///
/// ================== 线程模型（v4.3.0 重写） ==================
///
/// 上一版只有一把大盘锁 <c>Gate</c>，按键路径和后台推进线程抢同一把：
/// 后台正在写 DDC（一次 100~250ms）时，用户的按键要排队等它；
/// 而按住连发每 45ms 就来一次 —— 队列越积越长，等用户调完亮度、
/// 手还按着触发键再去按音量键时，动作被前面几十条亮度任务堵住，
/// 表现就是"按音量完全没反应"。这就是本次要修的根因。
///
/// 现在拆成两把锁，职责清清楚楚：
///   · <see cref="TinyGate"/> —— 只保护 <c>_target</c> / <c>_last</c> 几个 int，
///     按键路径只碰它，持锁时间以纳秒计，永不等待硬件；
///   · <see cref="HwGate"/> —— 真实硬件读写（WMI / DDC），
///     只允许后台推进线程和"第一次探测"持有，慢也没关系。
///
/// 锁顺序永远是 HwGate → TinyGate，不会反向，因此不会死锁。
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
    /// 35ms 是"不丢命令"和"按住时跟得上"之间的折中。
    /// </summary>
    private const int DdcMinGapMs = 35;

    /// <summary>两次平滑推进之间让出 CPU 的时间（毫秒）。</summary>
    private const int FastGapMs = 14;

    /// <summary>
    /// 单步最多走多少个百分点。
    ///
    /// 太小追不上按住的速度（目标每秒涨 20% 以上），
    /// 太大会在画面上一跳一跳。
    /// </summary>
    private const int MaxStepPerTick = 6;

    /// <summary>追上目标后隔多久回读一次，校正"命令被显示器丢了"的情况。</summary>
    private const int SettleVerifyMs = 900;

    /// <summary>目标值与硬件回读差多少才算需要重新对齐。</summary>
    private const int SettleTolerance = 1;

    /// <summary>连续多少次回读都对不上，就认为显示器有自己的想法，以实际值为准。</summary>
    private const int SettleGiveUpTimes = 3;

    /// <summary>
    /// 刚调完的这段时间内，回读不一致视为"命令被丢了"，要纠正；
    /// 超过这段时间就不再硬掰 —— 用户很可能是自己按了显示器上的按键。
    /// </summary>
    private const int SettleCorrectionWindowMs = 2500;

    /* ---- 两把锁：按键路径 / 硬件路径 ---- */
    private static readonly object TinyGate = new();
    private static readonly object HwGate = new();

    /// <summary>
    /// 叫醒推进线程。
    ///
    /// 目标没变时推进线程在 WaitOne 上睡着（不占 CPU），
    /// 用户一按键就 Set() 把它立刻叫起来 —— 既省电又不拖延迟。
    /// </summary>
    private static readonly AutoResetEvent Wake = new(false);

    private static long _lastDdcWriteTick;

    /// <summary>上次成功读写的亮度（-1 = 还不知道）。</summary>
    private static int _last = -1;

    /// <summary>上次同步 <see cref="_last"/> 的时刻。</summary>
    private static long _lastSyncTick;

    /// <summary>目标亮度（-1 = 还没建立）。按键只改这个值。</summary>
    private static int _target = -1;

    /// <summary>是否支持 software 调亮度：-1 未探测 / 0 不支持 / 1 支持。</summary>
    private static int _supported = -1;

    /// <summary>连续回读对不上的次数。</summary>
    private static int _settleMisses;

    /// <summary>
    /// 用户上一次用按键 / 界面改目标的时刻。
    ///
    /// 用来区分两种"回读和目标不一致"：
    ///   · 刚按完 → 很可能是命令被显示器丢了，要纠正；
    ///   · 很久以后 → 用户多半自己按了显示器上的实体键，我们要让路。
    /// </summary>
    private static long _lastUserTouchTick;

    private static BrightnessBackend _backend = BrightnessBackend.Unknown;

    /// <summary>上次探测失败的时刻（用来决定什么时候再试一次）。</summary>
    private static long _probeFailTick;

    private static Thread? _pump;

    /// <summary>当前使用的通道（界面提示 / 自检用）。</summary>
    public static BrightnessBackend Backend
    {
        get { lock (HwGate) { ProbeLocked(); return _backend; } }
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
        lock (HwGate)
        {
            ProbeLocked();
            return ReadLocked();
        }
    }

    /// <summary>
    /// 设置绝对亮度（0~100），**写完回读确认**。成功返回 true。
    /// 这条路是给界面 / 自检用的"要准确"，慢一点没关系。
    /// 按键调节不走这里 —— 见 <see cref="AddTarget"/>。
    /// </summary>
    public static bool Set(int percent)
    {
        percent = Math.Clamp(percent, 0, 100);

        lock (HwGate)
        {
            ProbeLocked();
            if (_backend == BrightnessBackend.Unsupported)
            {
                MarkUnsupported();
                return false;
            }

            bool ok = WriteLocked(percent, verify: true);
            if (ok) PublishLast(percent);
            else CoreLog.Warn($"[亮度] 写入 {percent}% 失败（通道 {BackendName}）");
            return ok;
        }
    }

    /// <summary>
    /// 在当前亮度上加减 <paramref name="delta"/> 个百分点（同步、要准确）。
    /// 返回值：null = 这台设备两条通道都不通；否则是调整后的亮度。
    ///
    /// 按键**不走**这里：一次读 + 一次写 + 一次回读 250ms 起步，
    /// 按住连发会互相排队。按键走 <see cref="AddTarget"/>。
    /// </summary>
    public static int? Step(int delta)
    {
        int? cur = Get();
        if (cur == null) return null;

        int target = Math.Clamp(cur.Value + delta, 0, 100);
        if (target == cur.Value) return cur;

        return Set(target) ? target : null;
    }

    /// <summary>当前目标亮度（-1 表示还没有建立）。</summary>
    public static int Target
    {
        get { lock (TinyGate) return _target; }
    }

    /// <summary>
    /// 在**目标亮度**上加减，立刻返回新目标（不等硬件）。
    ///
    /// 这是按键路径：**只改一个 int**，不碰硬件锁、不读寄存器，
    /// 所以耗时是微秒级。真正的硬件写入交给后台线程
    /// <see cref="PumpLoop"/> 一点点追上去。
    ///
    /// 返回 null = 这台设备不支持软件调亮度。
    /// </summary>
    public static int? AddTarget(int delta)
    {
        /* 快路径：目标已经建立 —— 全程只碰 TinyGate。
         *
         * 这里**绝对不能**调用任何硬件方法：后台线程正在写 DDC 时
         * 会占着 HwGate 一百多毫秒，按键等它就会排队，
         * 排到"调完亮度再按音量完全没反应"。 */
        int? fast = null;
        lock (TinyGate)
        {
            if (_supported == 0) return null;
            if (_target >= 0)
            {
                _target = Math.Clamp(_target + delta, 0, 100);
                _lastUserTouchTick = Environment.TickCount64;
                fast = _target;
            }
        }

        if (fast is int ready)
        {
            /* 目标变了就叫醒推进线程。
             * 放在锁外面：Wait/Set 是内核调用，持锁做没必要。 */
            Wake.Set();
            return ready;
        }

        // 慢路径：本次启动的第一次按键才需要读一次真实亮度当起点
        if (!TryBootstrapTarget()) return null;

        int result;
        lock (TinyGate)
        {
            _target = Math.Clamp(_target + delta, 0, 100);
            _lastUserTouchTick = Environment.TickCount64;
            result = _target;
        }

        EnsurePump();
        Wake.Set();
        return result;
    }

    /// <summary>把目标直接设成某个绝对亮度（界面拖动 / 恢复时用）。</summary>
    public static bool SetTarget(int percent)
    {
        if (!EnsureSupported()) return false;

        lock (TinyGate)
        {
            _target = Math.Clamp(percent, 0, 100);
            _lastUserTouchTick = Environment.TickCount64;
        }
        EnsurePump();
        Wake.Set();
        return true;
    }

    /// <summary>这台机器是否支持软件调亮度（用于界面提示 / 自检）。</summary>
    public static bool IsSupported() => EnsureSupported();

    /// <summary>确认设备可用；已确认过就直接返回，不再碰硬件。</summary>
    private static bool EnsureSupported()
    {
        lock (TinyGate)
        {
            if (_supported == 1) return true;
            if (_supported == 0) return false;
        }
        return TryBootstrapTarget();
    }

    /// <summary>
    /// 读一次真实亮度，把它同时当作起点（目标）和已知值。
    /// 只在"本次启动的第一次调节"和界面查询支持情况时才会跑到。
    /// </summary>
    private static bool TryBootstrapTarget()
    {
        int? cur;
        lock (HwGate)
        {
            ProbeLocked();
            if (_backend == BrightnessBackend.Unsupported)
            {
                MarkUnsupported();
                return false;
            }
            cur = ReadLocked();
        }

        if (cur is not int value || value < 0)
        {
            MarkUnsupported();
            return false;
        }

        lock (TinyGate)
        {
            _target = value;
            _last = value;
            _lastSyncTick = Environment.TickCount64;
            _supported = 1;
            _settleMisses = 0;
            _lastUserTouchTick = Environment.TickCount64;
        }
        Wake.Set();
        return true;
    }

    private static void MarkUnsupported()
    {
        lock (TinyGate) _supported = 0;
    }

    /// <summary>记下已知亮度（调用方已持有 TinyGate 时不要用这个）。</summary>
    private static void PublishLast(int value)
    {
        lock (TinyGate)
        {
            _last = value;
            _lastSyncTick = Environment.TickCount64;
        }
    }

    private static void EnsurePump()
    {
        lock (TinyGate)
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
    }

    /// <summary>
    /// 后台把实际亮度一步步推向目标。
    ///
    /// 每步走多少由 <see cref="PlanStep"/> 决定：离目标远时步子大（追得上），
    /// 快到时退到 1%（不跳变）。中间步只写不回读 —— 回读一次要多花
    /// 50~100ms，那点时间足够再走两三步，手感就从"一格一格跳"变成"滑过去"。
    ///
    /// 追上目标后隔 <see cref="SettleVerifyMs"/> 回读一次，
    /// 处理"命令被显示器丢了"的情况：以硬件为准重新对齐。
    /// </summary>
    private static void PumpLoop()
    {
        while (true)
        {
            int target, cur, supported;
            lock (TinyGate)
            {
                target = _target;
                cur = _last;
                supported = _supported;
            }

            // 没活干就睡着等（不是轮询）：按键时 Wake.Set() 会立刻叫醒
            if (supported != 1 || target < 0) { Wake.WaitOne(500); continue; }

            if (cur < 0)
            {
                int? read;
                lock (HwGate) read = ReadLocked();
                if (read is int r) PublishLast(r);
                else Wake.WaitOne(150);
                continue;
            }

            if (cur == target)
            {
                /* 已经追上目标。这里**不能**继续 25ms 醒一次 ——
                 * 那样即使没人调亮度，后台线程每秒也要醒 40 次，
                 * 白白耗电（笔记本上尤其明显）。改成等事件，
                 * 只有"目标变了"或"到点回读校验"才醒。 */
                SettleVerify(target);
                Wake.WaitOne(SettleVerifyMs);
                continue;
            }

            int next = Math.Clamp(cur + PlanStep(target - cur), 0, 100);

            bool ok;
            try
            {
                lock (HwGate) ok = WriteLocked(next, verify: false);
            }
            catch (Exception e)
            {
                CoreLog.Warn("[亮度] 平滑推进出错：" + e.Message);
                Wake.WaitOne(250);
                continue;
            }

            if (ok)
            {
                PublishLast(next);
                lock (TinyGate) _settleMisses = 0;
                Wake.WaitOne(FastGapMs);
            }
            else
            {
                // 写不进去：留给下一次探测 / 换通道，别死循环刷屏
                lock (TinyGate) _lastSyncTick = Environment.TickCount64;
                Wake.WaitOne(300);
            }
        }
    }

    /// <summary>
    /// 这一轮该走几个百分点：**按剩余距离成比例**。
    ///
    /// 匀速走会在最后"啪"地停住，而且离目标很远时显得温吞；
    /// 按比例走则是先快后慢，最后几格都是 1%，视觉上是一条平滑的曲线 ——
    /// 和按住 Windows 音量键看到的爬升一个感觉。
    /// </summary>
    internal static int PlanStep(int diff)
    {
        int mag = Math.Abs(diff);
        if (mag <= 1) return Math.Sign(diff);

        int step = (int)Math.Ceiling(mag / 4.0);
        step = Math.Clamp(step, 1, MaxStepPerTick);
        return Math.Sign(diff) * Math.Min(step, mag);
    }

    /// <summary>仅供自检。</summary>
    internal static int PlanStepForTest(int diff) => PlanStep(diff);

    /// <summary>追上目标后回读一次，确认显示器真的到了。</summary>
    private static void SettleVerify(int target)
    {
        long since, touched;
        lock (TinyGate)
        {
            since = Environment.TickCount64 - _lastSyncTick;
            touched = _lastUserTouchTick;
        }
        if (since < SettleVerifyMs) return;

        // 先盖时间戳：回读本身也要几十毫秒，否则它会被立刻再次触发
        lock (TinyGate) _lastSyncTick = Environment.TickCount64;

        int? read;
        lock (HwGate) read = ReadLocked();
        if (read is not int r) return;
        if (Math.Abs(r - target) <= SettleTolerance) return;

        /* 用户久没动过，值却变了 → 多半是他自己按了显示器上的实体键。
         * 这时候**以硬件为准**，别把人家刚调好的亮度又推回去。 */
        bool userRecentlyChanged = Environment.TickCount64 - touched < SettleCorrectionWindowMs;
        if (!userRecentlyChanged)
        {
            lock (TinyGate)
            {
                _target = r;
                _last = r;
                _settleMisses = 0;
            }
            CoreLog.Info($"[亮度] 检测到显示器被外部调整，同步为 {r}%");
            return;
        }

        bool giveUp;
        lock (TinyGate)
        {
            _last = r;                       // 以硬件为准，让推进线程重新追
            _settleMisses++;
            giveUp = _settleMisses >= SettleGiveUpTimes;
            if (giveUp) _target = r;
        }

        if (giveUp)
            CoreLog.Warn($"[亮度] 回读一直是 {r}%，与目标 {target}% 对不上，改为以显示器实际值为准");
        else
            CoreLog.Info($"[亮度] 回读 {r}%，重新对齐到目标 {target}%");
    }

    /// <summary>
    /// 调不动时给一句**能照着做**的说明。
    ///
    /// 泛泛一句"不支持"没有用：用户不知道是设备不行、设置没开，还是线材问题。
    /// 这里按当前探测到的事实分类回答 —— 没找到屏 / 找到屏但没响应 / 命令被丢。
    /// </summary>
    public static string Diagnose()
    {
        lock (HwGate)
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
        lock (HwGate)
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
        lock (HwGate)
        {
            ReleaseDdcLocked();
            _backend = BrightnessBackend.Unknown;
        }

        lock (TinyGate)
        {
            _last = -1;
            _target = -1;
            _supported = -1;
            _settleMisses = 0;
        }

        // 推进线程可能正睡在 WaitOne 上：叫醒它重新判断（不然要等超时）
        Wake.Set();
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
            lock (TinyGate) _supported = 1;
            CoreLog.Info("[亮度] 使用 WMI 通道");
            return;
        }

        if (DdcEnsureLocked() && DdcReadLocked() != null)
        {
            _backend = BrightnessBackend.Ddc;
            lock (TinyGate) _supported = 1;
            CoreLog.Info($"[亮度] 使用 DDC/CI 通道（{_ddc?.Count ?? 0} 台显示器）");
            return;
        }

        _backend = BrightnessBackend.Unsupported;
        _probeFailTick = Environment.TickCount64;
        MarkUnsupported();
        CoreLog.Warn("[亮度] 两条通道都不可用（设备不支持或未开启 DDC/CI）");
    }

    private static int? ReadLocked() => _backend switch
    {
        BrightnessBackend.Wmi => WmiGet() is int w && w >= 0 ? w : null,
        BrightnessBackend.Ddc => DdcReadLocked(),
        _ => null,
    };

    /// <summary>
    /// 写一次亮度。主通道失败时切到另一条再试一次。
    ///
    /// 为什么需要兜底：WMI 那套接口存在"读得到、写不进"的机型
    /// （驱动暴露了 CurrentBrightness 但 WmiSetBrightness 无效果），
    /// 反过来 DDC/CI 也会在显示器从休眠唤醒后短暂失效。
    /// 只试一条就放弃，用户看到的就是"按了没反应"。
    /// </summary>
    private static bool WriteLocked(int percent, bool verify) => _backend switch
    {
        BrightnessBackend.Wmi => WmiSet(percent) || DdcSet(percent, verify),
        BrightnessBackend.Ddc => DdcSet(percent, verify) || WmiSet(percent),
        _ => false,
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
                        PublishLast(val);
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
                if (pct >= 0) { PublishLast(pct); return pct; }
            }
        }
        return null;
    }

    /// <summary>
    /// 写 DDC。
    ///
    /// <paramref name="verify"/> = true（单次调节 / 界面设置）：
    ///   写完**回读确认**，失败时重建句柄再试 —— <c>SetVCPFeature</c> 返回 TRUE
    ///   只代表命令交给了驱动，显示器固件完全可能把这条命令丢掉。
    ///
    /// <paramref name="verify"/> = false（按住平滑推进的中间步）：
    ///   只写不回读。回读一次 50~100ms，比写入本身还贵；
    ///   追上目标后 <see cref="SettleVerify"/> 会统一校验一次。
    /// </summary>
    private static bool DdcSet(int percent, bool verify)
    {
        if (!DdcEnsureLocked()) return false;
        if (!verify) return DdcWriteFast(percent);

        if (DdcWriteVerified(percent)) return true;

        CoreLog.Warn("[亮度] DDC 写入未生效，重新枚举显示器后重试一次");
        ReleaseDdcLocked();
        return DdcEnsureLocked() && DdcWriteVerified(percent);
    }

    /// <summary>只写不回读（平滑推进用）。</summary>
    private static bool DdcWriteFast(int percent)
    {
        var monitors = _ddc!;
        ThrottleDdcLocked();

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

    /// <summary>
    /// 写 DDC 并回读确认，失败重试（最多 3 次）。
    ///
    /// 句柄什么时候会失效：显示器休眠/唤醒、切输入源、拔插线缆、
    /// 显卡驱动重启。表现为之后所有写入都返回 false —— 而用户那边的感受
    /// 只是"亮度键忽然不管用了"。重建一次就能恢复，不必重启程序。
    /// </summary>
    private static bool DdcWriteVerified(int percent)
    {
        var monitors = _ddc!;

        int before = DdcReadLocked() ?? -1;

        /* 已经就是这个亮度：不必再写。
         * 少了这个短路，回读值不会变，会被下面的逻辑误判成"写入被丢弃"，
         * 白重试三次还报失败。 */
        if (before == percent) { PublishLast(percent); return true; }

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
                PublishLast(after);
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
