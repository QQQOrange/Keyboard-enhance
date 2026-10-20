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

    public event Action? QbarRequested;
    public event Action? MathBoardRequested;
    public event Action? SettingsRequested;
    public event Action? HelpRequested;
    public event Action? BindingsRequested;
    public event Action? ClipboardHistoryRequested;
    public event Action? TranslateRequested;
    public event Action? ReloadRequested;
    public event Action? ToggleEnabledRequested;
    public event Action? ScreenshotRequested;
    public event Action? MacrosRequested;

    public void RequestQbar() => QbarRequested?.Invoke();
    public void RequestMathBoard() => MathBoardRequested?.Invoke();
    public void RequestOpenSettings() => SettingsRequested?.Invoke();
    public void RequestHelp() => HelpRequested?.Invoke();
    public void RequestOpenBindings() => BindingsRequested?.Invoke();
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

    /// <summary>Caps+Tab：把光标前的算式算出结果 / 展开热字串。</summary>
    public bool RunTabScript()
    {
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

    public void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
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
