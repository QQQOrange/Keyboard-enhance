using CapsLockPlus.Core.Settings;
using CapsLockPlus.Core.Localization;

namespace CapsLockPlus.Core.Search;

/// <summary>
/// 一条全局搜索结果。
///
/// 全部是**导航型**结果（跳到某个页面 / 选中某条别名），不直接执行动作 ——
/// 搜索框里误按回车就执行"打开程序"太危险了。
/// </summary>
public sealed record GlobalHit(
    string Kind,
    string Title,
    string Detail,
    string? Page = null,
    string? AliasTrigger = null);

/// <summary>
/// 全局搜索（Ctrl+K）。
///
/// 索引在打开面板时现建：数据量小（几十条别名 + 几百条绑定），
/// 建索引比维护增量索引可靠得多 —— 不会出现"改了配置但搜不到"。
/// </summary>
public static class GlobalSearch
{
    /// <summary>页面清单：和左侧导航一致（顺序也一致）。</summary>
    private static readonly (string Key, string Title)[] Pages =
    {
        ("guide", "快速上手"),
        ("keys", "按键映射"),
        ("hotstrings", "别名替换"),
        ("qbar", "Qbar 面板"),
        ("clipboard", "剪贴板"),
        ("macros", "宏"),
        ("layers", "触发键与层"),
        ("backup", "同步与备份"),
        ("settings", "设置"),
        ("test", "测试"),
        ("help", "快捷键速查"),
        ("about", "关于"),
    };

    /// <summary>建立一个搜索索引。</summary>
    public static List<GlobalHit> Build(AppSettings settings, Func<string, string> translate)
    {
        var hits = new List<GlobalHit>();

        foreach (var (key, title) in Pages)
        {
            hits.Add(new GlobalHit("页面", translate(title), Loc.T("打开这个页面"), Page: key));
        }

        /* 别名：显示时去掉前缀（和列表一致）。 */
        string prefix = settings.Hotstring?.EffectivePrefix ?? "";
        foreach (var snippet in settings.Global.Snippets)
        {
            string name = SnippetPrefixOps.Strip(snippet.Trigger, prefix);
            if (name.Length == 0) continue;

            string detail = !string.IsNullOrWhiteSpace(snippet.Comment)
                ? snippet.Comment!
                : Shorten(snippet.Text);

            hits.Add(new GlobalHit("别名", name, detail, Page: "hotstrings", AliasTrigger: snippet.Trigger));
        }

        /* 触发键。 */
        foreach (var trigger in settings.Triggers.Where(t => t.Enabled))
        {
            hits.Add(new GlobalHit("触发键", trigger.Key,
                Loc.F("{0} 毫秒后进入第 {1} 层", trigger.HoldMs, trigger.LayerId), Page: "layers"));
        }

        /* 层内绑定。 */
        foreach (var layer in settings.Layers)
        {
            foreach (var binding in layer.Keys)
            {
                if (!binding.IsBound) continue;

                string action = string.IsNullOrWhiteSpace(binding.Arg)
                    ? binding.Action
                    : binding.Action + " " + binding.Arg;

                hits.Add(new GlobalHit("绑定", binding.Combo,
                    Loc.F("第 {0} 层 · {1}", layer.Id, Shorten(action, 40)), Page: "layers"));
            }
        }

        return hits;
    }

    /// <summary>
    /// 过滤 + 排序。规则简单但可预期：
    ///   前缀命中 > 包含命中；同类里按标题长度（越短越可能是你要的）。
    /// </summary>
    public static List<GlobalHit> Query(IEnumerable<GlobalHit> all, string? query, int max = 40)
    {
        string q = (query ?? "").Trim();

        if (q.Length == 0)
        {
            /* 空查询：给最近最常用的入口 —— 页面 + 前几条别名。 */
            return all.Take(max).ToList();
        }

        var scored = new List<(GlobalHit Hit, int Score)>();

        foreach (var hit in all)
        {
            int score = Score(hit, q);
            if (score > 0) scored.Add((hit, score));
        }

        return scored
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Hit.Title.Length)
            .ThenBy(x => x.Hit.Title, StringComparer.CurrentCultureIgnoreCase)
            .Take(max)
            .Select(x => x.Hit)
            .ToList();
    }

    private static int Score(GlobalHit hit, string query)
    {
        const StringComparison C = StringComparison.CurrentCultureIgnoreCase;

        if (hit.Title.StartsWith(query, C)) return 100;
        if (hit.Title.Contains(query, C)) return 60;
        if (hit.Detail.Contains(query, C)) return 30;

        /* 别名再宽一点：允许按内容搜（"邮箱"能搜到 a@b.com 那条）。 */
        return 0;
    }

    private static string Shorten(string? text, int max = 48)
    {
        string s = (text ?? "").Replace("\r", "").Replace("\n", " ");
        return s.Length <= max ? s : s[..max] + "…";
    }
}
