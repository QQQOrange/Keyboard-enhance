using System;
using System.Windows;
using System.Windows.Interop;
using CapsLockPlus.Core.Interop;

namespace CapsLockPlus.Core.Windows;

/// <summary>
/// 把窗口变成"看得到、点不到、也抢不到焦点"的浮层。
///
/// ## 为什么不能只靠 XAML
///
/// `IsHitTestVisible="False"` 只是让 **WPF 内部**不做命中测试 —— 窗口本身照样
/// 收得到鼠标消息，于是它压在什么上面，那个位置就点不动了。
/// 录制悬浮条正好在屏幕顶部中央（浏览器标签栏那一带），一录就是几十秒；
/// 提示条压在右下角任务栏附近。用户点下去没反应，只会以为"程序卡了"。
///
/// 真正的点击穿透要设 `WS_EX_TRANSPARENT`；同时补 `WS_EX_NOACTIVATE`
/// （被别的窗口挤压时不会被激活）和 `WS_EX_TOOLWINDOW`（不出现在 Alt+Tab 里）。
/// OSD 早就这么做了（见 OsdWindow 的同名设置），Toast 与录制悬浮条以前漏了。
/// </summary>
public static class OverlayWindowStyle
{
    /// <summary>设为"点击穿透 + 不激活 + 不进 Alt+Tab"。失败只记日志，不影响显示。</summary>
    public static void Apply(Window window, string what)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;

            int ex = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
            ex |= NativeMethods.WS_EX_NOACTIVATE
                  | NativeMethods.WS_EX_TRANSPARENT
                  | NativeMethods.WS_EX_TOOLWINDOW;
            NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, ex);
        }
        catch (Exception e)
        {
            CoreLog.Warn($"{what}：浮层样式设置失败 —— {e.Message}");
        }
    }

    /// <summary>供自检：这个窗口是否已经是点击穿透的浮层。</summary>
    public static bool IsClickThrough(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        int ex = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
        return (ex & NativeMethods.WS_EX_TRANSPARENT) != 0
            && (ex & NativeMethods.WS_EX_NOACTIVATE) != 0;
    }
}
