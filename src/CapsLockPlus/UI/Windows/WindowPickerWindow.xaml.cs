using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using CapsLockPlus.Core;
using CapsLockPlus.Core.Windows;
using static CapsLockPlus.Core.Interop.NativeMethods;
using NativeMethods = CapsLockPlus.Core.Interop.NativeMethods;

namespace CapsLockPlus.UI.Windows;

/// <summary>
/// 十字准星窗口点选器。
///
/// 取窗口分两段，先用最准的，不准再退回扫描：
///
///   **① WindowFromPoint → GetAncestor(GA_ROOT)**
///   按真实 Z 序 + 命中测试判断"鼠标底下到底是谁"，不用我们猜矩形。
///   遮罩自己不会被选中 —— 它带着 WS_EX_TRANSPARENT，
///   而 WindowFromPoint 对这类窗口本来就跳过（"点击穿透"就是这个原理）。
///   微信这类自绘窗口、以及带透明边框的分层窗口，走这条路最稳。
///
///   **② Z 序矩形扫描**（原实现，现在降级为备用）
///   从 <c>GetTopWindow(desktop)</c> 沿 <c>GW_HWNDNEXT</c> 往下扫，
///   用窗口矩形判断是否包含该点。①拿不到结果时兜底。
///
/// 为什么原来只用 ②：早先担心 WindowFromPoint 永远返回遮罩自己。
/// 那个担心只在**遮罩不透明**时成立；遮罩既然已经设了 WS_EX_TRANSPARENT，
/// WindowFromPoint 就会穿过去。只留 ② 的代价是：窗口矩形受 DWM 隐形边框
/// 与 DPI 虚拟化影响，某些程序（尤其自绘 UI）会算不中，表现就是"点不到某个窗口"。
///
/// 输入（左键选中 / 右键取消 / Esc 取消）**全部靠轮询按键状态**，不用低级钩子：
///
///   * 遮罩是 WS_EX_NOACTIVATE 的（不能抢焦点，否则点选会把用户正在用的窗口
///     顶到后面），所以它收不到 WPF 的键盘鼠标事件 —— PreviewKeyDown 永远不触发；
///   * 低级钩子（WH_MOUSE_LL）在**目标窗口完整性级别更高**时会收不到事件
///     （UIPI 限制，比如对方以管理员身份运行），而且 UI 线程一忙就会被系统
///     静默摘掉钩子（LowLevelHooksTimeout）。表现正是"能高亮、点了没反应、
///     右键也取消不了" —— 用户报的就是这个；
///   * GetAsyncKeyState 查的是**物理按键状态**，不经过任何投递链路，
///     不受 UIPI 与钩子超时影响，永远拿得到。
///
/// 代价只是延迟最多 20ms（一个轮询周期），人手感觉不到。
/// Esc 另外还挂了一个键盘钩子，唯一目的是把它**吞掉**，不让它漏到下面的程序。
/// </summary>
public partial class WindowPickerWindow : Window
{
    /// <summary>用户选中的窗口句柄；取消时为 IntPtr.Zero。</summary>
    public IntPtr PickedWindow { get; private set; }

    private IntPtr _selfHandle;
    private IntPtr _kbdHook = IntPtr.Zero;
    private NativeMethods.HookProc _kbdProc;      // 必须保存引用，防止被 GC（构造函数里赋值）
    private readonly DispatcherTimer _poll;
    private bool _closing;

    /// <summary>上一轮轮询看到的按键状态，用来识别"按下沿"。</summary>
    private bool _prevLeft, _prevRight, _prevEsc;

    /// <summary>轮询计数：按键每 20ms 查一次，高亮不必这么勤，每 4 次刷一次。</summary>
    private int _pollTick;

    /* 只保留这三个"键码"常量 —— 钩子、GetAsyncKeyState、WM_KEY*、
     * WH_KEYBOARD_LL、HookProc 委托在 NativeMethods 里都已经有一份，
     * 之前这里重复声明了 5 个 P/Invoke 和 1 个委托，两处签名各改各的
     * 迟早会漂移。本文件已经 `using static NativeMethods`，直接用统一的那份。 */
    private const uint VK_ESCAPE = 0x1B;
    private const uint VK_LBUTTON = 0x01;
    private const uint VK_RBUTTON = 0x02;

    /// <summary>这个键此刻是否被按住（查物理状态，不受 UIPI / 钩子影响）。</summary>
    private static bool IsDown(uint vk) => (GetAsyncKeyState((int)vk) & 0x8000) != 0;

    /// <summary>
    /// 仅供 --render：出图时不要把遮罩铺到用户屏幕上。
    ///
    /// 这个窗口是 **Topmost + 铺满整个虚拟屏幕** 的。当成后台命令
    /// （exe --render 出图）跑时，用户根本不知道会弹东西，
    /// 屏幕上却会突然盖一层半透明遮罩 —— 看起来就像"程序弹了个怪窗口"。
    /// 走这个构造时尺寸照旧，只把窗口挪到屏幕外，RenderTargetBitmap 仍能截到内容。
    /// </summary>
    private readonly bool _renderOffScreen;

    public WindowPickerWindow() : this(false) { }

    internal WindowPickerWindow(bool renderOffScreen)
    {
        /* 必须在 InitializeComponent / FitToVirtualScreen **之前**赋值：
         * 位置是在构造里就定下来的，晚一步就已经在屏幕上闪过了。 */
        _renderOffScreen = renderOffScreen;

        InitializeComponent();
        FitToVirtualScreen();          // 先摆好尺寸再显示，避免一帧小窗闪过
        _kbdProc = KeyboardCallback;
        Loaded += OnLoaded;
        Closed += OnClosed;
        PreviewKeyDown += OnKeyDown;

        /* 20ms 一轮：既用来识别点击/取消，也顺便驱动高亮跟随。
         * 用 Input 优先级而不是 Render —— 它要在渲染之前跑，
         * 不然界面一忙，点击判定就跟着慢半拍。 */
        _poll = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(20),
        };
        _poll.Tick += (_, _) => PollInput();
    }

    /// <summary>
    /// 铺满**整个虚拟屏幕**，而不是只铺主屏。
    ///
    /// 原来用 WindowState=Maximized，它只覆盖主显示器；接了扩展屏时，
    /// 副屏上根本没有遮罩 —— 那里的窗口既看不到高亮也点不到。
    /// 副屏在主屏左侧/上方时 Left/Top 是负数，这里照抄系统给的值即可。
    /// </summary>
    private void FitToVirtualScreen()
    {
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        if (_renderOffScreen)
        {
            // 屏幕外：尺寸保持全屏（截图要那张铺满的效果），位置挪走
            Left = SystemParameters.VirtualScreenLeft - Width - 200;
            Top = SystemParameters.VirtualScreenTop - Height - 200;
            return;
        }

        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _selfHandle = new WindowInteropHelper(this).Handle;

        // 关键：点击穿透。这样点击直接落到下面的真实窗口上，
        // 我们的遮罩不会"吃掉"点击。
        int ex = GetWindowLong(_selfHandle, GWL_EXSTYLE);
        ex |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        SetWindowLong(_selfHandle, GWL_EXSTYLE, ex);
        SetWindowPos(_selfHandle, HWND_TOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_FRAMECHANGED);

        FitToVirtualScreen();

        /* 用"当前实际按键状态"初始化沿检测。
         *
         * 不初始化的话，用户点完「选择窗口」按钮、手还没松开时遮罩就弹出来了，
         * 第一轮轮询看到"左键是按下的"会立刻当成一次点击，当场选中鼠标底下
         * 那个窗口 —— 点选器一出来就自己关掉。 */
        _prevLeft = IsDown(VK_LBUTTON);
        _prevRight = IsDown(VK_RBUTTON);
        _prevEsc = IsDown(VK_ESCAPE);

        // 键盘钩子只为了把 Esc 吞掉，不让它漏到底下的程序
        _kbdHook = SetWindowsHookEx(WH_KEYBOARD_LL, _kbdProc, GetModuleHandle(null), 0);
        if (_kbdHook == IntPtr.Zero)
            CoreLog.Warn($"点选器键盘钩子安装失败（错误码 {Marshal.GetLastWin32Error()}），" +
                         "Esc 仍可用（轮询兜底），但会同时传到底下的程序");

        _poll.Start();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _poll.Stop();
        if (_kbdHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_kbdHook);
            _kbdHook = IntPtr.Zero;
        }
    }

    /// <summary>
    /// 每 20ms 一轮：识别左键选中 / 右键取消 / Esc 取消，顺便刷新高亮。
    ///
    /// 只在**按下沿**动作（上一次没按、这一次按下）。用按下而不是抬起：
    /// 抬起沿要依赖"看见过按下"，快速点击可能整个按下阶段都落在两轮轮询之间，
    /// 那就永远等不到抬起沿；按下沿只要碰上一次就成立。
    /// </summary>
    private void PollInput()
    {
        if (_closing) return;

        bool left = IsDown(VK_LBUTTON);
        bool right = IsDown(VK_RBUTTON);
        bool esc = IsDown(VK_ESCAPE);

        bool leftEdge = left && !_prevLeft;
        bool rightEdge = right && !_prevRight;
        bool escEdge = esc && !_prevEsc;

        _prevLeft = left;
        _prevRight = right;
        _prevEsc = esc;

        if (escEdge || rightEdge) { Cancel(); return; }
        if (leftEdge) { CommitAt(CurrentCursor()); return; }

        // 高亮每 4 轮（80ms）刷一次就够了
        if (++_pollTick % 4 == 0) UpdateHover(CurrentCursor());
    }

    /// <summary>在指定位置"定案"：认得出窗口就选中并关闭，认不出就提示换个位置。</summary>
    private void CommitAt(POINT pt)
    {
        if (_closing) return;

        var target = ResolveTargetUnder(pt.X, pt.Y);
        if (target == IntPtr.Zero)
        {
            HoverTitle.Text = "这里没认出窗口，换个位置再点一次";
            HoverProcess.Text = "（任务栏、桌面这类系统窗口不能绑）　·　Esc / 右键取消";
            HoverInfo.Visibility = Visibility.Visible;
            HoverInfo.Margin = new Thickness(
                Math.Max(0, ActualWidth / 2 - 200), 60, 0, 0);
            return;
        }

        var info = WindowService.Describe(target);
        CoreLog.Info($"点选到窗口 0x{target.ToInt64():X}" +
                     $" class={WindowService.ClassNameOf(target)}" +
                     $" title={info?.Title}");
        Finish(target);
    }

    /// <summary>WPF 事件通道的 Esc（键盘钩子装不上时的备用路径）。</summary>
    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Cancel();
    }

    private void Cancel() => Finish(IntPtr.Zero);

    /// <summary>
    /// 收尾：记下结果并关闭。取消时传 <see cref="IntPtr.Zero"/>。
    ///
    /// 选中和取消都走这里 —— 以前选中是"先赋值再调 Cancel()"，
    /// 而 Cancel() 会把 PickedWindow 抹成 0，等于白选。
    /// </summary>
    private void Finish(IntPtr picked)
    {
        if (_closing) return;
        _closing = true;
        PickedWindow = picked;
        Close();
    }

    /// <summary>
    /// 全局键盘钩子：只关心 Esc，按下和抬起都吞掉。
    ///
    /// 按下要吞，是为了不让 Esc 漏到底下正在用的程序（比如编辑器里
    /// 退出输入模式、浏览器里停止加载）；抬起也吞，避免目标程序收到
    /// 一个没有配对 keydown 的 keyup。
    /// </summary>
    private IntPtr KeyboardCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && !_closing)
        {
            int msg = (int)wParam;
            if (msg is WM_KEYDOWN or WM_KEYUP or WM_SYSKEYDOWN or WM_SYSKEYUP)
            {
                var data = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
                if (IsCancelKey(msg, data.vkCode))
                {
                    if (msg is WM_KEYDOWN or WM_SYSKEYDOWN)
                        Dispatcher.BeginInvoke(Cancel);
                    return (IntPtr)1;          // 吞掉，不再往下传
                }
            }
        }
        return CallNextHookEx(_kbdHook, nCode, wParam, lParam);
    }

    /// <summary>
    /// 这个键盘消息是不是"取消键"（Esc 的按下或抬起）。
    ///
    /// 抽成纯函数是为了让自检能直接断言 —— 装钩子、发按键的测试
    /// 会真的往系统里塞一个 Esc，代价太大。
    /// </summary>
    private static bool IsCancelKey(int msg, uint vk) =>
        vk == VK_ESCAPE &&
        msg is WM_KEYDOWN or WM_KEYUP or WM_SYSKEYDOWN or WM_SYSKEYUP;

    /// <summary>自检用：键盘钩子的判定逻辑（不装钩子、不真的按键）。</summary>
    public bool SwallowsKeyForTest(int msg, uint vk) => IsCancelKey(msg, vk);

    /// <summary>自检用：走一次"取消"，验证会把选中结果清空。</summary>
    public void CancelForTest() => Cancel();

    /// <summary>自检用：在指定坐标模拟一次点击，返回最终选中的句柄。</summary>
    public IntPtr CommitAtForTest(int x, int y)
    {
        CommitAt(new POINT { X = x, Y = y });
        return PickedWindow;
    }

    private static POINT CurrentCursor()
    {
        GetCursorPos(out var pt);
        return pt;
    }


    private void UpdateHover(POINT pt)
    {
        if (_closing) return;
        var target = ResolveTargetUnder(pt.X, pt.Y);

        if (target == IntPtr.Zero)
        {
            Highlight.Visibility = Visibility.Collapsed;
            HoverInfo.Visibility = Visibility.Collapsed;
            return;
        }

        var rect = WindowService.GetRect(target);
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            Highlight.Visibility = Visibility.Collapsed;
            HoverInfo.Visibility = Visibility.Collapsed;
            return;
        }

        // 屏幕坐标 → 本窗口坐标（本窗口铺满整个虚拟屏幕）
        var topLeft = PointFromScreen(new Point(rect.Left, rect.Top));
        var bottomRight = PointFromScreen(new Point(rect.Right, rect.Bottom));
        Highlight.Margin = new Thickness(
            topLeft.X, topLeft.Y,
            Math.Max(0, ActualWidth - bottomRight.X),
            Math.Max(0, ActualHeight - bottomRight.Y));
        Highlight.Visibility = Visibility.Visible;

        var info = WindowService.Describe(target);
        HoverTitle.Text = string.IsNullOrWhiteSpace(info?.Title) ? "(无标题)" : info!.Title;
        HoverProcess.Text = $"{info?.ExeName}　·　{rect.Width}×{rect.Height}　·　左键选中 / 右键取消";
        HoverInfo.Visibility = Visibility.Visible;

        /* 提示卡跟着鼠标走。
         *
         * 注意要减掉窗口自己的 Left/Top：遮罩铺的是**虚拟屏幕**，
         * 副屏在主屏左侧/上方时 Left/Top 是负数，直接用屏幕坐标当 Margin
         * 会让卡片跑到屏幕外。 */
        double left = Math.Min(pt.X - Left + 18, Math.Max(0, ActualWidth - 470));
        double top = Math.Min(pt.Y - Top + 18, Math.Max(0, ActualHeight - 90));
        HoverInfo.Margin = new Thickness(left, top, 0, 0);
    }

    /// <summary>
    /// 取指定坐标下"不属于本进程"的最上面那个可见顶层窗口。
    ///
    /// 两段式，顺序很重要：
    ///
    /// **① WindowFromPoint → GetAncestor(GA_ROOT)** —— 首选。
    /// 它按系统自己的命中测试判断"这一点属于哪个窗口"，天然处理：
    ///   · 真实 Z 序（不用我们比矩形）；
    ///   · 分层窗口的 alpha 与形状；
    ///   · 自绘 UI（微信这类程序的窗口结构五花八门，比矩形可靠得多）；
    ///   · DPI 虚拟化与 DWM 隐形边框 —— 矩形法在这里最容易算偏。
    ///
    /// **② Z 序矩形扫描** —— ①没给出可接受结果时的兜底。
    /// 从桌面 Z 序顶端沿 GW_HWNDNEXT 往下扫，用窗口矩形判断是否包含该点。
    /// 适合"某个驱动把 Z 序打乱、WindowFromPoint 只给出桌面"这类极端情况。
    /// </summary>
    private static IntPtr ResolveTargetUnder(int x, int y)
    {
        // ---------- ① 系统命中测试 ----------
        var direct = WindowFromPoint(new POINT { X = x, Y = y });
        if (direct != IntPtr.Zero)
        {
            /* 提到顶层：点在微信的聊天列表上时，WindowFromPoint 给的是
             * 那个子窗口，而我们要绑的是整个微信窗口。 */
            var root = GetAncestor(direct, GA_ROOT);
            if (root == IntPtr.Zero) root = direct;
            if (IsAcceptable(root)) return root;
        }

        // ---------- ② 兜底：Z 序 + 矩形 ----------
        uint me = (uint)Environment.ProcessId;
        var desktop = GetDesktopWindow();
        var h = GetTopWindow(desktop);
        int guard = 0;

        while (h != IntPtr.Zero && guard < 4096)
        {
            guard++;
            if (IsWindowVisible(h) && !IsIconic(h))
            {
                // 跳过点击穿透的蒙层：它们不是"鼠标底下的窗口"。
                // （WindowFromPoint 本来就跳过这类窗口，这里把两条路对齐。）
                int ex = GetWindowLong(h, GWL_EXSTYLE);
                if ((ex & WS_EX_TRANSPARENT) == 0)
                {
                    var r = WindowService.GetRect(h);
                    if (r.Width > 0 && r.Height > 0 &&
                        x >= r.Left && x < r.Right && y >= r.Top && y < r.Bottom)
                    {
                        if (IsAcceptable(h)) return h;
                    }
                }
            }
            // 注意：必须写全限定名。本类继承自 WPF 的 Window，
            // 类内部直接写 GetWindow 会被解析成 Window.GetWindow(DependencyObject)。
            h = NativeMethods.GetWindow(h, GW_HWNDNEXT);
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// 这个窗口能不能拿来绑定。
    ///
    /// 排除四类：无效/隐藏/最小化、本进程自己、系统壳窗口（桌面、任务栏、
    /// 开始菜单、输入法候选框等）、以及拿不到进程的窗口。
    ///
    /// 壳窗口必须排除：它们铺满屏幕、Z 序又常常在最上面，
    /// 不过滤的话点哪儿都只能选中桌面，绑完"切过去"就是回桌面 ——
    /// 看起来完全没生效。
    /// </summary>
    private static bool IsAcceptable(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return false;
        if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return false;

        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0 || pid == (uint)Environment.ProcessId) return false;

        string? cls = WindowService.ClassNameOf(hwnd);
        if (cls != null && IsShellClass(cls)) return false;

        return true;
    }

    /// <summary>
    /// 供自检使用的探针：把点选判定逻辑暴露出来，
    /// 让自检能在不弹窗、不接管鼠标的情况下验证规则。
    /// </summary>
    public PickerProbe Probe => new();

    /// <summary>遮罩窗口句柄（自检用）。</summary>
    public IntPtr HandleForTest => new WindowInteropHelper(this).Handle;

    /// <summary>遮罩是否已设"点击穿透"（自检用）。</summary>
    public bool HasClickThroughForTest
    {
        get
        {
            var h = HandleForTest;
            if (h == IntPtr.Zero) return false;
            return (GetWindowLong(h, GWL_EXSTYLE) & WS_EX_TRANSPARENT) != 0;
        }
    }

    /// <summary>遮罩是否置顶（自检用）。</summary>
    public bool IsTopmostForTest
    {
        get
        {
            var h = HandleForTest;
            if (h == IntPtr.Zero) return false;
            return (GetWindowLong(h, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0;
        }
    }

    public readonly struct PickerProbe
    {
        /// <summary>
        /// 该句柄是否可以被点选。
        ///
        /// 直接调用生产路径上的 <see cref="IsAcceptable"/>，不再另写一份 ——
        /// 早先这里比生产路径多一条"排除自身 exe"的检查，而真正的点选流程
        /// 漏了壳窗口过滤，结果自检通过、实际点哪儿都是桌面。
        /// 判据只能有一份。
        /// </summary>
        public bool AcceptsForTest(IntPtr hwnd) => IsAcceptable(hwnd);

        /// <summary>该类名是否是应当排除的系统壳窗口。</summary>
        public bool IsShellClassForTest(string cls) => IsShellClass(cls);

        /// <summary>按坐标取窗口（与真实点选走同一条逻辑）。</summary>
        public IntPtr ResolveAtForTest(int x, int y) => ResolveTargetUnder(x, y);
    }

    /// <summary>
    /// 桌面、任务栏、托盘溢出、开始菜单这些系统壳窗口：点选时要排除，
    /// 否则会绑到桌面上（绑完"切过去"就是回桌面，看起来完全没生效）。
    /// </summary>
    private static bool IsShellClass(string cls) =>
        cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd"
            or "Windows.UI.Core.CoreWindow"
            or "NotifyIconOverflowWindow"                     // Win10 托盘溢出
            or "TopLevelWindowForOverflowXamlIsland"          // Win11 托盘溢出
            or "SystemTray_Main";                             // Win11 托盘主窗口
}
