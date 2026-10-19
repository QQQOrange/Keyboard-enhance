using System.Diagnostics;
using System.Text;
using CapsLockPlus.Core.Settings;
using CapsLockPlus.Core.Text;
using CapsLockPlus.Core.Windows;

namespace CapsLockPlus.Core.Qbar;

public enum QbarKind
{
    Command,
    Search,
    Web,
    Run,
    Window,
    Math,
    Clipboard,
    File,
}

/// <summary>Qbar 的一条候选结果。</summary>
public sealed record QbarResult(
    QbarKind Kind,
    string Title,
    string Subtitle,
    string Badge,
    string Payload,
    string? IconPath = null,
    bool RunAsAdmin = false,
    string Arguments = "")
{
    /// <summary>用于排序的匹配分数（越小越靠前）。</summary>
    public int Score { get; init; }

    /// <summary>命中标签时，结果行上显示对应的标签图标。</summary>
    public QbarChip? Chip { get; init; }

    public string KindLabel => Kind switch
    {
        QbarKind.Command => "命令",
        QbarKind.Search => "搜索",
        QbarKind.Web => "网址",
        QbarKind.Run => "程序",
        QbarKind.Window => "窗口",
        QbarKind.Math => "计算",
        QbarKind.Clipboard => "剪贴板",
        QbarKind.File => "文件",
        _ => "",
    };
}

/// <summary>
/// Qbar 的检索逻辑（与界面无关，方便自检）。
/// 输入规则：
///   * “bd 关键词”   → 用百度搜索
///   * “= 1+2*3”     → 直接计算（也支持不带 = 的纯算式）
///   * “/settings”   → 内置命令
///   * 其它文本      → 默认搜索 + 匹配到的程序 / 网址 / 窗口 / 剪贴板历史
/// </summary>
public sealed class QbarService
{
    private readonly Func<AppSettings> _settings;
    private readonly Func<IReadOnlyList<string>> _clipboardHistory;

    public QbarService(Func<AppSettings> settings, Func<IReadOnlyList<string>> clipboardHistory)
    {
        _settings = settings;
        _clipboardHistory = clipboardHistory;
    }

    public sealed record BuiltinCommand(string Key, string Title, string Description, string Action);

    public static readonly BuiltinCommand[] Commands =
    {
        new("/settings", "打开设置", "CapsLock+ 设置窗口", "openSettings"),
        new("/bindings", "窗口绑定管理", "管理 10 个窗口槽位", "openBindings"),
        new("/clipboard", "剪贴板历史", "最近复制的内容", "openClipboard"),
        new("/help", "使用帮助", "快捷键速查表", "openHelp"),
        new("/math", "计算器", "带历史记录的计算器", "openMath"),
        new("/reload", "重新加载配置", "重新读取设置文件", "reload"),
        new("/pause", "暂停 / 恢复", "临时关闭 CapsLock+ 热键", "toggleEnabled"),
        new("/exit", "退出 CapsLock+", "完全退出程序", "exit"),
    };

    /// <summary>
    /// 输入 "bd " 时把 bd 变成标签：返回对应的搜索 / 程序 / 网址条目。
    /// 只认“整段完全匹配”（不区分大小写），避免把普通单词误转成标签。
    /// </summary>
    public QbarChip? ResolveChip(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        string key = token.Trim();
        if (key.Length == 0) return null;
        var settings = _settings();

        var search = settings.Search.FirstOrDefault(x =>
            !x.Key.Equals("default", StringComparison.OrdinalIgnoreCase) &&
            x.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (search != null) return QbarChip.FromEntry(ChipKind.Search, search);

        var run = settings.Run.FirstOrDefault(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (run != null) return QbarChip.FromEntry(ChipKind.Run, run);

        var web = settings.Web.FirstOrDefault(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (web != null) return QbarChip.FromEntry(ChipKind.Web, web);

        return null;
    }

    /// <summary>给自动补全用：列出所有可作为标签的关键字。</summary>
    public IEnumerable<(string Key, string Name, ChipKind Kind)> AllChips()
    {
        var settings = _settings();
        foreach (var s in settings.Search.Where(x => !x.Key.Equals("default", StringComparison.OrdinalIgnoreCase)))
            yield return (s.Key, s.DisplayName, ChipKind.Search);
        foreach (var r in settings.Run)
            yield return (r.Key, r.DisplayName, ChipKind.Run);
        foreach (var w in settings.Web)
            yield return (w.Key, w.DisplayName, ChipKind.Web);
    }

    public List<QbarResult> Query(string raw, int limit = 12) => Query(raw, null, limit);

    /// <summary>
    /// 带标签的查询：chip 不为空时，输入框里只剩“要搜索的内容”，
    /// 直接按该标签对应的引擎/程序/网址执行。
    /// </summary>
    public List<QbarResult> Query(string raw, QbarChip? chip, int limit = 12)
    {
        var settings = _settings();
        string tail = (raw ?? "").Trim();

        if (chip != null)
        {
            var list = new List<QbarResult>();
            switch (chip.Kind)
            {
                case ChipKind.Search:
                {
                    string url = chip.Value.Replace("{q}", Uri.EscapeDataString(tail));
                    list.Add(new QbarResult(QbarKind.Search,
                        tail.Length == 0 ? $"在 {chip.Label} 中搜索…" : tail,
                        chip.Value.Replace("{q}", tail),
                        chip.Label, url)
                    {
                        Score = -100,
                        IconPath = chip.IconPath,
                        Chip = chip,
                    });
                    break;
                }
                case ChipKind.Web:
                {
                    list.Add(new QbarResult(QbarKind.Web, chip.Label, chip.Value, chip.Label, chip.Value)
                    {
                        Score = -100,
                        IconPath = chip.IconPath,
                        Chip = chip,
                    });
                    break;
                }
                case ChipKind.Run:
                {
                    string args = string.IsNullOrEmpty(chip.Arguments)
                        ? tail
                        : (tail.Length == 0 ? chip.Arguments : chip.Arguments + " " + tail);
                    list.Add(new QbarResult(QbarKind.Run, chip.Label, chip.Value, chip.Label, chip.Value,
                        chip.IconPath, chip.RunAsAdmin, args)
                    {
                        Score = -100,
                        Chip = chip,
                    });
                    break;
                }
            }

            // 标签模式下仍允许用 = 直接计算，避免“在百度里搜 =1+2”这种尴尬
            if (tail.StartsWith('=') && ExpressionEvaluator.TryEvaluate(tail[1..], out double v, out _))
            {
                string formatted = ExpressionEvaluator.Format(v);
                list.Insert(0, new QbarResult(QbarKind.Math, formatted,
                    $"复制计算结果（{tail[1..].Trim()} = {formatted}）", "=", formatted) { Score = -200 });
            }
            return list.Take(limit).ToList();
        }

        return QueryCore(settings, tail, limit);
    }

    private List<QbarResult> QueryCore(AppSettings settings, string text, int limit)
    {
        var results = new List<QbarResult>();
        if (text.Length == 0)
        {
            results.AddRange(DefaultResults(settings));
            return results.Take(limit).ToList();
        }

        // 1) 内置命令
        if (text.StartsWith('/'))
        {
            foreach (var cmd in Commands)
            {
                if (cmd.Key.StartsWith(text, StringComparison.OrdinalIgnoreCase) ||
                    text.Length <= 1)
                {
                    results.Add(new QbarResult(QbarKind.Command, cmd.Title, cmd.Description, cmd.Key, cmd.Action)
                    { Score = 0 });
                }
            }
            if (results.Count > 0) return results.Take(limit).ToList();
        }

        // 2) 计算
        string expr = text.StartsWith('=') ? text[1..] : text;
        expr = expr.TrimEnd('=', ' ');
        if (expr.Length > 0 && (text.StartsWith('=') || LooksLikeMath(expr)))
        {
            if (ExpressionEvaluator.TryEvaluate(expr, out double value, out _))
            {
                string formatted = ExpressionEvaluator.Format(value);
                results.Add(new QbarResult(QbarKind.Math, formatted, $"复制计算结果（{expr.Trim()} = {formatted}）", "=", formatted)
                { Score = -10 });
                if (text.StartsWith('=')) return results.Take(limit).ToList();
            }
        }

        // 3) 第一段是已定义的搜索 / 网址 / 程序关键字
        int space = text.IndexOf(' ');
        string head = space > 0 ? text[..space] : text;
        string tail = space > 0 ? text[(space + 1)..].Trim() : "";

        var searchHit = settings.Search.FirstOrDefault(x =>
            x.Key.Equals(head, StringComparison.OrdinalIgnoreCase) && x.Key != "default");
        if (searchHit != null && space > 0)
        {
            string url = searchHit.Value.Replace("{q}", Uri.EscapeDataString(tail));
            results.Insert(0, new QbarResult(QbarKind.Search,
                $"{searchHit.Key}：{tail}",
                searchHit.Value.Replace("{q}", tail),
                searchHit.Key, url) { Score = -20 });
            return results.Take(limit).ToList();
        }

        // 4) 模糊匹配：程序 / 网址 / 搜索指令 / 内置命令
        results.AddRange(MatchEntries(settings, text));

        // 5) 剪贴板历史
        foreach (var item in _clipboardHistory())
        {
            string flat = item.Replace("\r", " ").Replace("\n", " ");
            if (flat.Contains(text, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(new QbarResult(QbarKind.Clipboard,
                    Shorten(flat, 90), "粘贴这段剪贴板内容", "剪贴板", item)
                { Score = 60 });
                if (results.Count(r => r.Kind == QbarKind.Clipboard) >= 3) break;
            }
        }

        // 6) 窗口
        foreach (var w in WindowService.ZOrderWindows())
        {
            if (w.Title.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                w.ExeName.Contains(text, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(new QbarResult(QbarKind.Window, w.Title, w.ExeName, "窗口",
                    w.Handle.ToInt64().ToString(), w.ExePath) { Score = 80 });
                if (results.Count(r => r.Kind == QbarKind.Window) >= 3) break;
            }
        }

        // 7) 文件 / 文件夹路径
        try
        {
            if (File.Exists(text) || Directory.Exists(text))
            {
                results.Add(new QbarResult(QbarKind.File, text, "打开这个路径", "路径", text) { Score = -5 });
            }
        }
        catch { }

        // 8) 默认搜索（永远兜底，回车即可搜）
        string defaultEngine = settings.Search.FirstOrDefault(x =>
            x.Key.Equals("default", StringComparison.OrdinalIgnoreCase))?.Value
            ?? "https://www.bing.com/search?q={q}";
        string defaultUrl = defaultEngine.Replace("{q}", Uri.EscapeDataString(text));
        results.Add(new QbarResult(QbarKind.Search, $"搜索“{text}”", defaultUrl, "默认", defaultUrl)
        { Score = 500 });

        return results
            .OrderBy(r => r.Score)
            .ThenBy(r => r.Title.Length)
            .Take(limit)
            .ToList();
    }

    private static IEnumerable<QbarResult> DefaultResults(AppSettings settings)
    {
        yield return new QbarResult(QbarKind.Command, "输入关键字搜索，或直接回车用默认搜索引擎", "输入 =算式 可以直接计算", "", "", null) { Score = 1 };
        foreach (var cmd in Commands.Take(5))
            yield return new QbarResult(QbarKind.Command, cmd.Title, cmd.Description, cmd.Key, cmd.Action) { Score = 2 };
        foreach (var run in settings.Run.Take(4))
            yield return new QbarResult(QbarKind.Run, run.Key, run.Value, "程序", run.Value) { Score = 3 };
    }

    private static IEnumerable<QbarResult> MatchEntries(AppSettings settings, string text)
    {
        foreach (var run in settings.Run)
        {
            if (run.Key.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                run.Value.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                run.Comment.Contains(text, StringComparison.OrdinalIgnoreCase))
            {
                yield return new QbarResult(QbarKind.Run,
                    string.IsNullOrEmpty(run.Comment) ? run.Key : $"{run.Key}  ·  {run.Comment}",
                    run.Value, "程序", run.Value, null, run.RunAsAdmin, run.Arguments)
                { Score = 10 + (run.Key.StartsWith(text, StringComparison.OrdinalIgnoreCase) ? 0 : 15) };
            }
        }
        foreach (var web in settings.Web)
        {
            if (web.Key.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                web.Value.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                web.Comment.Contains(text, StringComparison.OrdinalIgnoreCase))
            {
                yield return new QbarResult(QbarKind.Web,
                    string.IsNullOrEmpty(web.Comment) ? web.Key : $"{web.Key}  ·  {web.Comment}",
                    web.Value, "网址", web.Value)
                { Score = 20 };
            }
        }
        foreach (var s in settings.Search)
        {
            if (s.Key == "default") continue;
            if (s.Key.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                s.Comment.Contains(text, StringComparison.OrdinalIgnoreCase))
            {
                yield return new QbarResult(QbarKind.Search,
                    string.IsNullOrEmpty(s.Comment) ? $"{s.Key} 搜索" : $"{s.Key}  ·  {s.Comment}",
                    s.Value, "搜索", s.Value) { Score = 30 };
            }
        }
        foreach (var cmd in Commands)
        {
            if (cmd.Title.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                cmd.Key.Contains(text, StringComparison.OrdinalIgnoreCase))
            {
                yield return new QbarResult(QbarKind.Command, cmd.Title, cmd.Description, cmd.Key, cmd.Action)
                { Score = 40 };
            }
        }
    }

    /// <summary>粗略判断是否像算式（避免把普通单词当公式算）。</summary>
    public static bool LooksLikeMath(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        string text = s.TrimEnd('=', ' ');
        if (text.Length == 0) return false;
        bool hasDigit = text.Any(char.IsDigit);
        bool hasOperator = text.Any(c => c is '+' or '-' or '*' or '/' or '%' or '^' or '×' or '÷');
        bool onlyMathChars = text.All(c => char.IsDigit(c) || char.IsWhiteSpace(c) ||
                                           "+-*/%^().,×÷".Contains(c) ||
                                           char.IsLetter(c));
        bool hasFunction = text.Contains('(') && text.Any(char.IsLetter);
        return (hasDigit && hasOperator && onlyMathChars) || (hasFunction && onlyMathChars && hasDigit);
    }

    private static string Shorten(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    /// <summary>执行一条结果。</summary>
    public void Execute(QbarResult result, QbarCallbacks cb)
    {
        switch (result.Kind)
        {
            case QbarKind.Math:
                cb.CopyToClipboard(result.Payload);
                cb.Notify($"已复制：{result.Payload}");
                break;
            case QbarKind.Clipboard:
                cb.Paste(result.Payload);
                break;
            case QbarKind.Window:
                WindowService.Activate(new IntPtr(long.Parse(result.Payload)));
                break;
            case QbarKind.Search:
            case QbarKind.Web:
            case QbarKind.File:
                cb.OpenTarget(result.Payload);
                break;
            case QbarKind.Run:
                cb.RunTarget(result.Payload, result.RunAsAdmin, result.Arguments);
                break;
            case QbarKind.Command:
                cb.RunCommand(result.Payload);
                break;
        }
    }

    public sealed record QbarCallbacks(
        Action<string> Notify,
        Action<string> CopyToClipboard,
        Action<string> Paste,
        Action<string> OpenTarget,
        Action<string, bool, string> RunTarget,
        Action<string> RunCommand);
}
