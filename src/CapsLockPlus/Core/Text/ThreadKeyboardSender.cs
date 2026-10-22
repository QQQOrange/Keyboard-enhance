using System.Runtime.InteropServices;

namespace CapsLockPlus.Core.Text;

/// <summary>
/// **线程级**键盘注入。
///
/// 存在的理由：有些程序（Electron / 部分国产客户端）会过滤
/// <c>SendInput</c> 造出来的合成按键 —— 它们在系统里带 LLKHF_INJECTED
/// 标记，容易被当成"非真实输入"而忽略。
///
/// 线程级注入走的是另一条路：把自己的输入队列 **attach** 到目标线程，
/// 让目标程序"以为"按键就是它自己队列里的。配合
/// <see cref="SetKeyboardState"/> 把 Ctrl 的真实状态写进去，
/// 目标程序用 GetKeyState 判断修饰键时才能读到正确结果。
///
/// 注意：这是**尽力而为**的兜底通道，失败就失败，不影响其它路径。
/// </summary>
public static class ThreadKeyboardSender
{
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetKeyboardState(byte[] lpKeyState);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetKeyboardState(byte[] lpKeyState);

    [DllImport("user32.dll")]
    private static extern void PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern ushort MapVirtualKey(uint uCode, uint uMapType);

    private const uint WM_KEYDOWN = 0x0100;
    private const uint WM_KEYUP = 0x0101;
    private const int VK_CONTROL = 0x11;
    private const int VK_C = 0x43;
    private const int VK_INSERT = 0x2D;
    private const uint MAPVK_VK_TO_VSC = 0;

    /// <summary>
    /// 用线程级注入发一次 Ctrl+&lt;vk&gt;。
    ///
    /// 步骤（参考 QuickSearch 的 <c>post_ctrl_c_to_focus</c>）：
    ///   1. attach 到前台线程的输入队列
    ///   2. 保存并修改键盘状态（把 Ctrl 置为按下）
    ///   3. PostMessage 发 WM_KEYDOWN / WM_KEYUP
    ///   4. detach
    /// </summary>
    public static bool SendCtrlChord(int vk)
    {
        IntPtr fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;

        uint fgTid = GetWindowThreadProcessId(fg, out _);
        if (fgTid == 0) return false;

        uint myTid = GetCurrentThreadId();
        if (fgTid == myTid) return false;

        // ① attach
        bool attached = AttachThreadInput(myTid, fgTid, true);
        byte[] saved = new byte[256];
        bool haveSaved = false;
        try
        {
            haveSaved = GetKeyboardState(saved);
            if (attached && haveSaved)
            {
                byte[] down = (byte[])saved.Clone();
                down[VK_CONTROL] = 0x80;   // 高位置 1 = 按下
                SetKeyboardState(down);
            }
        }
        catch { }   /* 预期内：探测/重试型，失败即视为无结果 */

        // ② 发按键（带扫描码的 lParam）
        bool any = false;
        try
        {
            uint scan = MapVirtualKey((uint)vk, MAPVK_VK_TO_VSC);
            IntPtr dnLParam = new IntPtr((int)(1 | (scan << 16)));
            IntPtr upLParam = new IntPtr((int)(1 | (scan << 16) | (0xC0000000u)));

            PostMessage(fg, WM_KEYDOWN, new IntPtr(vk), dnLParam);
            any = true;
            System.Threading.Thread.Sleep(10);
            PostMessage(fg, WM_KEYUP, new IntPtr(vk), upLParam);
        }
        catch { }   /* 预期内：探测/重试型，失败即视为无结果 */

        // ③ 还原键盘状态并 detach
        try
        {
            if (attached && haveSaved) SetKeyboardState(saved);
            if (attached) AttachThreadInput(myTid, fgTid, false);
        }
        catch { }   /* 预期内：探测/重试型，失败即视为无结果 */

        return any;
    }
}
