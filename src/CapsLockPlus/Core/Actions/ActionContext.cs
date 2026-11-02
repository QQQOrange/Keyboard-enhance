using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using CapsLockPlus.Core.Input;
using CapsLockPlus.Core.Settings;
using CapsLockPlus.Core.Text;
using CapsLockPlus.Core.Windows;
using static CapsLockPlus.Core.Interop.NativeMethods;

namespace CapsLockPlus.Core.Actions;

public sealed partial class ActionContext
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

    /// <summary>
    /// 动作参数没填时的统一出口：提示"缺什么"，并按**失败**返回。
    ///
    /// 为什么单独做这一件事：直达网址 / 直达文件夹 / 直达文件 / 输入文本 / 运行程序
    /// 这些动作以前遇到空参数是 `if (empty) return;` —— 按下去**什么都不发生**，
    /// 而 <c>AppHost.InvokeAction</c> 还会记一条"动作：openFolder"的成功日志。
    /// 用户只会认为"这个键坏了"，日志里也看不出原因。
    ///
    /// 现在：明确告诉用户缺哪一项、去哪儿填，并且返回 false，
    /// 于是失败通知（Toast + 日志）会照常走一遍。
    /// </summary>
    /// <param name="what">缺的是什么，例如 "网址" / "文件夹路径"</param>
    public bool MissingArg(string what)
    {
        Notify(Loc.F("这个键还没填{0}：到「按键映射」里选中它，在右边把它填上", what));
        return false;
    }

    /// <summary>
    /// 播放指定 ID 的宏。arg 可覆盖重复次数（空 = 用宏自身设置）。
    ///
    /// 返回 **是否开始播放**（宏不存在 / 已在播放 / 流程控制配对错误都算没开始）。
    /// 为什么要这个返回值：宏里可以放"执行内置动作"的步骤，其中就有 `macro:xxx`。
    /// 以前这里返回 void、动作目录那边无条件 `return true`，于是
    /// "宏 A 调用一个已被删除的宏 B"会**静默继续**，用户只看到 A 少做了一半。
    /// 现在会如实返回失败，宏在那一格中止并说明原因。
    /// </summary>
    public bool PlayMacro(string macroId, string? arg)
    {
        var macro = Settings().Macros.FirstOrDefault(m => m.Id == macroId);
        if (macro == null)
        {
            Notify("宏不存在，可能已被删除");
            return false;
        }
        int? repeat = int.TryParse(arg, out int n) && n > 0 ? n : null;

        /* 用 TryPlay：流程控制配错时不播，并把原因弹出来。
         * 直接 Play 的话表现是"按了快捷键什么都不发生"，用户只会以为程序坏了。 */
        if (!MacroPlayer.TryPlay(macro, repeat))
        {
            Notify(Loc.F("没能播放宏「{0}」：{1}", macro.Name, MacroPlayer.LastError ?? Loc.T("未知原因")));
            return false;
        }
        Notify(Loc.F("播放宏：{0}", macro.Name));
        return true;
    }

    /// <summary>全局宏播放器（一个进程一个就够了）。</summary>
    public Macro.MacroPlayer MacroPlayer { get; set; } = new();

    private bool _undoNext;
}
