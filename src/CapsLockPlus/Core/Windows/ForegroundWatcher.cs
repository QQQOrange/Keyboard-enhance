using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace CapsLockPlus.Core.Windows;

/// <summary>
/// 前台窗口监听：当前前台窗口属于哪个进程。
///
/// 用于「按应用自动启用层」——切到 IDE 自动叠加开发层，切回浏览器自动回基础层。
///
/// ## 为什么用 SetWinEventHook 而不是轮询
///
/// 轮询要么太频繁（耗电），要么太迟钝（切过去半秒还不对）。
/// `EVENT_SYSTEM_FOREGROUND` 是系统主动通知，代价接近零。
///
/// ## 隐私
///
/// **只取进程名**（`code.exe`），不取窗口标题、不取路径、不上传。
/// 这一点在设置页也写明了 —— 监听前台容易被误读成"监视"。
/// </summary>
public sealed class ForegroundWatcher : IDisposable
{
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
    private const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    private delegate void WinEventDelegate(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod,
        WinEventDelegate proc, uint pid, uint tid, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int access, bool inherit, uint pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr h);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr proc, int flags,
        System.Text.StringBuilder name, ref int size);

    /// <summary>前台进程变化（参数是规范化的进程名，如 code.exe）。</summary>
    public event Action<string>? Changed;

    /// <summary>当前前台进程名；取不到时为空串。</summary>
    public string Current { get; private set; } = "";

    private WinEventDelegate? _proc;     // 必须保存引用，否则会被 GC 回收导致回调消失
    private IntPtr _hook = IntPtr.Zero;

    public bool Start()
    {
        if (_hook != IntPtr.Zero) return true;

        _proc = OnWinEvent;
        _hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _proc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);

        if (_hook == IntPtr.Zero)
        {
            CoreLog.Warn("前台窗口监听安装失败（按应用自动切层将不可用）");
            return false;
        }

        Refresh();      // 先按当前前台算一次，避免"必须先切一次窗口才生效"
        return true;
    }

    /// <summary>重新读一次当前前台（设置里改了规则后调用）。</summary>
    public void Refresh()
    {
        string name = ProcessNameOf(GetForegroundWindow());
        if (name.Length == 0) return;

        Current = name;
        try { Changed?.Invoke(name); } catch (Exception ex) { CoreLog.Warn("前台变更处理失败：" + ex.Message); }
    }

    private void OnWinEvent(IntPtr hook, uint evt, IntPtr hwnd,
        int idObject, int idChild, uint thread, uint time)
    {
        // 只关心窗口本身（会话里的 OBJECTID_SELF），过滤掉菜单/光标等子对象
        if (idObject != 0 || idChild != 0) return;

        string name = ProcessNameOf(hwnd);
        if (name.Length == 0) return;

        Current = name;
        try { Changed?.Invoke(name); } catch (Exception ex) { CoreLog.Warn("前台变更处理失败：" + ex.Message); }
    }

    /// <summary>取窗口所属进程名（小写，含 .exe）。</summary>
    public static string ProcessNameOf(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "";

        try
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return "";

            IntPtr proc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (proc == IntPtr.Zero) return "";

            try
            {
                var sb = new System.Text.StringBuilder(1024);
                int size = sb.Capacity;
                if (!QueryFullProcessImageName(proc, 0, sb, ref size)) return "";

                string path = sb.ToString(0, size);
                string name = Path.GetFileName(path);
                return name.ToLowerInvariant();
            }
            finally { CloseHandle(proc); }
        }
        catch { return ""; }
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            try { UnhookWinEvent(_hook); } catch { }
            _hook = IntPtr.Zero;
        }
        _proc = null;
    }
}
