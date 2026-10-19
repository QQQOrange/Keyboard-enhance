using System.Diagnostics;
using System.Runtime.InteropServices;
using CapsLockPlus.Core.Interop;
using static CapsLockPlus.Core.Interop.NativeMethods;

namespace CapsLockPlus.Core.Input;

/// <summary>
/// 全局低级键盘钩子（WH_KEYBOARD_LL）。
/// 只做“判断 + 排队”，任何耗时动作都要通过回调交给动作队列执行，
/// 否则系统会因为钩子超时把钩子静默移除（上游 AutoHotkey 版本没有这个保护）。
/// </summary>
public sealed class KeyboardHook : IDisposable
{
    private readonly HookProc _proc;          // 必须保存引用，防止被 GC
    private IntPtr _hook = IntPtr.Zero;
    private Thread? _thread;
    private uint _threadId;
    private volatile bool _running;

    /// <summary>
    /// 按键事件回调：vk、是否按下、是否注入事件；返回 true 表示吞掉这个按键
    /// （不再传给前台程序）。回调必须极快，不能阻塞。
    /// </summary>
    public event Func<int, bool, bool, bool>? KeyEvent;

    /// <summary>鼠标滚轮回调：滚动格数（上正下负）、是否按住 Caps。</summary>
    public event Action<int, bool, bool>? WheelEvent;

    private HookProc? _mouseProc;
    private IntPtr _mouseHook = IntPtr.Zero;

    public bool IsRunning => _running;

    /// <summary>钩子最近一次异常/失效的时间，用于自愈监控。</summary>
    public DateTime LastErrorTime { get; private set; } = DateTime.MinValue;

    /// <summary>已安装的钩子句柄（供自检使用）。</summary>
    public bool HookInstalled => _hook != IntPtr.Zero;
    public bool MouseHookInstalled => _mouseHook != IntPtr.Zero;

    public KeyboardHook()
    {
        _proc = HookCallback;
        _mouseProc = MouseCallback;
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        _thread = new Thread(() =>
        {
            _threadId = GetCurrentThreadId();
            _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
            _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc!, GetModuleHandle(null), 0);
            if (_hook == IntPtr.Zero)
            {
                LastErrorTime = DateTime.Now;
                CoreLog.Error($"键盘钩子安装失败，错误码 {Marshal.GetLastWin32Error()}");
            }
            if (_mouseHook == IntPtr.Zero)
            {
                LastErrorTime = DateTime.Now;
                CoreLog.Error($"鼠标钩子安装失败，错误码 {Marshal.GetLastWin32Error()}");
            }
            MSG msg;
            while (_running && GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
            if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
            if (_mouseHook != IntPtr.Zero) { UnhookWindowsHookEx(_mouseHook); _mouseHook = IntPtr.Zero; }
        })
        {
            IsBackground = true,
            Name = "CapsLockPlus.KeyboardHook",
        };
        _thread.Start();
        // 等钩子装好，避免界面已经显示但按键还不生效
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 1500 && _hook == IntPtr.Zero && _running) Thread.Sleep(10);
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return CallNextHookEx(_hook, nCode, wParam, lParam);
        try
        {
            var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            int msg = (int)wParam;
            bool isDown = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
            bool isUp = msg is WM_KEYUP or WM_SYSKEYUP;
            if (isDown || isUp)
            {
                bool injected = (data.flags & LLKHF_INJECTED) != 0 ||
                                data.dwExtraInfo == InputSender.InjectionTag;
                bool swallow = false;
                try
                {
                    swallow = KeyEvent?.Invoke((int)data.vkCode, isDown, injected) ?? false;
                }
                catch (Exception ex)
                {
                    CoreLog.Error("按键处理回调异常", ex);
                }
                // 返回 1（非零）且不调用 CallNextHookEx 即可吞掉该按键
                if (swallow) return new IntPtr(1);
            }
        }
        catch (Exception ex)
        {
            CoreLog.Error("键盘钩子回调异常", ex);
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private IntPtr MouseCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        try
        {
            if ((int)wParam == WM_MOUSEWHEEL)
            {
                var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                bool injected = (data.flags & LLKHF_INJECTED) != 0 || data.dwExtraInfo == InputSender.InjectionTag;
                if (!injected)
                {
                    int delta = (short)((data.mouseData >> 16) & 0xFFFF);
                    if (delta != 0)
                        WheelEvent?.Invoke(delta > 0 ? 1 : -1, false, false);
                }
            }
        }
        catch (Exception ex)
        {
            CoreLog.Error("鼠标钩子回调异常", ex);
        }
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpmsg);

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
        public POINT pt;
    }

    public void Dispose()
    {
        Stop();
        _hook = IntPtr.Zero;
        _mouseHook = IntPtr.Zero;
    }

    /// <summary>卸载钩子（可再次 Start 重装，用于自愈）。</summary>
    public void Stop()
    {
        if (!_running) return;
        _running = false;
        if (_threadId != 0) PostThreadMessage(_threadId, 0x0012 /*WM_QUIT*/, IntPtr.Zero, IntPtr.Zero);
        _thread?.Join(1500);
        _thread = null;
        _threadId = 0;
        if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
        if (_mouseHook != IntPtr.Zero) { UnhookWindowsHookEx(_mouseHook); _mouseHook = IntPtr.Zero; }
    }

    /// <summary>重装钩子。</summary>
    public void Restart()
    {
        Stop();
        Thread.Sleep(80);
        Start();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);
}
