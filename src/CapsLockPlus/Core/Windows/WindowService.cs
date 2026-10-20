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

    /// <summary>是否启用窗口激活动画（由设置控制）。</summary>
    public static bool AnimationEnabled { get; set; } = true;

    /// <summary>
    /// 激活窗口并播放 macOS「神奇效果（Genie）」式动画：窗口从任务栏/Dock 的位置
    /// 由小放大展开。按一次唤出用展开动画，再按一次收起用缩小动画。
    ///
    /// 与旧版单纯「原地缩放」的区别：旧版以窗口中心为锚点，看起来只是变大变小；
    /// Genie 的锚点在任务栏，所以窗口像是从任务栏"被拉出来"再塞回去，
    /// 更接近 macOS 的行为，方向感明确。
    ///
    /// 实现要点：
    ///   * 锚点取任务栏所在边的中点（默认屏幕底部）；
    ///   * 位置与尺寸同步插值，锚点处保持不动；
    ///   * 最小化/最大化窗口不做动画，避免与系统还原动画打架；
    ///   * 收尾把窗口精确对齐回原矩形，避免累积误差。
    /// </summary>
    public static void ActivateWithGenie(IntPtr hwnd, int durationMs = 260)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return;

        bool wasMinimized = IsIconic(hwnd);
        bool wasMaximized = IsZoomed(hwnd);
        if (wasMinimized) ShowWindowAsync(hwnd, SW_RESTORE);

        var target = GetRect(hwnd);
        bool canAnimate = AnimationEnabled
                          && !wasMaximized
                          && target.Width > 120 && target.Height > 80
                          && !wasMinimized;
        if (!canAnimate)
        {
            Activate(hwnd);
            return;
        }

        var anchor = DockAnchor();
        PlayGenie(hwnd, target, anchor, expand: true, durationMs);
        Activate(hwnd);
    }

    /// <summary>收起窗口：与 <see cref="ActivateWithGenie"/> 相反方向的动画。</summary>
    public static void HideWithGenie(IntPtr hwnd, int durationMs = 260)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return;
        if (IsIconic(hwnd) || IsZoomed(hwnd))
        {
            Minimize(hwnd);
            return;
        }

        var target = GetRect(hwnd);
        if (!AnimationEnabled || target.Width <= 120 || target.Height <= 80)
        {
            Minimize(hwnd);
            return;
        }

        var anchor = DockAnchor();
        PlayGenie(hwnd, target, anchor, expand: false, durationMs, onDone: () =>
        {
            // 动画结束后再最小化，否则窗口会提前消失
            Minimize(hwnd);
            // 还原矩形，避免下次唤出时从错误的位置展开
            SetWindowPos(hwnd, IntPtr.Zero, target.Left, target.Top, target.Width, target.Height,
                SWP_NOZORDER | SWP_NOACTIVATE);
        });
    }

    /// <summary>任务栏锚点：取任务栏所在屏幕边的中点。</summary>
    private static Point DockAnchor()
    {
        try
        {
            // 工作区通常不含任务栏，所以用"整屏底边"作为锚点：
            // 先取主屏全屏矩形，失败则退到工作区中点。
            int sw = GetSystemMetrics(0);      // SM_CXSCREEN
            int sh = GetSystemMetrics(1);      // SM_CYSCREEN
            if (sw > 0 && sh > 0)
                return new Point(sw / 2, sh - 6);
        }
        catch { }
        var wa = WorkArea(IntPtr.Zero);
        return new Point(wa.Left + wa.Width / 2, wa.Bottom);
    }

    /// <summary>播放 Genie 动画（展开或收起）。</summary>
    private static void PlayGenie(IntPtr hwnd, RECT target, Point anchor,
        bool expand, int durationMs, Action? onDone = null)
    {
        int frames = Math.Max(6, durationMs / 16);
        var timer = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16),
        };
        int frame = 0;
        // 展开：0 -> 1；收起：1 -> 0
        double Start = expand ? 0.06 : 1.0;
        double End = expand ? 1.0 : 0.06;

        timer.Tick += (_, _) =>
        {
            frame++;
            double t = Math.Clamp((double)frame / frames, 0, 1);
            // 展开用缓出（出场快、收尾稳），收起用缓入（先慢后快，像被吸进去）
            double eased = expand
                ? 1 - Math.Pow(1 - t, 3)
                : Math.Pow(t, 3);
            double s = Start + (End - Start) * eased;

            // 以锚点为基准做等比缩放：锚点处坐标不变
            int w = Math.Max(8, (int)(target.Width * s));
            int h = Math.Max(8, (int)(target.Height * s));
            int left = (int)(anchor.X - (anchor.X - target.Left) * s);
            int top = (int)(anchor.Y - (anchor.Y - target.Top) * s);

            SetWindowPos(hwnd, IntPtr.Zero, left, top, w, h, SWP_NOZORDER | SWP_NOACTIVATE);

            if (frame >= frames)
            {
                timer.Stop();
                if (expand)
                {
                    SetWindowPos(hwnd, IntPtr.Zero, target.Left, target.Top,
                        target.Width, target.Height, SWP_NOZORDER | SWP_NOACTIVATE);
                }
                onDone?.Invoke();
            }
        };
        timer.Start();
    }

    /// <summary>激活窗口并播放一段“从小到大”的动画。
    ///
    /// 做法：记住窗口原本的位置和大小，先把它收缩成一个较小的矩形，
    /// 再分帧放大回原尺寸。这样切换窗口时有明确的视觉反馈，
    /// 一眼就能看出刚才把哪个窗口调了出来。
    ///
    /// 细节：
    ///   * 最小化的窗口先还原，避免和系统的还原动画打架；
    ///   * 最大化窗口不做缩放动画（否则会先退出最大化，闪烁明显）；
    ///   * 结束时精确对齐到原矩形，避免累积误差。
    /// </summary>
    public static void ActivateWithZoom(IntPtr hwnd, int durationMs = 180, double startScale = 0.84)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return;

        bool wasMinimized = IsIconic(hwnd);
        bool wasMaximized = IsZoomed(hwnd);
        if (wasMinimized) ShowWindowAsync(hwnd, SW_RESTORE);

        RECT target = GetRect(hwnd);
        bool canAnimate = AnimationEnabled
                          && !wasMaximized
                          && target.Width > 240 && target.Height > 180
                          && !wasMinimized;   // 刚还原的窗口交给系统动画，不叠加
        if (!canAnimate)
        {
            Activate(hwnd);
            return;
        }

        int startW = Math.Max(120, (int)(target.Width * startScale));
        int startH = Math.Max(90, (int)(target.Height * startScale));
        int cx = target.Left + target.Width / 2;
        int cy = target.Top + target.Height / 2;

        SetWindowPos(hwnd, IntPtr.Zero, cx - startW / 2, cy - startH / 2, startW, startH,
            SWP_NOZORDER | SWP_NOACTIVATE);
        Activate(hwnd);

        int frames = Math.Max(5, durationMs / 16);
        var timer = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16),
        };
        int frame = 0;
        timer.Tick += (_, _) =>
        {
            frame++;
            double t = Math.Clamp((double)frame / frames, 0, 1);
            double eased = 1 - Math.Pow(1 - t, 3);      // 缓出：开始快、收尾稳
            double scale = startScale + (1 - startScale) * eased;

            int w = (int)(target.Width * scale);
            int h = (int)(target.Height * scale);
            SetWindowPos(hwnd, IntPtr.Zero, cx - w / 2, cy - h / 2, w, h, SWP_NOZORDER | SWP_NOACTIVATE);

            if (frame >= frames)
            {
                timer.Stop();
                SetWindowPos(hwnd, IntPtr.Zero, target.Left, target.Top, target.Width, target.Height,
                    SWP_NOZORDER | SWP_NOACTIVATE);
            }
        };
        timer.Start();
    }

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
