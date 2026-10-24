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
}