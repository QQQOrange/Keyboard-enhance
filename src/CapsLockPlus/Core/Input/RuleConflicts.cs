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
    Chord,
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
    public static List<Conflict> Analyze(AppSettings s)
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

        foreach (var c in global.Chords)
        {
            foreach (string key in new[] { c.Key1, c.Key2 })
            {
                if (string.IsNullOrWhiteSpace(key)) continue;
                if (InSet(triggers, key))
                {
                    list.Add(new Conflict(ConflictLevel.Warn, Loc.F(
                        "组合键里的「{0}」是触发键 —— 按住它会先进层，组合键组不起来。",
                        KeyName.Display(key)), ConflictTarget.Chord));
                }
            }

            if (!string.IsNullOrWhiteSpace(c.Key1) &&
                string.Equals(c.Key1, c.Key2, StringComparison.OrdinalIgnoreCase))
            {
                list.Add(new Conflict(ConflictLevel.Warn, Loc.F(
                    "组合键的两个键都是「{0}」，永远不会触发。", KeyName.Display(c.Key1),
                    ConflictTarget.Chord)));
            }
        }

        // ---- 2. 双击 vs 组合键：组合键先扣键，双击收不到 ----
        foreach (var g in global.KeyGestures)
        {
            if (string.IsNullOrWhiteSpace(g.Key)) continue;
            foreach (var c in global.Chords)
            {
                if (Same(g.Key, c.Key1) || Same(g.Key, c.Key2))
                {
                    list.Add(new Conflict(ConflictLevel.Warn, Loc.F(
                        "「{0}」同时配了双击和组合键 —— 组合键先扣住这个键，双击不会触发。",
                        KeyName.Display(g.Key)), ConflictTarget.Chord));
                    break;
                }
            }
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

            foreach (var c in global.Chords)
            {
                if (Same(r.From, c.Key1) || Same(r.From, c.Key2))
                {
                    list.Add(new Conflict(ConflictLevel.Warn, Loc.F(
                        "「{0}」既改键又参与组合键 —— 组合键优先，这条改键基本不会生效。",
                        KeyName.Display(r.From)), ConflictTarget.Chord));
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
        foreach (var c in global.Chords)
        {
            WarnBadKey(list, c.Key1, Loc.T("组合键"), ConflictTarget.Chord);
            WarnBadKey(list, c.Key2, Loc.T("组合键"), ConflictTarget.Chord);
        }

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

        // ---- 7. 片段触发词重复 ----
        var triggersSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sn in global.Snippets)
        {
            if (string.IsNullOrWhiteSpace(sn.Trigger)) continue;
            if (!triggersSeen.Add(sn.Trigger))
            {
                list.Add(new Conflict(ConflictLevel.Warn,
                    Loc.F("文本片段「{0}」定义了多次，只有最后一条会展开。", sn.Trigger),
                    ConflictTarget.Snippet));
            }
        }

        return list;
    }

    private static bool InSet(HashSet<int> set, string? key)
    {
        int? vk = KeyName.VkOf(key);
        return vk != null && set.Contains(vk.Value);
    }

    private static bool Same(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;

        int? va = KeyName.VkOf(a);
        int? vb = KeyName.VkOf(b);
        if (va != null && vb != null) return va.Value == vb.Value;

        return string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
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
