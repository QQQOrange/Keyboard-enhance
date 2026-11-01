using CapsLockPlus.Core.Input;
using CapsLockPlus.Core.Settings;

namespace CapsLockPlus.Core.Settings;

/// <summary>
/// 场景模板包：一键把一组"常用搭配"合并进当前配置。
///
/// ## 为什么需要
///
/// 动作目录有 124 个动作，新用户面对空白配置根本不知道从哪下手。
/// 模板的作用不是"提供功能"，而是**给一个能立刻用的起点**。
///
/// ## 合并语义（也是这套东西的"profile 继承"）
///
/// 只**添加缺失项**，绝不覆盖用户已有的配置：
///   · 同名的层内绑定（同一个组合键）→ 跳过；
///   · 同一个源键的改键 → 跳过；
///   · 同一个触发词的片段 → 跳过。
/// 重复点同一个模板 = 什么都不发生，可以放心点击。
/// </summary>
public static class ScenarioPacks
{
    /// <summary>模板里的一条层内绑定。</summary>
    public sealed record PackBinding(string Key, string Action, string Arg = "", KeyModifiers Mods = KeyModifiers.None);

    public sealed record Pack(
        string Id,
        string Title,
        string Description,
        (string From, string To)[] Remaps,
        PackBinding[] Bindings,
        (string Trigger, string Text)[] Snippets);

    /// <summary>内置模板：三个"装完就能感觉到"的场景。</summary>
    public static readonly Pack[] All =
    {
        new("vim",
            "Vim 导航",
            "Caps 与 Esc 互换，再用 h/j/k/l 移动光标、u/d 翻页。",
            new[] { ("capslock", "esc"), ("esc", "capslock") },
            new[]
            {
                new PackBinding("h", "moveLeft"),
                new PackBinding("j", "moveDown"),
                new PackBinding("k", "moveUp"),
                new PackBinding("l", "moveRight"),
                new PackBinding("u", "pageUp"),
                new PackBinding("d", "pageDown"),
                new PackBinding("g", "home"),
            },
            Array.Empty<(string, string)>()),

        new("window",
            "窗口管理",
            "方向键贴边/最大化，w 关闭窗口，Tab 切换下一个窗口。",
            Array.Empty<(string, string)>(),
            new[]
            {
                new PackBinding("left",  "winSnapLeft"),
                new PackBinding("right", "winSnapRight"),
                new PackBinding("up",    "winMaximize"),
                new PackBinding("down",  "winMinimize"),
                new PackBinding("w",     "winClose"),
                new PackBinding("tab",   "winNextApp"),
            },
            Array.Empty<(string, string)>()),

        new("clip",
            "剪贴板效率",
            "v 打开剪贴板历史，p 粘贴为纯文本，y 复制当前行。",
            Array.Empty<(string, string)>(),
            new[]
            {
                new PackBinding("v", "clipboardHistory"),
                new PackBinding("p", "pasteSystem"),
                new PackBinding("y", "copy1"),
            },
            new[] { (";mail", "you@example.com") }),
    };

    public static Pack? Find(string? id)
        => All.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 把模板合并进配置。返回（新增条数, 跳过条数）。
    /// 目标层不存在时回退到第一层。
    /// </summary>
    public static (int Added, int Skipped) Apply(AppSettings s, Pack pack, int layerId = 1)
    {
        int added = 0, skipped = 0;

        var layer = s.Layers.FirstOrDefault(l => l.Id == layerId) ?? s.Layers.FirstOrDefault();

        // 1) 层内绑定
        if (layer != null)
        {
            foreach (var b in pack.Bindings)
            {
                string combo = KeyName.BuildComboEx(b.Mods, b.Key);
                bool exists = layer.Keys.Any(k => string.Equals(k.Combo, combo, StringComparison.OrdinalIgnoreCase));
                if (exists) { skipped++; continue; }

                layer.Keys.Add(new KeyBinding { Combo = combo, Action = b.Action, Arg = b.Arg });
                added++;
            }
        }

        // 2) 基础改键
        foreach (var (from, to) in pack.Remaps)
        {
            bool exists = s.Global.BaseRemaps.Any(r => string.Equals(r.From, from, StringComparison.OrdinalIgnoreCase));
            if (exists) { skipped++; continue; }

            s.Global.BaseRemaps.Add(new KeyRemap { From = from, To = to, Comment = "pack:" + pack.Id });
            added++;
        }

        // 3) 片段
        foreach (var (trigger, text) in pack.Snippets)
        {
            bool exists = s.Global.Snippets.Any(x => x.Trigger == trigger);
            if (exists) { skipped++; continue; }

            s.Global.Snippets.Add(new Snippet { Trigger = trigger, Text = text, Comment = "pack:" + pack.Id });
            added++;
        }

        return (added, skipped);
    }
}
