using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using CapsLockPlus.Core;

namespace CapsLockPlus.Core.Input;

/// <summary>
/// 合成输入的"善后"工具。
///
/// 为什么需要这个类：我们发的是真实的 SendInput 事件，系统按"按下 / 抬起"成对记账。
/// 只要有一次抬起没发出去（进程被结束、异常跳过了 finally、目标程序卡住），
/// Windows 就会一直认为那个键/鼠标键处于按下状态 ——
/// 表现是 **Win 键粘住**（点什么都开开始菜单）、**Ctrl 粘住**（点击变成多选）、
/// 甚至 **鼠标左键粘住**（整台机器像一直按着鼠标）。用户很难自己恢复。
///
/// 所以：启动时、退出时、暂停时、以及后台巡检发现"疑似粘键"时，主动补发抬起。
/// 补发一个本来就没按下的键抬起是无害的 —— 系统只会忽略它。
/// </summary>
public static class InputSafety
{
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, UIntPtr dwExtraInfo);

    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;

    private const int VK_LBUTTON = 0x01;
    private const int VK_RBUTTON = 0x02;
    private const int VK_MBUTTON = 0x04;
    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;

    /// <summary>这些键粘住对系统影响最大：修饰键 + 三个鼠标键。</summary>
    private static readonly int[] GuardedKeys =
    {
        VK_CONTROL, 0xA2, 0xA3,      // Ctrl / LCtrl / RCtrl
        VK_SHIFT, 0xA0, 0xA1,        // Shift / LShift / RShift
        VK_MENU, 0xA4, 0xA5,         // Alt / LAlt / RAlt
        VK_LWIN, VK_RWIN,
    };

    private static readonly int[] GuardedMouse = { VK_LBUTTON, VK_RBUTTON, VK_MBUTTON };

    /// <summary>
    /// 把"系统认为按着"的修饰键补一次抬起。
    ///
    /// **只发真的按着的**：不论状态一律发一遍看起来更保险，实测却会在
    /// 某些机器上让合成输入和渲染/输入法互相卡住（一次 12 个事件全发出去，
    /// 钩子线程和界面线程一起排队）。只处理确实按下的是等价的效果，代价小得多。
    /// </summary>
    public static void ReleaseModifiers()
    {
        /* keybd_event 而不是 SendInput：要的是"最原始的抬键"，
         * 不参与我们自己的注入标记判断，也不会被自己的钩子当成用户按键。 */
        foreach (int vk in GuardedKeys)
        {
            try
            {
                if (!IsDown(vk)) continue;
                keybd_event((byte)vk, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            }
            catch (Exception ex) { CoreLog.Swallow(ex, "补发修饰键抬起"); }
        }
    }

    /// <summary>补发鼠标键的抬起（同样只处理真的按着的）。</summary>
    public static void ReleaseMouseButtons()
    {
        try
        {
            if (IsDown(VK_LBUTTON)) mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
            if (IsDown(VK_RBUTTON)) mouse_event(MOUSEEVENTF_RIGHTUP, 0, 0, 0, UIntPtr.Zero);
            if (IsDown(VK_MBUTTON)) mouse_event(MOUSEEVENTF_MIDDLEUP, 0, 0, 0, UIntPtr.Zero);
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "补发鼠标抬起");
        }
    }

    public static void ReleaseAll()
    {
        ReleaseModifiers();
        ReleaseMouseButtons();
    }

    /// <summary>当前物理上是否真的按着（用于巡检判断）。</summary>
    public static bool IsDown(int vk)
    {
        try { return (GetAsyncKeyState(vk) & 0x8000) != 0; }
        catch { return false; }
    }

    /// <summary>当前有哪些受关注的键处于"系统认为按下"的状态（诊断 / 日志用）。</summary>
    public static string DescribeHeld()
    {
        var parts = new List<string>();

        foreach (int vk in GuardedKeys)
            if (IsDown(vk)) parts.Add(KeyName.NameOf(vk));

        foreach (int vk in GuardedMouse)
            if (IsDown(vk)) parts.Add(vk switch
            {
                VK_LBUTTON => "MouseLeft",
                VK_RBUTTON => "MouseRight",
                _ => "MouseMiddle",
            });

        return parts.Count == 0 ? "" : string.Join(",", parts);
    }

    /// <summary>
    /// 巡检：如果某个修饰键/鼠标键"系统认为按着"，但引擎已经很久没收到任何按键，
    /// 就认为它是我们造成的粘键，补一次抬起。
    ///
    /// 阈值取 20 秒：正常人手按住 Shift 选文字不会超过 20 秒不动，
    /// 而真的粘键时用户会在几秒内就发现异常 —— 20 秒足够安全。
    /// </summary>
    public static bool HealIfStuck(long lastKeyEventUnixMs, bool anyKeyOwned, out string healed)
    {
        healed = "";

        long idleMs = Environment.TickCount64 - lastKeyEventUnixMs;
        if (idleMs < 20_000) return false;

        var held = DescribeHeld();
        if (held.Length == 0) return false;

        /* 用户正按着某个键且引擎确实收到过它的按下（anyKeyOwned）→ 不动，
         * 避免把人手正在按的键抢着松开。 */
        if (anyKeyOwned) return false;

        ReleaseAll();
        healed = held;

        CoreLog.Warn($"检测到疑似粘键（{held}），已补发抬起");
        return true;
    }
}
