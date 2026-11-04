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
/// </summary>
public sealed partial class HotkeyEngine
{
    /// <summary>原键 → 已经替它按下的目标键（松开时按这张表反向撤回）。</summary>
    private readonly Dictionary<int, int> _remappedDown = new();

    private readonly object _remapGate = new();

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

        /* 抬起**先看记账**，再查表。
         *
         * 顺序反了会卡键：设备专属规则在"抬起"这一刻没有设备标识（只有按下才解析），
         * 查表会落空 → 直接 return false → 注入的目标键永远不松开。
         * 表现是"按一下某键，之后那个目标键一直粘着"。
         */
        if (!down)
        {
            int held = 0;
            bool found;
            lock (_remapGate)
            {
                found = _remappedDown.Remove(vk, out held);
            }
            if (found)
            {
                int t = held;
                _queue.Post(() => InputSender.KeyUp(t));
                return true;
            }

            // 记账丢了（进程重启/状态复位）也要把目标键松开，宁可多一次无害的 up
            if (_remapTable.TryGet(vk, null, out int orphan))
            {
                int t = orphan;
                _queue.Post(() => InputSender.KeyUp(t));
                return true;
            }
            return false;
        }

        if (!_remapTable.TryGet(vk, device, out int target)) return false;

        lock (_remapGate) _remappedDown[vk] = target;
        int pressed = target;
        _queue.Post(() => InputSender.KeyDown(pressed));
        return true;
    }

    /// <summary>
    /// 松开所有"改键按下"的目标键。
    /// 引擎被禁用 / 层状态复位时调用，避免目标键粘住（表现为某个键一直按着）。
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
            targets = _remappedDown.Values.ToArray();
            _remappedDown.Clear();
        }

        foreach (int t in targets)
        {
            _queue.Post(() => InputSender.KeyUp(t));
        }
    }
}
