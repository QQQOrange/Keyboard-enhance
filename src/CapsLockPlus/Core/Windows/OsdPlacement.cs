using System;
using System.Runtime.InteropServices;
using CapsLockPlus.Core.Interop;

namespace CapsLockPlus.Core.Windows;

/// <summary>横条在**物理像素**上的矩形（SetWindowPos 用）。</summary>
internal readonly record struct OsdRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
}

/// <summary>目标显示器的工作区（物理像素）与它的缩放倍率。</summary>
internal readonly record struct OsdWorkArea(
    IntPtr Monitor, int Left, int Top, int Right, int Bottom, double DpiScale)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public bool IsValid => Width > 0 && Height > 0;
}

/// <summary>
/// 亮度横条的位置与尺寸计算。**和窗口、和 WPF 都无关的纯函数**，
/// 所以能在自检里用任意"分辨率 + 缩放"组合跑断言。
///
/// ── 为什么要这么写 ─────────────────────────────────────────────
///
/// 系统音量横条的量纲是 **DIP（96 DPI 基准的逻辑像素）**，不是屏幕百分比：
/// 它固定 192×48 DIP，所以在 100% 的屏上是 192×48 像素，在 200% 的屏上是
/// 384×96 像素 —— 占屏比例随分辨率变，但**物理观感**（几厘米宽）在各机器上
/// 一致。这正是"不同电脑看起来一样"的正确含义。
///
/// 因此本横条的做法是：
///   · 尺寸用 DIP 定义（192×48 + 四周 12 的阴影余量），
///     物理像素 = DIP × **目标显示器**的缩放；
///   · 位置用**工作区的相对关系**定义（水平居中、下沿离工作区底 14 DIP），
///     而不是写死屏幕坐标 —— 任务栏多高、屏幕多大、缩放多少都会自动跟上；
///   · 缩放取自目标显示器（GetDpiForMonitor），不是"窗口当前所在显示器"，
///     否则多屏不同缩放时会拿 A 屏的倍率去算 B 屏的像素。
///
/// ⚠ 为什么不按"屏幕宽度的百分比"来做：那样 4K 上横条会大得离谱、
///   1366×768 上小得看不清，反而和系统那条对不上。系统是按物理尺寸，
///   我们只能跟它同量纲。
/// </summary>
internal static class OsdPlacement
{
    /// <summary>卡片宽度（DIP）。实测系统音量条 192×48。两者必须同值。</summary>
    public const double CardWidthDip = 192;

    /// <summary>卡片高度（DIP）。</summary>
    public const double CardHeightDip = 48;

    /// <summary>
    /// 卡片四周留给阴影的余量（DIP）。
    /// 窗口必须比卡片大一圈，否则 DropShadowEffect 会被窗口边界裁掉。
    /// </summary>
    public const double ShadowPadDip = 12;

    /// <summary>
    /// 卡片下沿到**工作区**底边的距离（DIP）。
    /// 实测系统音量条：200% 下距屏底 28 物理像素 = 14 DIP。
    /// 不用写死像素，否则换台 125% 的机器就偏了。
    /// </summary>
    public const double BottomGapDip = 14;

    /* 极小屏保护：正常显示器永远不会触发。
     * 存在的意义是别在 640×480 这种屏上让横条横跨大半个屏幕
     * （192 DIP 在 100% 下就是 192 px）。真触发时按同一比例缩，
     * 长宽比不变。 */
    private const double MaxWorkWidthRatio = 0.80;
    private const double MaxWorkHeightRatio = 0.45;

    /// <summary>把"缩放值"洗成一个能用的数（0 / 非法值都当 1.0）。</summary>
    public static double EffectiveScale(double dpiScale) =>
        dpiScale > 0.05 && !double.IsNaN(dpiScale) ? dpiScale : 1.0;

    /// <summary>
    /// 核心：给定工作区（物理像素）+ 目标显示器缩放，算出窗口矩形。
    ///
    ///   x = 工作区左边 + (工作区宽 − 窗口宽) / 2        ← 水平居中（相对，不是写死）
    ///   y = 工作区底边 − (14 + 48 + 12) × 缩放          ← 贴底（相对，不是写死）
    ///   w = (192 + 24) × 缩放 ,  h = (48 + 24) × 缩放   ← 尺寸按缩放走
    /// </summary>
    public static OsdRect Compute(
        int workLeft, int workTop, int workRight, int workBottom, double dpiScale)
    {
        double s = EffectiveScale(dpiScale);

        int workW = workRight - workLeft;
        int workH = workBottom - workTop;
        if (workW <= 0 || workH <= 0) return new OsdRect(0, 0, 0, 0);

        double wDip = CardWidthDip + ShadowPadDip * 2;   // 216
        double hDip = CardHeightDip + ShadowPadDip * 2;  // 72

        // 极小屏保护（见上面的常量注释），正常分辨率下 fit == 1，不改变任何值
        double fit = Math.Min(1.0, Math.Min(
            workW * MaxWorkWidthRatio / (wDip * s),
            workH * MaxWorkHeightRatio / (hDip * s)));
        s *= fit;

        int w = (int)Math.Round(wDip * s);
        int h = (int)Math.Round(hDip * s);

        int x = workLeft + (workW - w) / 2;
        int y = workBottom - (int)Math.Round((BottomGapDip + CardHeightDip + ShadowPadDip) * s);

        // 兜底：万一算出来跑到工作区外面（超小屏 + 极端缩放），贴住上边
        if (y < workTop) y = workTop;
        if (x < workLeft) x = workLeft;

        return new OsdRect(x, y, w, h);
    }

    /// <summary>
    /// 横条该出现在哪块屏：**前台窗口所在的那块** → 鼠标所在 → 主屏。
    ///
    /// 为什么不是永远主屏：笔记本外接显示器时，人在外接屏上干活，
    /// 横条跑到笔记本屏上就成了"没反应"。前台窗口拿不到（锁屏、
    /// 桌面获得焦点）就退回鼠标位置，再退回主屏。
    /// </summary>
    public static bool TryGetTargetWorkArea(IntPtr fallbackWindow, out OsdWorkArea area)
    {
        area = default;

        IntPtr mon = IntPtr.Zero;

        try
        {
            IntPtr fg = NativeMethods.GetForegroundWindow();
            if (fg != IntPtr.Zero)
                mon = NativeMethods.MonitorFromWindow(fg, NativeMethods.MONITOR_DEFAULTTONEAREST);
        }
        catch { /* 取不到就往下走 */ }

        if (mon == IntPtr.Zero)
        {
            try
            {
                if (NativeMethods.GetCursorPos(out var pt))
                    mon = NativeMethods.MonitorFromPoint(pt, NativeMethods.MONITOR_DEFAULTTOPRIMARY);
            }
            catch { }
        }

        if (mon == IntPtr.Zero && fallbackWindow != IntPtr.Zero)
        {
            try
            {
                mon = NativeMethods.MonitorFromWindow(
                    fallbackWindow, NativeMethods.MONITOR_DEFAULTTONEAREST);
            }
            catch { }
        }

        if (mon == IntPtr.Zero) return false;

        var mi = new NativeMethods.MONITORINFO
        {
            cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>(),
        };
        if (!NativeMethods.GetMonitorInfo(mon, ref mi)) return false;

        double dpi = MonitorScale(mon, fallbackWindow);

        area = new OsdWorkArea(mon,
            mi.rcWork.Left, mi.rcWork.Top, mi.rcWork.Right, mi.rcWork.Bottom, dpi);

        return area.IsValid;
    }

    /// <summary>
    /// 目标显示器的缩放。优先 GetDpiForMonitor（问的是**这块屏**），
    /// 失败退 GetDpiForWindow（问的是窗口所在屏），再失败 1.0。
    /// </summary>
    private static double MonitorScale(IntPtr monitor, IntPtr fallbackWindow)
    {
        try
        {
            if (NativeMethods.GetDpiForMonitor(monitor, NativeMethods.MDT_EFFECTIVE_DPI,
                                               out uint dx, out _) == 0 && dx > 0)
                return dx / 96.0;
        }
        catch { /* Shcore 不可用（Win7 之类） */ }

        try
        {
            if (fallbackWindow != IntPtr.Zero)
            {
                uint d = NativeMethods.GetDpiForWindow(fallbackWindow);
                if (d > 0) return d / 96.0;
            }
        }
        catch { }

        return 1.0;
    }

    /// <summary>仅供自检：卡片（不含阴影余量）的物理尺寸。</summary>
    public static (int Width, int Height) CardSizeForTest(OsdRect r)
    {
        double s = r.Width / (CardWidthDip + ShadowPadDip * 2);
        return ((int)Math.Round(CardWidthDip * s), (int)Math.Round(CardHeightDip * s));
    }
}
