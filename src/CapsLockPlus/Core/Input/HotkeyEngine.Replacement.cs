using System;
using System.Linq;
using CapsLockPlus.Core.Settings;

namespace CapsLockPlus.Core.Input;

/// <summary>
/// 「原键替换层」的运行时：**不需要触发键**的那一组绑定。
///
/// 用户的原话是"把原来需要触发键才能触发的功能，现在变成不用触发键按了也能触发"。
/// 所以这里的语义只有一条：这个物理键被配过 → 按下去就执行它绑的功能，
/// 原来的字符不发给前台。没配过的键一概不管（不吞、不改），打字手感不受影响。
///
/// 与两套既有机制的分工：
///   · 基础改键（<see cref="AppSettings.GlobalOptions.BaseRemaps"/>）只能换成"另一个键"，
///     而且是先吞后发，多了一层中间态；
///   · 层内绑定要按住触发键。
/// 替换层是"任意动作 + 零触发"，两者都不覆盖，所以要单独一层。
///
/// 优先级：替换层 &gt; 基础改键 &gt; 层。都命中同一个键时，用户写得越具体越优先。
/// </summary>
public partial class HotkeyEngine
{
    /// <summary>按下被替换层吃掉的键 —— 松开时必须成对吞掉。</summary>
    private readonly System.Collections.Generic.HashSet<int> _replacementDown = new();

    /// <summary>修饰键本身不参与替换（"把 Ctrl 换成 1"没有意义，还会把组合键拆散）。</summary>
    private static bool IsModifierVk(int vk)
        => vk is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C
           or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5;

    /// <summary>鼠标左/右/中/侧键 1/侧键 2（滚轮不在此列）。</summary>
    private static bool IsMouseVk(int vk) => vk is >= 0x01 and <= 0x06;

    /// <summary>
    /// 原键替换层。命中就执行并吞掉这个键，返回 true。
    ///
    /// 位置在 <see cref="TryApplyRemap"/> 之前：改键是"换成另一个键"，
    /// 替换层是"这个键现在就是这个功能"，后者更具体，而且不该再走一遍
    /// "发一个合成键、再被自己拦回来"的路径。
    /// </summary>
    private bool TryApplyReplacement(AppSettings s, int vk, bool down, string? device)
    {
        /* 原键替换页关掉“鼠标键改动”时，鼠标键在替换层这一关直接放行。
         * 放在最前面：连账本都不碰，抬起自然也不会被误吞。 */
        if (IsMouseVk(vk) && !DeviceProfiles.ReplaceMouseKeysEnabled(s, device))
            return false;

        /* 绝大多数人没配过替换层：一次字段比较就返回，别让每个按键都去拼组合键。
         * 这也是"打字不能发涩"那条红线的延续。 */
        bool any = (s.Replacements?.Count ?? 0) > 0
                   || s.Profiles.Any(p => p.Replacements.Count > 0);
        if (!any)
        {
            if (_replacementDown.Count > 0) _replacementDown.Clear();
            return false;
        }

        if (!down)
        {
            if (!_replacementDown.Remove(vk)) return false;

            /* 抬起要按"层内键"那套账本来结：吞过的 down 记录、欠着的 up、
             * 连续量动作的计时器，三样都得清 —— 只清一处会让这个键
             * 下次按下被当成"重复触发"直接吞掉（动作永远只生效一次）。 */
            _swallowedDown.TryRemove(vk, out _);
            _swallowedUp.TryRemove(vk, out _);
            StopContinuousRepeat(vk);
            return true;
        }

        if (IsModifierVk(vk)) return false;

        string? keyName = KeyName.NameOf(vk);
        if (keyName == null) return false;

        var mods = KeyModifiers.None;
        if (_laltDown) mods |= KeyModifiers.Alt;
        if (_lwinDown || _rwinDown) mods |= KeyModifiers.Win;
        if (_ctrlDown) mods |= KeyModifiers.Ctrl;
        if (_shiftDown) mods |= KeyModifiers.Shift;

        string comboId = KeyName.BuildComboEx(mods, keyName);

        /* 设备方案 → 通用设置 → 全局那一份。blocked 表示"用户在这台设备上
         * 明确清掉了 / 这是独立方案"，那就不许再回退到全局。 */
        KeyBinding? hit = DeviceProfiles.ResolveDeviceReplacement(s, comboId, device, out bool blocked);
        if (hit == null && !blocked) hit = DeviceProfiles.FindGlobalReplacement(s, comboId);
        if (hit == null) return false;

        /* 替换层里「没有动作」＝没有规则，放行。
         *
         * 这条 return false 是 wGesture 事件的修法：替换层原来把 doNothing 当成
         * "吞掉这个键"，于是只要界面上不小心落过一条 doNothing（切分类、清空绑定
         * 都会），那个键在这台机器上就再也没有别的程序能收到了 —— 用户看到的是
         * "我什么都没配，中键却失效了"。禁用按键请用明确的「禁用这个键」动作。 */
        if (!hit.IsBound) return false;

        _replacementDown.Add(vk);

        /* 明确的禁用动作：吞掉，不执行任何东西。 */
        if (string.Equals(hit.Action, "swallowKey", StringComparison.OrdinalIgnoreCase)) return true;

        return ExecuteBinding(hit, vk, oweUp: true);
    }
}
