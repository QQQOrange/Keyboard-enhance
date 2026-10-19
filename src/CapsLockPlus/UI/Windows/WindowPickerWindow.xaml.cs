using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using CapsLockPlus.Core;
using CapsLockPlus.Core.Windows;
using static CapsLockPlus.Core.Interop.NativeMethods;

namespace CapsLockPlus.UI.Windows;

/// <summary>
/// 十字准星窗口点选器。
///
/// 之前的实现在 MouseLeftButtonDown 里用 WindowFromPoint 取窗口 —— 这条路是错的：
/// 遮罩窗口自己就铺满全屏并且在最上层，WindowFromPoint 返回的永远是遮罩本身，
/// 于是"点了没反应"。
///
/// 现在的做法（与 QuickSearch 那类启动器一致）：
///   * 遮罩设为"点击穿透"（WS_EX_TRANSPARENT + WS_EX_LAYERED），
///     鼠标事件根本不会落到遮罩上，点击直达下面的真实窗口；
///   * 用全局鼠标钩子来监听移动与松开，从钩子里拿到真实坐标，
///     再临时把遮罩隐藏一会儿取出该坐标下的窗口，随即恢复；
///   * 高亮框画在另一个"不接收鼠标"的小窗口里，
///     避免遮罩穿透之后连高亮也点不中。
///
/// 这样既能点中目标，也能实时预览，且不会误选自己。
/// </summary>
public partial class WindowPickerWindow : Window
{
    /// <summary>用户选中的窗口句柄；取消时为 IntPtr.Zero。</summary>
    public IntPtr PickedWindow { get; private set; }

    private IntPtr _selfHandle;
    private IntPtr _mouseHook = IntPtr.Zero;
    private HookProc? _mouseProc;                 // 必须保存，防止被 GC 回收
    private IntPtr _lastTarget = IntPtr.Zero;
    private readonly DispatcherTimer _poll;

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
    private const int WM_LBUTTONDOWN = 0x0201;
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

        // 兜底轮询：万一钩子被安全软件拦掉，至少高亮还能跟随鼠标
        _poll = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(90) };
        _poll.Tick += (_, _) => UpdateHover(CurrentCursor());
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _selfHandle = new WindowInteropHelper(this).Handle;
        MakeClickThrough();

        // 置顶但不激活，避免抢走目标窗口的焦点
        SetWindowPos(_selfHandle, HWND_TOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

        _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, GetModuleHandle(null), 0);
        if (_mouseHook == IntPtr.Zero)
            CoreLog.Warn($"点选器鼠标钩子安装失败（错误码 {Marshal.GetLastWin32Error()}），改用轮询模式");
        _poll.Start();

        Activate();
        Focus();
    }

    /// <summary>让遮罩完全穿透鼠标，点击直接落到下面的真实窗口。</summary>
    private void MakeClickThrough()
    {
        int ex = GetWindowLong(_selfHandle, GWL_EXSTYLE);
        ex |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        SetWindowLong(_selfHandle, GWL_EXSTYLE, ex);
        // 变更扩展样式后需要重新应用一次，样式才会生效
        SetWindowPos(_selfHandle, HWND_TOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_FRAMECHANGED);
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
        if (nCode >= 0)
        {
            int msg = (int)wParam;
            var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);

            if (msg == WM_MOUSEMOVE)
            {
                Dispatcher.BeginInvoke(() => UpdateHover(data.pt));
            }
            else if (msg == WM_LBUTTONUP)
            {
                // 在钩子里直接定案：这时遮罩已经穿透，WindowFromPoint 能拿到真实窗口。
                // 先记住句柄，再回到界面线程收尾（钩子回调里不能做重活）。
                var target = ResolveTargetUnder(data.pt);
                Dispatcher.BeginInvoke(() =>
                {
                    if (target != IntPtr.Zero)
                    {
                        PickedWindow = target;
                        CoreLog.Info($"点选到窗口 0x{target.ToInt64():X}");
                        Close();
                    }
                });
            }
            else if (msg == WM_RBUTTONUP)
            {
                Dispatcher.BeginInvoke(() => { PickedWindow = IntPtr.Zero; Close(); });
            }
        }
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private void UpdateHover(POINT pt)
    {
        var target = ResolveTargetUnder(pt);
        _lastTarget = target;

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
    /// 取指定坐标下"真实存在"的顶层窗口。
    ///
    /// 关键点：遮罩自己是点击穿透的，但 WindowFromPoint 仍可能把遮罩算进去
    /// （取决于是否已经应用了 WS_EX_TRANSPARENT+LAYERED）。
    /// 所以这里做双重保险：命中自己时，把遮罩临时移出屏幕再取一次。
    /// </summary>
    private IntPtr ResolveTargetUnder(POINT pt)
    {
        var hwnd = WindowFromPoint(pt);
        var root = RootOf(hwnd);

        if (IsUs(root))
        {
            // 自己挡路了：把遮罩挪走一瞬间，取到下面的真实窗口后立刻挪回来
            var saved = Highlight.Visibility;
            Hide();
            try
            {
                hwnd = WindowFromPoint(pt);
                root = RootOf(hwnd);
            }
            finally
            {
                Show();
                MakeClickThrough();
                Highlight.Visibility = saved;
            }
            if (IsUs(root)) return IntPtr.Zero;
        }

        if (root == IntPtr.Zero) return IntPtr.Zero;
        if (!IsWindowVisible(root)) return IntPtr.Zero;

        // 排除桌面、任务栏等系统壳窗口
        string? cls = WindowService.ClassNameOf(root);
        if (cls != null && IsShellClass(cls)) return IntPtr.Zero;

        var info = WindowService.Describe(root);
        if (info == null) return IntPtr.Zero;
        if (info.ExeName.Equals(AppInfo.ExeName, StringComparison.OrdinalIgnoreCase)) return IntPtr.Zero;

        return root;
    }

    private IntPtr RootOf(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return IntPtr.Zero;
        var root = GetAncestor(hwnd, GA_ROOT);
        return root == IntPtr.Zero ? hwnd : root;
    }

    private bool IsUs(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        if (hwnd == _selfHandle) return true;
        GetWindowThreadProcessId(hwnd, out uint pid);
        return pid == (uint)Environment.ProcessId;
    }

    /// <summary>
    /// 供自检使用的探针：把点选判定逻辑暴露出来，
    /// 让自检能在不弹窗、不接管鼠标的情况下验证规则是否正确。
    /// </summary>
    public PickerProbe Probe => new(this);

    public readonly struct PickerProbe
    {
        private readonly WindowPickerWindow _owner;
        internal PickerProbe(WindowPickerWindow owner) => _owner = owner;

        /// <summary>该句柄是否可以被点选（排除自身、壳窗口、无效窗口）。</summary>
        public bool AcceptsForTest(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return false;
            if (_owner.IsUs(hwnd)) return false;
            if (!IsWindowVisible(hwnd)) return false;

            string? cls = WindowService.ClassNameOf(hwnd);
            if (cls != null && IsShellClass(cls)) return false;

            var info = WindowService.Describe(hwnd);
            if (info == null) return false;
            if (info.ExeName.Equals(AppInfo.ExeName, StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        /// <summary>该类名是否是应当排除的系统壳窗口。</summary>
        public bool IsShellClassForTest(string cls) => IsShellClass(cls);
    }

    /// <summary>桌面、任务栏等系统壳窗口：点选时要排除，否则会绑到桌面上。</summary>
    private static bool IsShellClass(string cls) =>
        cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd"
            or "Windows.UI.Core.CoreWindow";
}
