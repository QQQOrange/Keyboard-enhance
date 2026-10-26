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
        QbarKind.Command => Loc.T("命令"),
        QbarKind.Search => Loc.T("搜索"),
        QbarKind.Web => Loc.T("网址"),
        QbarKind.Run => Loc.T("程序"),
        QbarKind.Window => Loc.T("窗口"),
        QbarKind.Math => Loc.T("计算"),
        QbarKind.Clipboard => Loc.T("剪贴板"),
        QbarKind.File => Loc.T("文件"),
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
        new("/settings", "打开设置", "按键增强 设置窗口", "openSettings"),
        new("/bindings", "窗口绑定管理", "管理 10 个窗口槽位", "openBindings"),
        new("/clipboard", "剪贴板历史", "最近复制的内容", "openClipboard"),
        new("/help", "使用帮助", "快捷键速查表", "openHelp"),
        new("/math", "计算器", "带历史记录的计算器", "openMath"),
        new("/reload", "重新加载配置", "重新读取设置文件", "reload"),
        new("/pause", "暂停 / 恢复", "临时关闭按键增强热键", "toggleEnabled"),
        new("/exit", "退出按键增强", "完全退出程序", "exit"),
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
                        tail.Length == 0 ? Loc.F("在 {0} 中搜索…", chip.Label) : tail,
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
                    Loc.F("复制计算结果（{0} = {1}）", tail[1..].Trim(), formatted), "=", formatted) { Score = -200 });
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
                results.Add(new QbarResult(QbarKind.Math, formatted, Loc.F("复制计算结果（{0} = {1}）", expr.Trim(), formatted), "=", formatted)
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
                Loc.F("{0}：{1}", searchHit.Key, tail),
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

        // 7) 网址 / 文件路径
        //
        // 需求：Qbar 里直接贴一个网址要能回车直达；
        // 贴一个文件路径要用对应的程序打开，贴文件夹要用资源管理器打开。
        // 这里把优先级提到很高，避免被"默认搜索"盖住。
        if (LooksLikeUrl(text))
        {
            string url = NormalizeUrl(text);
            results.Insert(0, new QbarResult(QbarKind.Web, url, "打开这个网址", "网址", url) { Score = -3 });
        }
        else
        {
            try
            {
                if (File.Exists(text))
                {
                    results.Insert(0, new QbarResult(QbarKind.File, text, "打开这个文件", "文件", text)
                    { Score = -4 });
                }
                else if (Directory.Exists(text))
                {
                    // 文件夹交给资源管理器打开（explorer 能选中、能浏览，双击方式更符合习惯）
                    results.Insert(0, new QbarResult(QbarKind.File, text, "在资源管理器中打开", "文件夹", text)
                    { Score = -4 });
                }
                else if (LooksLikePath(text))
                {
                    // 路径格式正确但不存在：仍然给一条，让用户知道自己打错了
                    results.Insert(0, new QbarResult(QbarKind.File, text, "这个路径不存在", "路径", text)
                    { Score = 200 });
                }
            }
            catch (Exception ex) { CoreLog.Swallow(ex, "识别路径候选"); }
        }

        // 8) 默认搜索（永远兜底，回车即可搜）
        string defaultEngine = settings.Search.FirstOrDefault(x =>
            x.Key.Equals("default", StringComparison.OrdinalIgnoreCase))?.Value
            ?? "https://www.bing.com/search?q={q}";
        string defaultUrl = defaultEngine.Replace("{q}", Uri.EscapeDataString(text));
        results.Add(new QbarResult(QbarKind.Search, Loc.F("搜索“{0}”", text), defaultUrl, "默认", defaultUrl)
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

    /// <summary>
    /// 判断是不是网址。
    ///
    /// 认识三种写法：
    ///   https://example.com          —— 带协议
    ///   example.com / example.cn     —— 裸域名（.com/.cn/.org/.net/.io…）
    ///   localhost:8080 / 192.168.1.1 —— 本机或局域网地址
    ///
    /// 刻意排除：
    ///   单个字母加冒号（C:）     —— 那是盘符，属于路径
    ///   含空格的                  —— 正常网址不带空格
    /// </summary>
    public static bool LooksLikeUrl(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();
        if (text.Contains(' ')) return false;
        if (text.StartsWith("\\\\")) return false;    // UNC 网络路径

        // 带协议
        if (text.Contains("://", StringComparison.OrdinalIgnoreCase)) return true;

        // 盘符（C:\ / D:/）不是网址
        if (text.Length >= 2 && char.IsLetter(text[0]) && text[1] == ':') return false;

        // 裸域名 / localhost 分支要在"含点"检查**之前**，
        // 否则 localhost 这种不含点的会被上面的 lastDot 判断挡掉。
        if (text.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("localhost:", StringComparison.OrdinalIgnoreCase))
            return true;

        // 裸域名：必须含点，且最后一段是常见后缀
        int lastDot = text.LastIndexOf('.');
        if (lastDot > 0 && lastDot < text.Length - 1)
        {
            string tld = text[(lastDot + 1)..].ToLowerInvariant();
            string[] known =
            {
                "com", "cn", "org", "net", "io", "co", "dev", "app", "ai", "me",
                "edu", "gov", "info", "biz", "tv", "cc", "xyz", "top", "vip",
                "jp", "kr", "uk", "de", "fr", "ru", "hk", "tw", "sg",
            };
            if (known.Contains(tld)) return true;
        }

        // 纯 IP
        if (System.Net.IPAddress.TryParse(text, out _)) return true;

        return false;
    }

    /// <summary>裸域名补上 https://，避免系统把它当搜索词。</summary>
    private static string NormalizeUrl(string text) =>
        text.Contains("://", StringComparison.OrdinalIgnoreCase) ? text : "https://" + text;

    /// <summary>
    /// 看起来像文件 / 文件夹路径（即使它不存在）。
    /// 用于给出"这个路径不存在"的友好提示，而不是默默去搜索。
    /// </summary>
    public static bool LooksLikePath(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();
        // 盘符路径 C:\xxx / D:/xxx
        if (text.Length >= 3 && char.IsLetter(text[0]) && text[1] == ':'
            && text[2] is '\\' or '/') return true;
        // UNC 网络路径 \\server\share
        if (text.StartsWith("\\\\")) return true;
        return false;
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
                    string.IsNullOrEmpty(s.Comment) ? Loc.F("{0} 搜索", s.Key) : $"{s.Key}  ·  {s.Comment}",
                    s.Value, Loc.T("搜索"), s.Value) { Score = 30 };
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

    /// <summary>
    /// 打开文件 / 文件夹。
    ///
    /// 文件夹显式交给资源管理器（explorer.exe），
    /// 这样行为可预期：既能浏览、也能拖拽，还能看到地址栏。
    /// 用 ShellExecute 也可以，但明确走 explorer 更稳
    /// （某些文件夹 ShellExecute 会因为"找不到应用"而反问用户）。
    /// </summary>
    private static void OpenFileTarget(QbarResult result)
    {
        string path = result.Payload ?? "";
        if (path.Length == 0) return;

        try
        {
            if (Directory.Exists(path))
            {
                // 和"直达文件夹"共用同一套逻辑：打开后把窗口带到前台，
                // 否则面板一收起，用户看到的就是任务栏闪一下（窗口在后台）。
                Core.Windows.WindowService.OpenFolderInForeground(path);
                return;
            }
        }
        catch (Exception ex)
        {
            // 打开文件夹失败：用户按了回车却什么都没发生，必须有痕迹
            CoreLog.Swallow(ex, "用资源管理器打开文件夹");
        }

        try
        {
            if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                return;
            }
        }
        catch (Exception ex)
        {
            CoreLog.Warn("打开文件失败：" + ex.Message);
        }
    }

    /// <summary>执行一条结果。</summary>
    public void Execute(QbarResult result, QbarCallbacks cb)
    {
        switch (result.Kind)
        {
            case QbarKind.Math:
                cb.CopyToClipboard(result.Payload);
                cb.Notify(Loc.F("已复制：{0}", result.Payload));
                break;
            case QbarKind.Clipboard:
                cb.Paste(result.Payload);
                break;
            case QbarKind.Window:
                WindowService.Activate(new IntPtr(long.Parse(result.Payload)));
                break;
            case QbarKind.Search:
            case QbarKind.Web:
                cb.OpenTarget(result.Payload);
                break;

            case QbarKind.File:
                OpenFileTarget(result);
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
