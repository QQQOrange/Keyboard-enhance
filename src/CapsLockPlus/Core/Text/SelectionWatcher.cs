using System.Diagnostics;
using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace CapsLockPlus.Core.Text;

/// <summary>
/// 「选中即捕获」：监听用户的 Ctrl+C，自动拿到选中的文字。
///
/// ## 为什么需要它
///
/// 主动取词（UIA / WM_COPY / SendInput）在 Electron / Chromium 应用上
/// **全部不可靠**（实测确认）：
///
///   · UIA：Edge 会拒绝客户端访问无障碍树（`FromPoint` 报"拒绝访问"）
///   · WM_COPY：窗口消息到不了渲染进程的控件
///   · SendInput / SendKeys：Chromium 会过滤合成输入 ——
///     用户手动按 Ctrl+C 能复制，程序发的同样按键却无效
///
/// 所以正确的做法不是"我们去取"，而是"**用户复制时我们顺手记下**"。
///
/// ## 原理
///
/// 装一个全局低级键盘钩子，监听 **Ctrl+C**。钩子回调在按键进入目标
/// 程序**之前**同步执行，此刻剪贴板序号还是"复制前"的值 ——
/// 把它记下来。等目标程序写完剪贴板，序号就会变，
/// 前后一比就是这次复制的内容。
///
/// 顺带白拿一个好处：**完全不碰用户剪贴板**。
/// 我们是旁观者，不是参与者 —— 不会出现"取词把用户剪贴板搞丢"。
/// </summary>
public sealed class SelectionWatcher : IDisposable
{
    // ---- Win32 ----

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;

    private const int VK_C = 0x43;
    private const int VK_INSERT = 0x2D;
    private const int VK_CONTROL = 0x11;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    /* 钩子必须装在有消息循环的线程上，而**不能是界面线程**。
     *
     * 这一条是 v4.5.65 的真正根因：以前 Start() 是在界面线程上调用的，
     * 钩子就挂在了界面线程上。低级钩子的回调会被投递到"装钩子的那条线程"，
     * 界面线程一忙（建页面、云同步、备份、渲染），系统就要等它 ——
     * 等超时（默认 300ms）就**丢弃这次输入**，并可能把钩子摘掉。
     *
     * 表现：整个系统的按键偶尔丢；更致命的是这条钩子排在 KeyboardHook 前面，
     * 它一卡，我们自己的钩子**连回调都进不去** —— 于是热键全部失灵、
     * 长按触发键一直输出原键，而所有状态显示都正常。 */
    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int x;
        public int y;
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private delegate IntPtr LowLevelProc(int nCode, IntPtr wParam, IntPtr lParam);

    private LowLevelProc? _proc;
    private IntPtr _hook;
    private Thread? _thread;
    private uint _threadId;
    private volatile bool _running;

    /// <summary>捕获到新选区时触发。参数是选中的文字。</summary>
    public event Action<string>? Captured;

    /// <summary>最近一次捕获的文字。</summary>
    public string? LastSelection { get; private set; }

    /// <summary>最近一次捕获的时间（UTC）。从没捕获过是 <see cref="DateTime.MinValue"/>。</summary>
    public DateTime LastSelectionAt { get; private set; } = DateTime.MinValue;

    /// <summary>
    /// 只在 <paramref name="window"/> 时间内的最近捕获才算"新鲜"。
    ///
    /// 为什么必须限时：<see cref="LastSelection"/> 会一直留着上一次复制的内容。
    /// 如果用户十分钟前复制过一段字，现在新选了一段却没复制就按搜索键，
    /// 直接拿 LastSelection 去搜就会**搜到十分钟前那段**——
    /// 比"搜不到"更让人困惑，所以过期的结果一律当没有。
    /// </summary>
    public string? LastSelectionWithin(TimeSpan window)
    {
        if (string.IsNullOrWhiteSpace(LastSelection)) return null;
        if (LastSelectionAt == DateTime.MinValue) return null;
        return DateTime.UtcNow - LastSelectionAt <= window ? LastSelection : null;
    }

    /// <summary>是否已启用（现在由 Raw Input 驱动，不再有自己的钩子）。</summary>
    public bool IsRunning => _running;

    /// <summary>
    /// 开始监听。重复调用安全（已在运行则直接返回）。
    /// </summary>
    public bool Start()
    {
        if (_running) return true;

        _running = true;

        /* ⚠ 这里**不再装第二个低级键盘钩子**（v4.5.65 的根因）。
         *
         * 以前这里自己 SetWindowsHookEx 了一个 WH_KEYBOARD_LL。低级钩子是**串行**的：
         * 它排在 KeyboardHook 前面，只要它的回调没及时返回（装在界面线程上时，
         * 界面一忙就会被系统等待），系统就会**丢弃这次输入**，后面的钩子（我们的）
         * 一个事件都收不到 —— 表现就是"热键全失灵、长按触发键一直输出原键"，
         * 而所有状态显示都正常，完全无法从日志看出问题。
         *
         * 现在改成由已有的 Raw Input 通道喂键（见 NotifyKey）：
         *   · 全进程只有一个低级钩子，链路简单；
         *   · Raw Input 对远程桌面 / 注入输入同样有效，比钩子更可靠；
         *   · 不再影响别的程序收键。
         */
        CoreLog.Info("选区监听已启用（Ctrl+C 即捕获，走 Raw Input，不额外装钩子）");
        return true;
    }

    public void Stop()
    {
        if (!_running && _hook == IntPtr.Zero) return;
        _running = false;
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);       // 老版本装过的钩子，兜底卸掉
            _hook = IntPtr.Zero;
        }
        _proc = null;
        CoreLog.Info("选区监听已停止");
    }

    /// <summary>
    /// 由 Raw Input 通道喂进来的按键（替代原来的低级钩子）。
    ///
    /// 看到 Ctrl+C / Ctrl+Insert 按下时，**同步**记下当前剪贴板序号 ——
    /// 此刻用户这次复制还没生效，序号是"复制前"的值；然后排延时任务比对。
    /// </summary>
    public void NotifyKey(int vk, bool down)
    {
        if (!_running || !down) return;
        if (vk != VK_C && vk != VK_INSERT) return;

        try
        {
            bool ctrlDown = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
            if (!ctrlDown) return;

            uint before = GetClipboardSequenceNumber();
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                await System.Threading.Tasks.Task.Delay(180);
                await CheckClipboard(before);
            });
        }
        catch { }   /* 预期内：探测/重试型，失败即视为无结果 */
    }

    /// <summary>
    /// 钩子回调（仅老版本残留路径；现在不再安装）。
    ///
    /// 看到 Ctrl+C / Ctrl+Insert 按下时，**同步**记下当前剪贴板序号 ——
    /// 此刻用户这次复制还没生效，序号是"复制前"的值。
    /// 然后排一个延时任务去比对：序号变了就是复制成功，读出来就是选区。
    /// </summary>
    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0 && wParam == (IntPtr)WM_KEYDOWN)
            {
                var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                uint vk = data.vkCode;

                bool isCopyKey = vk == VK_C || vk == VK_INSERT;
                bool ctrlDown = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;

                if (isCopyKey && ctrlDown)
                {
                    // 同步取"复制前"的序号（关键：此刻还没复制）
                    uint before = GetClipboardSequenceNumber();

                    // 目标程序处理按键后序号会变，等一会儿再比对
                    _ = System.Threading.Tasks.Task.Run(async () =>
                    {
                        await System.Threading.Tasks.Task.Delay(180);
                        await CheckClipboard(before);
                    });
                }
            }
        }
        catch { }   /* 预期内：探测/重试型，失败即视为无结果 */

        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <summary>
    /// 比对剪贴板序号：变了说明用户这次复制成功，读出内容并触发事件。
    /// </summary>
    private async System.Threading.Tasks.Task CheckClipboard(uint before)
    {
        try
        {
            // 最多等 1 秒（浏览器写剪贴板可能有延迟）
            for (int i = 0; i < 10; i++)
            {
                uint now = GetClipboardSequenceNumber();
                if (now != before)
                {
                    string text = await ReadClipboardOnSta();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        LastSelection = text;
                        LastSelectionAt = DateTime.UtcNow;
                        Captured?.Invoke(text);
                        CoreLog.Info($"[选区监听] 捕获到 {text.Length} 字");
                    }
                    return;
                }
                await System.Threading.Tasks.Task.Delay(60);
            }
        }
        catch (Exception ex)
        {
            CoreLog.Warn("[选区监听] 读取剪贴板失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 在 STA 线程读剪贴板（Clipboard 是 OLE 组件，MTA 线程会抛异常）。
    /// </summary>
    private static async System.Threading.Tasks.Task<string> ReadClipboardOnSta()
    {
        var tcs = new System.Threading.Tasks.TaskCompletionSource<string>();
        var t = new Thread(() =>
        {
            try
            {
                tcs.SetResult(Clipboard.ContainsText() ? Clipboard.GetText() : "");
            }
            catch (Exception ex)
            {
                tcs.SetResult("");
                CoreLog.Warn("[选区监听] 剪贴板异常：" + ex.Message);
            }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
        return await tcs.Task;
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }
}
