using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using CapsLockPlus.Core.Input;
using CapsLockPlus.Core.Settings;
using CapsLockPlus.Core.Text;
using CapsLockPlus.Core.Windows;
using static CapsLockPlus.Core.Interop.NativeMethods;

namespace CapsLockPlus.Core.Actions;

/// <summary>
/// 音量、静音、亮度的调节与长按加速。与 ActionContext 同属 partial 类。
/// </summary>
public sealed partial class ActionContext
{

    /// <summary>
    /// 调整主音量（Core Audio API）。<paramref name="delta"/> 为正=加，为负=减。
    ///
    /// 不再模拟 VK_VOLUME_UP/DOWN：那条路在有键盘过滤驱动、远程会话、
    /// 或前台程序抢走媒体键的机器上会静默失效（用户看到的就是"按了没反应"）。
    /// 直接调音频引擎既能保证生效，也能拿到真实值显示百分比。
    /// </summary>
    /* ---- 单次 vs 连发：靠参数区分，不靠时间猜 ----
     *
     * delta 的语义很明确：
     *   · 单次按键 → 设置里的幅度（音量默认 5%、亮度默认 10%）；
     *   · 按住连发 → 引擎固定传 1，表示"这一步只走 1%"。
     *
     * 早先用"距上次调用的时间差"去猜，调度一抖就猜错 ——
     * 长按中途突然跳一大格，看起来一格一格地顿。 */

    /// <summary>
    /// 按设定幅度算出这次要发几次系统音量键。
    ///
    /// 系统步进是固定的（通常 2%），设定值往往不是它的整数倍：
    /// 设 5% 就"这次 2 次（4%）、下次 3 次（6%）"，把差的那 1% 记下来下次补。
    /// 十次下来正好 50%，不会出现"设 5 实际调了 40"的漂移。
    /// </summary>
    internal static (int Taps, double Remainder) PlanVolumeTaps(
        double want, int sysStep, double remainder)
    {
        sysStep = Math.Max(1, sysStep);
        double total = Math.Max(0, want) + remainder;
        int taps = Math.Max(1, (int)Math.Round(total / sysStep));
        // 误差只留在一个步进之内，避免长期累积成整数倍
        double next = Math.Clamp(total - taps * sysStep, -(sysStep - 1), sysStep - 1);
        return (taps, next);
    }

    /// <summary>音量一次调多少（百分比，设置里可改）。</summary>
    public int VolumeStep => Math.Clamp(Settings().Global.VolumeStep, 1, 50);

    /// <summary>亮度一次调多少（百分比，设置里可改）。</summary>
    public int BrightnessStep => Math.Clamp(Settings().Global.BrightnessStep, 1, 50);

    /// <summary>按住连发时用的一点点加速状态（见 <see cref="BrightnessAccel"/>）。</summary>
    private readonly BrightnessAccel _brightnessAccel = new();

    /// <summary>仅供自检：把加速状态清零，模拟"松开后重新按"。</summary>
    internal void ResetBrightnessAccelForTest() => _brightnessAccel.Reset();

    /// <summary>仅供自检：连发下一步会走多少（不碰硬件）。</summary>
    internal int NextBrightnessAccelForTest() => _brightnessAccel.Next();

    /// <summary>
    /// 音量步进的累计误差（百分比）。
    ///
    /// 系统步进是固定的（通常 2%），用户设 5% 没法一步到位 ——
    /// 那就这一次发 2 次键（4%）、下一次发 3 次（6%），
    /// 把差的那 1% 记下来下次补上。长期平均正好等于用户设的值，
    /// 不会出现"调十次差了 10%"。
    /// </summary>
    private double _volumeRemainder;

    /// <summary>上一次调节的方向（+1 加 / -1 减 / 0 还没调过）。</summary>
    private int _volumeDirection;

    public bool ChangeVolume(int delta)
    {
        /* delta=0 是"不动"，必须原地返回。
         * PlanVolumeTaps 里有 `Math.Max(1, ...)`（保证一次点击至少走一格），
         * 所以不拦的话 delta=0 也会发出一次音量键 —— 音量凭空变一格。
         * 现在所有调用方都给 ±1 以上的值，但这是 API 边界，该拦住。 */
        if (delta == 0) return true;

        /* 音量走**系统音量键**（VK_VOLUME_UP / DOWN）。
         *
         * 这是唯一能同时做到两件事的路：
         *   · 音量真的变了 —— 由 shell 自己调，不是我们猜；
         *   · Windows 弹出它自己的横条 —— 那条横条就是 shell 处理这个键时画的，
         *     自绘永远做不出"系统那个"。
         *
         * ⚠ v4.3.0 起**不再画自绘的音量横条**。
         * 上一版在"媒体键似乎没生效"时会退回音频引擎并自绘一条 ——
         * 结果用户常常一次看到**两条**横条（系统的 + 我们的），
         * 而且音量还被多调了一次。现在音量的一切反馈都交给系统，
         * 自绘横条只保留亮度那一条（系统对亮度没有通用 OSD）。
         *
         * 兜底：极少数机器上媒体键会被键盘过滤驱动 / 远程会话 / 抢媒体键的
         * 前台程序吞掉。单次调节时回读一次音量，没变就改用音频引擎直接调，
         * 音量照样会变（只是没有横条，比"按了没反应"好得多）。 */
        int sysStep = Math.Max(1, VolumeService.StepPercent());

        /* 换方向时把"欠账"清掉。
         *
         * 余数补偿是为了"设 5% 就长期平均 5%"：设 5（系统 2%）时
         * 这次发 2 次键（4%）、下次补 3 次（6%）。
         * 但用户加完马上又减时，那 1% 的欠账会跟着跑到反方向去 ——
         * 来回按几下音量会慢慢往回漂。方向一变就重新起算最稳妥。 */
        int direction = Math.Sign(delta);
        if (direction != 0 && direction != _volumeDirection)
        {
            _volumeRemainder = 0;
            _volumeDirection = direction;
        }

        var (taps, rem) = PlanVolumeTaps(Math.Abs(delta), sysStep, _volumeRemainder);
        _volumeRemainder = rem;

        // 细调（连发）不做回读校验：每次等 40ms 会把节奏拖垮
        bool fine = Math.Abs(delta) <= sysStep;

        int? before = VolumeService.Get();

        /* 已经到 0 / 100：系统音量键再发也不会变。
         * 不先拦住的话，"回读没变化"会被当成"媒体键被吞"，
         * 从而误触发兜底逻辑 —— 用户看到的就是"明明调不动还弹了条提示"。 */
        if (before is int edge && ((delta > 0 && edge >= 100) || (delta < 0 && edge <= 0)))
        {
            CoreLog.Info($"[音量] 已经是 {edge}%，到头了");
            return true;
        }

        InputSender.Tap(delta > 0 ? VK.VolumeUp : VK.VolumeDown, taps);

        if (fine) return true;

        int? after = before;
        for (int i = 0; i < 4 && before is int b0; i++)
        {
            System.Threading.Thread.Sleep(18);
            after = VolumeService.Get();
            if (after != null && after != b0) break;
        }

        if (before is int start && (after == null || after == start))
        {
            CoreLog.Warn("[音量] 系统媒体键没生效，改为直接调音频引擎");
            int? now = VolumeService.Step(delta);
            if (now == null)
            {
                Notify("没有找到可用的播放设备，音量调不了");
                return true;
            }
            CoreLog.Info($"[音量] {now}%（直接调音频引擎）");
            return true;
        }

        // 读不到当前音量（设备刚切换 / 非宿主线程）时只报"已发送"，别打出一个空百分号
        if (after is int now2) CoreLog.Info($"[音量] {now2}%（系统横条）");
        else CoreLog.Info("[音量] 已发送系统音量键（这次读不到当前音量）");
        return true;
    }

    /// <summary>切换系统静音。</summary>
    public bool ToggleMute()
    {
        // 同样发系统静音键：这样才有系统自己的静音横条
        bool? before = VolumeService.GetMute();
        InputSender.Tap(VK.VolumeMute);

        bool? after = before;
        for (int i = 0; i < 4 && before != null; i++)
        {
            System.Threading.Thread.Sleep(18);
            after = VolumeService.GetMute();
            if (after != before) break;
        }

        if (before == null || after == before)
        {
            // 媒体键被吞了：直接切。**不自绘横条** —— 系统那条才是用户认的，
            // 两条一起出现只会让人以为按了两次。
            bool? muted = VolumeService.ToggleMute();
            if (muted == null)
            {
                Notify("没有找到可用的播放设备，静音切不了");
                return true;
            }
            CoreLog.Info(muted.Value ? "[静音] 已静音（直接调音频引擎）" : "[静音] 已取消（直接调音频引擎）");
            return true;
        }

        CoreLog.Info(after == true ? "[静音] 已静音（系统横条）" : "[静音] 已取消（系统横条）");
        return true;
    }

    /// <summary>
    /// 调整屏幕亮度。<paramref name="delta"/> 为正=调亮，为负=调暗。
    ///
    /// <paramref name="continuous"/> 为 true 表示这是**按住时的连发**
    /// （引擎每一步都传 arg="1"）。连发时步进不再固定 1%，
    /// 而是走 <see cref="BrightnessAccel"/> 的匀加速：按得越久，每步越大，
    /// 但封顶 2.5 倍 —— 手感上是"越按越快"，不会一下冲到黑屏。
    ///
    /// 两条通道（见 <see cref="BrightnessService"/>）：
    ///   · 笔记本内置屏 → WMI，快且不闪；
    ///   · 台式机外接屏 → DDC/CI，直接跟显示器对话。
    /// 两条都不通时**明确告诉用户**能力不支持，否则按了没反应会被当成按键坏了。
    /// </summary>
    public bool ChangeBrightness(int delta, bool continuous = false)
    {
        if (continuous)
        {
            /* 连发：步长交给加速器算，方向沿用 delta 的符号。
             *
             * 为什么不用固定 1%：45ms 一步 = 22%/秒，从 100 调到 0 要 4.5 秒，
             * 长按到后面会觉得"怎么按都到底了还没到"；但一上来就给大步又太冲。
             * 匀加速正好：起步细、越按越大。 */
            int sign = Math.Sign(delta);
            if (sign != 0) delta = sign * _brightnessAccel.Next();
        }
        else
        {
            // 单次按键（或松开后重新按）= 新的一轮，加速从头开始
            _brightnessAccel.Reset();
        }

        /* 只改**目标值**，不等硬件。
         *
         * DDC/CI 写一次要 50~150ms，读一次 50~100ms —— 在按键路径上同步做，
         * 一个动作 250ms，用户体感就是"按了没反应"，按住连发还会互相排队。
         * 所以这里先把目标定下来、立刻刷横条（跟手），
         * 真正的硬件写入交给 BrightnessService 里的后台线程一步步追，
         * 看起来就是亮度在平滑爬升 —— 和 Windows 音量横条一个路数。
         *
         * v4.3.0：AddTarget 现在只改一个 int，不再抢"硬件锁"。
         * 上一版它会等后台的 DDC 写入（100~250ms），按住连发时
         * 动作队列越积越长 —— 用户调完亮度、手还按着触发键去按音量，
         * 音量动作排在几十条亮度任务后面，表现就是"完全没反应"。 */
        int? target = BrightnessService.AddTarget(delta);
        if (target == null)
        {
            // 说清"为什么没动"，比一句"不支持"有用得多
            string why = BrightnessService.Diagnose();
            CoreLog.Warn("[亮度] 调节失败：" + why);
            Notify(why);
            return true;
        }

        /* 亮度横条只能自己画：Windows 的亮度 OSD 只对内置屏存在
         * （那条是 OEM 驱动画的），外接屏走 DDC/CI 时系统根本不知道值变了。
         * 横条样式与音量那条一致，位置也一样。 */
        UI.Windows.OsdWindow.Show(target.Value);

        // 连续调节时每 45ms 一条会把日志刷爆，只在"单次"时记
        if (Math.Abs(delta) > 1) CoreLog.Info($"[亮度] 目标 {target}%");
        return true;
    }

    public bool ChangeMouseSpeed(int delta)
    {
        var s = Settings();
        s.Global.MouseSpeed = Math.Clamp(s.Global.MouseSpeed + delta, 1, 20);
        Mouse.Speed = s.Global.MouseSpeed;
        Notify($"鼠标速度 {s.Global.MouseSpeed}");
        return true;
    }
}