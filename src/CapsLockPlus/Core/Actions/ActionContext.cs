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
/// 需要弹窗的动作（Qbar、计算器、翻译……）只发出请求事件，由界面层接管，
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
    public event Action? TranslateRequested;
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
    public void RequestTranslate() => TranslateRequested?.Invoke();
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
            InputSender.Text(payload);
        }
        catch (Exception ex)
        {
            Notify("输入文字失败：" + ex.Message);
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

        Notify("无法计算：" + err);
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
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex) { Notify("打开网址失败：" + ex.Message); }
    }

    /// <summary>
    /// 仅供自检：拦截"打开文件夹"，免得测试真的弹出资源管理器。
    /// 置为 null 时走真实的 Process.Start。
    /// </summary>
    public static Action<string>? OpenFolderInterceptForTest { get; set; }

    /// <summary>
    /// 在文件资源管理器里打开一个文件夹。
    ///
    /// 为什么交给 shell（UseShellExecute）而不是 `explorer.exe "路径"`：
    ///   · 路径里有空格 / 中文 / 方括号时，命令行引号很容易拼错；
    ///   · 用 shell 打开目录是系统标准行为，OneDrive、库、快捷方式都能认；
    ///   · 顺带支持 `%USERPROFILE%`、`~`、`shell:Downloads` 这些写法。
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
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex) { Notify("打开文件夹失败：" + ex.Message); }
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
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex) { Notify("打开文件失败：" + ex.Message); }
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
        catch (Exception ex) { Notify("运行失败：" + ex.Message); }
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
                Notify("无法启动截图工具：" + ex.Message);
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
    public bool ChangeVolume(int delta)
    {
        int? now = VolumeService.Step(delta);
        if (now == null)
        {
            Notify("没有找到可用的播放设备，音量调不了");
            return true;
        }

        // 静音时调音量不会有声音，顺手提示一句，免得用户以为又没生效
        string suffix = VolumeService.GetMute() == true ? "（静音中）" : "";
        Notify($"音量 {now}%{suffix}");
        return true;
    }

    /// <summary>切换系统静音。</summary>
    public bool ToggleMute()
    {
        bool? muted = VolumeService.ToggleMute();
        if (muted == null)
        {
            Notify("没有找到可用的播放设备，静音切不了");
            return true;
        }
        Notify(muted.Value ? "已静音" : "已取消静音");
        return true;
    }

    /// <summary>
    /// 调整屏幕亮度。<paramref name="delta"/> 为正=调亮，为负=调暗。
    ///
    /// 两条通道（见 <see cref="BrightnessService"/>）：
    ///   · 笔记本内置屏 → WMI，快且不闪；
    ///   · 台式机外接屏 → DDC/CI，直接跟显示器对话。
    /// 两条都不通时**明确告诉用户**能力不支持，否则按了没反应会被当成按键坏了。
    /// </summary>
    public bool ChangeBrightness(int delta)
    {
        int? now = BrightnessService.Step(delta);
        if (now == null)
        {
            Notify("这台显示器不支持用软件调亮度（笔记本内置屏一般可以，外接屏需开启 DDC/CI）");
            return true;
        }
        Notify($"屏幕亮度 {now}%");
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
