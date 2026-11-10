using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using CapsLockPlus.Core.Input;
using static CapsLockPlus.Core.Interop.NativeMethods;

namespace CapsLockPlus.Core;

/// <summary>
/// 独立钩子自检（<c>--hooktest</c>）：只装钩子，自己敲两下，数回调。
///
/// 为什么单独做一条命令：<c>--diag</c> 里的"钩子自测"装的是它**自己**的钩子，
/// 通过与否和程序实际用的 <see cref="KeyboardHook"/> 不是一回事。
/// 排查"钩子句柄在、却一个事件都收不到"时，需要一条只包含钩子本身的最小路径。
///
/// 输出走 <see cref="CliOutput"/>：本程序是窗口型 exe，直接 <c>Console.WriteLine</c>
/// 在 PowerShell 里既不会等待也不会显示 —— 结果会"一点反应都没有"。
/// </summary>
internal static class HookSelfTest
{

    public static int Run()
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== 钩子独立自检 ===");
        sb.AppendLine($"时间      : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"管理员权限: {(Core.ElevationHelper.IsElevated ? "是" : "否")}");
        sb.AppendLine($"前台窗口  : 0x{GetForegroundWindow().ToInt64():X}");
        sb.AppendLine();

        using var hook = new KeyboardHook();
        hook.Start();

        sb.AppendLine($"安装          : {(hook.IsRunning ? "成功" : "失败")}");
        sb.AppendLine($"句柄非零      : {hook.HookInstalled}");
        sb.AppendLine($"鼠标钩子      : {hook.MouseHookInstalled}");
        sb.AppendLine($"探活 ping     : {(hook.Ping() ? "有响应" : "无响应")}");
        sb.AppendLine();

        int total = 0;
        for (int round = 1; round <= 3; round++)
        {
            long before = hook.KeyEventCount;
            keybd_event(0x87, 0, 0, UIntPtr.Zero);        // F24 按下（不带标记）
            Thread.Sleep(40);
            keybd_event(0x87, 0, 2, UIntPtr.Zero);        // 抬起
            Thread.Sleep(500);

            long after = hook.KeyEventCount;
            long delta = after - before;
            total += (int)delta;
            sb.AppendLine($"第 {round} 轮：注入前={before} 注入后={after} 增量={delta}" +
                          $"  探活={(hook.Ping() ? "有响应" : "无响应")}" +
                          $"  最长回调={hook.MaxCallbackCostMs:F0}ms");
        }

        sb.AppendLine();
        sb.AppendLine(total > 0 ? "结论：钩子能收到按键 ✓" : "结论：钩子收不到任何按键 ✗");
        if (total == 0)
        {
            sb.AppendLine();
            sb.AppendLine("说明：连程序自己注入的按键都收不到，说明这台机器上有东西");
            sb.AppendLine("（安全软件 / 输入过滤驱动 / 系统策略）拦住了低级键盘钩子。");
            sb.AppendLine("下一步：把本程序加入安全软件白名单，或改用普通权限再试一次。");
        }

        CliOutput.Report("按键增强 · 钩子自检", "hooktest.txt", sb.ToString());
        return total > 0 ? 0 : 1;
    }
}
