using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using CapsLockPlus.Core.Settings;

namespace CapsLockPlus.Core.Input;

/// <summary>
/// 文本片段：**打完触发词按空格**就替换。
///
/// 为什么要在引擎里维护一个"最近敲了什么"的缓冲：
/// 替换必须发生在空格按下的那一刻，而那一刻唯一可靠的输入源就是低级键盘钩子
/// （界面线程的文本框根本不知道用户在别的程序里打了什么）。
/// 钩子只给 VK，所以这里维护一张最小的 VK → ASCII 映射，按"一个词"的粒度累积；
/// 遇到空格 / 回车 / 方向键 / 修饰键就断词。
///
/// 与旧的「Caps+Tab 展开」并存：那条路读的是光标前的真实文本，不依赖这个缓冲。
/// </summary>
public partial class HotkeyEngine
{
    /// <summary>当前正在敲的词（小写化，最长 64 字符）。</summary>
    private readonly List<char> _typed = new(64);

    private const int MaxTyped = 64;

    /// <summary>空格 / 回车等"断词"键。</summary>
    private static bool IsWordBreakKey(int vk) => vk is
        0x0D or 0x09 or 0x1B or                    // Enter / Tab / Esc
        0x25 or 0x26 or 0x27 or 0x28 or            // 方向键
        0x21 or 0x22 or 0x23 or 0x24 or            // PgUp / PgDn / End / Home
        0x2D or 0x2E;                              // Insert / Delete

    /// <summary>
    /// VK → 字符（小写）。不认识的返回 '\0'（视为断词）。
    ///
    /// 只覆盖 ASCII：文本片段的触发词本来就该是手能顺畅敲出来的东西，
    /// 也避免和输入法打架（打中文时这些 VK 根本对不上词）。
    /// </summary>
    private static char VkToChar(int vk)
    {
        if (vk >= 0x41 && vk <= 0x5A) return (char)('a' + (vk - 0x41));     // A–Z
        if (vk >= 0x30 && vk <= 0x39) return (char)('0' + (vk - 0x30));     // 0–9
        return vk switch
        {
            0x20 => ' ',        // 空格单独处理
            0x6A => '*',        // 小键盘 *
            0x6B => '+',
            0x6D => '-',
            0x6E => '.',
            0x6F => '/',
            0xBA => ';',
            0xBB => '=',
            0xBC => ',',
            0xBD => '-',
            0xBE => '.',
            0xBF => '/',
            0xC0 => '`',
            0xDB => '[',
            0xDC => '\\',
            0xDD => ']',
            0xDE => '\'',
            _ => '\0',
        };
    }

    /// <summary>
    /// 匹配一条文本片段。
    ///
    /// 规则：缓冲以「前缀 + 触发词」结尾即命中；多个同时命中时取**最长**的那个
    /// （"ab" 和 "abc" 都在表里时，用户刚打完 abc 应该用 abc）。
    /// 抽成静态纯函数是为了自检能直接验，不必真的敲键盘。
    /// </summary>
    internal static bool TryMatchHotstring(string typed, HotstringOptions options,
                                           IEnumerable<QEntry> entries,
                                           out int deleteCount, out string replacement,
                                           out QEntry? matched)
    {
        deleteCount = 0;
        replacement = "";
        matched = null;

        if (string.IsNullOrEmpty(typed)) return false;

        string prefix = options.EffectivePrefix;
        int bestLen = 0;

        foreach (var hs in entries)
        {
            if (hs == null || !hs.Enabled) continue;
            string key = hs.Key?.Trim() ?? "";
            if (key.Length == 0) continue;

            string trigger = prefix + key;
            if (trigger.Length > typed.Length) continue;
            if (!typed.EndsWith(trigger, StringComparison.OrdinalIgnoreCase)) continue;

            if (trigger.Length > bestLen)
            {
                bestLen = trigger.Length;
                matched = hs;
                replacement = hs.Value ?? "";
            }
        }

        if (matched == null) return false;

        deleteCount = bestLen;
        return true;
    }

    /// <summary>
    /// 处理一个按键事件里的"文本片段"部分。
    /// 返回 true = 这个键已经被消费（目前只有"展开时吞掉那个空格"）。
    /// </summary>
    private bool HandleHotstringKey(AppSettings settings, int vk, bool down)
    {
        var opt = settings.Hotstring;
        if (opt == null || !opt.ExpandOnSpace) return false;
        if (settings.HotStrings == null || settings.HotStrings.Count == 0) return false;

        /* 按住触发键时一律不管：那是在用层功能（Caps+Space 可能是用户的绑定）。 */
        if (IsTriggerHeld) return false;

        /* 带 Ctrl/Alt/Win 的组合键不是"打字"，断词即可。 */
        if (down && (vk is 0x11 or 0x12 or 0x5B or 0x5C or 0xA2 or 0xA3 or 0xA0 or 0xA1)) { _typed.Clear(); return false; }

        if (!down) return false;

        if (vk == 0x20)                       // 空格
        {
            string typed = new string(_typed.ToArray());
            _typed.Clear();

            if (!TryMatchHotstring(typed, opt, settings.HotStrings,
                                   out int deleteCount, out string replacement, out _))
                return false;

            string text = replacement.Replace("\\n", Environment.NewLine);
            bool swallow = opt.SwallowSpace;

            /* 真正干活放到动作队列线程：钩子回调里不能做几十次 SendInput，
             * 那会把回调拖过系统的 300ms 限制（钩子被摘掉的老问题）。 */
            _queue.Post(() =>
            {
                try
                {
                    for (int i = 0; i < deleteCount; i++) InputSender.Tap(VK.Back);
                    if (!swallow)
                    {
                        // 先补回那个空格，再插替换内容（顺序反了会变成"文字+空格"）
                        InputSender.Tap(VK.Space);
                    }
                    InputSender.Text(text);
                }
                catch (Exception ex) { CoreLog.Swallow(ex, "展开文本片段"); }
            });

            CoreLog.Info($"文本片段：{typed} → 已展开（删 {deleteCount} 个字符，吞空格={swallow}）");

            /* 返回 true 会把这个空格吞掉；不吞时我们自己在队列里补了一个空格，
             * 所以这里一律返回 true，避免"补一个 + 系统再传一个"变成两个空格。 */
            return true;
        }

        if (vk == 0x08)                              // 退格 = 删掉一个字符
        {
            if (_typed.Count > 0) _typed.RemoveAt(_typed.Count - 1);
            return false;
        }

        if (IsWordBreakKey(vk))
        {
            _typed.Clear();
            return false;
        }

        char c = VkToChar(vk);
        if (c == '\0')
        {
            _typed.Clear();                          // 不认识的键 = 词断了
            return false;
        }

        if (_typed.Count >= MaxTyped) _typed.RemoveAt(0);
        _typed.Add(c);
        return false;
    }
}
