using System.Diagnostics;
using System.Runtime.InteropServices;
using static CapsLockPlus.Core.Interop.NativeMethods;

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

    private static EventWaitHandle? _wakeEvent;
    private static Thread? _wakeThread;
    private static volatile bool _wakeStop;

    private static string WakeEventName => EffectiveMutexName + ".Wake";

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
    public static bool TryAcquire(out bool alreadyRunning, int waitMs = 1200)
    {
        alreadyRunning = false;

        // 先尝试直接获取；失败则等待旧实例退出（重启交接时由调用方给更长预算）
        var deadline = Environment.TickCount64 + Math.Max(200, waitMs);
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

    /// <summary>
    /// 主实例建立“唤醒事件”。第二个实例只要 Set 一下，就能让托盘里的主窗口出来：
    /// 比向 HWND_BROADCAST 发自定义消息可靠，也不受 UIPI / 隐藏窗口影响。
    /// </summary>
    public static void StartWakeListener(Action onWake)
    {
        if (_wakeEvent != null) return;
        try
        {
            _wakeEvent = new EventWaitHandle(false, EventResetMode.AutoReset, WakeEventName);
            _wakeStop = false;
            _wakeThread = new Thread(() =>
            {
                while (!_wakeStop)
                {
                    try
                    {
                        if (_wakeEvent.WaitOne(500) && !_wakeStop)
                            onWake();
                    }
                    catch { Thread.Sleep(100); }
                }
            })
            {
                IsBackground = true,
                Name = "CapsLockPlus.WakeListener",
            };
            _wakeThread.Start();
        }
        catch (Exception ex)
        {
            CoreLog.Warn("创建唤醒事件失败，仍使用窗口广播兜底：" + ex.Message);
            _wakeEvent?.Dispose();
            _wakeEvent = null;
        }
    }

    public static void StopWakeListener()
    {
        _wakeStop = true;
        try { _wakeEvent?.Set(); } catch { }
        _wakeEvent?.Dispose();
        _wakeEvent = null;
    }

    private static bool SignalWake()
    {
        try
        {
            using var ev = EventWaitHandle.OpenExisting(WakeEventName);
            ev.Set();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void Release()
    {
        // 退出/交接时先把唤醒监听停掉；否则新实例会与旧监听共享同名事件，
        // 出现“第二次启动时窗口有时出来、有时不出来”。
        StopWakeListener();
        try
        {
            if (_owned) _mutex?.ReleaseMutex();
            _mutex?.Dispose();
        }
        catch { }   /* 预期内：探测/重试型，失败即视为无结果 */
        _mutex = null;
        _owned = false;
    }

    /// <summary>把已经运行的实例唤到前台。</summary>
    public static void ActivateExisting()
    {
        try
        {
            bool signaled = SignalWake();
            using var current = Process.GetCurrentProcess();
            var processes = Process.GetProcessesByName(current.ProcessName);
            try
            {
                foreach (var p in processes)
                {
                    if (p.Id == current.Id) continue;
                    if (p.MainWindowHandle != IntPtr.Zero)
                    {
                        ShowWindow(p.MainWindowHandle, 9);
                        SetForegroundWindow(p.MainWindowHandle);
                    }
                    else if (!signaled)
                    {
                        // 没有唤醒事件（老版本 / 权限不同）：退回自定义消息广播
                        PostMessage(new IntPtr(0xFFFF), WmShowWindow, IntPtr.Zero, IntPtr.Zero);
                    }
                }
            }
            finally
            {
                foreach (var p in processes) p.Dispose();
            }
        }
        catch (Exception ex)
        {
            CoreLog.Warn("唤醒已有实例失败：" + ex.Message);
        }
    }

    public const int WmShowWindow = 0x8000 + 0x501;

}
