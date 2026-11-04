namespace CapsLockPlus.Core.Settings;

/// <summary>
/// 别名体检：把"看起来能用、实际不会生效"的配置一次性找出来。
///
/// 这些问题的共同点是**界面不报错**：用户以为设好了，实际打字时毫无反应，
/// 只能靠人肉逐条比对。体检把它们显式列出来，比让用户猜强。
/// </summary>
public static class SnippetAudit
{
    /// <summary>一条体检结果。</summary>
    public sealed record Issue(string Title, string Detail, Snippet Source);

    /// <summary>体检；没有问题时返回空列表。</summary>
    public static List<Issue> Run(AppSettings settings)
    {
        var issues = new List<Issue>();
        var snippets = settings.Global.Snippets;

        string prefix = settings.Hotstring?.EffectivePrefix ?? "";

        /* 已登记的分组（忽略大小写）；空分组名不算。 */
        var groups = new HashSet<string>(
            (settings.Global.SnippetGroups ?? new List<string>())
                .Where(g => !string.IsNullOrWhiteSpace(g))
                .Select(g => g.Trim()),
            StringComparer.CurrentCultureIgnoreCase);

        /* 1) 触发词重复：只有一条会命中，另一条永远打不出来。 */
        var byTrigger = new Dictionary<string, List<Snippet>>(StringComparer.CurrentCultureIgnoreCase);
        foreach (var s in snippets)
        {
            string key = (s.Trigger ?? "").Trim();
            if (key.Length == 0) continue;

            if (!byTrigger.TryGetValue(key, out var list))
                byTrigger[key] = list = new List<Snippet>();
            list.Add(s);
        }

        foreach (var (trigger, list) in byTrigger)
        {
            if (list.Count < 2) continue;
            issues.Add(new Issue(
                "触发词重复",
                $"{trigger} 出现了 {list.Count} 次，只有其中一条会生效",
                list[1]));
        }

        foreach (var s in snippets)
        {
            string trigger = (s.Trigger ?? "").Trim();

            /* 2) 没有触发词：这条永远不会被匹配到。 */
            if (trigger.Length == 0)
            {
                issues.Add(new Issue("没有触发词", "这条别名没有触发词，永远不会展开", s));
                continue;
            }

            /* 3) 没有内容：展开后等于什么都没输入。 */
            if (string.IsNullOrEmpty(s.Text))
                issues.Add(new Issue("内容为空", $"{trigger} 没有替换内容", s));

            /* 4) 触发词没带前缀：引擎只认"前缀 + 触发词"，这种永远命中不了。 */
            if (prefix.Length > 0 &&
                !trigger.StartsWith(prefix, StringComparison.Ordinal) &&
                SnippetPrefixOps.IsSymbol(trigger[0]))
            {
                issues.Add(new Issue("前缀不对", $"{trigger} 用的不是当前前缀「{prefix}」", s));
            }

            /* 5) 触发词里带空格：空格本身是"展开"的快捷键，永远打不出来。 */
            if (trigger.Contains(' ', StringComparison.Ordinal))
                issues.Add(new Issue("触发词含空格", $"{trigger} 里有空格，空格会被当成展开动作", s));

            /* 6) 分组没登记：左侧分组栏不显示，用户找不到这一组。 */
            string group = (s.Group ?? "").Trim();
            if (group.Length > 0 && !groups.Contains(group))
                issues.Add(new Issue("分组未登记", $"{trigger} 的分组「{group}」不在分组列表里", s));
        }

        return issues;
    }

    /// <summary>把结果拼成一段给对话框看的文字（最多 <paramref name="max"/> 条）。</summary>
    public static string Describe(IReadOnlyList<Issue> issues, int max = 12)
    {
        if (issues.Count == 0) return "";

        var sb = new System.Text.StringBuilder();
        int shown = Math.Min(issues.Count, max);

        for (int i = 0; i < shown; i++)
        {
            var issue = issues[i];
            sb.Append("· ").Append(issue.Title).Append("：").Append(issue.Detail).Append('\n');
        }

        if (issues.Count > shown)
            sb.Append(Localization.Loc.F("…还有 {0} 条\n", issues.Count - shown));

        return sb.ToString().TrimEnd('\n');
    }
}
