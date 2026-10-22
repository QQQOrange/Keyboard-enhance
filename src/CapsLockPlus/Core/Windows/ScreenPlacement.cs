using System;
using System.Windows;
using System.Windows.Interop;
using CapsLockPlus.Core.Interop;

namespace CapsLockPlus.Core.Windows;

/// <summary>
/// 浮层定位：按**物理像素 + 目标显示器的工作区**摆放。
///
/// 为什么需要它（踩过的坑）：
///   · `SystemParameters.WorkArea` 是"**系统 DPI** 下的 DIP"，而 WPF 的 `Left/Top`
///     是"**窗口所在显示器** DPI 下的 DIP"。在 200% 缩放的机器上两者差一倍，
///     在混 DPI 的多屏上更是互相错位 —— 浮层会跑到屏幕外或压到任务栏。
///   · 而且它永远只认主屏：用户在副屏上打字，Qbar / 提示却弹到主屏。
///
/// 这里统一改成：`MonitorFromWindow` 取显示器 → `GetMonitorInfo` 取工作区（物理像素）
/// → 用 `GetDpiForWindow` 把窗口尺寸换成物理像素 → `SetWindowPos` 直接落位。
/// </summary>
public static class ScreenPlacement
{
    /// <summary>锚点。</summary>
    public enum Anchor
    {
        /// <summary>水平居中、贴工作区顶部（+ 边距）。</summary>
        TopCenter,

        /// <summary>水平居中、落在工作区上方 22% 处（Qbar 的观感位置）。</summary>
        UpperCenter,

        /// <summary>工作区右下角（提示条）。</summary>
        BottomRight,
    }

    /// <summary>
    /// 摆放窗口（只挪位置，不改尺寸、不改 Z 序、不抢焦点）。
    /// </summary>
    /// <param name="window">已 Show 或至少已建句柄的窗口。</param>
    /// <param name="anchor">锚点。</param>
    /// <param name="marginX">左右边距（DIP）。</param>
    /// <param name="marginY">上下边距（DIP）。</param>
    /// <param name="target">以哪个窗口所在显示器为准；默认用自己。</param>
    /// <returns>摆放成功返回 true；拿不到句柄/显示器信息时返回 false（调用方可退回旧逻辑）。</returns>
    public static bool Place(Window window, Anchor anchor,
        double marginX = 12, double marginY = 12, IntPtr target = default)
    {
        try
        {
            var helper = new WindowInteropHelper(window);
            IntPtr hwnd = helper.Handle;
            if (hwnd == IntPtr.Zero) hwnd = helper.EnsureHandle();
            if (hwnd == IntPtr.Zero) return false;

            IntPtr from = target != IntPtr.Zero ? target : hwnd;
            IntPtr monitor = NativeMethods.MonitorFromWindow(from, NativeMethods.MONITOR_DEFAULTTONEAREST);
            if (monitor == IntPtr.Zero) return false;

            var mi = new NativeMethods.MONITORINFO
            {
                cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>(),
            };
            if (!NativeMethods.GetMonitorInfo(monitor, ref mi)) return false;

            var work = mi.rcWork;
            if (work.Width <= 0 || work.Height <= 0) return false;

            double scale = ScaleOf(hwnd);

            // 尺寸用 DIP → 物理像素；窗口还没排过版时退回显式 Width/Height
            double dipW = window.ActualWidth > 1 ? window.ActualWidth : window.Width;
            double dipH = window.ActualHeight > 1 ? window.ActualHeight : window.Height;
            int w = (int)Math.Round(Math.Max(0, dipW) * scale);
            int h = (int)Math.Round(Math.Max(0, dipH) * scale);

            int mx = (int)Math.Round(marginX * scale);
            int my = (int)Math.Round(marginY * scale);

            int x, y;
            switch (anchor)
            {
                case Anchor.BottomRight:
                    x = work.Right - w - mx;
                    y = work.Bottom - h - my;
                    break;

                case Anchor.UpperCenter:
                    x = work.Left + (work.Width - w) / 2;
                    y = work.Top + (int)Math.Round(work.Height * 0.22);
                    break;

                default: // TopCenter
                    x = work.Left + (work.Width - w) / 2;
                    y = work.Top + my;
                    break;
            }

            NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0,
                NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
            return true;
        }
        catch (Exception ex)
        {
            CoreLog.Warn("浮层定位失败，退回默认位置：" + ex.Message);
            return false;
        }
    }

    /// <summary>窗口所在显示器的缩放（拿不到就按 1.0，等于按 96 DPI 算）。</summary>
    private static double ScaleOf(IntPtr hwnd)
    {
        try
        {
            uint dpi = NativeMethods.GetDpiForWindow(hwnd);
            if (dpi > 0) return dpi / 96.0;
        }
        catch
        {
            // 老系统上没有这个 API：退回 1.0
        }
        return 1.0;
    }
}
