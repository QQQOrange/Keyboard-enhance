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

    /// <summary>捕获到新选区时触发。参数是选中的文字。</summary>
    public event Action<string>? Captured;

    /// <summary>最近一次捕获的文字。</summary>
    public string? LastSelection { get; private set; }

    /// <summary>钩子是否在跑。</summary>
    public bool IsRunning => _hook != IntPtr.Zero;

    /// <summary>
    /// 开始监听。重复调用安全（已在运行则直接返回）。
    /// </summary>
    public bool Start()
    {
        if (_hook != IntPtr.Zero) return true;

        _proc = HookCallback;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc,
            GetModuleHandle(null), 0);

        if (_hook == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            CoreLog.Warn($"选区监听钩子安装失败，错误码 {err}");
            return false;
        }

        CoreLog.Info("选区监听已启动（Ctrl+C 即捕获）");
        return true;
    }

    public void Stop()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
            CoreLog.Info("选区监听已停止");
        }
        _proc = null;
    }

    /// <summary>
    /// 钩子回调。
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
        catch { }

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
