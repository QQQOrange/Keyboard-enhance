using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using CapsLockPlus.Core.Interop;
using static CapsLockPlus.Core.Interop.NativeMethods;

namespace CapsLockPlus.Core.Windows;

public sealed record WindowInfo(IntPtr Handle, string Title, string ClassName, string ExePath, string ExeName)
{
    public string Display => string.IsNullOrWhiteSpace(Title) ? Loc.F("{0} (无标题)", ExeName) : Title;
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
            if (string.Equals(info.ExeName, Core.AppInfo.ExeName, StringComparison.OrdinalIgnoreCase)) return true;
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

    /// <summary>
    /// 强行把窗口抢到前台并拿到键盘焦点 —— 用于"面板弹出后必须能立刻打字"。
    ///
    /// 为什么普通 <c>SetForegroundWindow</c> 不够：
    /// 热键触发时前台还是用户正在用的那个程序，我们并不是前台进程，
    /// Windows 的**前台锁定**（默认 2000ms 内只允许前台进程自己换前台）
    /// 会直接拒绝这次调用。而 <c>Window.Show()</c> / <c>Activate()</c>
    /// 内部就是调它 —— 失败时窗口照样显示（Topmost 还在最上面），
    /// 但键盘焦点仍留在原来的程序里，于是"面板出来了，打字却打在别处"。
    ///
    /// 三步，逐级加强：
    ///   1. <c>SetForegroundWindow</c>（碰运气，前台正好是我们时有效）；
    ///   2. <c>AttachThreadInput</c> 挂到当前前台线程上再抢 —— 这一步能过掉
    ///      大部分限制，因为输入队列共享后被当成同一个线程；
    ///   3. 仍失败则**临时**把前台锁定超时置 0 再抢，然后立刻还原
    ///      （AutoHotkey 的 #WinActivateForce 用的就是这招）。
    ///
    /// 第 3 步会动一个全局系统设置，所以只在最后才用，并且用 finally 还原。
    /// </summary>
    public static bool ForceForeground(IntPtr hwnd, bool allowUnlockTimeout = false)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return false;
        if (GetForegroundWindow() == hwnd) return true;

        if (IsIconic(hwnd)) ShowWindowAsync(hwnd, SW_RESTORE);

        // ① 直接抢
        if (SetForegroundWindow(hwnd) && GetForegroundWindow() == hwnd) return true;

        // ② 挂到前台线程上再抢
        uint fgThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        uint curThread = GetCurrentThreadId();
        bool attached = false;
        try
        {
            if (fgThread != 0 && curThread != 0 && fgThread != curThread)
                attached = AttachThreadInput(curThread, fgThread, true);

            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
            if (GetForegroundWindow() == hwnd)
            {
                SetFocus(hwnd);
                return true;
            }
        }
        catch { }   /* 预期内：探测/重试型，失败即视为无结果 */
        finally
        {
            if (attached) AttachThreadInput(curThread, fgThread, false);
        }

        /* ③ 临时解除前台锁定。
         *
         * 这一步会广播 WM_SETTINGCHANGE 给所有窗口，开销明显，
         * 所以做成**显式开关**：调用方确认前两步反复失败后再打开，
         * 不能每次重试都做一遍。 */
        return allowUnlockTimeout && ForceForegroundUnlocking(hwnd);
    }

    /// <summary>这个窗口此刻是不是系统的前台窗口。</summary>
    public static bool ForegroundIs(IntPtr hwnd) =>
        hwnd != IntPtr.Zero && GetForegroundWindow() == hwnd;

    /// <summary>临时把前台锁定超时置 0 抢焦点，随后还原（最后手段）。</summary>
    private static bool ForceForegroundUnlocking(IntPtr hwnd)
    {
        uint old = 0;
        bool changed = false;
        try
        {
            if (SystemParametersInfo(SPI_GETFOREGROUNDLOCKTIMEOUT, 0, out old, 0))
            {
                // SET 的值放在 uiParam，pvParam 必须为 NULL
                SystemParametersInfo(SPI_SETFOREGROUNDLOCKTIMEOUT, 0, IntPtr.Zero, SPIF_SENDCHANGE);
                changed = true;
            }

            SetForegroundWindow(hwnd);
            bool ok = GetForegroundWindow() == hwnd;
            if (ok) SetFocus(hwnd);
            return ok;
        }
        catch
        {
            return false;
        }
        finally
        {
            // 无论成败都要还原：这是全局设置，改完不还会影响所有程序
            if (changed)
            {
                try
                {
                    SystemParametersInfo(SPI_SETFOREGROUNDLOCKTIMEOUT, old, IntPtr.Zero, SPIF_SENDCHANGE);
                }
                catch { }   /* 预期内：探测/重试型，失败即视为无结果 */
            }
        }
    }

    public static void Minimize(IntPtr hwnd) => ShowWindowAsync(hwnd, SW_MINIMIZE);
    public static void Restore(IntPtr hwnd) => ShowWindowAsync(hwnd, SW_RESTORE);

    /// <summary>
    /// 窗口是否处于最小化状态。
    ///
    /// 用于"按键绑定窗口"：用户按一次最小化，再按一次要能还原回来。
    /// 如果直接对最小化窗口调 SetForegroundWindow，Windows 不会帮你还原 ——
    /// 表现就是"按键没反应"。所以必须先判断这个状态再决定是 Restore 还是 Activate。
    /// </summary>
    public static bool IsMinimized(IntPtr hwnd) => IsIconic(hwnd);

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

    // ================= 打开文件夹并把窗口带到前台 =================

    /// <summary>资源管理器主窗口的类名。</summary>
    private const string ExplorerWindowClass = "CabinetWClass";

    /// <summary>AllowSetForegroundWindow 的 "any process" 特殊值。</summary>
    private const uint ASFW_ANY = 0xFFFFFFFF;

    /// <summary>
    /// 在资源管理器里打开文件夹，并保证**它出现在最前面**。
    ///
    /// 为什么需要专门做这件事：
    ///   按下直达链接的键时，前台是别的程序；我们用 ShellExecute 拉起 explorer 后，
    ///   Windows 的前台锁定（ForegroundLockTimeout）会把新窗口压在任务栏闪烁 ——
    ///   用户看到的就是"文件夹在后台打开了"，还得自己去点任务栏。
    ///
    /// 做法分两步：
    ///   1. 启动前 <c>AllowSetForegroundWindow(ASFW_ANY)</c>，
    ///      把"可以抢前台"的许可交给接下来启动的进程（这一步最有效，
    ///      很多情况下 explorer 自己就正确置前了）；
    ///   2. 启动后异步等窗口出现，再用 <see cref="ForceForeground"/> 兜底
    ///      （它会挂前台线程输入队列，必要时临时解除前台锁定）。
    ///
    /// 异步的原因：explorer 冷启动可能要几百毫秒，**不能**在动作队列线程上等 ——
    /// 那会把后面的按键动作全堵住（按住连发时体感就是"卡住"）。
    /// </summary>
    public static void OpenFolderInForeground(string path)
    {
        // 记下"打开之前"已有哪些资源管理器窗口，用来识别新出现的那个
        var before = SnapshotExplorerWindows();

        AllowSetForegroundWindow(ASFW_ANY);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

        string leaf = LeafNameOf(path);
        Task.Run(() => WaitAndActivateFolder(before, leaf));
    }

    /// <summary>文件夹名（用于在"复用已有窗口"时按标题认人）。</summary>
    internal static string LeafNameOf(string path)
    {
        try
        {
            string p = path.TrimEnd('\\', '/');
            string leaf = Path.GetFileName(p);
            // 盘符根目录（C:\）的 GetFileName 是空串，用盘符代替
            return leaf.Length > 0 ? leaf : p.TrimEnd(':');
        }
        catch { return ""; }
    }

    private static HashSet<IntPtr> SnapshotExplorerWindows()
    {
        var set = new HashSet<IntPtr>();
        EnumWindows((h, _) =>
        {
            if (IsWindowVisible(h) && ClassNameOf(h) == ExplorerWindowClass) set.Add(h);
            return true;
        }, IntPtr.Zero);
        return set;
    }

    /// <summary>
    /// 等资源管理器窗口出现并把它拉到前台；最多等 <paramref name="timeoutMs"/>。
    ///
    /// 两种情况都要覆盖：
    ///   · <b>新窗口</b>：打开前没有的 CabinetWClass 句柄出现了 → 就是它；
    ///   · <b>复用已有窗口</b>：explorer 把现成的窗口导航到新路径（不开新窗），
    ///     此时按标题里的文件夹名认人 —— 认不出就**不动**，
    ///     宁可没置前，也不要切到不相干的窗口上。
    /// </summary>
    private static void WaitAndActivateFolder(HashSet<IntPtr> before, string leaf, int timeoutMs = 3000)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        int tries = 0;
        bool warnedNewWindow = false;

        /* 两种目标（新窗口 / 复用的窗口）都**在循环里重试**。
         *
         * 曾经只在循环结束后做一次标题匹配 —— 结果很看运气：
         * explorer 正忙的时候那一次 SetForegroundWindow 被拒，就彻底放弃了，
         * 用户看到的就是"有时在前台、有时在后台"。
         *
         * 另外 ForceForeground(allowUnlockTimeout: true) 会临时改全局的前台锁定
         * 超时并广播 WM_SETTINGCHANGE，代价明显，不能每 70ms 试一次：
         * 前几轮用普通方式，之后每 4 轮才动一次"强制解锁"。 */
        while (Environment.TickCount64 < deadline)
        {
            tries++;
            bool hard = tries % 4 == 0;

            foreach (var h in SnapshotExplorerWindows())
            {
                if (before.Contains(h)) continue;
                if (ForceForeground(h, allowUnlockTimeout: hard))
                {
                    CoreLog.Info($"[打开文件夹] 新窗口已置前（0x{h.ToInt64():X}，第 {tries} 次尝试）");
                    return;
                }
                if (!warnedNewWindow)
                {
                    warnedNewWindow = true;
                    CoreLog.Info($"[打开文件夹] 新窗口已出现，正在尝试置前（0x{h.ToInt64():X}）");
                }
            }

            if (leaf.Length > 0)
            {
                foreach (var w in EnumerateTopLevel())
                {
                    if (!string.Equals(w.ClassName, ExplorerWindowClass, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!w.Title.Contains(leaf, StringComparison.OrdinalIgnoreCase)) continue;
                    if (ForceForeground(w.Handle, allowUnlockTimeout: hard))
                    {
                        CoreLog.Info($"[打开文件夹] 复用窗口已置前：{w.Title}（第 {tries} 次尝试）");
                        return;
                    }
                }
            }

            Thread.Sleep(70);
        }

        var titles = EnumerateTopLevel()
            .Where(w => string.Equals(w.ClassName, ExplorerWindowClass, StringComparison.OrdinalIgnoreCase))
            .Select(w => w.Title)
            .ToList();

        CoreLog.Warn($"[打开文件夹] 没能置前（{timeoutMs}ms 内试了 {tries} 次；" +
                     $"现有资源管理器窗口：{(titles.Count == 0 ? "无" : string.Join(" | ", titles))}）");
    }

    // ================= 打开网址 / 文件，并把目标窗口带到前台 =================

    /// <summary>
    /// 打开的东西属于哪一类 —— 决定用什么办法认出"该置前哪个窗口"。
    /// </summary>
    public enum ShellTargetKind
    {
        /// <summary>网址：目标程序大概率是默认浏览器。</summary>
        Url,
        /// <summary>文件：目标程序由文件关联决定（Word / 播放器 / …）。</summary>
        File,
    }

    /// <summary>
    /// 用 shell 打开网址或文件，并保证它出现在**最前面**。
    ///
    /// 为什么需要：按下直达链接的键时前台是别的程序，Windows 的前台锁定
    /// 会把新窗口压在任务栏闪烁 —— 用户看到的就是"浏览器/文档在后台打开了"。
    ///
    /// 三步（与 <see cref="OpenFolderInForeground"/> 同一套思路）：
    ///   1. 启动前 <c>AllowSetForegroundWindow(ASFW_ANY)</c>，
    ///      把"可以抢前台"的许可交给接下来启动的进程 ——
    ///      大多数情况下浏览器/Office 自己就会正确置前；
    ///   2. 启动后异步等**新窗口**出现（按目标 exe 认人），再
    ///      <see cref="ForceForeground"/> 兜底；
    ///   3. 浏览器把网址开成**已有窗口里的新标签**时不会出现新窗口 ——
    ///      这时退一步，把目标程序最上层的那个窗口拉到前台。
    ///
    /// 为什么要先解析"目标 exe"：只有知道该看哪个程序，才能在"新窗口"
    /// 与"复用的旧窗口"之间做判断，也不会误把别的程序的弹窗拉到前台。
    ///
    /// 异步的原因同文件夹：shell 启动 + 窗口创建要几百毫秒，
    /// 在动作队列线程上等会把后面的按键动作全堵住。
    /// </summary>
    public static void LaunchInForeground(string target, ShellTargetKind kind)
    {
        string expectedExe = kind == ShellTargetKind.Url ? DefaultBrowserExe() : AssociatedExe(target);
        string expectedName = expectedExe.Length > 0 ? Path.GetFileName(expectedExe) : "";

        var before = SnapshotAppWindows();

        AllowSetForegroundWindow(ASFW_ANY);
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });

        if (expectedExe.Length == 0)
            CoreLog.Warn($"[打开{KindName(kind)}] 没能解析出目标程序，改用\"任何新窗口\"判断：{target}");
        else
            CoreLog.Info($"[打开{KindName(kind)}] 目标程序 {expectedName}：{target}");

        Task.Run(() => WaitAndActivateApp(before, expectedName, kind));
    }

    private static string KindName(ShellTargetKind kind)
        => kind == ShellTargetKind.Url ? "网址" : "文件";

    /// <summary>当前所有"正经的可切换窗口"的句柄（不含本程序 / 工具窗 / 桌面壳）。</summary>
    private static HashSet<IntPtr> SnapshotAppWindows()
    {
        var set = new HashSet<IntPtr>();
        foreach (var w in EnumerateTopLevel()) set.Add(w.Handle);
        return set;
    }

    /// <summary>
    /// 等目标窗口出现并置前；最多等 <paramref name="timeoutMs"/>。
    ///
    /// 新窗口优先：有的程序（浏览器新窗口、Word 新文档）会新建窗口，
    /// 有的（浏览器新标签）只是复用已有窗口 —— 两种都要覆盖。
    /// 复用窗口只在等了约 1 秒后才尝试，避免"先激活旧窗口、新窗口
    /// 稍后出现又抢一次"的闪烁。
    /// </summary>
    private static void WaitAndActivateApp(HashSet<IntPtr> before, string expectedName,
        ShellTargetKind kind, int timeoutMs = 3500)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        int tries = 0;
        bool loggedNew = false;
        string kindName = KindName(kind);

        while (Environment.TickCount64 < deadline)
        {
            tries++;
            // 临时解除前台锁定代价高（全局广播），每 4 轮才用一次
            bool hard = tries % 4 == 0;

            var windows = EnumerateTopLevel().ToList();

            /* ① 新出现的窗口。
             *
             * expectedName 为空（解析不出关联程序）时接受任何新窗口 ——
             * 否则就等于直接放弃置前。 */
            foreach (var w in windows)
            {
                if (before.Contains(w.Handle)) continue;
                if (expectedName.Length > 0 && !SameExeName(w.ExeName, expectedName)) continue;

                if (!loggedNew)
                {
                    loggedNew = true;
                    CoreLog.Info($"[打开{kindName}] 新窗口出现：{w.Display}（{w.ExeName}）");
                }
                if (ForceForeground(w.Handle, allowUnlockTimeout: hard))
                {
                    CoreLog.Info($"[打开{kindName}] 新窗口已置前（{w.Display}，第 {tries} 次尝试）");
                    return;
                }
            }

            /* ② 复用已有窗口：浏览器把网址开成当前窗口的新标签时走这里。
             *
             * EnumerateTopLevel 按 Z 序（最上层在前）返回，取第一个匹配的即可 ——
             * 浏览器总是把新标签开在"最近活动的那个窗口"里，也就是 Z 序最前的。 */
            if (expectedName.Length > 0 && tries >= 8)
            {
                foreach (var w in windows)
                {
                    if (!SameExeName(w.ExeName, expectedName)) continue;
                    if (ForceForeground(w.Handle, allowUnlockTimeout: hard))
                    {
                        CoreLog.Info($"[打开{kindName}] 复用窗口已置前：{w.Display}（第 {tries} 次尝试）");
                        return;
                    }
                    break;   // 只看最上层的那个，别把后台窗口挨个翻上来
                }
            }

            Thread.Sleep(110);
        }

        CoreLog.Warn($"[打开{kindName}] 没能置前（{timeoutMs}ms 内试了 {tries} 次，目标程序 " +
                     $"{(expectedName.Length == 0 ? "(未知)" : expectedName)}）");
    }

    /// <summary>两个 exe 文件名是否指同一个程序（忽略路径与大小写）。</summary>
    internal static bool SameExeName(string a, string b)
        => a.Length > 0 && b.Length > 0
           && string.Equals(Path.GetFileName(a), Path.GetFileName(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>默认浏览器的可执行文件路径；解析不出来返回空串。</summary>
    internal static string DefaultBrowserExe()
    {
        try
        {
            foreach (string scheme in new[] { "https", "http" })
            {
                using var choice = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    $@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\{scheme}\UserChoice");
                if (choice?.GetValue("ProgId") is not string progId || progId.Length == 0) continue;

                using var cmdKey = Microsoft.Win32.Registry.ClassesRoot
                    .OpenSubKey(progId + @"\shell\open\command");
                if (cmdKey?.GetValue(null) is not string command) continue;

                string exe = ParseExeFromShellCommand(command);
                if (exe.Length > 0) return exe;
            }
        }
        catch (Exception e)
        {
            CoreLog.Warn("读取默认浏览器失败：" + e.Message);
        }
        return "";
    }

    /// <summary>
    /// 从 shell 的 open 命令里取出 exe 路径。
    ///
    /// 三种真实写法都要认：
    ///   "C:\Program Files\…\chrome.exe" -- "%1"     ← 带引号（最常见）
    ///   C:\Program Files\…\browser.exe %1           ← 不带引号但路径有空格
    ///   %ProgramFiles%\…\msedge.exe --single-argument %1
    ///
    /// 不带引号时不能按空格切（路径本身可能有空格），以 <c>.exe</c> 为界更稳。
    /// 抽成纯函数是为了让自检直接断言，不必去读这台机器的注册表。
    /// </summary>
    internal static string ParseExeFromShellCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return "";
        string s = Environment.ExpandEnvironmentVariables(command.Trim());

        if (s.StartsWith('"'))
        {
            int end = s.IndexOf('"', 1);
            return end > 1 ? s[1..end].Trim() : "";
        }

        int exe = s.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe <= 0) return "";
        return s[..(exe + 4)].Trim();
    }

    /// <summary>
    /// 文件关联的程序路径（双击这个文件时系统会用谁打开）。
    ///
    /// 用 <c>AssocQueryString(ASSOCSTR_EXECUTABLE)</c> 而不是自己翻注册表：
    /// UserChoice、AppX、Compatibility Assistant 的映射都由它统一处理。
    /// 优先按扩展名查（.docx），查不到再按整条路径查（有些程序按文件注册）。
    /// </summary>
    internal static string AssociatedExe(string path)
    {
        foreach (string assoc in new[] { Path.GetExtension(path), path })
        {
            if (string.IsNullOrWhiteSpace(assoc)) continue;
            try
            {
                // S_OK 才算拿到结果；S_FALSE 表示缓冲区不够，len 会被写成所需长度，放大重试一次。
                uint len = 1024;
                var sb = new StringBuilder((int)len);
                uint hr = AssocQueryString(0 /*ASSOCF_NONE*/, ASSOCSTR_EXECUTABLE, assoc, null, sb, ref len);
                if (hr == 1 /*S_FALSE*/ && len > sb.Capacity)
                {
                    sb = new StringBuilder((int)len);
                    hr = AssocQueryString(0 /*ASSOCF_NONE*/, ASSOCSTR_EXECUTABLE, assoc, null, sb, ref len);
                }

                if (hr == 0)
                {
                    string exe = Environment.ExpandEnvironmentVariables(sb.ToString());
                    if (exe.Length > 0) return exe;
                }
            }
            catch (Exception e)
            {
                CoreLog.Warn($"查询文件关联失败（{assoc}）：" + e.Message);
            }
        }
        return "";
    }

    private const uint ASSOCSTR_EXECUTABLE = 2;

    // 必须显式点名 AssocQueryStringW：Shlwapi 里没有无后缀的 AssocQueryString，
    // 之前写了 ExactSpelling = true 就按裸名去导出表找，运行时直接 "Unable to find an entry point"。
    [DllImport("Shlwapi.dll", CharSet = CharSet.Unicode, EntryPoint = "AssocQueryStringW")]
    private static extern uint AssocQueryString(uint flags, uint str,
        string pszAssoc, string? pszExtra, StringBuilder pszOut, ref uint pcchOut);
}
