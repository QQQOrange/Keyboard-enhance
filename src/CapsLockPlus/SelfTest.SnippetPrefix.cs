using System;
using System.Collections.Generic;
using System.Linq;
using CapsLockPlus.Core.Input;
using CapsLockPlus.Core.Settings;

namespace CapsLockPlus;

/// <summary>
/// 别名（文本片段）的自检：前缀多值、输入法全角/半角等价、以及"找回丢失的别名"。
///
/// 这一组盯的是用户实际踩到的两个坑：
///   · 前缀改成 `、` 之后**怎么打都不触发** —— 中文输入法打 `、` 时按下的是 `\` 键，
///     缓冲区里记的是 `\`，只做字符串相等比较永远对不上；
///   · 升几次级之后**别名不见了** —— 保存是防抖的、读盘失败会回退备份，
///     两条路径都不会在界面上留痕迹，只能靠历史副本找回来。
/// </summary>
public static partial class SelfTest
{
    private static void TestSnippetPrefixes()
    {
        /* ① 折叠：全角符号折成半角，长度必须 1:1（Match 靠下标判词边界）。 */
        CheckEqual("折叠：顿号折成反斜杠", SnippetMatcher.Fold('、'), '\\');
        CheckEqual("折叠：全角逗号", SnippetMatcher.Fold('，'), ',');
        CheckEqual("折叠：全角问号", SnippetMatcher.Fold('？'), '?');
        CheckEqual("折叠：半角原样", SnippetMatcher.Fold(';'), ';');
        CheckEqual("折叠：中文原样", SnippetMatcher.Fold('中'), '中');
        CheckEqual("折叠字符串不改变长度", SnippetMatcher.Fold("、mail").Length, 5);
        CheckEqual("折叠字符串结果", SnippetMatcher.Fold("、mail"), "\\mail");

        /* ② 前缀命中：IME 打 `、` → 缓冲区 `\`，配置里的 `、mail` 必须能对上。 */
        var prefixes = new List<string> { "、", ";" };
        Check("前缀：顿号前缀认得缓冲区里的反斜杠",
            SnippetMatcher.StartsWithAnyPrefix("、mail", prefixes));
        Check("前缀：分号前缀也认",
            SnippetMatcher.StartsWithAnyPrefix(";mail", prefixes));
        Check("前缀：不在列表里的符号不认",
            !SnippetMatcher.StartsWithAnyPrefix("+mail", prefixes));
        Check("前缀：空列表一律放行",
            SnippetMatcher.StartsWithAnyPrefix("任何", new List<string>()));

        /* ③ 真正走一遍 Match：缓冲区是 `\`，配置里写的是 `、mail`。 */
        var snippets = new List<Snippet>
        {
            new() { Trigger = "、mail", Text = "someone@example.com", Enabled = true },
        };
        var hit = SnippetMatcher.Match(snippets, "\\mail", "");
        Check("匹配：反斜杠缓冲区能展开顿号别名", hit != null);
        CheckEqual("匹配：展开的是同一条", hit?.Trigger ?? "", "、mail");

        /* ④ 前缀列表：老配置（单个 Prefix + 全角开关）要能读成列表。 */
        var legacy = new HotstringOptions { Prefix = "、", FullWidth = false };
        CheckEqual("老配置：单前缀读成列表", legacy.EffectivePrefixes.Count, 1);
        CheckEqual("老配置：值不变", legacy.EffectivePrefix, "、");

        var legacyFull = new HotstringOptions { Prefix = "/", FullWidth = true };
        CheckEqual("老配置：全角开关仍换算", legacyFull.EffectivePrefix, "／");
        legacyFull.NormalizePrefixes();
        CheckEqual("归一后前缀写回列表", legacyFull.Prefix, "／");
        Check("归一后关掉全角开关", !legacyFull.FullWidth);

        var multi = new HotstringOptions { Prefixes = new List<string> { "、", ";", "、", "  " } };
        CheckEqual("多前缀去重与去空白", multi.EffectivePrefixes.Count, 2);
        CheckEqual("多前缀顺序不变", string.Join("", multi.EffectivePrefixes), "、;");

        /* ⑤ 剥 / 补：多个前缀时按命中的那个剥，补的时候统一补第一个。 */
        CheckEqual("剥前缀：顿号", SnippetPrefixOps.StripAny("、mail", prefixes), "mail");
        CheckEqual("剥前缀：分号", SnippetPrefixOps.StripAny(";mail", prefixes), "mail");
        CheckEqual("剥前缀：没前缀原样", SnippetPrefixOps.StripAny("mail", prefixes), "mail");
        CheckEqual("补前缀：裸词补第一个", SnippetPrefixOps.ApplyAny("mail", prefixes), "、mail");
        CheckEqual("补前缀：已带另一个前缀不动",
            SnippetPrefixOps.ApplyAny(";mail", prefixes), ";mail");

        /* ⑥ 换前缀：旧符号换成新符号，裸词补默认，已有的不动。 */
        var settings = new AppSettings();
        settings.Global.Snippets.Add(new Snippet { Trigger = ";mail", Text = "a" });
        settings.Global.Snippets.Add(new Snippet { Trigger = "、tel", Text = "b" });
        settings.Global.Snippets.Add(new Snippet { Trigger = "bare", Text = "c" });
        SnippetPrefixOps.MigrateAll(settings, new[] { ";" }, new List<string> { "、", ";" });
        CheckEqual("迁移：旧前缀保留为多前缀之一时不动",
            settings.Global.Snippets.First(x => x.Text == "a").Trigger, ";mail");
        SnippetPrefixOps.MigrateAll(settings, new[] { "、" }, new List<string> { "/" });
        CheckEqual("迁移：旧前缀换成新前缀",
            settings.Global.Snippets.First(x => x.Text == "b").Trigger, "/tel");
        CheckEqual("迁移：裸词补新前缀",
            settings.Global.Snippets.First(x => x.Text == "c").Trigger, "/bare");

        /* ⑦ 找回：只补当前没有的，同触发词不覆盖。 */
        var target = new AppSettings();
        target.Global.Snippets.Add(new Snippet { Trigger = "、keep", Text = "现在的版本" });
        var picks = new List<SnippetRecovery.Candidate>
        {
            new("、keep", "旧版本", "", "settings.json.bak", DateTime.Now),
            new("、lost", "被弄丢的内容", "工作", "settings.json.bak", DateTime.Now),
            new("   ", "空触发词", "", "x", DateTime.Now),
        };
        int added = SnippetRecovery.Restore(target, picks);
        CheckEqual("找回：只加当前没有的那条", added, 1);
        CheckEqual("找回：已存在的保持现有内容",
            target.Global.Snippets.First(x => x.Trigger == "、keep").Text, "现在的版本");
        CheckEqual("找回：新加的落在原分组",
            target.Global.Snippets.First(x => x.Trigger == "、lost").Group, "工作");
        Check("找回：空触发词被丢掉",
            target.Global.Snippets.All(x => x.Trigger.Trim().Length > 0));

        /* ⑧ 触发词合法性：`、mail` 这类符号开头要算合法（否则列表里存了也不展开）。 */
        Check("触发器：顿号开头合法", SnippetMatcher.IsValidTrigger("、mail"));
        Check("触发器：字母开头不合法", !SnippetMatcher.IsValidTrigger("mail"));

        /* ⑦b 中文触发词：汉字不再算"词字符"，所以「中文」「地址」这类触发词合法。
         * 少了这条，用户存下来的中文别名永远不会展开 —— 表现就是"别名只能设英文"。 */
        Check("触发器：中文开头合法", SnippetMatcher.IsValidTrigger("中文"));
        Check("触发器：中文短语合法", SnippetMatcher.IsValidTrigger("地址公司"));
        Check("触发器：中文+英文混合合法", SnippetMatcher.IsValidTrigger("中文mail"));
        Check("触发器：英文开头仍不合法", !SnippetMatcher.IsValidTrigger("mail中文"));
        Check("触发器：中文中间有空格不合法", !SnippetMatcher.IsValidTrigger("中文 别名"));

        /* ⑨ 界面上那一组可选符号：八个、全是**半角单字符**，胶囊里只列它们。 */
        CheckEqual("可选符号个数", HotstringOptions.SymbolChoices.Length, 8);
        Check("可选符号都是半角单字符",
            HotstringOptions.SymbolChoices.All(x => x.Symbol.Length == 1 && x.Symbol[0] < 0x80),
            string.Join(" ", HotstringOptions.SymbolChoices.Select(x => x.Symbol)));
        CheckEqual("可选符号首项是冒号", HotstringOptions.SymbolChoices[0].Symbol, ":");
        Check("每个可选符号都配了中文名",
            HotstringOptions.SymbolChoices.All(x => !string.IsNullOrWhiteSpace(x.Label)));
        CheckEqual("符号能反查名字", HotstringOptions.LabelOf(";"), "分号");
        CheckEqual("列表外的符号没有名字", HotstringOptions.LabelOf("、"), "");

        /* 全新配置默认就是冒号 —— 老配置里的前缀不受影响（读盘时按原值生效）。 */
        var fresh = new HotstringOptions();
        CheckEqual("新配置默认前缀", fresh.EffectivePrefix, ":");

        /* ⑩ 展开后自动补一下 Shift：默认开启，关掉之后能存住这个 false。 */
        Check("新配置默认会补 Shift", fresh.RestoreImeAfterExpand);

        var off = new AppSettings();
        off.Hotstring.RestoreImeAfterExpand = false;
        string json = System.Text.Json.JsonSerializer.Serialize(off, SettingsStore.JsonOptions);
        var back = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json, SettingsStore.JsonOptions);
        Check("关掉补 Shift 能存进配置", back != null && !back.Hotstring.RestoreImeAfterExpand);

        /* ⑪ Qbar 的两个输入法开关：默认都关（不替用户改输入法状态），打开后能存住。 */
        var qbar = new QbarOptions();
        Check("Qbar 默认不切换输入法", !qbar.SwitchToEnglishOnOpen && !qbar.SwitchBackOnEngine);

        var qOn = new AppSettings();
        qOn.Qbar.SwitchToEnglishOnOpen = true;
        qOn.Qbar.SwitchBackOnEngine = true;
        string qjson = System.Text.Json.JsonSerializer.Serialize(qOn, SettingsStore.JsonOptions);
        var qback = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(qjson, SettingsStore.JsonOptions);
        Check("Qbar 输入法开关能存进配置",
            qback != null && qback.Qbar.SwitchToEnglishOnOpen && qback.Qbar.SwitchBackOnEngine);
    }
}
