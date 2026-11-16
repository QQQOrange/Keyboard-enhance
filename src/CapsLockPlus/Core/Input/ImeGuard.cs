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
    /// 确保目标窗口所在线程处于“会拼候选”的输入语言（中文/日文/韩文…）。
    ///
    /// Qbar 的引擎标签是通过 <c>qbar:bd</c> 这类动作带出来的：面板一打开
    /// 就已经是“等待输入关键词”的状态，此时用户要打的是中文，不是英文。
    /// 不能再盲目按 Shift —— 中文输入法下按一下 Shift 只会切成英文。
    ///
    /// 只从**已安装**的布局里挑，不强行加载简中布局：用户可能用繁体、
    /// 日文或自定义中文布局；一个都没有时返回 false，保持原状。
    /// </summary>
    public static bool EnsureComposing(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;

        uint tid = GetWindowThreadProcessId(hwnd, out _);
        if (tid == 0) return false;

        IntPtr current = GetKeyboardLayout(tid);
        if (!IsComposing(current))
        {
            IntPtr target = FindInstalledComposing();
            if (target == IntPtr.Zero) return false;
            /* 100ms 足够 PostMessage 落地。原来 400ms 会在 Qbar 打开时
             * 卡住 UI 线程（两个选项都开时会连续切两次），用户能明显感觉到。
             * 布局切换本身是异步的，后面还会用 WPF 的 InputMethod 兜底。 */
            if (!SwitchTo(hwnd, target, 100)) return false;
        }

        /* 关键补丁：中文输入法在“英文模式”下 HKL 仍然是 0x0804，
         * IsComposing 已经为 true，但用户打出来还是英文。
         * Qbar 带引擎标签唤出时用户下一步就是打中文，所以这里直接
         * 把输入上下文的 conversion mode 拉回 IME_CMODE_NATIVE。
         *
         * ImmGetContext 对 TSF-only 窗口可能返回 NULL；此时保持布局结果，
         * 不把“拿不到上下文”误判成失败。 */
        return ForceNativeMode(hwnd) || ForceNativeMode(FocusOf(tid)) || IsComposing(current);
    }

    /// <summary>把输入上下文切到“本地语言”（中文/日文假名等）模式。</summary>
    private static bool ForceNativeMode(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        IntPtr himc = IntPtr.Zero;
        try
        {
            himc = ImmGetContext(hwnd);
            if (himc == IntPtr.Zero) return false;

            if (!ImmGetConversionStatus(himc, out uint conversion, out uint sentence))
                return false;

            const uint ImeCmodeNative = 0x0001;
            if ((conversion & ImeCmodeNative) != 0) return true;

            /* 保留全角/软键盘等其它位，只把 NATIVE 置上。 */
            return ImmSetConversionStatus(himc, conversion | ImeCmodeNative, sentence);
        }
        catch
        {
            return false;
        }
        finally
        {
            if (himc != IntPtr.Zero) ImmReleaseContext(hwnd, himc);
        }
    }

    /// <summary>
    /// 确保目标窗口处于不会拼候选的输入语言（英文）。
    ///
    /// 只在“当前是中文/日文等”时才切；已经是英文布局就什么都不做。
    /// 这一条对应 Qbar 的“打开时切英文”选项：用于直接打引擎关键字。
    /// </summary>
    public static bool EnsureEnglish(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;

        uint tid = GetWindowThreadProcessId(hwnd, out _);
        if (tid == 0) return false;

        IntPtr current = GetKeyboardLayout(tid);
        if (current != IntPtr.Zero && !IsComposing(current)) return true;

        IntPtr english = EnglishLayout;
        if (english == IntPtr.Zero) return false;
        return SwitchTo(hwnd, english, 100);
    }

    /// <summary>从系统已加载的键盘布局里找第一个会拼候选的（优先当前用户默认）。</summary>
    private static IntPtr FindInstalledComposing()
    {
        try
        {
            int count = GetKeyboardLayoutList(0, null);
            if (count <= 0) return IntPtr.Zero;

            var list = new IntPtr[count];
            count = GetKeyboardLayoutList(count, list);

            /* 先找简体中文（0x0804）。系统里往往同时装了中日韩，
             * 直接拿第一个 composing 布局会命中日文/韩文，
             * 用户按 CapsLock 出来的是假名输入，比英文还难用。 */
            foreach (var layout in list)
                if (((int)(layout.ToInt64() & 0xFFFF)) == 0x0804) return layout;

            for (int i = 0; i < count; i++)
                if (IsComposing(list[i])) return list[i];
        }
        catch
        {
            /* 预期内：枚举失败就当系统没有可用中文布局 */
        }
        return IntPtr.Zero;
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
