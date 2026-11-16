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

    /// <summary>动作目录里的一条动作（Qbar 直接执行）。</summary>
    Action,
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
        QbarKind.Action => Loc.T("动作"),
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

    /// <summary>动作目录的来源（可为空：空则 Qbar 不索引动作）。</summary>
    private readonly Func<IReadOnlyList<Core.Actions.ActionInfo>>? _actions;

    public QbarService(Func<AppSettings> settings, Func<IReadOnlyList<string>> clipboardHistory,
        Func<IReadOnlyList<Core.Actions.ActionInfo>>? actions = null)
    {
        _settings = settings;
        _clipboardHistory = clipboardHistory;
        _actions = actions;
    }

    public sealed record BuiltinCommand(string Key, string Title, string Description, string Action);

    /// <summary>
    /// 不带斜杠的快捷命令：输入关键字后按空格立即执行。
    /// 这些词故意选得短，且和普通程序名区分开；它们优先于同名 Run 条目。
    /// </summary>
    public sealed record QuickCommand(
        string Key,
        string Title,
        string Description,
        string Target,
        string Arguments = "",
        bool RunAsAdmin = false,
        bool IsCommand = false);

    public static readonly QuickCommand[] QuickCommands =
    {
        new("cmd", "打开命令行窗口", "cmd.exe", "cmd.exe"),
        new("cmda", "打开命令行窗口（管理员）", "以管理员身份运行 cmd.exe", "cmd.exe", RunAsAdmin: true),
        new("psh", "PowerShell", "powershell.exe", "powershell.exe", "-NoExit"),
        new("psha", "PowerShell（管理员）", "以管理员身份运行 PowerShell", "powershell.exe", "-NoExit", true),
        new("touch", "创建文件", "在桌面新建一个空文件", "touch", IsCommand: true),
        new("shutdown", "关机", "立即关闭计算机", "shutdown.exe", "/s /t 0"),
        new("reboot", "重启计算机", "立即重启计算机", "shutdown.exe", "/r /t 0"),
        new("uninstall", "卸载程序", "打开「应用和功能」", "ms-settings:appsfeatures"),
        new("connections", "管理网络连接", "打开「网络连接」", "ncpa.cpl"),
        new("hosts", "编辑 Hosts", "以管理员身份打开 hosts 文件", "notepad.exe",
            @"C:\Windows\System32\drivers\etc\hosts", true),
        new("lock", "锁定计算机", "立即锁定当前会话", "rundll32.exe", "user32.dll,LockWorkStation"),
        new("sleep", "睡眠", "让计算机进入睡眠", "rundll32.exe", "powrprof.dll,SetSuspendState 0,1,0"),
    };

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

        /* 先查“常用网址”，再查启动程序，最后才是搜索指令。
         *
         * 同一个关键字可能同时存在于三张表里：例如 bili 既是 B 站搜索，
         * 也是 B 站首页。用户输入 bili 再按空格，期望的是打开 B 站，
         * 而不是把 bili 变成“搜索 B 站”的标签；网址表优先最符合这个直觉。
         * 只存在于搜索表的 bd / zh 等不受影响。 */
        var web = settings.Web.FirstOrDefault(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (web != null) return QbarChip.FromEntry(ChipKind.Web, web);

        var run = settings.Run.FirstOrDefault(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (run != null) return QbarChip.FromEntry(ChipKind.Run, run);

        var search = settings.Search.FirstOrDefault(x =>
            !x.Key.Equals("default", StringComparison.OrdinalIgnoreCase) &&
            x.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (search != null) return QbarChip.FromEntry(ChipKind.Search, search);

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

    /// <summary>输入框内容恰好是快捷命令关键字时返回它（按空格直接执行）。</summary>
    public static QuickCommand? FindQuickCommand(string text)
    {
        string key = (text ?? "").Trim();
        return QuickCommands.FirstOrDefault(q => q.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>把快捷命令转成结果行；touch 走内置命令分支，其余直接启动程序。</summary>
    public static QbarResult ToResult(QuickCommand q)
    {
        if (q.IsCommand)
            return new QbarResult(QbarKind.Command, q.Title, q.Description, "快捷", q.Key) { Score = -70 };

        return new QbarResult(QbarKind.Run, q.Title, q.Description, "快捷", q.Target,
            null, q.RunAsAdmin, q.Arguments) { Score = -70 };
    }

    /// <summary>
    /// 参数模板展开：`{q}` 换成用户输入；没有 `{q}` 时把输入追加到末尾。
    /// 带空格的词会自动加引号，否则 `eudic -w hello world` 会被拆成两个参数。
    /// </summary>
    public static string ExpandArguments(string template, string tail)
    {
        tail = (tail ?? "").Trim();
        template ??= "";
        if (template.Contains("{q}", StringComparison.Ordinal))
        {
            /* 模板里已经写了引号（例如 --query="{q}"）时只做转义，不再套一层引号。 */
            int at = template.IndexOf("{q}", StringComparison.Ordinal);
            bool quoted = (at > 0 && template[at - 1] == '"') ||
                          (at + 2 < template.Length && template[at + 2] == '"');
            return template.Replace("{q}", quoted ? EscapeArgument(tail) : QuoteArgument(tail));
        }
        if (tail.Length == 0) return template;
        return (template.Length == 0 ? "" : template + " ") + QuoteArgument(tail);
    }

    private static string QuoteArgument(string value)
    {
        if (value.Length == 0) return "\"\"";
        return value.Any(char.IsWhiteSpace) || value.Contains('"')
            ? "\"" + EscapeArgument(value) + "\""
            : value;
    }

    private static string EscapeArgument(string value) => value.Replace("\"", "\\\"");

    /// <summary>
    /// 搜索条目既可以是网址模板，也可以是一个程序的 exe（例如欧路词典 `-w {q}`）。
    /// 只有明确像本地程序/脚本/快捷方式时才走启动分支，避免把 `https://...` 误判。
    /// </summary>
    public static bool LooksLikeProgram(string value)
    {
        string s = (value ?? "").Trim().Trim('"');
        if (s.Length == 0 || s.Contains("://", StringComparison.Ordinal)) return false;
        string ext = System.IO.Path.GetExtension(s);
        return ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".bat", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".ps1", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".py", StringComparison.OrdinalIgnoreCase);
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
                    /* 搜索条目可能是网址模板，也可能是本地词典/程序的 exe：
                     *   eudic.exe  +  -w {q}
                     * 后者不能拼成 URL，直接把查询词展开进参数并启动程序。 */
                    if (LooksLikeProgram(chip.Value))
                    {
                        string args = ExpandArguments(chip.Arguments, tail);
                        list.Add(new QbarResult(QbarKind.Run,
                            tail.Length == 0 ? Loc.F("用 {0} 查询…", chip.Label) : tail,
                            string.IsNullOrEmpty(args) ? chip.Value : $"{chip.Value} {args}",
                            chip.Label, chip.Value.Trim('"'), chip.IconPath, chip.RunAsAdmin, args)
                        {
                            Score = -100,
                            Chip = chip,
                        });
                        break;
                    }

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
                    string args = ExpandArguments(chip.Arguments, tail);
                    list.Add(new QbarResult(QbarKind.Run, chip.Label, chip.Value, chip.Label, chip.Value.Trim('"'),
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

        /* 0) 快捷命令：cmd / cmda / psh / psha / touch / shutdown / reboot /
         *    uninstall / connections / hosts。
         *    输入完整关键字后按空格会直接执行（见 QbarWindow.OnInputKeyDown），
         *    这里先把它们放进候选，保证用户能看到提示、也能用方向键选中后回车。 */
        foreach (var q in QuickCommands)
        {
            if (q.Key.StartsWith(text, StringComparison.OrdinalIgnoreCase))
                results.Add(ToResult(q));
        }
        if (FindQuickCommand(text) != null) return results.Take(limit).ToList();

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

        /* 2.5) PowerShell 直通：
         *   ps Get-Process
         *   $ Get-Date
         * 只生成一条“在 PowerShell 里执行”的结果，真正启动发生在回车之后；
         * 不在 Query 阶段执行任何命令，避免每敲一个字符就跑一次进程。 */
        string psCommand = "";
        if (text.StartsWith("ps ", StringComparison.OrdinalIgnoreCase)) psCommand = text[3..].Trim();
        else if (text.StartsWith("$ ", StringComparison.Ordinal)) psCommand = text[2..].Trim();
        if (psCommand.Length > 0)
        {
            string escaped = psCommand.Replace("\"", "\\\"");
            results.Insert(0, new QbarResult(QbarKind.Run,
                Loc.F("PowerShell：{0}", Shorten(psCommand, 48)),
                Loc.T("在 PowerShell 窗口中执行这条命令"),
                "PS", "powershell.exe", null, false, $"-NoExit -Command \"{escaped}\"")
            { Score = -15 });
        }

        /* 2.6) cmd 直通：> ipconfig /all
         * 和 ps 一样，只在回车后启动，不在 Query 阶段执行。 */
        if (text.StartsWith("> ", StringComparison.Ordinal))
        {
            string cmdCommand = text[2..].Trim();
            if (cmdCommand.Length > 0)
            {
                results.Insert(0, new QbarResult(QbarKind.Run,
                    Loc.F("CMD：{0}", Shorten(cmdCommand, 48)),
                    Loc.T("在命令提示符窗口中执行这条命令"),
                    "CMD", "cmd.exe", null, false, $"/k {cmdCommand}")
                { Score = -14 });
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
            if (LooksLikeProgram(searchHit.Value))
            {
                string args = ExpandArguments(searchHit.Arguments, tail);
                results.Insert(0, new QbarResult(QbarKind.Run,
                    Loc.F("{0}：{1}", searchHit.Key, tail),
                    string.IsNullOrEmpty(args) ? searchHit.Value : $"{searchHit.Value} {args}",
                    searchHit.Key, searchHit.Value.Trim('"'), searchHit.IconPath,
                    searchHit.RunAsAdmin, args) { Score = -20 });
                return results.Take(limit).ToList();
            }

            string url = searchHit.Value.Replace("{q}", Uri.EscapeDataString(tail));
            results.Insert(0, new QbarResult(QbarKind.Search,
                Loc.F("{0}：{1}", searchHit.Key, tail),
                searchHit.Value.Replace("{q}", tail),
                searchHit.Key, url) { Score = -20 });
            return results.Take(limit).ToList();
        }

        // 4) 模糊匹配：程序 / 网址 / 搜索指令 / 内置命令
        results.AddRange(MatchEntries(settings, text));

        /* 4.1) 本地内容源：固定项、开始菜单应用、浏览器书签、Windows 设置、文件。
         * 它们比“动作/剪贴板/窗口”更像是用户要找的目标，所以排在前面；
         * 具体分数由各 Provider 决定，默认搜索仍然兜底。 */
        results.AddRange(QbarProviders.Search(settings, text, limit));

        /* 4.5) 动作目录：把 124 个动作变成"可搜索、可执行"的能力。
         *
         * 排序放在程序之后、剪贴板之前：先给"要打开的东西"，再给"要做的事"，
         * 剪贴板属于兜底内容，不该压过动作。 */
        if (_actions != null && text.Length >= 1)
        {
            int actionHits = 0;
            foreach (var a in _actions())
            {
                if (a.Hidden) continue;
                if (!a.Title.Contains(text, StringComparison.OrdinalIgnoreCase) &&
                    !a.Category.Contains(text, StringComparison.OrdinalIgnoreCase)) continue;

                results.Add(new QbarResult(QbarKind.Action, a.Title, a.Description, a.Category, a.Id)
                { Score = 40 });
                if (++actionHits >= 5) break;
            }
        }

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
        //
        // 走带缓存的列表：Qbar 每个字符都会重跑一遍 Query，而枚举窗口要为每个窗口
        // 打开进程取 exe 名。缓存 700ms 只影响"刚打开/关闭窗口"的即时性，
        // 不影响选中结果（选中后仍按句柄操作）。
        foreach (var w in WindowService.ZOrderWindowsCached())
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
        yield return new QbarResult(QbarKind.Command,
            "输入关键字搜索，或直接回车用默认搜索引擎",
            "支持应用/文件/书签/设置；=算式 直接计算，ps 命令走 PowerShell，Ctrl+P 固定选中项",
            "", "", null) { Score = 1 };
        foreach (var pinned in QbarProviders.MatchPinned(settings.Qbar.Pinned, "").Take(6))
            yield return pinned with { Score = 1 };
        foreach (var cmd in Commands.Take(5))
            yield return new QbarResult(QbarKind.Command, cmd.Title, cmd.Description, cmd.Key, cmd.Action) { Score = 2 };
        foreach (var q in QuickCommands.Take(4))
            yield return ToResult(q) with { Score = 2 };
        foreach (var run in settings.Run.Take(4))
            yield return new QbarResult(QbarKind.Run, run.Key, run.Value, "程序", run.Value, run.IconPath) { Score = 3 };
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
                    run.Value, "程序", run.Value, run.IconPath, run.RunAsAdmin, run.Arguments)
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
                { Score = 20, IconPath = web.IconPath };
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
                /* 句柄是从本进程的结果里来的，正常一定是十进制；
                 * 但结果列表可能在下一次刷新前被复用（窗口已经关掉 / 换了条目），
                 * 这里做一次安全解析，绝不让一条坏数据把整个执行路径打断。 */
                if (long.TryParse(result.Payload, out long hwnd))
                    WindowService.Activate(new IntPtr(hwnd));
                else
                    cb.Notify(Loc.T("这个窗口已经不在了"));
                break;
            case QbarKind.Search:
            case QbarKind.Web:
                /* 载荷里还留着 {q} 的，是"搜索引擎模板"而不是一个具体地址。
                 *
                 * 这种条目本该先被补全成「关键字 + 空格」的标签。
                 * 万一走到执行（其它调用方 / 键盘路径变了），
                 * 也绝不能把 `...?wd={q}` 原样丢给浏览器 ——
                 * 用户会看到浏览器在搜"{q}"这个字符串。 */
                if (result.Payload.Contains("{q}", StringComparison.Ordinal))
                {
                    cb.Notify(Loc.T("先输入要搜索的内容：关键字后面加一个空格"));
                    break;
                }
                cb.OpenTarget(result.Payload);
                break;

            case QbarKind.File:
                OpenFileTarget(result);
                break;
            case QbarKind.Run:
                /* 程序参数里可能带 {q}（欧路词典的 -w {q}）。
                 * 用户只按了回车、还没输入关键词时，绝不能把字面量 {q}
                 * 传给程序 —— 那会打开一个查 "{q}" 的错误窗口。 */
                if (result.Arguments.Contains("{q}", StringComparison.Ordinal))
                {
                    cb.Notify(Loc.T("先输入要搜索的内容：关键字后面加一个空格"));
                    break;
                }
                cb.RunTarget(result.Payload, result.RunAsAdmin, result.Arguments);
                break;
            case QbarKind.Command:
                cb.RunCommand(result.Payload);
                break;
            case QbarKind.Action:
                cb.RunAction(result.Payload);
                break;
        }
    }

    public sealed record QbarCallbacks(
        Action<string> Notify,
        Action<string> CopyToClipboard,
        Action<string> Paste,
        Action<string> OpenTarget,
        Action<string, bool, string> RunTarget,
        Action<string> RunCommand,
        Action<string> RunAction);
}
