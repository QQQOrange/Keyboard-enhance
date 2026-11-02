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

    private System.Threading.Timer? _watchdog;

    /// <summary>看门狗投递给钩子线程的自定义消息（重装钩子）。</summary>
    private const int WM_APP_REARM = 0x8001;
    private const int WM_APP_PING = 0x0400 + 0x2A;   // 探活：确认钩子线程还在跑（不是被摘/卡死）

    /// <summary>首次巡检延迟：覆盖"装钩失败"这种一开始就不工作的情况。</summary>
    private const int WatchdogFirstDelayMs = 5_000;

    /// <summary>巡检周期。重装只有几微秒，但没必要太频繁。</summary>
    private const int WatchdogIntervalMs = 180_000;

    /// <summary>
    /// 按键事件回调：vk、是否按下、是否注入事件；返回 true 表示吞掉这个按键
    /// （不再传给前台程序）。回调必须极快，不能阻塞。
    /// </summary>
    public event Func<int, bool, bool, bool>? KeyEvent;

    /// <summary>
    /// 回调抛异常时的兜底：由引擎判断这个键是否仍要吞掉（fail-closed）。
    ///
    /// 默认行为是"异常即放行"（fail-open）—— 对普通按键无害，但对触发键
    /// 与层内按键是灾难：层明明已经生效，Tab 却连每一次自动重复都被漏给
    /// 前台，用户看到的就是"切完层焦点还在跳"。
    /// 这里把决定权交回引擎，由它按"当前是否处于层语义"给答案。
    /// </summary>
    public Func<int, bool>? SwallowOnError { get; set; }

    /// <summary>鼠标滚轮回调：滚动格数（上正下负）、是否按住 Caps。</summary>
    public event Action<int, bool, bool>? WheelEvent;

    /// <summary>
    /// 鼠标按键回调：屏幕物理坐标 x、y 和鼠标消息（WM_LBUTTONDOWN 等）。
    /// 只在本程序自己没注入时才触发。回调运行在钩子线程，必须极快。
    ///
    /// 用途：Qbar 面板打开期间判断"点没点到面板外面" —— 任务栏这类
    /// 不会换前台的地方不会产生 Deactivated，只能靠全局钩子兜底。
    /// </summary>
    public event Action<int, int, int>? MouseDownEvent;

    private HookProc? _mouseProc;
    private IntPtr _mouseHook = IntPtr.Zero;

    /// <summary>
    /// 钩子是否真正可用。
    /// 不能只看 _running 这个“意愿”标志 —— 钩子句柄为 0 时说明安装失败或被系统摘掉，
    /// 此时 _running 仍是 true，自愈监控会一直以为一切正常。
    /// </summary>
    public bool IsRunning => _running && _hook != IntPtr.Zero;

    /// <summary>钩子最近一次异常/失效的时间，用于自愈监控。</summary>
    public DateTime LastErrorTime { get; private set; } = DateTime.MinValue;

    /// <summary>已安装的钩子句柄（供自检使用）。</summary>
    public bool HookInstalled => _hook != IntPtr.Zero;
    public bool MouseHookInstalled => _mouseHook != IntPtr.Zero;

    /// <summary>诊断开关：把收到的每个按键写进日志（排查“按键没反应”时打开）。</summary>
    public bool TraceKeys { get; set; }

    /// <summary>累计收到的按键数（诊断用）。</summary>
    public long KeyEventCount => Interlocked.Read(ref _keyEvents);
    private long _keyEvents;

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
                /* 看门狗的重装请求必须在本线程处理：SetWindowsHookEx 装出来的
                 * 钩子绑定的是"调用它的线程"，换线程装会得到一个没有消息循环
                 * 的钩子，等于装了个不会回调的空壳。 */
                if (msg.message == WM_APP_REARM) { RearmHooks(); continue; }
                if (msg.message == WM_APP_PING) { Interlocked.Increment(ref _pingAcks); continue; }

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
        StartWatchdog();
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        long t0 = Stopwatch.GetTimestamp();
        try
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
                Interlocked.Increment(ref _keyEvents);
                // 只忽略“本程序自己发的”按键：靠 dwExtraInfo 标记判断。
                //
                // 不要用 LLKHF_INJECTED —— 它会把所有合成按键都算进去，
                // 包括触摸键盘、屏幕键盘、其他自动化工具和远程桌面输入。
                // 那样一来，用这些方式输入时 Caps 组合键会全部失效。
                bool injectedByUs = data.dwExtraInfo == InputSender.InjectionTag;
                if (TraceKeys)
                {
                    CoreLog.Info($"钩子收到 vk=0x{data.vkCode:X2} down={isDown} " +
                                 $"flags=0x{data.flags:X} extra=0x{data.dwExtraInfo.ToInt64():X} 自己发的={injectedByUs}");
                }
                bool swallow = false;
                try
                {
                    swallow = KeyEvent?.Invoke((int)data.vkCode, isDown, injectedByUs) ?? false;
                }
                catch (Exception ex)
                {
                    /* 异常绝不能等于"放行"。
                     *
                     * 早先是 swallow=false 直接落到 CallNextHookEx：只要引擎在
                     * 层内分支上抛一次（空引用、绑定数据异常、P/Invoke 失败），
                     * 这个键的**每一次自动重复**都会被漏给前台 ——
                     * 表现正是"层已经切好，Tab 还在别的程序里跳焦点"。
                     *
                     * 现在把决定权交回引擎：处于层语义就吞，宁可这次按键
                     * 没反应，也不要把按键漏出去。 */
                    CoreLog.Error($"按键处理回调异常 vk=0x{data.vkCode:X2} down={isDown}", ex);
                    try { swallow = SwallowOnError?.Invoke((int)data.vkCode) ?? false; }
                    catch { swallow = false; }
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
        finally { ObserveCallbackCost(t0, lParam); }
    }

    /// <summary>
    /// 钩子回调耗时统计。
    ///
    /// 系统有一条硬限制：低级钩子回调超过 LowLevelHooksTimeout（默认 300ms）
    /// 就会被**静默摘掉** —— 之后所有按键都收不到，而句柄还在，IsHookAlive 照样是 true。
    /// 表现就是「录制宏时一个键都没有」「组合键突然全失灵」。
    /// 所以这里把每次回调的耗时量出来：慢的当场记一条，最快能定位是谁卡住了钩子线程。
    /// </summary>
    private void ObserveCallbackCost(long t0, IntPtr lParam)
    {
        double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
        if (ms > MaxCallbackCostMs) MaxCallbackCostMs = ms;
        if (ms < 30) return;

        // 限流：卡住时往往连续很多次，别把日志刷爆
        long now = Environment.TickCount64;
        if (now - _lastSlowLogAt < 5000) return;
        _lastSlowLogAt = now;

        int vk = 0;
        try { vk = (int)Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam).vkCode; } catch { }
        CoreLog.Warn($"钩子回调耗时 {ms:F0}ms（vk=0x{vk:X2}）—— 超过 300ms 系统会直接摘钩，之后不再收到任何按键");
    }

    /// <summary>钩子回调的最长耗时（毫秒），诊断用。</summary>
    public double MaxCallbackCostMs { get; private set; }

    private long _lastSlowLogAt;

    private IntPtr MouseCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        try
        {
            int msg = (int)wParam;
            if (msg == WM_MOUSEWHEEL)
            {
                var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                // 与键盘钩子保持一致：只忽略本程序自己发的滚轮事件。
                bool injectedByUs = data.dwExtraInfo == InputSender.InjectionTag;
                if (!injectedByUs)
                {
                    int delta = (short)((data.mouseData >> 16) & 0xFFFF);
                    if (delta != 0)
                        WheelEvent?.Invoke(delta > 0 ? 1 : -1, false, false);
                }
            }
            else if (msg is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN)
            {
                var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                if (data.dwExtraInfo != InputSender.InjectionTag)
                    MouseDownEvent?.Invoke(data.pt.X, data.pt.Y, msg);
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

    /// <summary>
    /// 让钩子线程在"下一次空闲"时把钩子重装一遍，全程没有空窗。
    ///
    /// 为什么需要：低级钩子被系统静默摘掉（回调超过 LowLevelHooksTimeout）
    /// 之后，SetWindowsHookEx 返回的句柄**仍然非零**，没有任何 API 能查出来。
    /// 唯一可靠的兜底就是定期重装 —— 否则一旦被摘，整个程序直到重启
    /// 都不再拦截任何按键，而界面上一切正常。
    /// </summary>
    private void StartWatchdog()
    {
        _watchdog ??= new System.Threading.Timer(_ =>
        {
            if (!_running || _threadId == 0) return;
            PostThreadMessage(_threadId, WM_APP_REARM, IntPtr.Zero, IntPtr.Zero);
        }, null, WatchdogFirstDelayMs, WatchdogIntervalMs);
    }

    /// <summary>
    /// 重装钩子：**先装新的，再卸旧的**。
    ///
    /// Stop() + Start() 的写法中间有 80ms 全放行窗口，若恰好落在切层瞬间，
    /// 就会表现为"层已经切好，Tab 还是漏出去了"。原子替换没有这个缺口。
    /// </summary>
    private void RearmHooks()
    {
        var fresh = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
        if (fresh != IntPtr.Zero)
        {
            var old = _hook;
            _hook = fresh;
            if (old != IntPtr.Zero) UnhookWindowsHookEx(old);
        }
        else
        {
            LastErrorTime = DateTime.Now;
            CoreLog.Error($"键盘钩子重装失败，错误码 {Marshal.GetLastWin32Error()}");
        }

        var freshMouse = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc!, GetModuleHandle(null), 0);
        if (freshMouse != IntPtr.Zero)
        {
            var oldMouse = _mouseHook;
            _mouseHook = freshMouse;
            if (oldMouse != IntPtr.Zero) UnhookWindowsHookEx(oldMouse);
        }
    }

    /// <summary>卸载钩子（可再次 Start 重装，用于自愈）。</summary>
    private long _pingAcks;

    /// <summary>
    /// 钩子线程还活着吗（发一条消息过去，看它有没有处理）。
    ///
    /// 为什么需要：低级钩子**被系统摘掉**之后，句柄还在、`IsRunning` 仍是 true，
    /// 但从此刻起一个按键都收不到 —— 表现就是「宏录制没反应」「组合键突然全失灵」，
    /// 而所有状态显示都正常。线程卡死（回调里死等）也是同样的症状。
    /// 探活能把这三种情况分开：正常 / 线程卡死 / 线程已退出。
    /// </summary>
    public bool Ping(int timeoutMs = 400)
    {
        if (!_running || _hook == IntPtr.Zero || _threadId == 0) return false;
        long before = Interlocked.Read(ref _pingAcks);
        if (!PostThreadMessage(_threadId, WM_APP_PING, IntPtr.Zero, IntPtr.Zero)) return false;

        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (Interlocked.Read(ref _pingAcks) != before) return true;
            Thread.Sleep(5);
        }
        return false;
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        _watchdog?.Dispose();
        _watchdog = null;
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
