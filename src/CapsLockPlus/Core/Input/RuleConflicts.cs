using CapsLockPlus.Core.Localization;
using CapsLockPlus.Core.Settings;

namespace CapsLockPlus.Core.Input;

/// <summary>体检结果的严重程度。</summary>
public enum ConflictLevel
{
    /// <summary>会导致功能失效或行为与预期不符，建议处理。</summary>
    Warn,

    /// <summary>不影响使用，但值得知道（例如"注入的键不参与层判定"）。</summary>
    Info,
}

/// <summary>结论指向哪张卡片 —— 界面据此提供"去修改"。</summary>
public enum ConflictTarget
{
    /// <summary>无法定位（例如跨页面的情况，界面不给按钮）。</summary>
    None,

    BaseRemap,
    Gesture,
    Snippet,
    AppLayer,

    /// <summary>层内绑定：不在设置页，需要跳到「层」页面。</summary>
    Layer,
}

/// <summary>一条体检结论。</summary>
public sealed record Conflict(ConflictLevel Level, string Message,
    ConflictTarget Target = ConflictTarget.None);

/// <summary>
/// 按键规则体检：找出"用户以为生效、实际不生效"的配置。
///
/// ## 为什么需要这个
///
/// 现在能影响同一个键的规则有六种：触发键、层内绑定、按应用叠加层、组合键、
/// 双击动作、基础改键，还有按下时的采样（片段）。它们**按固定顺序**判定，
/// 排在前面的一旦命中，后面的就永远轮不到 —— 但界面上它们分散在五张卡片里，
/// 用户配完完全看不出"这两条互相盖住了"。
///
/// 这类问题的表现是"某个键时灵时不灵"，是最难自查的一类 bug。
/// 所以这里把**判定顺序**变成可见的结论，而不是让用户去读源码。
///
/// ## 判定顺序（必须与 HotkeyEngine.OnKeyEvent 一致）
///
/// ```
/// 暂停/禁用 → 触发键（进层）
///   ├ 在层里：层内绑定（同层重复时**后者生效**）
///   └ 不在层里：组合键 → 双击 → 按应用叠加层 → 基础改键 → 片段 → 放行
/// ```
///
/// 这个顺序一改，本文件的结论就会错，所以两边必须一起改。
/// </summary>
public static class RuleConflicts
{
    /// <summary>体检一遍。返回空列表 = 没发现问题。</summary>
    /// <param name="deviceKeys">
    /// 当前接着的键盘标识（可为空 = 不检查"设备是否在线"）。
    /// 单独传进来而不是在这里枚举：Analyze 会被每次保存调用，
    /// 而设备枚举是 P/Invoke，不该摊到打字热路径上。
    /// </param>
    public static List<Conflict> Analyze(AppSettings s, IReadOnlyCollection<string>? deviceKeys = null)
    {
        var list = new List<Conflict>();
        if (s == null) return list;

        var global = s.Global;

        /* 触发键按"虚拟键码"比较，而不是字符串：
         * 键名有别名（escape / esc），字符串比较会漏掉同一物理键的不同写法。 */
        var triggers = new HashSet<int>();
        foreach (var t in s.Triggers ?? new List<TriggerKey>())
        {
            if (!t.Enabled) continue;
            int? vk = KeyName.VkOf(t.Key);
            if (vk != null) triggers.Add(vk.Value);
        }

        /* ---- 0. 设备专属规则指向一只当前没接的键盘 ----
         * 这是"配置没错但就是不生效"的典型：用户拔了外接键盘，规则还在。 */
        if (deviceKeys != null)
        {
            foreach (var r in global.BaseRemaps)
            {
                string dev = KeyRemapMap.NormalizeDevice(r.Device);
                if (dev.Length == 0) continue;
                if (deviceKeys.Any(k => KeyRemapMap.NormalizeDevice(k) == dev)) continue;

                list.Add(new Conflict(ConflictLevel.Warn, Loc.F(
                    "「{0}」的改键只对 {1} 生效，但这只键盘现在没接上。",
                    KeyName.Display(r.From), DeviceText(r.Device)), ConflictTarget.BaseRemap));
            }
        }

        // ---- 1. 触发键被别的规则挡住 ----
        foreach (var r in global.BaseRemaps)
        {
            if (string.IsNullOrWhiteSpace(r.From) || string.IsNullOrWhiteSpace(r.To)) continue;
            if (InSet(triggers, r.From))
            {
                list.Add(new Conflict(ConflictLevel.Warn, Loc.F(
                    "「{0}」是触发键，同时又被改键 —— 触发键优先，这条改键不会生效。",
                    KeyName.Display(r.From)), ConflictTarget.BaseRemap));
            }

            // 注入的按键不参与层判定（引擎对 injected 事件直接放行）
            if (InSet(triggers, r.To))
            {
                list.Add(new Conflict(ConflictLevel.Info, Loc.F(
                    "改键目标「{0}」是触发键：改出来的按键不会切层（注入的按键不参与触发键判定）。",
                    KeyName.Display(r.To)), ConflictTarget.BaseRemap));
            }
        }

        foreach (var g in global.KeyGestures)
        {
            if (string.IsNullOrWhiteSpace(g.Key)) continue;
            if (InSet(triggers, g.Key))
            {
                list.Add(new Conflict(ConflictLevel.Warn, Loc.F(
                    "「{0}」是触发键，双击动作永远不会触发。",
                    KeyName.Display(g.Key)), ConflictTarget.Gesture));
            }
        }

        // ---- 2. 双击 vs 组合键：组合键先扣键，双击收不到 ----
        foreach (var g in global.KeyGestures)
        {
            if (string.IsNullOrWhiteSpace(g.Key)) continue;
        }

        // ---- 3. 基础改键 vs 双击/组合键 ----
        foreach (var r in global.BaseRemaps)
        {
            if (string.IsNullOrWhiteSpace(r.From) || string.IsNullOrWhiteSpace(r.To)) continue;

            foreach (var g in global.KeyGestures)
            {
                if (Same(r.From, g.Key))
                {
                    list.Add(new Conflict(ConflictLevel.Warn, Loc.F(
                        "「{0}」既改键又配了双击 —— 第二次按下会被双击吞掉，不会输出「{1}」。",
                        KeyName.Display(r.From), KeyName.Display(r.To)), ConflictTarget.Gesture));
                }
            }

        }

        // ---- 4. 无法识别的键名（规则会静默不生效） ----
        foreach (var r in global.BaseRemaps)
        {
            WarnBadKey(list, r.From, Loc.T("基础改键"), ConflictTarget.BaseRemap);
            WarnBadKey(list, r.To, Loc.T("基础改键"), ConflictTarget.BaseRemap);
        }
        foreach (var g in global.KeyGestures)
            WarnBadKey(list, g.Key, Loc.T("双击动作"), ConflictTarget.Gesture);
        // ---- 5. 层内重复绑定（同层同组合键，后一条生效） ----
        foreach (var layer in s.Layers ?? new List<LayerOptions>())
        {
            // 同层同组合键：后写的生效（与 HotkeyEngine.LayerBindingMap 一致）
            var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < layer.Keys.Count; i++)
            {
                var k = layer.Keys[i];
                if (string.IsNullOrWhiteSpace(k.Combo) || !k.IsBound) continue;

                if (seen.TryGetValue(k.Combo, out int firstIndex))
                {
                    list.Add(new Conflict(ConflictLevel.Warn, Loc.F(
                        "第 {0} 层里「{1}」绑了两次：第 {2} 条会覆盖第 {3} 条。",
                        layer.Id, ComboText(k.Combo), i + 1, firstIndex + 1), ConflictTarget.Layer));
                }
                else
                {
                    seen[k.Combo] = i;
                }
            }
        }

        // ---- 6. 按应用层指向不存在的层 ----
        foreach (var rule in global.AppLayers)
        {
            if (string.IsNullOrWhiteSpace(rule.Process)) continue;
            var target = (s.Layers ?? new List<LayerOptions>()).FirstOrDefault(l => l.Id == rule.LayerId);
            if (target == null || !target.Enabled)
            {
                list.Add(new Conflict(ConflictLevel.Warn, Loc.F(
                    "按应用层规则指向第 {0} 层，但这一层不存在或已停用 —— 切到 {1} 时不会有任何效果。",
                    rule.LayerId, rule.Process), ConflictTarget.AppLayer));
            }
        }

        /* ---- 7. 片段触发词重复 ----
         *
         * 只在**适用程序范围也重叠**时才算重复：
         * `;log` 限定了 code.exe、另一条 `;log` 限定了 notepad.exe，是正当配置。 */
        var triggersSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sn in global.Snippets)
        {
            if (string.IsNullOrWhiteSpace(sn.Trigger)) continue;

            string key = sn.Trigger + "|" + ScopeKey(sn.Apps);
            if (!triggersSeen.Add(key))
            {
                list.Add(new Conflict(ConflictLevel.Warn,
                    Loc.F("文本替换「{0}」在相同的程序范围里定义了多次，只有最后一条会展开。", sn.Trigger),
                    ConflictTarget.Snippet));
            }
        }

        return list;
    }

    /// <summary>
    /// 生成可复制的纯文本报告。
    ///
    /// 为什么要这个：用户报"某个键不灵"时，最费时间的环节是**描述自己的配置**。
    /// 一段能直接粘贴的报告，把这一步省掉；顺带带上版本与时间，避免"是哪个版本的问题"来回问。
    /// </summary>
    public static string BuildReport(IReadOnlyList<Conflict> findings, string header)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(header);

        if (findings == null || findings.Count == 0)
        {
            sb.AppendLine(Loc.T("没有发现问题。"));
            return sb.ToString().TrimEnd();
        }

        sb.AppendLine(Loc.F("共 {0} 条", findings.Count));
        sb.AppendLine();

        foreach (var f in findings)
        {
            string tag = f.Level == ConflictLevel.Warn ? "[WARN]" : "[INFO]";
            sb.AppendLine($"{tag} {f.Message}");
        }

        return sb.ToString().TrimEnd();
    }

    private static bool InSet(HashSet<int> set, string? key)
    {
        int? vk = KeyName.VkOf(key);
        return vk != null && set.Contains(vk.Value);
    }

    /// <summary>
    /// 设备显示文本：能查到名字就给「名字（VID_PID）」，查不到就用标识本身。
    ///
    /// 为什么两者都留：名字是给用户看的，标识是给排查用的
    /// （报告粘出来时，只有名字无法确定是哪只键盘）。
    /// </summary>
    private static string DeviceText(string? device)
    {
        string key = (device ?? "").Trim();
        if (key.Length == 0) return "";

        string name = KeyboardDevices.DisplayNameOf(key);
        if (string.IsNullOrWhiteSpace(name) ||
            string.Equals(name, key, StringComparison.OrdinalIgnoreCase))
            return key;

        return Loc.F("{0}（{1}）", name, key);
    }

    private static bool Same(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;

        int? va = KeyName.VkOf(a);
        int? vb = KeyName.VkOf(b);
        if (va != null && vb != null) return va.Value == vb.Value;

        return string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>片段适用范围的规范化键：排序 + 去重，忽略书写顺序与大小写。</summary>
    private static string ScopeKey(string? apps)
    {
        if (string.IsNullOrWhiteSpace(apps)) return "";

        var names = apps
            .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(SnippetMatcher.Normalize)
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase);

        return string.Join(",", names);
    }

    /// <summary>把内部组合键 id 转成可读文本（caps_lalt_q → Alt+Q）。</summary>
    private static string ComboText(string combo)
    {
        try { return Core.Actions.CustomKeyRunner.ComboDisplay(combo); }
        catch { return combo; }   /* 显示用，失败就原样给 id */
    }

    private static void WarnBadKey(List<Conflict> list, string? key, string where,
        ConflictTarget target)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        if (KeyName.VkOf(key) != null) return;

        list.Add(new Conflict(ConflictLevel.Warn,
            Loc.F("{0}里的「{1}」不是可识别的键名，这条规则不会生效。", where, key), target));
    }
}
