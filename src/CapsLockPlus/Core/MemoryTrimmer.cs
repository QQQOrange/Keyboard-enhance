using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CapsLockPlus.Core;

/// <summary>
/// 空闲内存回收。
///
/// WPF 在首次渲染界面时会申请一批临时的渲染缓冲与位图，这些页在界面稳定后
/// 仍然驻留在工作集里，任务管理器里看起来会“很占内存”。窗口显示后调用
/// SetProcessWorkingSetSize(-1,-1) 可以让系统把这些暂时不用的页换出去，
/// 常驻内存会明显下降，而再次打开界面时系统会按需换回来，不影响使用。
/// </summary>
public static class MemoryTrimmer
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessWorkingSetSize(IntPtr hProcess, IntPtr dwMinimumWorkingSetSize,
        IntPtr dwMaximumWorkingSetSize);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    private static DateTime _lastTrim = DateTime.MinValue;

    /// <summary>把当前进程的工作集收缩到最小（内部有 30 秒节流）。</summary>
    public static void Trim(bool force = false)
    {
        if (!force && (DateTime.Now - _lastTrim).TotalSeconds < 30) return;
        _lastTrim = DateTime.Now;
        try
        {
            using var p = Process.GetCurrentProcess();
            if (SetProcessWorkingSetSize(p.Handle, new IntPtr(-1), new IntPtr(-1)))
            {
                CoreLog.Info("已回收空闲内存页");
            }
            else
            {
                // 某些受管环境（沙箱、组策略）会拒绝这个调用，属于可忽略的非致命情况
                CoreLog.Info($"回收内存页被系统拒绝（错误码 {Marshal.GetLastWin32Error()}），继续正常运行");
            }
        }
        catch (Exception ex)
        {
            CoreLog.Info("回收内存页跳过：" + ex.Message);
        }
    }
}
