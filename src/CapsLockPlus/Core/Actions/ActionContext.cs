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

        /* 取选中文字。
         *
         * GetSelectedText 的实现是"模拟 Ctrl+C 读剪贴板"，
         * 需要几十毫秒；它在引擎的动作队列里跑（不在钩子回调里），
         * 所以阻塞这一下不会触发 Windows 的钩子超时。 */
        string text = overrideText ?? Text.TextActions.GetSelectedText();

        if (string.IsNullOrWhiteSpace(text))
        {
            /* 提权运行时，三条取词通道（UIA / WM_COPY / SendInput）都会被
             * Windows 的 UIPI 拦掉。这时必须把**真正的原因**告诉用户，
             * 否则他会以为功能坏了 —— 这正是之前反复反馈"取不到"的根源。 */
            if (Core.ElevationHelper.IsElevated)
                Notify("读不到选区：当前是管理员模式。请到「设置 → 运行权限」"
                     + "关掉「以管理员身份运行」后重启 —— Windows 不允许管理员"
                     + "进程向普通权限的程序注入输入。");
            else
                Notify("没有选中的文字 —— 先选中要搜索的内容再按这个键");
            return true;
        }

        text = text.Trim();

        // 太长的话不截断会撑爆 GET 请求长度，也会让搜索页体验很差
        const int maxLen = 1000;
        if (text.Length > maxLen) text = text[..maxLen];

        string url = entry.Value.Replace("{q}", Uri.EscapeDataString(text));
        if (!url.Contains("://", StringComparison.Ordinal))
            url = "https://" + url;

        Notify(overrideText == null ? "正在搜索选中的文字…" : "正在搜索…");
        OpenUrlRaw(url);   // 已编码，跳过规范化避免搜索词被解码/截断
        return true;
    }

    /// <summary>
    /// 把一段文字直接输入到当前焦点处。
    ///
    /// 走 InputSender.Text（逐字符 Unicode 发送），不用剪贴板 ——
    /// 既不会破坏用户剪贴板内容，也能正常走 IME。
    /// </summary>
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
