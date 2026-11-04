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

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

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
                InputSender.KeyUp(vk);
            }
            catch (Exception ex) { CoreLog.Swallow(ex, "补发修饰键抬起"); }
        }
    }

    /// <summary>补发鼠标键的抬起（同样只处理真的按着的）。</summary>
    public static void ReleaseMouseButtons()
    {
        try
        {
            if (IsDown(VK_LBUTTON)) InputSender.MouseUp(VK_LBUTTON);
            if (IsDown(VK_RBUTTON)) InputSender.MouseUp(VK_RBUTTON);
            if (IsDown(VK_MBUTTON)) InputSender.MouseUp(VK_MBUTTON);
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "补发鼠标抬起");
        }
    }

    /// <summary>受关注的修饰键（巡检用）。</summary>
    public static IReadOnlyList<int> WatchedModifiers => GuardedKeys;

    /// <summary>
    /// 补发单个键的抬起（调用方已经确认它确实按着）。
    ///
    /// ⚠ 必须走 <see cref="InputSender.KeyUp"/>（带 InjectionTag），**不能**用裸
    /// `keybd_event`：裸事件在我们自己的钩子看来是"用户真的按下的键"，
    /// 于是会被基础改键的 up 分支吞掉并重新映射成别的键 ——
    /// 如果 lalt 是某条改键的源键，善后发出的每一次抬起都会被吞，
    /// 系统的 Alt 状态永远清不掉（现场表现为日志每 5 秒重试一次、连续 2393 次都失败）。
    /// 带标记的注入在钩子里走 injected 分支直接放行，改键逻辑碰不到它。
    /// </summary>
    public static void ReleaseKey(int vk)
    {
        try { InputSender.KeyUp(vk); }
        catch (Exception ex) { CoreLog.Swallow(ex, "补发单个键抬起"); }
    }

    public static void ReleaseAll()
    {
        ReleaseModifiers();
        ReleaseMouseButtons();
    }

    /// <summary>
    /// 系统层面的"无输入"时长（毫秒）。
    ///
    /// 为什么不用我们自己的按键时间戳：鼠标移动、点击、滚轮都不产生键盘事件，
    /// 用户拖着一个窗口拖了 30 秒，在"只看键盘事件"的判据里就是"空闲 30 秒"，
    /// 于是巡检会把人家正按着的鼠标键强行松开。GetLastInputInfo 才是"整机有没有人在操作"。
    /// </summary>
    public static long SystemIdleMs
    {
        get
        {
            try
            {
                var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
                if (!GetLastInputInfo(ref info)) return 0;

                /* dwTime 是 32 位的 tick，溢出后要按无符号减法算差值。 */
                uint now = (uint)Environment.TickCount;
                return (uint)(now - info.dwTime);
            }
            catch { return 0; }
        }
    }

    private static long _lastHealLogAt;

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

        /* 通用码和左右码会同时为真（VK_MENU 与 VK_LMENU），去重后再输出，
         * 否则日志里是 "alt,lalt"，看起来像两个键都粘住了。 */
        return parts.Count == 0 ? "" : string.Join(",", parts.Distinct());
    }

    /// <summary>
    /// 巡检：如果某个修饰键/鼠标键"系统认为按着"，但引擎已经很久没收到任何按键，
    /// 就认为它是我们造成的粘键，补一次抬起。
    ///
    /// 阈值取 20 秒：正常人手按住 Shift 选文字不会超过 20 秒不动，
    /// 而真的粘键时用户会在几秒内就发现异常 —— 20 秒足够安全。
    /// </summary>
    public static bool HealIfStuck(long lastKeyEventUnixMs, out string healed)
    {
        healed = "";

        /* 判据：**整机** 30 秒没有任何输入（键盘、鼠标、滚轮都算）。
         *
         * 为什么不看引擎自己的按键时间戳、也不看"引擎认为有没有按着"：
         * 抬起丢失时，引擎的物理表会一直留着那个键 —— 于是"还有键按着"永远成立，
         * 善后永远不会触发（现场就是这个死循环）。整机 30 秒零输入才是可靠证据：
         * 真人有按键动作就一定会有输入事件，一条都没有 = 状态卡住了。
         *
         * 阈值可用 CLP_HEAL_IDLE_MS 覆盖（只在诊断 / 自检时用）。 */
        long threshold = 30_000;
        string? env = Environment.GetEnvironmentVariable("CLP_HEAL_IDLE_MS");
        if (long.TryParse(env, out long custom) && custom >= 500) threshold = custom;

        if (SystemIdleMs < threshold) return false;

        long idleMs = Environment.TickCount64 - lastKeyEventUnixMs;
        if (idleMs < Math.Min(threshold, 5_000)) return false;

        var held = DescribeHeld();
        if (held.Length == 0) return false;

        ReleaseAll();
        healed = held;

        /* 每 60 秒最多记一条：一直清不掉时（说明另有原因）不要每 5 秒刷一行。 */
        long now = Environment.TickCount64;
        if (now - _lastHealLogAt >= 60_000)
        {
            _lastHealLogAt = now;
            CoreLog.Warn($"检测到疑似粘键（{held}），已补发抬起");
        }

        return true;
    }
}
