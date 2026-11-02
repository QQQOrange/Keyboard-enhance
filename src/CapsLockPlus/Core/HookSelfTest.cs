using System;
using System.Runtime.InteropServices;
using System.Threading;
using CapsLockPlus.Core.Input;

namespace CapsLockPlus.Core;

/// <summary>
/// 独立钩子自检（<c>--hooktest</c>）：只装钩子，自己敲两下，数回调。
///
/// 为什么单独做一条命令：<c>--diag</c> 里的"钩子自测"装的是它**自己**的钩子，
/// 通过与否和程序实际用的 <see cref="KeyboardHook"/> 不是一回事。
/// 排查"钩子句柄在、却一个事件都收不到"时，需要一条只包含钩子本身的最小路径。
/// </summary>
internal static class HookSelfTest
{
    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    public static int Run()
    {
        Console.WriteLine("=== 钩子独立自检 ===");
        Console.WriteLine($"前台窗口: 0x{GetForegroundWindow().ToInt64():X}");

        using var hook = new KeyboardHook();
        hook.Start();

        Console.WriteLine($"安装: {(hook.IsRunning ? "成功" : "失败")}  句柄非零={hook.HookInstalled}  鼠标钩子={hook.MouseHookInstalled}");
        Console.WriteLine($"探活 ping: {(hook.Ping() ? "有响应" : "无响应")}");

        for (int round = 1; round <= 3; round++)
        {
            long before = hook.KeyEventCount;
            keybd_event(0x87, 0, 0, UIntPtr.Zero);        // F24 按下（不带标记）
            Thread.Sleep(40);
            keybd_event(0x87, 0, 2, UIntPtr.Zero);        // 抬起
            Thread.Sleep(500);

            long after = hook.KeyEventCount;
            Console.WriteLine($"第 {round} 轮：注入前={before} 注入后={after} 增量={after - before}" +
                              $"  探活={(hook.Ping() ? "有响应" : "无响应")}" +
                              $"  最长回调={hook.MaxCallbackCostMs:F0}ms");
        }

        int total = (int)hook.KeyEventCount;
        Console.WriteLine(total > 0 ? "结论：钩子能收到按键" : "结论：钩子收不到任何按键");
        return total > 0 ? 0 : 1;
    }
}
