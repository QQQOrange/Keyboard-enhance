using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CapsLockPlus.Core;

/// <summary>单实例控制：用命名互斥体 + 窗口消息唤醒已有实例。</summary>
public static class SingleInstance
{
    private const string MutexName = @"Global\CapsLockPlus.SingleInstance.7E3A";
    private static Mutex? _mutex;

    public static bool TryAcquire(out bool alreadyRunning)
    {
        _mutex = new Mutex(true, MutexName, out bool created);
        alreadyRunning = !created;
        return created;
    }

    public static void Release()
    {
        try
        {
            _mutex?.ReleaseMutex();
            _mutex?.Dispose();
        }
        catch { }
        _mutex = null;
    }

    /// <summary>把已经运行的实例唤到前台。</summary>
    public static void ActivateExisting()
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

    public const int WmShowWindow = 0x8000 + 0x501;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}
