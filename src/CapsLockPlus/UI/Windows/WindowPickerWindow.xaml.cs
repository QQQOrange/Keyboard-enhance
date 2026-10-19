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
/// 取窗口的逻辑参考了 QuickSearch（D:\Local AI\git\Quick search 源代码）里的
/// <c>foreign_window_at</c>。它踩过的坑，这里也踩过：
///
///   1) 遮罩铺满全屏且 always-on-top，直接用 <c>WindowFromPoint</c> 取到的**永远是遮罩自己**；
///   2) 沿着 <c>GW_HWNDNEXT</c> 从遮罩往下跳也不行 —— 置顶窗口自成一段 Z 序，
///      跳两三跳就断了，根本够不到下面的真实程序，于是"点哪都认不出来"。
///
/// QuickSearch 的可靠做法（本类照此实现）：
///   从桌面 Z 序最顶端（<c>GetTopWindow(desktop)</c>）开始，沿 <c>GW_HWNDNEXT</c>
///   一层层往下扫；对每个可见、非最小化的顶层窗口，用**窗口矩形**判断是否包含该点；
///   跳过点击穿透的蒙层（WS_EX_TRANSPARENT），命中第一个非本进程的窗口就返回。
///   全过程纯同步，不依赖焦点、也不依赖"自己的窗口关没关"。
///
/// 另外遮罩本身设了 WS_EX_TRANSPARENT，让点击穿透到真实窗口；
/// 鼠标事件则由全局鼠标钩子捕获，不靠遮罩自己收事件。
/// </summary>
public partial class WindowPickerWindow : Window
{
    /// <summary>用户选中的窗口句柄；取消时为 IntPtr.Zero。</summary>
    public IntPtr PickedWindow { get; private set; }

    private IntPtr _selfHandle;
    private IntPtr _mouseHook = IntPtr.Zero;
    private HookProc? _mouseProc;                 // 必须保存引用，防止被 GC
    private readonly DispatcherTimer _poll;
    private bool _closing;

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private const int WH_MOUSE_LL = 14;
    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONUP = 0x0205;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);

    public WindowPickerWindow()
    {
        InitializeComponent();
        _mouseProc = MouseCallback;
        Loaded += OnLoaded;
        Closed += OnClosed;
        PreviewKeyDown += OnKeyDown;

        // 兜底轮询：万一全局鼠标钩子被安全软件拦掉，高亮仍能跟随鼠标
        _poll = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(80) };
        _poll.Tick += (_, _) => UpdateHover(CurrentCursor());
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

        _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, GetModuleHandle(null), 0);
        if (_mouseHook == IntPtr.Zero)
            CoreLog.Warn($"点选器鼠标钩子安装失败（错误码 {Marshal.GetLastWin32Error()}），改用轮询");
        _poll.Start();

        Activate();
        Focus();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _poll.Stop();
        if (_mouseHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            PickedWindow = IntPtr.Zero;
            Close();
        }
    }

    private static POINT CurrentCursor()
    {
        GetCursorPos(out var pt);
        return pt;
    }

    private IntPtr MouseCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && !_closing)
        {
            int msg = (int)wParam;
            var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);

            if (msg == WM_MOUSEMOVE)
            {
                Dispatcher.BeginInvoke(() => UpdateHover(data.pt));
            }
            else if (msg == WM_LBUTTONUP)
            {
                // 在钩子里就定案，避免遮罩先关掉导致坐标失效
                var target = ResolveTargetUnder(data.pt.X, data.pt.Y);
                Dispatcher.BeginInvoke(() =>
                {
                    if (target != IntPtr.Zero)
                    {
                        PickedWindow = target;
                        CoreLog.Info($"点选到窗口 0x{target.ToInt64():X}");
                        _closing = true;
                        Close();
                    }
                    else
                    {
                        // 取不到时不要静默失败——告诉用户换个位置再点
                        HoverTitle.Text = "这里没认出窗口，换个位置再点一次";
                        HoverProcess.Text = "也可以按 Esc / 右键取消";
                        HoverInfo.Visibility = Visibility.Visible;
                        HoverInfo.Margin = new Thickness(
                            Math.Max(0, ActualWidth / 2 - 200), 60, 0, 0);
                    }
                });
            }
            else if (msg == WM_RBUTTONUP)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    PickedWindow = IntPtr.Zero;
                    _closing = true;
                    Close();
                });
            }
        }
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
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

        double left = Math.Min(pt.X + 18, Math.Max(0, ActualWidth - 470));
        double top = Math.Min(pt.Y + 18, Math.Max(0, ActualHeight - 90));
        HoverInfo.Margin = new Thickness(left, top, 0, 0);
    }

    /// <summary>
    /// 取指定坐标下"不属于本进程"的最上面那个可见顶层窗口。
    ///
    /// 这是整个点选功能的核心，做法照 QuickSearch 的 foreign_window_at：
    /// 从桌面 Z 序顶端开始沿 GW_HWNDNEXT 往下扫，用窗口矩形判断是否命中。
    ///
    /// 为什么不用 WindowFromPoint：遮罩铺满全屏且置顶，它永远返回遮罩自己；
    /// 为什么不用 GW_HWNDNEXT 从遮罩往下跳：置顶窗口自成一段 Z 序，跳几跳就断。
    /// </summary>
    private static IntPtr ResolveTargetUnder(int x, int y)
    {
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
                        GetWindowThreadProcessId(h, out uint pid);
                        if (pid != 0 && pid != me)
                            return h;
                    }
                }
            }
            // 注意：必须写全限定名。本类继承自 WPF 的 Window，
            // 类内部直接写 GetWindow 会被解析成 Window.GetWindow(DependencyObject)。
            h = NativeMethods.GetWindow(h, GW_HWNDNEXT);
        }

        // 极端情况：一次都没扫到（比如某个驱动把 Z 序打乱）。
        // 退回 WindowFromPoint，并显式跳过自己，聊胜于无。
        var fallback = WindowFromPoint(new POINT { X = x, Y = y });
        if (fallback != IntPtr.Zero)
        {
            var root = GetAncestor(fallback, GA_ROOT);
            if (root == IntPtr.Zero) root = fallback;
            GetWindowThreadProcessId(root, out uint pid);
            if (pid != 0 && pid != me) return root;
        }
        return IntPtr.Zero;
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
        /// <summary>该句柄是否可以被点选（排除自身、壳窗口、无效窗口）。</summary>
        public bool AcceptsForTest(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return false;
            if (!IsWindowVisible(hwnd)) return false;

            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == (uint)Environment.ProcessId) return false;

            string? cls = WindowService.ClassNameOf(hwnd);
            if (cls != null && IsShellClass(cls)) return false;

            var info = WindowService.Describe(hwnd);
            if (info == null) return false;
            if (info.ExeName.Equals(AppInfo.ExeName, StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        /// <summary>该类名是否是应当排除的系统壳窗口。</summary>
        public bool IsShellClassForTest(string cls) => IsShellClass(cls);

        /// <summary>按坐标取窗口（与真实点选走同一条逻辑）。</summary>
        public IntPtr ResolveAtForTest(int x, int y) => ResolveTargetUnder(x, y);
    }

    /// <summary>桌面、任务栏等系统壳窗口：点选时要排除，否则会绑到桌面上。</summary>
    private static bool IsShellClass(string cls) =>
        cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd"
            or "Windows.UI.Core.CoreWindow";
}
