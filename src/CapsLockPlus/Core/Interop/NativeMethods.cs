using System.Runtime.InteropServices;
using System.Text;

namespace CapsLockPlus.Core.Interop;

/// <summary>Win32 互操作声明（仅保留本程序用到的部分）。</summary>
public static class NativeMethods
{
    // ---------------- 窗口消息 ----------------
    public const int WM_KEYDOWN = 0x0100;
    public const int WM_KEYUP = 0x0101;
    public const int WM_SYSKEYDOWN = 0x0104;
    public const int WM_SYSKEYUP = 0x0105;
    public const int WM_MOUSEWHEEL = 0x020A;
    public const int WM_MOUSEHWHEEL = 0x020E;

    public const int WH_KEYBOARD_LL = 13;
    public const int WH_MOUSE_LL = 14;
    public const int HC_ACTION = 0;

    public const uint LLKHF_INJECTED = 0x10;
    public const uint LLKHF_EXTENDED = 0x01;

    // ---------------- 输入 ----------------
    public const uint INPUT_KEYBOARD = 1;
    public const uint INPUT_MOUSE = 0;

    public const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const uint KEYEVENTF_UNICODE = 0x0004;
    public const uint KEYEVENTF_SCANCODE = 0x0008;

    public const uint MOUSEEVENTF_MOVE = 0x0001;
    public const uint MOUSEEVENTF_WHEEL = 0x0800;
    public const uint MOUSEEVENTF_HWHEEL = 0x1000;
    public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    public const uint MOUSEEVENTF_LEFTUP = 0x0004;
    public const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    public const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    public const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    public const uint MOUSEEVENTF_MIDDLEUP = 0x0040;

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    public static extern ushort MapVirtualKey(uint uCode, uint uMapType);

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    public static extern short GetKeyState(int nVirtKey);

    // ---------------- 钩子 ----------------
    public delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
        public POINT(int x, int y) { X = x; Y = y; }
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandle(string? lpModuleName);

    // ---------------- 窗口 ----------------
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    /// <summary>按类名找顶层窗口（自检要找任务栏 Shell_TrayWnd 验证 Z 序）。</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr SetFocus(IntPtr hWnd);

    /* 「输入文本」用真实按键敲字时，必须按**前台窗口所在线程的键盘布局**
     * 把字符映射成虚拟键 —— 否则德语键盘上打 @ 会变成别的字符。 */
    [DllImport("user32.dll")]
    public static extern IntPtr GetKeyboardLayout(uint idThread);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern short VkKeyScanEx(char ch, IntPtr dwhkl);

    /* 前台锁定超时：Windows 默认 2000ms 内不允许非前台进程抢焦点，
     * 这正是"面板弹出来了但打字进不去"的根因。
     *
     * 用法有坑：
     *   · GET 把值写进 out uint；
     *   · SET 的值放在 **uiParam**，pvParam 必须为 NULL。
     * 临时置 0 抢到焦点后立刻还原（见 WindowService.ForceForeground）。 */
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SystemParametersInfo(uint uiAction, uint uiParam,
        out uint pvParam, uint fWinIni);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SystemParametersInfo(uint uiAction, uint uiParam,
        IntPtr pvParam, uint fWinIni);

    public const uint SPI_GETFOREGROUNDLOCKTIMEOUT = 0x2000;
    public const uint SPI_SETFOREGROUNDLOCKTIMEOUT = 0x2001;
    public const uint SPIF_SENDCHANGE = 0x0002;

    [DllImport("user32.dll")]
    public static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd);

    // ---- 顶层窗口 Z 序枚举（点选窗口时用来绕过自己的全屏遮罩）----
    [DllImport("user32.dll")]
    public static extern IntPtr GetDesktopWindow();

    [DllImport("user32.dll")]
    public static extern IntPtr GetTopWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    public const uint GW_HWNDNEXT = 2;

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("user32.dll")]
    public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    public static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    /* 显示器自己的缩放（Win8.1+，Shcore）。
     *
     * GetDpiForWindow 给的是"窗口当前所在显示器"的缩放；而横条要放到
     * **目标显示器**上（可能是另一块），移动之前必须知道那块屏的缩放，
     * 否则会拿着 A 屏的倍率去算 B 屏的像素 —— 多屏且缩放不同时必然错位。
     *
     * 失败（老系统 / 该 API 不可用）时调用方会退回 GetDpiForWindow。 */
    [DllImport("Shcore.dll", ExactSpelling = true)]
    public static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType,
        out uint dpiX, out uint dpiY);

    /// <summary>MDT_EFFECTIVE_DPI：用户实际设置的缩放（考虑兼容性重定向）。</summary>
    public const int MDT_EFFECTIVE_DPI = 0;

    /* 任务栏矩形（ABM_GETTASKBARPOS）。
     *
     * ⚠ 已删除。它曾经被用来"让亮度横条避开任务栏"，但这个前提是错的：
     *   任务栏固定时 rcWork 已经扣除了它；任务栏自动隐藏时 rcWork 等于整屏，
     *   而系统横条实测（3840×2160@200%，黑底截图量边缘）**仍然贴着屏幕底**
     *   （下沿距底 28 px），并没有额外让开。多让一次的结果就是整条横条
     *   比系统高 96 px —— 用户反馈"比系统音量条高一个横条"。
     *   定位只用 rcWork，见 OsdWindow.Place()。 */

    /* 窗口所在显示器的 DPI（Win10 1607+）。
     * 定位窗口必须用它，不能用 SystemParameters.WorkArea —— 后者是
     * "系统 DPI 下的 DIP"，而 Left/Top 是"窗口所在显示器 DPI 下的 DIP"，
     * 200% 缩放时两者差一倍，窗口会直接落到屏幕外。 */
    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref RECT pvParam, uint fWinIni);

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    public static extern bool DestroyIcon(IntPtr hIcon);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    public const int GWL_EXSTYLE = -20;
    public const int GWL_STYLE = -16;
    public const int WS_EX_TOPMOST = 0x00000008;
    public const int WS_EX_TRANSPARENT = 0x00000020;   // 鼠标点击穿透
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_APPWINDOW = 0x00040000;
    public const int WS_EX_LAYERED = 0x00080000;
    public const int WS_EX_NOACTIVATE = 0x08000000;

    public const int SW_MINIMIZE = 6;
    public const int SW_RESTORE = 9;
    public const int SW_MAXIMIZE = 3;
    public const int SW_SHOWNOACTIVATE = 4;

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public const uint SWP_FRAMECHANGED = 0x0020;

    /// <summary>不发 WM_WINDOWPOSCHANGING（置顶重提升时避免被自己的处理拦下）。</summary>
    public const uint SWP_NOSENDCHANGING = 0x0400;

    /// <summary>
    /// 放到 Z 序最前。
    ///
    /// 与 <see cref="HWND_TOPMOST"/> 的区别是**关键**：
    /// HWND_TOPMOST 只保证"在所有非 topmost 窗口之上"，
    /// 在两个**同为 topmost** 的窗口之间不改变次序 —— 所以任务栏/播放器悬浮条
    /// 顶上来之后，反复发 HWND_TOPMOST 是抢不回来的（实测一秒没动静）。
    /// HWND_TOP 才是"放到 Z 序最顶"，对已经 topmost 的窗口来说
    /// 就等于"站到 topmost 组的最前面"。
    /// </summary>
    public static readonly IntPtr HWND_TOP = new(0);

    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public static readonly IntPtr HWND_NOTOPMOST = new(-2);
    public static readonly IntPtr HWND_BOTTOM = new(1);

    public const uint GA_ROOT = 2;
    public const uint MONITOR_DEFAULTTONEAREST = 2;

    /// <summary>MonitorFromPoint 的兜底：点不在任何显示器上时给主显示器。</summary>
    public const uint MONITOR_DEFAULTTOPRIMARY = 1;

    // ---------------- 进程 ----------------
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref int lpdwSize);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentProcessId();

    [DllImport("user32.dll")]
    public static extern bool AllowSetForegroundWindow(uint dwProcessId);

    // ================= 窗口带（window band）与分层窗口 =================

    /* 为什么需要这一整块 —— 亮度横条被开始菜单遮挡的根因。
     *
     * Windows 8 起，外壳把窗口分进若干「带」（band）。Z 序先比 band、再比
     * 带内的先后。实测（tools/probe_start_band.py）本机：
     *
     *   普通窗口 / 任务栏        band 1  (DESKTOP)
     *   开始菜单 / 搜索          band 6  (IMMERSIVE_INACTIVEMOBODY)
     *   系统音量条（Shell 自己） band ≥ 6 —— 和开始菜单同一层，所以盖不住
     *
     * 普通进程能用 SetWindowPos 干的事**全都在 band 1 之内**：
     * HWND_TOPMOST 只影响同带内次序，HWND_TOP 也只到 band 1 的顶。
     * 所以不管怎么重提升，开始菜单一打开就是压在我们上面。
     *
     * 唯一不需要 UIAccess 签名的出路：**创建时**就把窗口生进更高的带
     * （CreateWindowInBand）。实测普通进程被允许的带上限是 16：
     * band 2~12 一律返回 ERROR_ACCESS_DENIED(5)，16 可以，
     * 而且 16 > 6 —— 实测能压在开始菜单之上（tools/probe_band16_overlap.py）。
     *
     * SetWindowBand 对已存在的窗口无效（同样 ACCESS_DENIED），所以只能在
     * CreateWindowInBand 里一次定死；WPF 的 Window 不提供这个能力，
     * 于是横条需要一个自己的分层窗口来当宿主。
     */

    /// <summary>band 值（Win8+ 的 ZBID_*）。只列本程序用得到的。</summary>
    public const uint ZBID_DESKTOP = 1;
    public const uint ZBID_IMMERSIVE_MOGO = 4;              // 开始菜单
    public const uint ZBID_IMMERSIVE_INACTIVEMOBODY = 6;    // 搜索/开始（实测）
    public const uint ZBID_UIACCESS = 11;

    /// <summary>
    /// 实测普通进程能创建的最高带。16 在本机（Win11 23H2）可用且高于开始菜单；
    /// 17 起又是 ACCESS_DENIED。换系统版本若不允许，创建会失败并回退老路径。
    /// </summary>
    public const uint ZBID_ABOVE_SHELL = 16;

    public delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASS
    {
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SIZE
    {
        public int cx;
        public int cy;
        public SIZE(int cx, int cy) { this.cx = cx; this.cy = cy; }
    }

    /// <summary>UpdateLayeredWindow 的混合参数（Pack=1 是硬要求）。</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
        public uint[] bmiColors;
    }

    public const uint WS_POPUP = 0x80000000;

    public const uint BI_RGB = 0;
    public const uint DIB_RGB_COLORS = 0;

    public const uint AC_SRC_OVER = 0x00;
    public const uint AC_SRC_ALPHA = 0x01;
    public const uint ULW_ALPHA = 0x02;
    public const uint ULW_OPAQUE = 0x04;

    public const int SW_SHOWNA = 8;
    public const int SW_HIDE = 0;

    /// <summary>
    /// 未文档化导出。**按序号之外的名字导出**，所以能用 DllImport 直接绑；
    /// 不存在时（Win7 / 某些精简版）会抛 EntryPointNotFoundException，
    /// 调用方必须接住并回退。
    /// </summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateWindowInBand(
        int dwExStyle, IntPtr atomClass, string? lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam, uint dwBand);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern ushort RegisterClass(ref WNDCLASS lpWndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool UnregisterClass(string lpClassName, IntPtr hInstance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool DestroyWindow(IntPtr hWnd);

    /// <summary>读窗口所在带。窗口不在任何带里（普通窗口）时返回 false。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetWindowBand(IntPtr hWnd, out uint pdwBand);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UpdateLayeredWindow(
        IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
        IntPtr hdcSrc, ref POINT pptSrc, uint crKey,
        ref BLENDFUNCTION pblend, uint dwFlags);

    [DllImport("user32.dll")]
    public static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateDIBSection(
        IntPtr hdc, ref BITMAPINFO pbmi, uint usage, out IntPtr ppvBits,
        IntPtr hSection, uint offset);

    [DllImport("gdi32.dll")]
    public static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteObject(IntPtr ho);
}
