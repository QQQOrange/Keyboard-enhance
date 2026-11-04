using System;
using System.Collections.Generic;
using System.Linq;
using CapsLockPlus.Core.Settings;

namespace CapsLockPlus.Core.Input;

/// <summary>
/// 基础改键（partial 拆分之四）。
///
/// 语义：把一个物理键改成另一个键（Caps → Esc、Ctrl ↔ Alt）。和"层"是两套东西：
///   · 改键在任何时候都生效（不进层），层内绑定优先；
///   · 改键是**按下即转发**目标键，所以要记账 —— 松开原键时得把目标键也松开，
///     否则会出现"粘键"：Alt 一直按着，之后每个字母都变成菜单快捷键。
///
/// 记账表 <see cref="_remappedDown"/> 在钩子线程读写，统一过 <see cref="_remapGate"/>。
///
/// ⚠ 两个历史坑（v4.17 修）：
///   1. 抬起的注入**必须直发**，不能排进 <c>ActionQueue</c> —— 那条队列上排着
///      启动程序、输入长文本、宏播放，实测能晚 900ms，这期间每个按键都带着修饰键。
///   2. 抬起**绝不能吞**（除了真的有自己的账本那一次）。早先"孤儿抬起"也会被
///      改成目标键的抬起，于是善后补发的抬起被吞 → 系统里的 Alt 永远清不掉。
/// </summary>
public sealed partial class HotkeyEngine
{
    /// <summary>改键账本的一条：替源键按下的目标键 + 按下时刻（毫秒）。</summary>
    private readonly record struct RemapEntry(int Target, long AtMs);

    /// <summary>原键 → 已经替它按下的目标键（松开时按这张表反向撤回）。</summary>
    private readonly Dictionary<int, RemapEntry> _remappedDown = new();

    private readonly object _remapGate = new();

    /// <summary>还欠着抬起的改键条数（诊断 / 看门狗用）。</summary>
    public int PendingRemapCount
    {
        get { lock (_remapGate) return _remappedDown.Count; }
    }

    /// <summary>直发一次注入；失败只记日志，绝不让钩子回调抛出去。</summary>
    private static void SendDirect(Action send)
    {
        try { send(); }
        catch (Exception ex) { CoreLog.Swallow(ex, "改键注入"); }
    }

    /// <summary>
    /// 基础改键：命中则把源键换成目标键。返回 true 表示"这个事件已经被接管"。
    ///
    /// 为什么按 down/up 成对转发，而不是在 down 时发一次 Tap：
    ///   · 按住不放时要能连发 —— 系统对物理键产生的重复 down 会被原样转发成
    ///     目标键的重复 down，效果与真实按住一致；
    ///   · 只发 Tap 的话，长按只会出一个字符。
    /// 代价是要记"谁按下了"，否则抬起时找不到目标键，会粘住。
    /// </summary>
    private bool TryApplyRemap(AppSettings s, int vk, bool down)
    {
        var list = s.Global.BaseRemaps;
        if (list == null || list.Count == 0)
        {
            _remapTable = null;
            return false;
        }

        string stamp = KeyRemapMap.Stamp(list);
        if (_remapTable == null || !string.Equals(stamp, _remapStamp, StringComparison.Ordinal))
        {
            _remapTable = KeyRemapMap.BuildTable(list);
            _remapStamp = stamp;
        }

        /* 只有"配了设备专属规则"的键才去问设备：问设备最多要等十几毫秒
         * （等 Raw Input 到达），摊到每个按键上打字会发涩。 */
        string? device = null;
        if (down && _remapTable.DeviceScopedKeys.Contains(vk))
            device = DeviceResolver?.Invoke(vk, down);

        /* 抬起**先看记账**。 */
        if (!down)
        {
            RemapEntry entry = default;
            bool found;
            lock (_remapGate) found = _remappedDown.Remove(vk, out entry);

            if (found)
            {
                /* 同步直发，不走 ActionQueue —— 见文件头说明。 */
                SendDirect(() => InputSender.KeyUp(entry.Target));
                return true;
            }

            /* 没有账本的孤立抬起：**放行**。
             *
             * 这类抬起通常来自外部善后（InputSafety / 系统清理 / 别的工具）。
             * 吞掉它并改写成"目标键抬起"看起来"更一致"，实际后果是：
             * 如果 lalt 是改键源键，任何补发的 Alt 抬起都会被吞掉，
             * 系统就永远认为 Alt 按着 —— 现场连续 3 小时 20 分、2393 次自愈全部无效。
             *
             * 放行一次孤立的 up 是安全的：它本身不产生输入，只是把状态清干净。 */
            return false;
        }

        if (!_remapTable.TryGet(vk, device, out int target)) return false;

        lock (_remapGate)
            _remappedDown[vk] = new RemapEntry(target, Environment.TickCount64);

        SendDirect(() => InputSender.KeyDown(target));
        return true;
    }

    /// <summary>
    /// 改键看门狗：账本里躺太久、而源键其实早就松开的条目 —— 直接把目标键抬起来。
    ///
    /// 为什么需要：钩子线程可能被系统摘掉、队列可能被 Dispose、进程可能被挂起，
    /// 任何一种都让"抬起"永远到不了。只要目标键（尤其修饰键）留在按下状态，
    /// 整台机器的输入都会变得古怪，必须有一道兜底把它松开。
    /// </summary>
    public int ReleaseStaleRemaps(int maxAgeMs = 2000)
    {
        List<int>? release = null;
        lock (_remapGate)
        {
            if (_remappedDown.Count == 0) return 0;

            long now = Environment.TickCount64;
            foreach (var pair in _remappedDown.ToArray())
            {
                bool tooOld = now - pair.Value.AtMs >= maxAgeMs;

                /* "源键已经松开"的两种证据：
                 *   · 引擎的物理表里没有它（正常路径 —— 抬起被我们收到过）；
                 *   · 钩子线程已经不在跑了（被系统摘钩 / 卸载）—— 这时永远等不到抬起，
                 *     必须靠超时兜底，否则目标键会一直按着。 */
                /* ⚠ 宿主模式下本进程**本来就不装钩子**（_hook.IsRunning 恒为 false），
                 * 不能把它当成"钩子死了"，否则用户按住 Ctrl 超过 2 秒就会被我们强行松开。 */
                bool hookDead = !HookDelegatedToHost && !_hook.IsRunning;
                bool sourceUp = !_physDown.ContainsKey(pair.Key) || hookDead;
                if (!tooOld || !sourceUp) continue;

                (release ??= new List<int>()).Add(pair.Value.Target);
                _remappedDown.Remove(pair.Key);
            }
        }

        if (release == null) return 0;

        foreach (int t in release) SendDirect(() => InputSender.KeyUp(t));

        CoreLog.Warn($"[注入] 改键抬起超时兜底：{release.Count} 个目标键已松开（{string.Join(",", release.Select(KeyName.NameOf))}）");
        return release.Count;
    }

    /// <summary>
    /// 立刻松开所有改键目标键（公开版，供 AppHost 在钩子宿主退出 / 暂停时调用）。
    /// </summary>
    public void ReleaseRemapTargets()
    {
        try { ReleaseRemappedKeys(); }
        catch (Exception ex) { CoreLog.Swallow(ex, "松开改键目标键"); }
    }

    /// <summary>
    /// 松开所有"改键按下"的目标键。
    /// 引擎被禁用 / 层状态复位 / 钩子退出时调用，避免目标键粘住（表现为某个键一直按着）。
    /// </summary>
    private void ReleaseRemappedKeys()
    {
        /* 先加锁取快照再发键：
         * 这个方法会被界面线程（暂停 / 挂起）和钩子线程（触发键复位）同时调用，
         * 而 `_remappedDown` 另一头正被 TryApplyRemap 增删。
         * 直接遍历会抛"集合已修改"—— 那样后面几个键就松不开了。 */
        int[] targets;
        lock (_remapGate)
        {
            if (_remappedDown.Count == 0) return;
            targets = _remappedDown.Values.Select(v => v.Target).ToArray();
            _remappedDown.Clear();
        }

        /* 同步直发：复位/暂停/退出这些路径上，队列可能已经停了或者正忙，
         * 再往队列里排等于把这几个键永远按着。 */
        foreach (int t in targets) SendDirect(() => InputSender.KeyUp(t));
    }
}
