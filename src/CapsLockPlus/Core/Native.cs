using System;
using System.Runtime.InteropServices;
using static CapsLockPlus.Core.Interop.NativeMethods;

namespace CapsLockPlus.Core;

/// <summary>
/// 少量 Win32 调用（集中一处，方便审计与测试）。
/// </summary>
internal static class Native
{

    /// <summary>
    /// 让系统回收本进程的工作集（内存占用会明显下降，代价是下次访问要重新缺页）。
    ///
    /// 参数传 -1 / -1 是官方推荐的"尽量多地回收"写法；
    /// 调用失败无副作用，也**不能**影响功能，所以全部吞掉。
    /// </summary>
    internal static void TrimWorkingSet(IntPtr processHandle)
    {
        try
        {
            SetProcessWorkingSetSize(processHandle, new IntPtr(-1), new IntPtr(-1));
        }
        catch
        {
            // 预期内：只是个优化，失败就算了
        }
    }
}
