using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using CapsLockPlus.Core.Interop;

namespace CapsLockPlus.Core.Windows;

/// <summary>
/// 亮度横条的**高带宿主**：一个用 <c>CreateWindowInBand</c> 创建的分层窗口，
/// 内容由 WPF 离屏渲染成位图后贴上去。
///
/// ── 存在的理由 ────────────────────────────────────────────────
///
/// 用户报的现象：按亮度键时，横条被**开始菜单**整个盖住；而系统自带的
/// 音量条却没事。根因不是"没置顶"，而是 **Windows 的窗口带（band）**：
///
///   band 1  普通窗口、任务栏（Shell_TrayWnd）
///   band 6  开始菜单 / 搜索（StartMenuExperienceHost / SearchHost）
///
/// Z 序先比 band 再比带内次序，所以 `SetWindowPos(HWND_TOPMOST/HWND_TOP)`
/// 做得再多也只在 band 1 里打转，永远追不上 band 6。系统音量条是 Shell
/// 自己画的，本来就在高带里 —— 这就是"它能显示、我们不能"的原因。
///
/// 往上走通常只有 UIAccess 一条路（要微软签名 + 装进 Program Files），
/// 但实测发现：**普通进程用 CreateWindowInBand 可以在创建时进 band 16**，
/// 而 16 高于开始菜单的 6 —— 实测（tools/probe_band16_overlap.py）
/// band 16 的普通窗口和分层窗口都稳稳压在开始菜单之上，band 1 的 topmost
/// 对照窗口则被盖住。
///
/// 于是横条不再由 WPF 窗口直接显示，而是：
///   1. 这里建一个 band 16 的 <c>WS_EX_LAYERED</c> 弹窗；
///   2. <see cref="OsdCard"/> 用 RenderTargetBitmap 渲染成 Pbgra32 位图；
///   3. UpdateLayeredWindow 贴上去，透明度走 SourceConstantAlpha。
///
/// 这样圆角/阴影/半透明材质全都不受影响（位图自带 alpha），
/// 而 Z 序由 band 保证，不需要每 200ms 去抢一次。
///
/// 失败时（老系统不给 band 16、或 CreateWindowInBand 不存在）返回 null，
/// 调用方回退到原来的 WPF 窗口路径 —— 那条路在任务栏之上仍然有效，
/// 只是压不过开始菜单。
/// </summary>
internal sealed class OsdBandSurface : IDisposable
{
    private const string ClassName = "CapsLockPlus.OsdBand16";

    /// <summary>
    /// 窗口标题。和 WPF 那条**故意同名** —— 排查/端到端脚本按标题找窗口，
    /// 换条路径就找不到的话，测试会误判成"横条没出现"。
    /// </summary>
    public const string WindowTitle = "按键增强 横条";

    /// <summary>WM_ERASEBKGND。分层窗口自己带 alpha，绝不能让 DefWindowProc 擦成黑色。</summary>
    private const uint WM_ERASEBKGND = 0x0014;

    /* 委托必须静态保活：RegisterClass 只存函数指针，GC 掉委托就会在
     * 窗口收到消息时跳进已释放的 thunk，表现为随机崩溃。 */
    private static readonly NativeMethods.WndProcDelegate WndProcDelegate = WndProc;
    private static ushort s_atom;
    private static bool s_classTried;

    private IntPtr _hwnd;
    private IntPtr _memDc;
    private IntPtr _dib;
    private IntPtr _oldBitmap = IntPtr.Zero;
    private IntPtr _bits;

    private int _width;
    private int _height;
    private int _x;
    private int _y;
    private byte _alpha = 255;
    private bool _visible;
    private bool _disposed;

    public IntPtr Handle => _hwnd;

    /// <summary>上次贴上去的尺寸，用来判断要不要重建 DIB。</summary>
    public int PixelWidth => _width;
    public int PixelHeight => _height;

    private OsdBandSurface(IntPtr hwnd) => _hwnd = hwnd;

    /// <summary>
    /// 试着建一个 <paramref name="band"/> 带里的分层弹窗。
    /// 失败返回 null 并给出原因（写日志用），**不抛** —— 上层要能降级。
    /// </summary>
    public static OsdBandSurface? TryCreate(uint band, out string error)
    {
        error = "";

        try
        {
            if (!EnsureClass(out error)) return null;

            IntPtr hinst = NativeMethods.GetModuleHandle(null);
            int exStyle = NativeMethods.WS_EX_LAYERED
                        | NativeMethods.WS_EX_TOOLWINDOW      // 不进 Alt+Tab
                        | NativeMethods.WS_EX_TRANSPARENT     // 点击穿透
                        | NativeMethods.WS_EX_NOACTIVATE;     // 不抢焦点

            IntPtr hwnd = NativeMethods.CreateWindowInBand(
                exStyle, (IntPtr)s_atom, WindowTitle, NativeMethods.WS_POPUP,
                0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, hinst, IntPtr.Zero, band);

            if (hwnd == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                error = $"CreateWindowInBand(band {band}) 失败，Win32 错误 {err}";
                return null;
            }

            /* 兜底：确认真的进了目标带。某些系统会"创建成功但忽略 band"，
             * 那样横条还是会被开始菜单盖住 —— 宁可知情地回退。 */
            if (NativeMethods.GetWindowBand(hwnd, out uint actual) && actual != band)
            {
                error = $"窗口落在 band {actual}，不是请求的 {band}";
                NativeMethods.DestroyWindow(hwnd);
                return null;
            }

            return new OsdBandSurface(hwnd);
        }
        catch (EntryPointNotFoundException)
        {
            error = "本系统的 user32 没有 CreateWindowInBand";
            return null;
        }
        catch (Exception e)
        {
            error = e.Message;
            return null;
        }
    }

    private static bool EnsureClass(out string error)
    {
        error = "";
        if (s_classTried) return s_atom != 0;
        s_classTried = true;

        try
        {
            var wc = new NativeMethods.WNDCLASS
            {
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(WndProcDelegate),
                hInstance = NativeMethods.GetModuleHandle(null),
                lpszClassName = ClassName,
            };

            s_atom = NativeMethods.RegisterClass(ref wc);
            if (s_atom == 0)
            {
                error = $"RegisterClass 失败，Win32 错误 {Marshal.GetLastWin32Error()}";
                return false;
            }

            return true;
        }
        catch (Exception e)
        {
            error = e.Message;
            return false;
        }
    }

    private static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        // 分层窗口的背景由 UpdateLayeredWindow 提供，擦背景只会闪一下黑
        if (msg == WM_ERASEBKGND) return new IntPtr(1);
        return NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    /// <summary>
    /// 贴一张位图，并把它摆到 (x, y)（物理像素）。
    ///
    /// 尺寸没变时复用同一块 DIB —— 连按亮度键每秒几十次，
    /// 每次重建 DC/DIB 会让 DWM 重组图层，看起来像在抖。
    /// </summary>
    public void Present(BitmapSource bitmap, int x, int y, byte alpha)
    {
        if (_disposed || _hwnd == IntPtr.Zero) return;

        int w = bitmap.PixelWidth;
        int h = bitmap.PixelHeight;
        if (w <= 0 || h <= 0) return;

        EnsureDib(w, h);
        if (_bits == IntPtr.Zero) return;

        bitmap.CopyPixels(new Int32Rect(0, 0, w, h), _bits, w * h * 4, w * 4);

        _x = x;
        _y = y;
        _alpha = alpha;
        Push();

        if (!_visible)
        {
            NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_SHOWNA);
            _visible = true;
        }
    }

    /// <summary>只改不透明度（淡入淡出用），不重传像素。</summary>
    public void SetAlpha(byte alpha)
    {
        if (_disposed || _hwnd == IntPtr.Zero) return;
        if (_alpha == alpha) return;

        _alpha = alpha;
        Push();
    }

    public void Hide()
    {
        if (_disposed || _hwnd == IntPtr.Zero) return;
        if (!_visible) return;

        NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_HIDE);
        _visible = false;
    }

    private void EnsureDib(int w, int h)
    {
        if (_dib != IntPtr.Zero && _width == w && _height == h) return;

        ReleaseDib();

        IntPtr screen = NativeMethods.GetDC(IntPtr.Zero);
        try
        {
            _memDc = NativeMethods.CreateCompatibleDC(screen);
            if (_memDc == IntPtr.Zero) return;

            var bi = new NativeMethods.BITMAPINFO
            {
                bmiHeader = new NativeMethods.BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
                    biWidth = w,
                    biHeight = -h,                 // 负数 = top-down，和 RenderTargetBitmap 的行序一致
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = NativeMethods.BI_RGB,
                },
                bmiColors = new uint[3],
            };

            _dib = NativeMethods.CreateDIBSection(
                _memDc, ref bi, NativeMethods.DIB_RGB_COLORS, out _bits, IntPtr.Zero, 0);
            if (_dib == IntPtr.Zero || _bits == IntPtr.Zero)
            {
                ReleaseDib();
                return;
            }

            _oldBitmap = NativeMethods.SelectObject(_memDc, _dib);
            _width = w;
            _height = h;

            NativeMethods.SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, w, h,
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOMOVE);
        }
        finally
        {
            NativeMethods.ReleaseDC(IntPtr.Zero, screen);
        }
    }

    private void Push()
    {
        if (_memDc == IntPtr.Zero || _dib == IntPtr.Zero || _width <= 0) return;

        IntPtr screen = NativeMethods.GetDC(IntPtr.Zero);
        try
        {
            var dst = new NativeMethods.POINT(_x, _y);
            var size = new NativeMethods.SIZE(_width, _height);
            var src = new NativeMethods.POINT(0, 0);
            var blend = new NativeMethods.BLENDFUNCTION
            {
                BlendOp = (byte)NativeMethods.AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = _alpha,
                AlphaFormat = (byte)NativeMethods.AC_SRC_ALPHA,
            };

            NativeMethods.UpdateLayeredWindow(
                _hwnd, screen, ref dst, ref size, _memDc, ref src, 0,
                ref blend, NativeMethods.ULW_ALPHA);
        }
        finally
        {
            NativeMethods.ReleaseDC(IntPtr.Zero, screen);
        }
    }

    private void ReleaseDib()
    {
        if (_memDc != IntPtr.Zero)
        {
            if (_oldBitmap != IntPtr.Zero) NativeMethods.SelectObject(_memDc, _oldBitmap);
            NativeMethods.DeleteDC(_memDc);
            _memDc = IntPtr.Zero;
        }

        if (_dib != IntPtr.Zero)
        {
            NativeMethods.DeleteObject(_dib);
            _dib = IntPtr.Zero;
        }

        _oldBitmap = IntPtr.Zero;
        _bits = IntPtr.Zero;
        _width = 0;
        _height = 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        ReleaseDib();

        if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }

        _visible = false;
    }

    /// <summary>仅供自检：当前窗口所在的带（读不到返回 0）。</summary>
    internal uint BandForTest =>
        _hwnd != IntPtr.Zero && NativeMethods.GetWindowBand(_hwnd, out uint b) ? b : 0;

    /// <summary>仅供自检：窗口是否可见。</summary>
    internal bool IsVisibleForTest => NativeMethods.IsWindowVisible(_hwnd);
}
