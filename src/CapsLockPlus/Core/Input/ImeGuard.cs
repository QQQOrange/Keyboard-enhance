using CapsLockPlus.Core.Interop;
using static CapsLockPlus.Core.Interop.NativeMethods;

namespace CapsLockPlus.Core.Input;

/// <summary>
/// 「输入文本」走真实按键时，把输入法挡在外面的守卫。
///
/// **要解决的问题**：中文输入法开着的时候，程序替用户敲的 "hello" 会被
/// 输入法当成拼音，屏幕上弹出候选框，文本框里一个字都没有。
///
/// **为什么原来的做法不管用**：以前是 <c>ImmAssociateContext(hwnd, NULL)</c>
/// 把输入法上下文摘掉。那是 IMM32 时代的手段，只对老式程序有效 ——
/// 现代程序（Win11 记事本 / Edge / VS Code / Word）都走 TSF 架构，
/// 摘掉 IMM32 上下文它们根本不理会，字母照样进输入法。
/// 顺带一提 <c>ImmIsIME</c> 也指望不上：实测它对**所有**已加载布局都返回
/// TRUE（连德语、法语布局都算输入法），没法当判据。
///
/// **实际做法**：临时把目标窗口的**输入语言**切到英文（美国）布局。
/// 输入语言是 Win32 层的概念，TSF 也归它管，切了就是切了；
/// 打完再切回用户原来的输入语言，输入法状态（中/英模式）不受影响。
///
/// **失败也不要紧**：切换是异步的，有些程序不认这条消息。此时调用方会
/// 退回 Unicode 注入 —— 那条路不经过键盘布局和输入法，一样能落字。
/// 两种退路都不会让文字被输入法吃掉。
/// </summary>
internal static class ImeGuard
{
    /// <summary>英文（美国）布局的 KLID。所有 Windows 都带 kbdus.dll，不必预装。</summary>
    private const string EnglishLayoutId = "00000409";

    /// <summary>
    /// 这些输入语言的字母键会被输入法拼成候选：
    /// 中文（简/繁/港/新加坡）、日文、韩文、越南文。
    ///
    /// 不用 <c>ImmIsIME</c> 判定的原因见类注释 —— 它对所有布局都返回 TRUE。
    /// 用语言 ID 至少是"宁可多切一次"：误判的代价只是多花几十毫秒，
    /// 漏判的代价是用户按了快捷键却什么都没打出来。
    /// </summary>
    private static readonly ushort[] ComposingLanguages = new ushort[]
        { 0x0804, 0x0404, 0x0C04, 0x1004, 0x0411, 0x0412, 0x042A };

    /// <summary>切换生效后再等一会儿，让目标线程真正落稳（输入队列是按序处理的）。</summary>
    private const int SettleMs = 20;

    private static IntPtr _english;
    private static bool _englishLoaded;

    /// <summary>英文（美国）布局的 HKL；加载失败返回 <see cref="IntPtr.Zero"/>。</summary>
    public static IntPtr EnglishLayout
    {
        get
        {
            if (_englishLoaded) return _english;
            _englishLoaded = true;
            try
            {
                _english = LoadKeyboardLayout(EnglishLayoutId, 0);
            }
            catch
            {
                _english = IntPtr.Zero;   /* 预期内：系统没有该布局时退回 Unicode */
            }
            return _english;
        }
    }

    /// <summary>这个布局下的字母键会不会被输入法拼成候选。</summary>
    public static bool IsComposing(IntPtr layout)
    {
        if (layout == IntPtr.Zero) return false;

        // HKL 的低 16 位是语言 ID，高 16 位是设备句柄 —— 只看低 16 位
        int lang = (int)(layout.ToInt64() & 0xFFFF);
        foreach (ushort candidate in ComposingLanguages)
            if (lang == candidate) return true;
        return false;
    }

    /// <summary>
    /// 把某个窗口所在线程的输入语言切到 <paramref name="layout"/>，等到真正生效。
    /// 返回 false = 这个程序不认这条消息（或超时），调用方应改走 Unicode。
    /// </summary>
    public static bool SwitchTo(IntPtr hwnd, IntPtr layout, int timeoutMs)
    {
        if (hwnd == IntPtr.Zero || layout == IntPtr.Zero) return false;

        uint tid = GetWindowThreadProcessId(hwnd, out _);
        if (tid == 0) return false;
        if (GetKeyboardLayout(tid) == layout) return true;

        // ① 常规做法：投给顶层窗口，DefWindowProc 会转成 ActivateKeyboardLayout
        PostMessage(hwnd, WM_INPUTLANGCHANGEREQUEST, IntPtr.Zero, layout);
        if (WaitFor(tid, layout, timeoutMs)) return true;

        // ② 顶层窗口自己写了 WndProc 又没转给 DefWindowProc 时，改投真正有焦点的子窗口
        IntPtr focus = FocusOf(tid);
        if (focus != IntPtr.Zero && focus != hwnd)
        {
            PostMessage(focus, WM_INPUTLANGCHANGEREQUEST, IntPtr.Zero, layout);
            if (WaitFor(tid, layout, timeoutMs)) return true;
        }

        CoreLog.Warn($"[输入文本] 切输入语言失败（目标 HKL=0x{layout.ToInt64():X8}），改走 Unicode");
        return false;
    }

    /// <summary>
    /// 把输入语言切回去。换岗失败会留在英文布局上，对用户是可见的打扰，
    /// 所以这里多重试一轮。
    /// </summary>
    public static void Restore(IntPtr hwnd, IntPtr layout, int timeoutMs)
    {
        if (layout == IntPtr.Zero) return;
        if (SwitchTo(hwnd, layout, timeoutMs)) return;

        Thread.Sleep(40);
        SwitchTo(hwnd, layout, timeoutMs);
    }

    /// <summary>轮询到布局真的生效为止。切换是异步的，post 完立刻发按键会被旧布局吃掉。</summary>
    private static bool WaitFor(uint tid, IntPtr layout, int timeoutMs)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < timeoutMs)
        {
            if (GetKeyboardLayout(tid) == layout)
            {
                Thread.Sleep(SettleMs);
                return true;
            }
            Thread.Sleep(5);
        }
        return false;
    }

    /// <summary>取该线程当前有焦点的窗口（顶层窗口之外的那一层）。</summary>
    private static IntPtr FocusOf(uint tid)
    {
        try
        {
            var info = new GUITHREADINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<GUITHREADINFO>() };
            if (GetGUIThreadInfo(tid, ref info)) return info.hwndFocus;
        }
        catch
        {
            /* 预期内：跨会话/受保护进程取不到线程信息，就当没有焦点窗口 */
        }
        return IntPtr.Zero;
    }
}
