using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using CapsLockPlus.Core.Input;
using CapsLockPlus.Core.Settings;
using CapsLockPlus.Core.Text;
using CapsLockPlus.Core.Windows;
using static CapsLockPlus.Core.Interop.NativeMethods;

namespace CapsLockPlus.Core.Actions;

/// <summary>
/// 动作执行上下文：把“功能”与“界面”解耦。
/// 需要弹窗的动作（Qbar、计算器……）只发出请求事件，由界面层接管，
/// 这样核心逻辑可以在没有界面的自检里单独跑。
/// </summary>
public sealed class ActionContext
{
    public required Func<AppSettings> Settings { get; init; }
    public required ClipboardService Clipboard { get; init; }
    public required WindowActions Windows { get; init; }
    public required WindowBindingService WindowBindings { get; init; }
    public required MouseService Mouse { get; init; }
    public required Action<string> Notify { get; init; }

    public event Action<string?>? QbarRequested;
    public event Action? MathBoardRequested;
    public event Action? SettingsRequested;
    public event Action? HelpRequested;
    public event Action? ClipboardHistoryRequested;
    public event Action? ReloadRequested;
    public event Action? ToggleEnabledRequested;
    public event Action? ScreenshotRequested;
    public event Action? MacrosRequested;

    /// <summary>
    /// 请求打开 Qbar。
    /// </summary>
    /// <param name="engineKey">默认搜索引擎的关键字（如 "zh" = 知乎）；
    /// 为空时用设置里的 default 引擎。绑定按键时可以指定，
    /// 实现"按 Caps+G 打开就是 Google 搜索"。</param>
    public void RequestQbar(string? engineKey = null) => QbarRequested?.Invoke(engineKey);
    public void RequestMathBoard() => MathBoardRequested?.Invoke();
    public void RequestOpenSettings() => SettingsRequested?.Invoke();
    public void RequestHelp() => HelpRequested?.Invoke();
    public void RequestOpenMacros() => MacrosRequested?.Invoke();
    public void RequestOpenClipboardHistory() => ClipboardHistoryRequested?.Invoke();
    public void RequestReload() => ReloadRequested?.Invoke();
    public void RequestToggleEnabled() => ToggleEnabledRequested?.Invoke();
    public void RequestScreenshot() => ScreenshotRequested?.Invoke();

    /// <summary>播放指定 ID 的宏。arg 可覆盖重复次数（空 = 用宏自身设置）。</summary>
    public void PlayMacro(string macroId, string? arg)
    {
        var macro = Settings().Macros.FirstOrDefault(m => m.Id == macroId);
        if (macro == null)
        {
            Notify("宏不存在，可能已被删除");
            return;
        }
        int? repeat = int.TryParse(arg, out int n) && n > 0 ? n : null;
        MacroPlayer.Play(macro, repeat);
        Notify($"播放宏：{macro.Name}");
    }

    /// <summary>全局宏播放器（一个进程一个就够了）。</summary>
    public Macro.MacroPlayer MacroPlayer { get; set; } = new();

    private bool _undoNext;

    public bool UndoRedo()
    {
        if (_undoNext) { InputSender.Ctrl(0x59); }   // Ctrl+Y
        else { InputSender.Ctrl(0x5A); }             // Ctrl+Z
        _undoNext = !_undoNext;
        return true;
    }

    /// <summary>
    /// 用指定搜索引擎在默认浏览器里搜索**当前选中的文字**。
    ///
    /// 与「Qbar 搜索」的区别：不弹面板，直接拿选区去搜。
    /// 适合"看到一段文字，按一下就查"这种高频动作。
    /// </summary>
    /// <param name="engineKey">搜索引擎关键字（bd / zh / go…）</param>
    /// <param name="overrideText">自检用：直接指定要搜索的文字</param>
    public bool SearchSelection(string engineKey, string? overrideText = null)
    {
        var entry = Settings().Search.FirstOrDefault(x =>
            x.Key.Equals(engineKey, StringComparison.OrdinalIgnoreCase));

        if (entry == null || string.IsNullOrWhiteSpace(entry.Value))
        {
            Notify("这个搜索引擎还没配置");
            return true;
        }

        /* ⚠ 这是"直接搜索按了没反应"的**真正原因**，别再改回去：
         *
         * 按键绑定里 search:bd 的 Arg 默认是**空字符串**（不是 null）。
         * 而 "" ?? xxx 的结果是 ""（?? 只认 null）——
         * 于是 overrideText 拿着一个空串，后面"去读选区"的分支永远走不到，
         * 直接落到"没有选中的文字"就 return 了：
         * 既不取词，也不开浏览器，日志里连一条 [取词] 都不会有。
         *
         * v3.0 到 v3.4 换了三套取词方案都没修好，就是因为它不在取词通道上，
         * 而在参数判断这一步。所以：空白参数一律归一成 null，回退去读选区。 */
        if (string.IsNullOrWhiteSpace(overrideText)) overrideText = null;

        string? text = overrideText;

        if (text == null)
        {
            text = CaptureSelectionText();
            if (!string.IsNullOrWhiteSpace(text))
                CoreLog.Info($"[搜索] 已取到选中文字 长度={text.Length}");
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            /* 所有程序化通道都失败时的**保底**：请用户亲自按一下 Ctrl+C。
             * 真实按键不会被 Chromium / Electron 过滤，也不受 UIPI 影响，
             * 拿到内容就立刻用它搜索 —— 这样"直接搜索"在任何程序里都能用。 */
            Notify(Core.ElevationHelper.IsElevated
                ? "没读到选中文字（管理员模式取不到普通程序的选区）。请按一下 Ctrl+C，我马上用它搜索"
                : "没读到选中文字，请按一下 Ctrl+C，我马上用它搜索");

            var waitManualCopy = ManualCopyWaitForTest
                                 ?? new Func<int, string>(Text.TextActions.WaitForManualCopy);
            text = waitManualCopy(5000);
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            Notify("没有取到选中的文字 —— 先选中要搜索的内容再按这个键");
            return true;
        }

        text = text.Trim();

        // 太长的话不截断会撑爆 GET 请求长度，也会让搜索页体验很差
        const int maxLen = 1000;
        if (text.Length > maxLen) text = text[..maxLen];

        string encoded = Uri.EscapeDataString(text);
        string url;
        if (entry.Value.Contains("{q}", StringComparison.Ordinal))
        {
            url = entry.Value.Replace("{q}", encoded);
        }
        else
        {
            /* 自定义引擎忘了写 {q} 时，不能把关键词默默丢掉 ——
             * 那样只会打开搜索引擎首页，用户看到的就是"没搜"。
             * 按最常见的约定补一个 q 参数。 */
            CoreLog.Warn($"[搜索] 引擎 {engineKey} 的地址里没有 {{q}}，改为追加 q 参数");
            url = entry.Value + (entry.Value.Contains('?') ? "&q=" : "?q=") + encoded;
        }

        if (!url.Contains("://", StringComparison.Ordinal))
            url = "https://" + url;

        Notify(overrideText == null ? "正在搜索选中的文字…" : "正在搜索…");
        CoreLog.Info($"[搜索] {engineKey} → {url}");
        OpenUrlRaw(url);   // 已编码，跳过规范化避免搜索词被解码/截断
        return true;
    }

    /// <summary>
    /// 取当前选区文字：主动取词优先，其次用「选中即捕获」的新鲜结果。
    ///
    /// 顺序是刻意这样排的：
    ///   ① 主动取词不需要用户额外操作，是"选中 + 按键"一步到位的正路；
    ///   ② 监听器的结果只在**几秒内**才用，否则会搜到很久以前复制的内容。
    /// 两条都拿不到才交给调用方去请用户手动 Ctrl+C。
    /// </summary>
    private string CaptureSelectionText()
    {
        if (CaptureTextForTest != null) return CaptureTextForTest() ?? "";

        // ① UIA → WM_COPY → SendInput → 线程级注入 → Ctrl+Insert
        string text = Text.TextActions.GetSelectedText();
        if (!string.IsNullOrWhiteSpace(text)) return text;

        // ② 用户几秒内自己按过 Ctrl+C，就别再折腾了
        string? fresh = SelectionWatcher?.LastSelectionWithin(TimeSpan.FromSeconds(6));
        if (!string.IsNullOrWhiteSpace(fresh))
        {
            CoreLog.Info($"[搜索] 用选区监听器捕获的内容 长度={fresh.Length}");
            return fresh;
        }

        return string.Empty;
    }

    /// <summary>
    /// 把一段文字直接输入到当前焦点处。
    ///
    /// 走 InputSender.Text（逐字符 Unicode 发送），不用剪贴板 ——
    /// 既不会破坏用户剪贴板内容，也能正常走 IME。
    /// </summary>
    /// <summary>
    /// 「选中即捕获」监听器。用户在任何程序里 Ctrl+C 时，
    /// 这里就存下了选中的文字 —— 比主动取词可靠得多
    /// （Electron / Chromium 会过滤合成输入，但用户的真实按键不会被拦）。
    /// </summary>
    public Core.Text.SelectionWatcher? SelectionWatcher { get; set; }

    /// <summary>
    /// 自检用：替换真实的取词通道，直接返回指定文字。
    /// 自检跑在没有真实选区的环境里，必须能绕开剪贴板/UI Automation。
    /// </summary>
    public Func<string?>? CaptureTextForTest { get; set; }

    /// <summary>
    /// 自检用：替换"请用户按 Ctrl+C 再等待"的保底逻辑，避免自检真的卡 4 秒。
    /// </summary>
    public Func<int, string>? ManualCopyWaitForTest { get; set; }

    public bool TypeText(string text)
    {
        if (string.IsNullOrEmpty(text)) return true;

        try
        {
            // 太长会有明显延迟（逐字符发送），限制一下避免误操作
            const int maxLen = 2000;
            string payload = text.Length > maxLen ? text[..maxLen] : text;

            /* 输入方式跟着设置走：
             *   auto（默认）→ 纯 ASCII 用真实按键（密码框认这个），其它 Unicode；
             *   keys        → 强制真实按键；
             *   unicode     → 强制 Unicode 注入。
             * 老配置里没有这个字段，反序列化后是默认值 auto，行为即"修好密码框"。 */
            string mode = Settings().Global.TextInputMode ?? "auto";
            var input = mode.ToLowerInvariant() switch
            {
                "keys" => InputSender.TextMode.Keys,
                "unicode" => InputSender.TextMode.Unicode,
                _ => InputSender.TextMode.Auto,
            };

            InputSender.Text(payload, input);
        }
        catch (Exception ex)
        {
            Notify(UiText.Failed("输入文字", ex.Message));
            CoreLog.Warn("输入文字失败：" + ex.Message);
        }
        return true;
    }
    /// <summary>
    /// 「直达链接」：把当前**选中的文字**识别成网址 / 文件夹 / 文件并打开。
    ///
    /// 与 <see cref="OpenUrl"/>、<see cref="OpenFolder"/> 的区别：
    /// 那两个是"先填地址、按键就开"，目标在设置里写死；
    /// 这个是"眼前有什么就开什么"，用户选中一段文字按下即可，不用预先配置。
    ///
    /// 也正因为目标来自选区，它被归到「系统」分类，和「打开网址」「运行程序」并列 ——
    /// 三者都是"给什么开什么"，只是参数来源不同（手填 / 选区）。
    /// </summary>
    /// <param name="overrideText">自检用：直接指定"选中的文字"，绕开真实取词。</param>
    public bool SmartOpen(string? overrideText = null)
    {
        string text = overrideText ?? "";

        if (string.IsNullOrWhiteSpace(text))
        {
            text = CaptureSelectionText();
            if (!string.IsNullOrWhiteSpace(text))
                CoreLog.Info($"[直达链接] 已取到选中文字 长度={text.Length}");
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            /* 取词失败时的保底：请用户亲手按一下 Ctrl+C。
             *
             * 这不是偷懒 —— Chromium / Electron 会过滤程序合成的 Ctrl+C，
             * 管理员模式下还会被 UIPI 拦掉，只有"真人按的"一定有效。
             * 和「直接搜索」用的是同一条保底路径。 */
            Notify(Core.ElevationHelper.IsElevated
                ? "没读到选中内容（管理员模式取不到普通程序的选区）。请按一下 Ctrl+C，我马上打开它"
                : "没读到选中内容，请按一下 Ctrl+C，我马上打开它");

            var waitManualCopy = ManualCopyWaitForTest
                                 ?? new Func<int, string>(Text.TextActions.WaitForManualCopy);
            text = waitManualCopy(4000);
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            Notify("先选中一段文字（网址、文件夹或文件路径），再按这个键");
            return true;
        }

        var hit = SmartTargetDetector.Detect(text);
        CoreLog.Info($"[直达链接] 识别结果={hit.Kind} 目标={hit.Target} 原文长度={text.Length}");

        switch (hit.Kind)
        {
            case SmartKind.Url:
                Notify($"打开网址：{UrlUtil.HostOf(hit.Target)}");
                OpenUrl(hit.Target);
                break;

            case SmartKind.Folder:
                Notify("正在打开文件夹…");
                OpenFolder(hit.Target);
                break;

            case SmartKind.File:
                Notify("正在用默认程序打开…");
                OpenFile(hit.Target);
                break;

            case SmartKind.MissingFolder:
                Notify($"文件夹不存在：{hit.Target}");
                break;

            case SmartKind.MissingFile:
                Notify($"文件不存在：{hit.Target}");
                break;

            default:
                /* 选中了普通文字（一句话、一个单词）。不要试图去搜索 ——
                 * 这个动作的名字是「直达链接」，猜错了打开浏览器比"什么都不做"更烦人。 */
                Notify("选中的内容既不是网址，也不是文件夹或文件路径");
                break;
        }

        return true;
    }

    /// <summary>Caps+Tab：把光标前的算式算出结果 / 展开热字串。</summary>
    public bool RunTabScript()    {
        var settings = Settings();
        string selected = Text.TextActions.GetSelectedText();
        if (!string.IsNullOrEmpty(selected))
        {
            string replaced = ExpandHotString(selected, settings);
            if (replaced == selected && ExpressionEvaluator.TryEvaluate(selected.TrimEnd('='), out var v, out _))
                replaced = ExpressionEvaluator.Format(v);
            if (replaced != selected) Text.TextActions.ReplaceSelection(replaced);
            return true;
        }

        // 没有选区：选中光标左边这一“词”，尝试计算 / 展开
        Text.TextActions.SelectLineToCaret(out string left);
        if (string.IsNullOrEmpty(left)) return true;

        string expanded = ExpandHotString(left, settings);
        if (expanded != left)
        {
            Text.TextActions.ReplaceSelection(expanded);
            return true;
        }

        string trimmed = left.TrimEnd('=', ' ');
        if (ExpressionEvaluator.TryEvaluate(trimmed, out double value, out string err))
        {
            string result = ExpressionEvaluator.Format(value);
            Notify($"{trimmed} = {result}");
            Text.TextActions.ReplaceSelection(left.TrimEnd().EndsWith('=') ? left + result : result);
            return true;
        }

        Notify(UiText.Failed("算出结果", err));
        return true;
    }

    private static string ExpandHotString(string text, AppSettings settings)
    {
        foreach (var hs in settings.HotStrings)
        {
            if (string.IsNullOrEmpty(hs.Key)) continue;
            if (text.EndsWith(hs.Key, StringComparison.Ordinal))
                return text[..^hs.Key.Length] + hs.Value.Replace("\\n", Environment.NewLine);
        }
        return text;
    }

    /// <summary>
    /// 仅供自检：拦截"打开网址"这一步，免得测试真的拉起浏览器。
    /// 置为 null 时走真实的 Process.Start。
    /// </summary>
    public static Action<string>? OpenUrlInterceptForTest { get; set; }

    public void OpenUrl(string url) => OpenUrlCore(url, normalize: true);

    /// <summary>
    /// 打开一个**已经构造好**的网址，不再做规范化。
    ///
    /// 「直接搜索」拼出来的 URL 里，关键词已经过 <see cref="Uri.EscapeDataString"/>
    /// 编码（<c>%E6%B5%8B</c> 这类）。如果再过一遍
    /// <see cref="UrlUtil"/> 的规范化，<c>Uri.ToString()</c> 会把百分号转义
    /// 重新解码成可读字符，长关键词还可能被截断 —— 搜索词就丢了。
    /// 所以这条路径要跳过规范化。
    /// </summary>
    public void OpenUrlRaw(string url) => OpenUrlCore(url, normalize: false);

    private void OpenUrlCore(string url, bool normalize)
    {
        string target = normalize ? (UrlUtil.Normalize(url) ?? url) : url;

        if (OpenUrlInterceptForTest != null)
        {
            OpenUrlInterceptForTest(target);
            return;
        }

        try
        {
            /* 不只是 Process.Start —— 那样浏览器会**在后台**打开
             * （前台锁定把新窗口压在任务栏），用户按下直达网址的键
             * 只看到任务栏闪一下。这里和"直达文件夹"走同一套置前逻辑。 */
            bool web = target.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                       || target.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
            WindowService.LaunchInForeground(target,
                web ? WindowService.ShellTargetKind.Url : WindowService.ShellTargetKind.File);
        }
        catch (Exception ex) { Notify(UiText.Failed("打开网址", ex.Message)); }
    }

    /// <summary>
    /// 仅供自检：拦截"打开文件夹"，免得测试真的弹出资源管理器。
    /// 置为 null 时走真实的 Process.Start。
    /// </summary>
    public static Action<string>? OpenFolderInterceptForTest { get; set; }

    /// <summary>
    /// 在文件资源管理器里打开一个文件夹，并把它带到**最前面**。
    ///
    /// 为什么交给 shell（UseShellExecute）而不是 `explorer.exe "路径"`：
    ///   · 路径里有空格 / 中文 / 方括号时，命令行引号很容易拼错；
    ///   · 用 shell 打开目录是系统标准行为，OneDrive、库、快捷方式都能认；
    ///   · 顺带支持 `%USERPROFILE%`、`~`、`shell:Downloads` 这些写法。
    ///
    /// 为什么还要额外做"置前"：我们是从后台按下快捷键的，前台是别的程序，
    /// Windows 的前台锁定会把新开的资源管理器窗口压在任务栏 ——
    /// 用户看到的就是"文件夹在后台打开了"。具体做法见
    /// <see cref="Windows.WindowService.OpenFolderInForeground"/>。
    ///
    /// 路径不存在时给明确提示，而不是静默失败 ——
    /// 用户最怕的就是"按了没反应，也不知道为什么"。
    /// </summary>
    public void OpenFolder(string path)
    {
        string target = ExpandFolderPath(path);
        if (target.Length == 0) return;

        if (OpenFolderInterceptForTest != null)
        {
            OpenFolderInterceptForTest(target);
            return;
        }

        try
        {
            if (!Directory.Exists(target))
            {
                Notify($"文件夹不存在：{target}");
                return;
            }
            CapsLockPlus.Core.Windows.WindowService.OpenFolderInForeground(target);
        }
        catch (Exception ex) { Notify(UiText.Failed("打开文件夹", ex.Message)); }
    }

    /// <summary>
    /// 仅供自检：拦截"打开文件"，免得测试真的拉起 Office / 播放器。
    /// 置为 null 时走真实的 Process.Start。
    /// </summary>
    public static Action<string>? OpenFileInterceptForTest { get; set; }

    /// <summary>
    /// 用系统默认程序打开一个**文件**（双击它时会发生什么，这里就做什么）。
    ///
    /// 用 shell 而不是手工查注册表：`.psd`、`.docx`、`.mp4` 该用什么程序打开
    /// 完全由系统关联决定，我们没必要也不应该自己判断。
    /// </summary>
    public void OpenFile(string path)
    {
        string target = ExpandPath(path);
        if (target.Length == 0) return;

        if (OpenFileInterceptForTest != null)
        {
            OpenFileInterceptForTest(target);
            return;
        }

        try
        {
            if (!File.Exists(target))
            {
                Notify($"文件不存在：{target}");
                return;
            }

            /* 和网址/文件夹一样：打开之后把那个程序带到最前面。
             * 具体是谁打开由文件关联决定（.docx → Word、.mp4 → 播放器），
             * WindowService 会自己查关联再认窗口。 */
            WindowService.LaunchInForeground(target, WindowService.ShellTargetKind.File);
        }
        catch (Exception ex) { Notify(UiText.Failed("打开这个文件", ex.Message)); }
    }

    /// <summary>
    /// 把用户填的路径整理成可以直接交给 shell 的形式：
    /// 去引号 / `%ENV%` / `~` / `shell:` / 正斜杠归一 / 去尾部分隔符。
    /// 文件夹、文件、选区里来的路径都走这里，保证"同一个路径只有一种写法"。
    /// 抽成静态是为了让自检能直接验，不必真的打开资源管理器。
    /// </summary>
    public static string ExpandPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        string p = path.Trim().Trim('"').Trim();
        if (p.Length == 0) return "";

        // shell: 开头的虚拟路径（shell:Downloads / shell:Startup…）原样交给系统
        if (p.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)) return p;

        p = Environment.ExpandEnvironmentVariables(p);

        if (p == "~")
            p = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        else if (p.StartsWith("~/") || p.StartsWith("~\\"))
            p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), p[2..]);

        // 统一分隔符，去掉末尾多余的 \（"C:\" 这种根目录要保留）
        p = p.Replace('/', '\\');
        if (p.Length > 3 && p.EndsWith('\\')) p = p.TrimEnd('\\');
        return p;
    }

    /// <summary>旧名字，保留给已有调用点；行为与 <see cref="ExpandPath"/> 一致。</summary>
    public static string ExpandFolderPath(string? path) => ExpandPath(path);

    /// <summary>
    /// 运行一个程序或命令。
    /// 注意不要用 "cmd /c start" 去绕——那样会闪出一个控制台窗口，
    /// 而且带空格路径的引号处理很容易出错。这里直接交给 shell 执行。
    /// </summary>
    public void RunProgram(string command)
    {
        try
        {
            string cmd = command.Trim();
            if (cmd.Length == 0) return;

            // 形如 "C:\path with space\x.exe" -arg 的写法：拆出程序和参数
            string exe = cmd;
            string args = "";
            if (cmd.StartsWith('"'))
            {
                int end = cmd.IndexOf('"', 1);
                if (end > 0)
                {
                    exe = cmd[1..end];
                    args = cmd[(end + 1)..].Trim();
                }
            }
            else
            {
                int sp = cmd.IndexOf(' ');
                if (sp > 0 && !File.Exists(cmd))
                {
                    exe = cmd[..sp];
                    args = cmd[(sp + 1)..].Trim();
                }
            }

            var psi = new ProcessStartInfo(exe) { UseShellExecute = true };
            if (args.Length > 0) psi.Arguments = args;
            Process.Start(psi);
        }
        catch (Exception ex) { Notify(UiText.Failed("运行这个程序", ex.Message)); }
    }

    public bool Screenshot()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-screenclip:") { UseShellExecute = true });
            return true;
        }
        catch
        {
            try
            {
                Process.Start(new ProcessStartInfo("SnippingTool.exe") { UseShellExecute = true });
                return true;
            }
            catch (Exception ex)
            {
                Notify(UiText.Failed("启动截图工具", ex.Message));
                return false;
            }
        }
    }

    /// <summary>
    /// 调整主音量（Core Audio API）。<paramref name="delta"/> 为正=加，为负=减。
    ///
    /// 不再模拟 VK_VOLUME_UP/DOWN：那条路在有键盘过滤驱动、远程会话、
    /// 或前台程序抢走媒体键的机器上会静默失效（用户看到的就是"按了没反应"）。
    /// 直接调音频引擎既能保证生效，也能拿到真实值显示百分比。
    /// </summary>
    /* ---- 单次 vs 连发：靠参数区分，不靠时间猜 ----
     *
     * delta 的语义很明确：
     *   · 单次按键 → 设置里的幅度（音量默认 5%、亮度默认 10%）；
     *   · 按住连发 → 引擎固定传 1，表示"这一步只走 1%"。
     *
     * 早先用"距上次调用的时间差"去猜，调度一抖就猜错 ——
     * 长按中途突然跳一大格，看起来一格一格地顿。 */

    /// <summary>
    /// 按设定幅度算出这次要发几次系统音量键。
    ///
    /// 系统步进是固定的（通常 2%），设定值往往不是它的整数倍：
    /// 设 5% 就"这次 2 次（4%）、下次 3 次（6%）"，把差的那 1% 记下来下次补。
    /// 十次下来正好 50%，不会出现"设 5 实际调了 40"的漂移。
    /// </summary>
    internal static (int Taps, double Remainder) PlanVolumeTaps(
        double want, int sysStep, double remainder)
    {
        sysStep = Math.Max(1, sysStep);
        double total = Math.Max(0, want) + remainder;
        int taps = Math.Max(1, (int)Math.Round(total / sysStep));
        // 误差只留在一个步进之内，避免长期累积成整数倍
        double next = Math.Clamp(total - taps * sysStep, -(sysStep - 1), sysStep - 1);
        return (taps, next);
    }

    /// <summary>音量一次调多少（百分比，设置里可改）。</summary>
    public int VolumeStep => Math.Clamp(Settings().Global.VolumeStep, 1, 50);

    /// <summary>亮度一次调多少（百分比，设置里可改）。</summary>
    public int BrightnessStep => Math.Clamp(Settings().Global.BrightnessStep, 1, 50);

    /// <summary>按住连发时用的一点点加速状态（见 <see cref="BrightnessAccel"/>）。</summary>
    private readonly BrightnessAccel _brightnessAccel = new();

    /// <summary>仅供自检：把加速状态清零，模拟"松开后重新按"。</summary>
    internal void ResetBrightnessAccelForTest() => _brightnessAccel.Reset();

    /// <summary>仅供自检：连发下一步会走多少（不碰硬件）。</summary>
    internal int NextBrightnessAccelForTest() => _brightnessAccel.Next();

    /// <summary>
    /// 音量步进的累计误差（百分比）。
    ///
    /// 系统步进是固定的（通常 2%），用户设 5% 没法一步到位 ——
    /// 那就这一次发 2 次键（4%）、下一次发 3 次（6%），
    /// 把差的那 1% 记下来下次补上。长期平均正好等于用户设的值，
    /// 不会出现"调十次差了 10%"。
    /// </summary>
    private double _volumeRemainder;

    /// <summary>上一次调节的方向（+1 加 / -1 减 / 0 还没调过）。</summary>
    private int _volumeDirection;

    public bool ChangeVolume(int delta)
    {
        /* 音量走**系统音量键**（VK_VOLUME_UP / DOWN）。
         *
         * 这是唯一能同时做到两件事的路：
         *   · 音量真的变了 —— 由 shell 自己调，不是我们猜；
         *   · Windows 弹出它自己的横条 —— 那条横条就是 shell 处理这个键时画的，
         *     自绘永远做不出"系统那个"。
         *
         * ⚠ v4.3.0 起**不再画自绘的音量横条**。
         * 上一版在"媒体键似乎没生效"时会退回音频引擎并自绘一条 ——
         * 结果用户常常一次看到**两条**横条（系统的 + 我们的），
         * 而且音量还被多调了一次。现在音量的一切反馈都交给系统，
         * 自绘横条只保留亮度那一条（系统对亮度没有通用 OSD）。
         *
         * 兜底：极少数机器上媒体键会被键盘过滤驱动 / 远程会话 / 抢媒体键的
         * 前台程序吞掉。单次调节时回读一次音量，没变就改用音频引擎直接调，
         * 音量照样会变（只是没有横条，比"按了没反应"好得多）。 */
        int sysStep = Math.Max(1, VolumeService.StepPercent());

        /* 换方向时把"欠账"清掉。
         *
         * 余数补偿是为了"设 5% 就长期平均 5%"：设 5（系统 2%）时
         * 这次发 2 次键（4%）、下次补 3 次（6%）。
         * 但用户加完马上又减时，那 1% 的欠账会跟着跑到反方向去 ——
         * 来回按几下音量会慢慢往回漂。方向一变就重新起算最稳妥。 */
        int direction = Math.Sign(delta);
        if (direction != 0 && direction != _volumeDirection)
        {
            _volumeRemainder = 0;
            _volumeDirection = direction;
        }

        var (taps, rem) = PlanVolumeTaps(Math.Abs(delta), sysStep, _volumeRemainder);
        _volumeRemainder = rem;

        // 细调（连发）不做回读校验：每次等 40ms 会把节奏拖垮
        bool fine = Math.Abs(delta) <= sysStep;

        int? before = VolumeService.Get();

        /* 已经到 0 / 100：系统音量键再发也不会变。
         * 不先拦住的话，"回读没变化"会被当成"媒体键被吞"，
         * 从而误触发兜底逻辑 —— 用户看到的就是"明明调不动还弹了条提示"。 */
        if (before is int edge && ((delta > 0 && edge >= 100) || (delta < 0 && edge <= 0)))
        {
            CoreLog.Info($"[音量] 已经是 {edge}%，到头了");
            return true;
        }

        InputSender.Tap(delta > 0 ? VK.VolumeUp : VK.VolumeDown, taps);

        if (fine) return true;

        int? after = before;
        for (int i = 0; i < 4 && before is int b0; i++)
        {
            System.Threading.Thread.Sleep(18);
            after = VolumeService.Get();
            if (after != null && after != b0) break;
        }

        if (before is int start && (after == null || after == start))
        {
            CoreLog.Warn("[音量] 系统媒体键没生效，改为直接调音频引擎");
            int? now = VolumeService.Step(delta);
            if (now == null)
            {
                Notify("没有找到可用的播放设备，音量调不了");
                return true;
            }
            CoreLog.Info($"[音量] {now}%（直接调音频引擎）");
            return true;
        }

        // 读不到当前音量（设备刚切换 / 非宿主线程）时只报"已发送"，别打出一个空百分号
        if (after is int now2) CoreLog.Info($"[音量] {now2}%（系统横条）");
        else CoreLog.Info("[音量] 已发送系统音量键（这次读不到当前音量）");
        return true;
    }

    /// <summary>切换系统静音。</summary>
    public bool ToggleMute()
    {
        // 同样发系统静音键：这样才有系统自己的静音横条
        bool? before = VolumeService.GetMute();
        InputSender.Tap(VK.VolumeMute);

        bool? after = before;
        for (int i = 0; i < 4 && before != null; i++)
        {
            System.Threading.Thread.Sleep(18);
            after = VolumeService.GetMute();
            if (after != before) break;
        }

        if (before == null || after == before)
        {
            // 媒体键被吞了：直接切。**不自绘横条** —— 系统那条才是用户认的，
            // 两条一起出现只会让人以为按了两次。
            bool? muted = VolumeService.ToggleMute();
            if (muted == null)
            {
                Notify("没有找到可用的播放设备，静音切不了");
                return true;
            }
            CoreLog.Info(muted.Value ? "[静音] 已静音（直接调音频引擎）" : "[静音] 已取消（直接调音频引擎）");
            return true;
        }

        CoreLog.Info(after == true ? "[静音] 已静音（系统横条）" : "[静音] 已取消（系统横条）");
        return true;
    }

    /// <summary>
    /// 调整屏幕亮度。<paramref name="delta"/> 为正=调亮，为负=调暗。
    ///
    /// <paramref name="continuous"/> 为 true 表示这是**按住时的连发**
    /// （引擎每一步都传 arg="1"）。连发时步进不再固定 1%，
    /// 而是走 <see cref="BrightnessAccel"/> 的匀加速：按得越久，每步越大，
    /// 但封顶 2.5 倍 —— 手感上是"越按越快"，不会一下冲到黑屏。
    ///
    /// 两条通道（见 <see cref="BrightnessService"/>）：
    ///   · 笔记本内置屏 → WMI，快且不闪；
    ///   · 台式机外接屏 → DDC/CI，直接跟显示器对话。
    /// 两条都不通时**明确告诉用户**能力不支持，否则按了没反应会被当成按键坏了。
    /// </summary>
    public bool ChangeBrightness(int delta, bool continuous = false)
    {
        if (continuous)
        {
            /* 连发：步长交给加速器算，方向沿用 delta 的符号。
             *
             * 为什么不用固定 1%：45ms 一步 = 22%/秒，从 100 调到 0 要 4.5 秒，
             * 长按到后面会觉得"怎么按都到底了还没到"；但一上来就给大步又太冲。
             * 匀加速正好：起步细、越按越大。 */
            int sign = Math.Sign(delta);
            if (sign != 0) delta = sign * _brightnessAccel.Next();
        }
        else
        {
            // 单次按键（或松开后重新按）= 新的一轮，加速从头开始
            _brightnessAccel.Reset();
        }

        /* 只改**目标值**，不等硬件。
         *
         * DDC/CI 写一次要 50~150ms，读一次 50~100ms —— 在按键路径上同步做，
         * 一个动作 250ms，用户体感就是"按了没反应"，按住连发还会互相排队。
         * 所以这里先把目标定下来、立刻刷横条（跟手），
         * 真正的硬件写入交给 BrightnessService 里的后台线程一步步追，
         * 看起来就是亮度在平滑爬升 —— 和 Windows 音量横条一个路数。
         *
         * v4.3.0：AddTarget 现在只改一个 int，不再抢"硬件锁"。
         * 上一版它会等后台的 DDC 写入（100~250ms），按住连发时
         * 动作队列越积越长 —— 用户调完亮度、手还按着触发键去按音量，
         * 音量动作排在几十条亮度任务后面，表现就是"完全没反应"。 */
        int? target = BrightnessService.AddTarget(delta);
        if (target == null)
        {
            // 说清"为什么没动"，比一句"不支持"有用得多
            string why = BrightnessService.Diagnose();
            CoreLog.Warn("[亮度] 调节失败：" + why);
            Notify(why);
            return true;
        }

        /* 亮度横条只能自己画：Windows 的亮度 OSD 只对内置屏存在
         * （那条是 OEM 驱动画的），外接屏走 DDC/CI 时系统根本不知道值变了。
         * 横条样式与音量那条一致，位置也一样。 */
        UI.Windows.OsdWindow.Show(target.Value);

        // 连续调节时每 45ms 一条会把日志刷爆，只在"单次"时记
        if (Math.Abs(delta) > 1) CoreLog.Info($"[亮度] 目标 {target}%");
        return true;
    }

    public bool ChangeMouseSpeed(int delta)
    {
        var s = Settings();
        s.Global.MouseSpeed = Math.Clamp(s.Global.MouseSpeed + delta, 1, 20);
        Mouse.Speed = s.Global.MouseSpeed;
        Notify($"鼠标速度 {s.Global.MouseSpeed}");
        return true;
    }
}

/// <summary>窗口相关动作。</summary>
public sealed class WindowActions
{
    private IntPtr Target => WindowService.Foreground;

    public bool ToggleTopmost()
    {
        WindowService.ToggleTopmost(Target);
        return true;
    }

    public bool CycleOpacity()
    {
        byte a = WindowService.NextOpacity(Target);
        return true;
    }

    public bool Minimize() { WindowService.Minimize(Target); return true; }
    public bool ToggleMaximize() { WindowService.ToggleMaximize(Target); return true; }
    public bool Close() { WindowService.Close(Target); return true; }
    public bool Center() { WindowService.Center(Target); return true; }
    public bool Snap(SnapSide side) { WindowService.Snap(Target, side); return true; }
    public bool SendToBottom() { WindowService.SendToBottom(Target); return true; }
    public bool CycleSameApp(bool backward) => WindowService.CycleSameApp(backward);
    public bool ActivateDirectional(string dir) => WindowService.ActivateDirectional(dir);
}

/// <summary>鼠标移动（带连续按下的加速手感，对应上游 dynamic_speed）。</summary>
public sealed class MouseService
{
    private int _streak;
    private DateTime _lastMove = DateTime.MinValue;
    public int Speed { get; set; } = 3;
    public int Step { get; set; } = 10;

    public bool Move(int dx, int dy)
    {
        var now = DateTime.Now;
        if ((now - _lastMove).TotalMilliseconds < 350) _streak++;
        else _streak = 0;
        _lastMove = now;
        double factor = 1 + Math.Min(_streak, 24) * 0.12;
        int baseStep = Math.Max(1, Step) * Math.Max(1, Speed) / 3;
        int dist = (int)Math.Round(baseStep * factor);
        InputSender.MoveRelative(dx * dist, dy * dist);
        return true;
    }
}

/// <summary>
/// 亮度长按的匀加速器：按住越久，每一步走得越多。
///
/// 手感的来源就是隔壁 <see cref="MouseService"/> 的 dynamic_speed
/// （按住连续移动会越走越快），这里把同一条思路搬到亮度上：
///   · 起步 1%/步（和原来一样细，单次微调不吃亏）；
///   · 每多一步，系数 +0.10，封顶 15 步 → 2.5 倍；
///   · 45ms 一步时约 0.7 秒到满速，全程 0→100 约 2 秒 ——
///     既不会"按半天不到底"，也不会一按就黑屏。
///
/// 为什么不是"每步直接取整"：1.1 倍写成整数会永远是 1，
/// 加速感全丢了。这里累计**理想增量**再取整下发，小数部分留到下一步，
/// 所以平均速率严格等于 1 + streak×0.10，看不出跳格。
/// </summary>
public sealed class BrightnessAccel
{
    /// <summary>加速到顶需要多少步（45ms/步时约 0.7 秒）。</summary>
    public const int MaxStreak = 15;

    /// <summary>每多一步，步长增加多少（1.0 → 2.5 倍）。</summary>
    public const double PerStep = 0.10;

    private int _streak;
    private double _ideal;
    private int _sent;

    public int Streak => _streak;

    /// <summary>松开 / 单次按键后重新起步。</summary>
    public void Reset()
    {
        _streak = 0;
        _ideal = 0;
        _sent = 0;
    }

    /// <summary>连发的下一步该走几个百分点（正数，方向由调用方给）。</summary>
    public int Next()
    {
        _streak = Math.Min(_streak + 1, MaxStreak);

        double factor = 1 + _streak * PerStep;
        _ideal += factor;

        // 四舍五入到最近整数；Math.Floor(x+0.5) 避开 2.5→2 的银行家舍入
        int target = (int)Math.Floor(_ideal + 0.5);
        int step = Math.Max(1, target - _sent);

        _sent += step;
        return step;
    }
}
