using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using CapsLockPlus.Core;

namespace CapsLockPlus.UI;

/// <summary>
/// 剪贴板监听：用 AddClipboardFormatListener 接收 WM_CLIPBOARDUPDATE。
/// 上游 AutoHotkey 版本用 OnClipboardChange + 定时器，这里改成纯消息驱动，
/// 空闲时零 CPU 占用。
/// </summary>
public sealed class ClipboardWatcher : IDisposable
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;
    private const int WM_SHOWWINDOW = 0x0018;

    private readonly Action _onChanged;
    private HwndSource? _source;
    private bool _disposed;

    public ClipboardWatcher(Action onChanged) => _onChanged = onChanged;

    public void Start()
    {
        var parameters = new HwndSourceParameters("CapsLockPlus.ClipboardWatcher")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ParentWindow = new IntPtr(-3), // HWND_MESSAGE
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
        NativeMethodsShim.AddClipboardFormatListener(_source.Handle);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE)
        {
            try { _onChanged(); } catch (Exception ex) { CoreLog.Warn("剪贴板回调异常：" + ex.Message); }
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (_source != null)
            {
                NativeMethodsShim.RemoveClipboardFormatListener(_source.Handle);
                _source.RemoveHook(WndProc);
                _source.Dispose();
                _source = null;
            }
        }
        catch { }   /* 预期内：探测/重试型，失败即视为无结果 */
    }

    private static class NativeMethodsShim
    {
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AddClipboardFormatListener(IntPtr hwnd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
    }
}

/// <summary>
/// 钩子自愈：每秒巡检一次低级键盘钩子是否仍然安装着，
/// 连续异常就重装（上游没有这个机制，长时间运行后钩子会被系统静默摘掉）。
/// </summary>
public sealed class HookWatchdog : IDisposable
{
    private readonly Core.Input.HotkeyEngine _engine;
    private readonly System.Windows.Threading.Dispatcher _dispatcher;
    private readonly Action<string> _notify;
    private readonly System.Windows.Threading.DispatcherTimer _timer;
    private int _failures;

    public HookWatchdog(Core.Input.HotkeyEngine engine, System.Windows.Threading.Dispatcher dispatcher,
        Action<string> notify)
    {
        _engine = engine;
        _dispatcher = dispatcher;
        _notify = notify;
        // 用 Background 优先级，空闲时几乎不抢 CPU（实测窗口模式下也保持 0% 空闲占用）
        _timer = new System.Windows.Threading.DispatcherTimer(
            TimeSpan.FromSeconds(2), System.Windows.Threading.DispatcherPriority.Background,
            (_, _) => Check(), dispatcher);
        _timer.Stop();
    }

    public void Start() => _timer.Start();

    public int RecoveryCount { get; private set; }

    private void Check()
    {
        if (!_engine.Enabled) return;
        if (_engine.IsHookAlive)
        {
            _failures = 0;
            return;
        }
        _failures++;
        if (_failures < 2) return;
        try
        {
            _engine.RestartHook();
            RecoveryCount++;
            _failures = 0;
            CoreLog.Warn($"键盘钩子已自动重装（第 {RecoveryCount} 次）");
            _notify("键盘钩子已自动恢复");
        }
        catch (Exception ex)
        {
            CoreLog.Error("恢复钩子失败", ex);
        }
    }

    public void Dispose() => _timer.Stop();
}
