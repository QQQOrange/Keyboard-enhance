using CapsLockPlus.Core.Settings;

namespace CapsLockPlus.Core.Input;

/// <summary>
/// 「基础改键」的解析与缓存。
///
/// ## 为什么要单独一层
///
/// 层内绑定（按住触发键才生效）已经很强，但用户最朴素的诉求是
/// "**把这个键直接换成那个键**"——Caps 占着最好的位置却最没用、
/// Ctrl 与 Alt 反人类、笔记本没有独立的 Home/End。
/// 这些都不该强制用户"先按住一个前缀键"，所以需要一张**始终生效**的映射表。
///
/// ## 与层的优先级
///
/// 层内绑定优先：层是"临时接管整个键盘"，基础改键只在基础状态生效。
/// 判定顺序在 <see cref="HotkeyEngine"/> 里：触发键 → 层 → 基础改键。
///
/// ## 性能
///
/// 这张表在**每一次按键**上都要查一次，所以：
///   · 用 int→int 的字典，不做字符串比较；
///   · 用"内容指纹"判断是否需要重建，避免每次按键都重新解析键名。
/// </summary>
public static class KeyRemapMap
{
    /// <summary>按内容指纹判断缓存是否失效。</summary>
    public static string Stamp(IEnumerable<KeyRemap> list)
    {
        var sb = new System.Text.StringBuilder(64);
        foreach (var r in list)
        {
            if (string.IsNullOrWhiteSpace(r.From) || string.IsNullOrWhiteSpace(r.To)) continue;
            sb.Append(r.From).Append('>').Append(r.To).Append('|');
        }
        return sb.ToString();
    }

    /// <summary>构建 vk → vk 的映射表。解析不出键名的条目直接跳过。</summary>
    public static Dictionary<int, int> Build(IEnumerable<KeyRemap> list)
    {
        var map = new Dictionary<int, int>();
        foreach (var r in list)
        {
            if (string.IsNullOrWhiteSpace(r.From) || string.IsNullOrWhiteSpace(r.To)) continue;

            int? from = KeyName.VkOf(r.From);
            int? to = KeyName.VkOf(r.To);
            if (from == null || to == null) continue;
            if (from == to) continue;          // 自己映射自己：无意义，且会白吞一次

            map[from.Value] = to.Value;        // 后写的覆盖先写的
        }
        return map;
    }

    /// <summary>解析一条配置的显示文案（用于界面与通知）。</summary>
    public static string Describe(IEnumerable<KeyRemap> list)
    {
        var parts = new List<string>();
        foreach (var r in list)
        {
            if (string.IsNullOrWhiteSpace(r.From) || string.IsNullOrWhiteSpace(r.To)) continue;
            parts.Add($"{KeyName.Display(r.From)} → {KeyName.Display(r.To)}");
        }
        return string.Join("、", parts);
    }

    /// <summary>一条预设：成对的改键（成对是为了支持"互换"）。</summary>
    public sealed class Preset
    {
        public Preset(string id, string title, params (string From, string To)[] pairs)
        {
            Id = id;
            Title = title;
            Pairs = pairs;
        }

        public string Id { get; }
        public string Title { get; }
        public (string From, string To)[] Pairs { get; }
    }

    /// <summary>
    /// 内置预设。
    ///
    /// 只收"机械键盘用户第一周一定会改"的几组 —— 预设的价值是降低首次配置门槛，
    /// 不是覆盖所有玩法；自定义映射走配置表。
    /// </summary>
    public static readonly Preset[] Presets =
    {
        new("capsEsc", "Caps ↔ Esc",
            ("capslock", "esc"), ("esc", "capslock")),
        new("ctrlAlt", "Ctrl ↔ Alt（左）",
            ("lctrl", "lalt"), ("lalt", "lctrl")),
        new("capsCtrl", "Caps → Ctrl",
            ("capslock", "lctrl")),
        new("winAlt", "Win ↔ Alt（左）",
            ("lwin", "lalt"), ("lalt", "lwin")),
    };

    public static Preset? FindPreset(string? id)
        => Presets.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 切换一条预设：已全部启用则移除，否则补齐。
    /// 返回 true = 本次是"启用"。
    /// </summary>
    public static bool Toggle(List<KeyRemap> list, Preset preset)
    {
        bool allPresent = preset.Pairs.All(p =>
            list.Any(r => Same(r.From, p.From) && Same(r.To, p.To)));

        if (allPresent)
        {
            list.RemoveAll(r => preset.Pairs.Any(p => Same(r.From, p.From) && Same(r.To, p.To)));
            return false;
        }

        foreach (var p in preset.Pairs)
        {
            /* 先把"同一源键"的旧映射删掉，避免 Caps 既映射到 Esc 又映射到 Ctrl
             * （字典里后者会覆盖前者，界面却显示两条，用户会以为没生效）。 */
            list.RemoveAll(r => Same(r.From, p.From));
            list.Add(new KeyRemap { From = p.From, To = p.To, Comment = preset.Id });
        }
        return true;
    }

    private static bool Same(string a, string b)
        => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
