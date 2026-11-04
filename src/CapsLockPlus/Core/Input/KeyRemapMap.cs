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
            sb.Append(r.From).Append('>').Append(r.To).Append('@').Append(NormalizeDevice(r.Device)).Append('|');
        }
        return sb.ToString();
    }

    /// <summary>构建 vk → vk 的映射表。解析不出键名的条目直接跳过。</summary>
    /// <summary>
    /// 只构建"所有键盘"的映射表（设备专属规则会被忽略）。
    /// 运行时请用 <see cref="BuildTable"/> —— 这个重载主要给"只关心全局规则"的
    /// 场景与自检用。
    /// </summary>
    public static Dictionary<int, int> Build(IEnumerable<KeyRemap> list)
    {
        var map = new Dictionary<int, int>();
        foreach (var r in list)
        {
            if (string.IsNullOrWhiteSpace(r.From) || string.IsNullOrWhiteSpace(r.To)) continue;

            /* 只收"所有键盘"的规则：设备专属规则如果混进这张表，
             * 就会在每只键盘上都生效 —— 那正是这个功能要避免的事。 */
            if (NormalizeDevice(r.Device).Length > 0) continue;

            int? from = KeyName.VkOf(r.From);
            int? to = KeyName.VkOf(r.To);
            if (from == null || to == null) continue;
            if (from == to) continue;          // 自己映射自己：无意义，且会白吞一次

            map[from.Value] = to.Value;        // 后写的覆盖先写的
        }
        return map;
    }

    /// <summary>设备标识规范化（去空白、转大写）：VID/PID 与路径都用大写比较。</summary>
    public static string NormalizeDevice(string? device)
        => (device ?? "").Trim().ToUpperInvariant();

    /// <summary>按设备分组构建改键表。</summary>
    public static RemapTable BuildTable(IEnumerable<KeyRemap> list)
    {
        var table = new RemapTable();
        foreach (var r in list)
        {
            if (string.IsNullOrWhiteSpace(r.From) || string.IsNullOrWhiteSpace(r.To)) continue;

            int? from = KeyName.VkOf(r.From);
            int? to = KeyName.VkOf(r.To);
            if (from == null || to == null) continue;
            if (from == to) continue;

            string dev = NormalizeDevice(r.Device);
            if (dev.Length == 0)
            {
                table.All[from.Value] = to.Value;
                continue;
            }

            if (!table.ByDevice.TryGetValue(dev, out var map))
            {
                map = new Dictionary<int, int>();
                table.ByDevice[dev] = map;
            }

            /* 必须**每条都记**，而不是只在新建字典时记：
             * 同一只键盘的第二条规则如果漏记，那个键就永远不会去问设备，
             * 于是设备规则静默失效（表现为"配了没用"）。 */
            table.DeviceScopedKeys.Add(from.Value);
            map[from.Value] = to.Value;
        }
        return table;
    }

    /// <summary>解析一条配置的显示文案（用于界面与通知）。</summary>
    public static string Describe(IEnumerable<KeyRemap> list)
    {
        var parts = new List<string>();
        foreach (var r in list)
        {
            if (string.IsNullOrWhiteSpace(r.From) || string.IsNullOrWhiteSpace(r.To)) continue;
            string scope = NormalizeDevice(r.Device);
            /* 设备标识（VID/PID）对用户没有意义，展示时换成设备名；
             * 配置里存的仍是标识 —— 名字会随系统语言变，标识不会。 */
            parts.Add(scope.Length == 0
                ? $"{KeyName.Display(r.From)} → {KeyName.Display(r.To)}"
                : Loc.F("{0} → {1}（仅 {2}）",
                    KeyName.Display(r.From), KeyName.Display(r.To),
                    KeyboardDevices.DisplayNameOf(r.Device)));
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
    public static bool Toggle(List<KeyRemap> list, Preset preset) => Toggle(list, preset, "");

    /// <summary>
    /// 切换一条预设（可指定只对某只键盘生效）。
    ///
    /// 判定与删除都带上设备：否则"Caps↔Esc 只在键盘 A"和"Caps↔Esc 在所有键盘"
    /// 会互相把对方toggle 掉，用户看到的是"点一下两处一起消失"。
    /// </summary>
    public static bool Toggle(List<KeyRemap> list, Preset preset, string? device)
    {
        string dev = NormalizeDevice(device);

        bool allPresent = preset.Pairs.All(p =>
            list.Any(r => Same(r.From, p.From) && Same(r.To, p.To) &&
                          NormalizeDevice(r.Device) == dev));

        if (allPresent)
        {
            list.RemoveAll(r => preset.Pairs.Any(p =>
                Same(r.From, p.From) && Same(r.To, p.To) && NormalizeDevice(r.Device) == dev));
            return false;
        }

        foreach (var p in preset.Pairs)
        {
            /* 先把"同一源键 + 同一设备范围"的旧映射删掉，避免 Caps 既映射到 Esc 又映射到 Ctrl
             * （字典里后者会覆盖前者，界面却显示两条，用户会以为没生效）。
             * 注意范围要带上设备：不同键盘的同源键是两条独立规则。 */
            list.RemoveAll(r => Same(r.From, p.From) && NormalizeDevice(r.Device) == dev);
            list.Add(new KeyRemap { From = p.From, To = p.To, Comment = preset.Id, Device = dev });
        }
        return true;
    }

    /// <summary>这条预设当前是否已经生效（供界面决定要不要先确认）。</summary>
    public static bool IsActive(List<KeyRemap> list, Preset preset, string? device)
    {
        string dev = NormalizeDevice(device);
        return preset.Pairs.All(p =>
            list.Any(r => Same(r.From, p.From) && Same(r.To, p.To) &&
                          NormalizeDevice(r.Device) == dev));
    }

    /// <summary>
    /// 这份配置里有没有"把修饰键本身换掉"的规则（Ctrl/Shift/Alt/Win）。
    ///
    /// 这类规则最容易造成"Ctrl+C 变成 Alt+C""所有快捷键都不对了"的错觉，
    /// 启动时要专门问一次。
    /// </summary>
    public static bool HasModifierSwap(IEnumerable<KeyRemap>? list)
    {
        if (list == null) return false;

        static bool IsModifier(string? key) => key?.Trim().ToLowerInvariant() is
            "ctrl" or "lctrl" or "rctrl" or "control" or
            "shift" or "lshift" or "rshift" or
            "alt" or "lalt" or "ralt" or "menu" or
            "win" or "lwin" or "rwin";

        foreach (var r in list)
            if (IsModifier(r.From) || IsModifier(r.To)) return true;

        return false;
    }

    private static bool Same(string a, string b)
        => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
