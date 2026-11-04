using System;
using System.Collections.Generic;
using CapsLockPlus.Core.Settings;

namespace CapsLockPlus.Core.Input;

/// <summary>
/// 「双击动作」（partial 拆分之三）。
///
/// 语义：同一个键在 <see cref="DoubleTapWindowMs"/> 内被按两次 → 执行配置的动作，
/// 第二次的按下和抬起都要吞掉（不能漏出第二个字符）。
///
/// 与触发键自带的 DoubleTapAction 的区别：
///   · 那个是"按住进层"模型的附属品，会改变该键的基本行为；
///   · 这里对任何键都成立，单击完全保持原样。
/// </summary>
public sealed partial class HotkeyEngine
{
    /* 第二次按下被吞之后，它的抬起也要吞 —— 否则前台会收到一个
     * "从未按下过"的 keyup，游戏 / 远程桌面会判定输入异常。 */
    private readonly HashSet<int> _gestureSwallowUp = new();

    /// <summary>
    /// 双击触发键的判定窗口（毫秒）。
    ///
    /// 公开出来给界面文案引用 —— 之前界面上两处都写"600 毫秒内"，
    /// 而实现是 420ms，用户按 500ms 的间隔双击会失败，还以为双击坏了。
    /// </summary>
    public const int DoubleTapWindowMs = 420;

    /// <summary>
    /// 双击动作：同一个键在 <see cref="DoubleTapWindowMs"/> 内被按两次时执行动作。
    ///
    /// 语义取舍：
    ///   · **第一次按下照常放行**（该打字就打字），只有"第二次"才被吞掉并改成动作；
    ///   · 自动重复的 down 不算"第二次按下"，否则按住会疯狂触发；
    ///   · 触发后连这个键的抬起一起吞掉，避免前台收到"没按下过的抬起"。
    /// </summary>
    private bool TryRunDoubleTapGesture(AppSettings s, int vk, bool wasPhysicallyDown)
    {
        var list = s.Global.KeyGestures;
        if (list == null || list.Count == 0)
        {
            _gestureMap = null;
            return false;
        }

        string stamp = string.Concat(list.Select(g => g.Key + ">" + g.DoubleTapAction + "|"));
        if (_gestureMap == null || !string.Equals(stamp, _gestureStamp, StringComparison.Ordinal))
        {
            var map = new Dictionary<int, string>();
            foreach (var g in list)
            {
                if (string.IsNullOrWhiteSpace(g.Key) || string.IsNullOrWhiteSpace(g.DoubleTapAction)) continue;
                int? k = KeyName.VkOf(g.Key);
                if (k == null) continue;
                map[k.Value] = g.DoubleTapAction;
            }
            _gestureMap = map;
            _gestureStamp = stamp;
        }

        if (!_gestureMap.TryGetValue(vk, out string? action) || string.IsNullOrWhiteSpace(action))
            return false;

        if (wasPhysicallyDown) return false;      // 系统自动重复，不算第二次

        long now = Environment.TickCount64;
        bool isSecond;
        lock (_remapGate)                       // 与 ResetTriggerState 的 Clear 互斥（同一把短锁）
        {
            isSecond = _gestureLastTick.TryGetValue(vk, out long last)
                       && now - last <= DoubleTapWindowMs;
            if (isSecond) _gestureLastTick.Remove(vk);
            else _gestureLastTick[vk] = now;
        }
        if (isSecond)
        {
            _gestureSwallowUp.Add(vk);
            _queue.Post(() => RunTapAction(action));
            return true;
        }
        return false;
    }
}
