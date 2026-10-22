using System.Runtime.InteropServices;

namespace CapsLockPlus.Core.Text;

/// <summary>
/// 用**窗口消息**请目标窗口把选区复制到剪贴板（WM_COPY）。
///
/// 存在的理由：以管理员身份运行时，`SendInput` 往非管理员进程投递按键
/// 会被 **UIPI 拦掉** —— 现象就是"管理员模式下取词全都不工作"。
/// 窗口消息走的是另一条路（直接进目标窗口的消息队列），UIPI 拦不住
/// 同权限或低权限窗口的**消息投递**。
///
/// 另外对做过反自动化处理的程序也有效：它们会忽略带 LLKHF_INJECTED
/// 标记的合成按键，但拦不住 WM_COPY。
/// </summary>
public static class WindowMessageCopier
{
    private const uint WM_COPY = 0x0301;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GUITHREADINFO
    {
        public int cbSize;
        public uint flags;
        public IntPtr hwndActive;
        public IntPtr hwndFocus;
        public IntPtr hwndCapture;
        public IntPtr hwndMenuOwner;
        public IntPtr hwndMoveSize;
        public IntPtr hwndCaret;
        public RECT rcCaret;
    }

    /// <summary>
    /// 往几个候选窗口投 WM_COPY。返回"至少投出去一条"。
    ///
    /// 为什么是一串候选：真正"有选区"的控件未必是焦点窗口 ——
    /// 浏览器里选区归渲染进程的子窗口，Qt 应用内容在自己的子窗口里。
    /// 把焦点窗口、活动窗口、前台窗口都投一遍命中率最高；
    /// WM_COPY 投到没有选区的窗口上是空操作，不会有副作用。
    /// </summary>
    public static bool PostCopyToFocus()
    {
        var targets = new List<IntPtr>();
        try
        {
            IntPtr fg = GetForegroundWindow();
            if (fg != IntPtr.Zero)
            {
                uint tid = GetWindowThreadProcessId(fg, out _);
                if (tid != 0)
                {
                    var gti = new GUITHREADINFO
                    {
                        cbSize = Marshal.SizeOf<GUITHREADINFO>(),
                    };
                    if (GetGUIThreadInfo(tid, ref gti))
                    {
                        foreach (var h in new[] { gti.hwndFocus, gti.hwndCaret, gti.hwndActive })
                            if (h != IntPtr.Zero && !targets.Contains(h)) targets.Add(h);
                    }
                }
                if (!targets.Contains(fg)) targets.Add(fg);
            }
        }
        catch { }   /* 预期内：探测/重试型，失败即视为无结果 */

        bool any = false;
        foreach (var h in targets)
        {
            try
            {
                if (PostMessage(h, WM_COPY, IntPtr.Zero, IntPtr.Zero)) any = true;
            }
            catch { }   /* 预期内：探测/重试型，失败即视为无结果 */
        }
        return any;
    }
}
