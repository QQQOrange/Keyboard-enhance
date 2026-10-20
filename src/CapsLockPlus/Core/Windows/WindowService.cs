using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using CapsLockPlus.Core.Interop;
using static CapsLockPlus.Core.Interop.NativeMethods;

namespace CapsLockPlus.Core.Windows;

public sealed record WindowInfo(IntPtr Handle, string Title, string ClassName, string ExePath, string ExeName)
{
    public string Display => string.IsNullOrWhiteSpace(Title) ? $"{ExeName} (无标题)" : Title;
}

public enum SnapSide
{
    Left, Right, Top, Bottom,
    TopLeft, TopRight, BottomLeft, BottomRight,
    Center, Maximize, Minimize,
}

/// <summary>窗口操作封装（激活、贴边、置顶、透明、关闭……）。</summary>
public static class WindowService
{
    private static readonly HashSet<string> IgnoredClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow",
        "ApplicationFrameWindow_", "DV2ControlHost", "MsgrIMEWindowClass", "SysShadow", "Button",
        "Windows.Internal.Shell.TabProxyWindow", "XamlExplorerHostIslandWindow",
    };

    public static IntPtr Foreground => GetForegroundWindow();

    /// <summary>
    /// 取窗口的根（顶层）窗口句柄。
    ///
    /// 前台窗口常是某个程序的子窗口或模态对话框（HWND 与主窗口不同），
    /// 判断“这个程序是否已在前台”时必须归一到根窗口再比，
    /// 否则会误判成“没激活过”。
    /// </summary>
    public static IntPtr RootOf(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return IntPtr.Zero;
        var root = GetAncestor(hwnd, GA_ROOT);
        return root == IntPtr.Zero ? hwnd : root;
    }

    public static bool Exists(IntPtr hwnd) => hwnd != IntPtr.Zero && IsWindow(hwnd);

    public static WindowInfo? Describe(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return null;
        hwnd = GetAncestor(hwnd, GA_ROOT);
        var sb = new StringBuilder(512);
        GetWindowText(hwnd, sb, sb.Capacity);
        var title = sb.ToString();
        var cls = new StringBuilder(256);
        GetClassName(hwnd, cls, cls.Capacity);
        GetWindowThreadProcessId(hwnd, out var pid);
        var exe = GetProcessPath(pid);
        var exeName = string.IsNullOrEmpty(exe) ? "" : Path.GetFileName(exe);
        return new WindowInfo(hwnd, title, cls.ToString(), exe, exeName);
    }

    public static WindowInfo? ForegroundInfo() => Describe(Foreground);

    /// <summary>取窗口所属进程 ID（供自检使用）。</summary>
    public static void GetWindowThreadProcessIdForTest(IntPtr hwnd, out uint pid) =>
        GetWindowThreadProcessId(hwnd, out pid);

    /// <summary>取窗口类名（用于识别桌面、任务栏等系统壳窗口）。</summary>
    public static string? ClassNameOf(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return null;
        var cls = new StringBuilder(256);
        GetClassName(hwnd, cls, cls.Capacity);
        return cls.Length > 0 ? cls.ToString() : null;
    }

    public static string GetProcessPath(uint pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return "";
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : "";
        }
        finally { CloseHandle(h); }
    }

    public static IEnumerable<WindowInfo> EnumerateTopLevel(bool visibleOnly = true)
    {
        var list = new List<WindowInfo>();
        EnumWindows((h, _) =>
        {
            if (visibleOnly && !IsWindowVisible(h)) return true;
            if (GetWindowTextLength(h) == 0) return true;
            var info = Describe(h);
            if (info == null) return true;
            if (IgnoredClasses.Contains(info.ClassName)) return true;
            if (string.Equals(info.ExeName, "CapsLockPlus.exe", StringComparison.OrdinalIgnoreCase)) return true;
            var ex = GetWindowLong(h, GWL_EXSTYLE);
            if ((ex & WS_EX_TOOLWINDOW) != 0) return true;
            list.Add(info);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>用 Alt+Tab 的排序规则列出可切换窗口。</summary>
    public static List<WindowInfo> ZOrderWindows()
    {
        var list = new List<WindowInfo>();
        foreach (var w in EnumerateTopLevel())
        {
            if (!IsWindowVisible(w.Handle)) continue;
            list.Add(w);
        }
        return list;
    }

    /// <summary>
    /// 把窗口切到前台。
    ///
    /// 直接 SetForegroundWindow 在 Win11 上经常失败（系统只允许“当前前台进程”
    /// 或“收到过用户输入”的进程抢前台）。这里的做法是：
    ///   1. 先直接试一次；
    ///   2. 失败就把“前台线程”的输入队列临时挂到当前线程上再试，
    ///      这会继承前台线程的“可设置前台”权限，成功率接近 100%；
    ///   3. 无论成功与否都必须 detach，否则两个线程的输入状态会一直纠缠。
    /// </summary>
    public static bool Activate(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return false;
        if (IsIconic(hwnd)) ShowWindowAsync(hwnd, SW_RESTORE);
        if (SetForegroundWindow(hwnd)) return true;

        uint fgThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        uint curThread = GetCurrentThreadId();
        bool attached = false;
        try
        {
            if (fgThread != 0 && curThread != 0 && fgThread != curThread)
                attached = AttachThreadInput(curThread, fgThread, true);

            if (SetForegroundWindow(hwnd)) return true;

            BringWindowToTop(hwnd);
            ShowWindowAsync(hwnd, SW_RESTORE);
            return SetForegroundWindow(hwnd);
        }
        catch
        {
            return false;
        }
        finally
        {
            if (attached) AttachThreadInput(curThread, fgThread, false);
        }
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    public static void Minimize(IntPtr hwnd) => ShowWindowAsync(hwnd, SW_MINIMIZE);
    public static void Restore(IntPtr hwnd) => ShowWindowAsync(hwnd, SW_RESTORE);

    // 窗口激活不再播放任何动画（用户明确要求删掉）。
    // 只保留「弹到前台」与「最小化」两个动作，见 WindowBindingService。

    public static void ToggleMaximize(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        ShowWindowAsync(hwnd, IsZoomed(hwnd) ? SW_RESTORE : 3 /*SW_MAXIMIZE*/);
    }

    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr hWnd);

    public static void Close(IntPtr hwnd)
    {
        if (hwnd != IntPtr.Zero) PostMessage(hwnd, 0x0010 /*WM_CLOSE*/, IntPtr.Zero, IntPtr.Zero);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    public static void SetTopmost(IntPtr hwnd, bool topmost)
    {
        SetWindowPos(hwnd, topmost ? HWND_TOPMOST : HWND_NOTOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    public static bool IsTopmost(IntPtr hwnd) => (GetWindowLong(hwnd, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0;

    public static void ToggleTopmost(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        SetTopmost(hwnd, !IsTopmost(hwnd));
    }

    public static void SendToBottom(IntPtr hwnd)
    {
        SetWindowPos(hwnd, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    public static void SetOpacity(IntPtr hwnd, byte alpha)
    {
        int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_LAYERED);
        SetLayeredWindowAttributes(hwnd, 0, alpha, 0x2 /*LWA_ALPHA*/);
    }

    /// <summary>循环切换窗口透明度档位。</summary>
    public static byte NextOpacity(IntPtr hwnd)
    {
        PruneOpacityCache();
        var current = OpacityOf.GetValueOrDefault(hwnd, (byte)255);
        byte next = current switch
        {
            > 235 => 215,
            > 195 => 175,
            > 155 => 135,
            > 115 => 90,
            _ => 255,
        };
        OpacityOf[hwnd] = next;
        if (next == 255)
        {
            int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, ex & ~WS_EX_LAYERED);
        }
        else
        {
            SetOpacity(hwnd, next);
        }
        return next;
    }

    /// <summary>
    /// 清理已关闭窗口的透明度记录。
    /// 窗口句柄会被系统复用，字典如果只增不减，长期运行会一直涨；
    /// 这里把已经不存在的句柄清掉，同时也避免复用句柄带来的档位错乱。
    /// </summary>
    private static void PruneOpacityCache()
    {
        if (OpacityOf.Count < 32) return;
        foreach (var h in OpacityOf.Keys.Where(k => !IsWindow(k)).ToList())
            OpacityOf.Remove(h);
    }

    private static readonly Dictionary<IntPtr, byte> OpacityOf = new();

    public static RECT GetRect(IntPtr hwnd)
    {
        GetWindowRect(hwnd, out var r);
        return r;
    }

    public static RECT WorkArea(IntPtr hwnd)
    {
        var mon = MonitorFromWindow(hwnd == IntPtr.Zero ? Foreground : hwnd, MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (GetMonitorInfo(mon, ref mi)) return mi.rcWork;
        return new RECT { Left = 0, Top = 0, Right = GetSystemMetrics(0), Bottom = GetSystemMetrics(1) };
    }

    /// <summary>把窗口贴到屏幕的指定位置（对齐 Win11 的贴边行为）。</summary>
    public static void Snap(IntPtr hwnd, SnapSide side)
    {
        if (hwnd == IntPtr.Zero) return;
        if (IsIconic(hwnd)) ShowWindowAsync(hwnd, SW_RESTORE);
        var wa = WorkArea(hwnd);
        int w = wa.Width, h = wa.Height;
        int halfW = w / 2, halfH = h / 2;
        (int x, int y, int cx, int cy) = side switch
        {
            SnapSide.Left => (wa.Left, wa.Top, halfW, h),
            SnapSide.Right => (wa.Left + halfW, wa.Top, w - halfW, h),
            SnapSide.Top => (wa.Left, wa.Top, w, halfH),
            SnapSide.Bottom => (wa.Left, wa.Top + halfH, w, h - halfH),
            SnapSide.TopLeft => (wa.Left, wa.Top, halfW, halfH),
            SnapSide.TopRight => (wa.Left + halfW, wa.Top, w - halfW, halfH),
            SnapSide.BottomLeft => (wa.Left, wa.Top + halfH, halfW, h - halfH),
            SnapSide.BottomRight => (wa.Left + halfW, wa.Top + halfH, w - halfW, h - halfH),
            SnapSide.Center => (wa.Left + (w - Math.Min(w, 1200)) / 2, wa.Top + (h - Math.Min(h, 820)) / 2, Math.Min(w, 1200), Math.Min(h, 820)),
            _ => (wa.Left, wa.Top, w, h),
        };
        if (IsZoomed(hwnd)) ShowWindowAsync(hwnd, SW_RESTORE);
        SetWindowPos(hwnd, IntPtr.Zero, x, y, cx, cy, SWP_NOZORDER | SWP_NOACTIVATE);
    }

    /// <summary>把窗口居中（保持原大小）。</summary>
    public static void Center(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        var wa = WorkArea(hwnd);
        var r = GetRect(hwnd);
        int cx = Math.Min(r.Width, wa.Width), cy = Math.Min(r.Height, wa.Height);
        int x = wa.Left + (wa.Width - cx) / 2;
        int y = wa.Top + (wa.Height - cy) / 2;
        if (IsZoomed(hwnd)) ShowWindowAsync(hwnd, SW_RESTORE);
        SetWindowPos(hwnd, IntPtr.Zero, x, y, cx, cy, SWP_NOZORDER | SWP_NOACTIVATE);
    }

    /// <summary>激活屏幕某个方向的下一个窗口（Caps+LAlt+F/S/D/E 那种）。</summary>
    public static bool ActivateDirectional(string dir)
    {
        var fg = ForegroundInfo();
        if (fg == null) return false;
        var fgRect = GetRect(fg.Handle);
        int fgCx = (fgRect.Left + fgRect.Right) / 2;
        int fgCy = (fgRect.Top + fgRect.Bottom) / 2;
        var candidates = new List<(WindowInfo W, double Score)>();
        foreach (var w in ZOrderWindows())
        {
            if (w.Handle == fg.Handle) continue;
            var r = GetRect(w.Handle);
            int cx = (r.Left + r.Right) / 2, cy = (r.Top + r.Bottom) / 2;
            int dx = cx - fgCx, dy = cy - fgCy;
            bool match = dir switch
            {
                "l" => dx < 0 && Math.Abs(dx) > Math.Abs(dy) * 0.6,
                "r" => dx > 0 && Math.Abs(dx) > Math.Abs(dy) * 0.6,
                "u" => dy < 0 && Math.Abs(dy) > Math.Abs(dx) * 0.6,
                "d" => dy > 0 && Math.Abs(dy) > Math.Abs(dx) * 0.6,
                _ => true,
            };
            if (!match) continue;
            double dist = Math.Sqrt((double)dx * dx + (double)dy * dy);
            candidates.Add((w, dist));
        }
        if (candidates.Count == 0) return false;
        var target = candidates.OrderBy(c => c.Score).First().W;
        return Activate(target.Handle);
    }

    /// <summary>在当前窗口的同一程序窗口之间循环切换。</summary>
    public static bool CycleSameApp(bool backward)
    {
        var fg = ForegroundInfo();
        if (fg == null) return false;
        var same = ZOrderWindows()
            .Where(w => string.Equals(w.ExeName, fg.ExeName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (same.Count <= 1) return false;
        int idx = same.FindIndex(w => w.Handle == fg.Handle);
        if (idx < 0) idx = 0;
        int next = backward ? (idx - 1 + same.Count) % same.Count : (idx + 1) % same.Count;
        return Activate(same[next].Handle);
    }
}
