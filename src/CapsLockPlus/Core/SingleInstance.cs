using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CapsLockPlus.Core;

/// <summary>
/// 单实例控制：用命名互斥体 + 唤醒已有实例。
///
/// 这里有两个必须处理的坑（都曾导致“程序启动了但什么都没发生”）：
///
/// 1. 不要用 "Global\" 前缀。
///    Global 命名对象对普通权限进程可能不可访问：非管理员进程去打开
///    管理员创建的 Global 互斥体会直接抛 UnauthorizedAccessException。
///    用默认作用域即可满足“同一登录会话单实例”。
///
/// 2. 必须捕获异常。
///    拿不到互斥体时不能把异常抛到启动流程里，否则进程会在装钩子之前就退出。
/// </summary>
public static class SingleInstance
{
    private const string MutexName = "CapsLockPlus.SingleInstance.7E3A-4C19-9F42-BD08B4E6A1C7";

    /// <summary>
    /// 互斥体名。设了环境变量 <c>CAPSLOCKPLUS_MUTEX_SUFFIX</c> 就加后缀 ——
    /// 用于并行测试多个实例（比如一边跑稳定版、一边跑调试版）。
    /// </summary>
    private static string EffectiveMutexName
    {
        get
        {
            string? suffix = Environment.GetEnvironmentVariable("CAPSLOCKPLUS_MUTEX_SUFFIX");
            return string.IsNullOrWhiteSpace(suffix) ? MutexName : MutexName + "." + suffix;
        }
    }
    private static Mutex? _mutex;
    private static bool _owned;

    /// <summary>
    /// 尝试成为唯一实例。
    /// 返回 true 表示当前进程应当继续运行；false 表示已有实例在运行。
    ///
    /// 会先等一段时间再判定“已有实例”。
    /// 原因是提权重启：旧进程发起 runas 之后立刻 Environment.Exit，
    /// 新进程几乎同时启动并尝试获取互斥体。如果不等，就会在旧进程
    /// 尚未释放互斥体时误判为“已有实例”而退出 —— 结果是两个进程都没了，
    /// 钩子没装上，表现就是“按什么键都没反应”。
    /// </summary>
    public static bool TryAcquire(out bool alreadyRunning)
    {
        alreadyRunning = false;

        // 先尝试直接获取；失败则等待旧实例退出，最多等 6 秒
        var deadline = Environment.TickCount64 + 6000;
        while (true)
        {
            try
            {
                _mutex = new Mutex(true, EffectiveMutexName, out bool created);
                _owned = created;
                if (created) return true;

                // 已存在：可能是刚重启的旧实例，等它退出
                if (Environment.TickCount64 >= deadline)
                {
                    alreadyRunning = true;
                    return false;
                }

                // 句柄留着没用，先释放再重试
                _mutex.Dispose();
                _mutex = null;
                Thread.Sleep(120);
            }
            catch (UnauthorizedAccessException)
            {
                // 互斥体被别的权限级别的实例持有。
                // 典型场景：当前以普通权限启动，而已有一个提权实例在运行 ——
                // 这种情况确实应当判定为“已有实例”。
                CoreLog.Warn("互斥体访问被拒绝，判定为已有实例在运行");
                alreadyRunning = true;
                return false;
            }
            catch (Exception ex)
            {
                // 其它异常（例如名称非法）：宁可放行，也不要不让用户用
                CoreLog.Warn("创建互斥体失败，按单实例放行：" + ex.Message);
                alreadyRunning = false;
                return true;
            }
        }
    }

    public static void Release()
    {
        try
        {
            if (_owned) _mutex?.ReleaseMutex();
            _mutex?.Dispose();
        }
        catch { }
        _mutex = null;
        _owned = false;
    }

    /// <summary>把已经运行的实例唤到前台。</summary>
    public static void ActivateExisting()
    {
        try
        {
            var current = Process.GetCurrentProcess();
            foreach (var p in Process.GetProcessesByName(current.ProcessName))
            {
                if (p.Id == current.Id) continue;
                if (p.MainWindowHandle != IntPtr.Zero)
                {
                    ShowWindow(p.MainWindowHandle, 9);
                    SetForegroundWindow(p.MainWindowHandle);
                }
                else
                {
                    // 窗口在托盘里：广播一个自定义消息让老实例自己弹出来
                    PostMessage(new IntPtr(0xFFFF), WmShowWindow, IntPtr.Zero, IntPtr.Zero);
                }
            }
        }
        catch (Exception ex)
        {
            CoreLog.Warn("唤醒已有实例失败：" + ex.Message);
        }
    }

    public const int WmShowWindow = 0x8000 + 0x501;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}
