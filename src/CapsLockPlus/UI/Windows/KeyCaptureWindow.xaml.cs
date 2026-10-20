using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using CapsLockPlus.Core;
using CapsLockPlus.Core.Input;
using CapsLockPlus.Core.Interop;

namespace CapsLockPlus.UI.Windows;

/// <summary>
/// 自定义按键捕获窗口。
///
/// 打开后用户直接按目标键，窗口用全局低级键盘钩子捕获，并把键名/虚拟键码
/// 显示出来供确认。这样键盘上不显示的键（小键盘、媒体键、浏览器键等）也能绑定。
///
/// 为什么用钩子而不是 PreviewKeyDown：WPF 的键盘事件拿不到
/// 有些扩展键的原始 VK（例如 VK_BROWSER_BACK = 0xA6），
/// 而组合键引擎需要的就是 VK。
/// </summary>
public partial class KeyCaptureWindow : Window
{
    private const int WH_KEYBOARD_LL = 13;

    private IntPtr _hook = IntPtr.Zero;
    private HookProc? _proc;

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);

    /// <summary>用户选中的键名（KeyName.NameOf 的形式）；取消时为空。</summary>
    public string? CapturedKey { get; private set; }

    /// <summary>捕获到的虚拟键码。</summary>
    public int CapturedVk { get; private set; }

    public KeyCaptureWindow()
    {
        InitializeComponent();
        _proc = HookCallback;
        Loaded += (_, _) =>
        {
            _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
            if (_hook == IntPtr.Zero)
            {
                PromptText.Text = "无法安装键盘钩子";
                DetailText.Text = "请检查是否有安全软件拦截全局钩子。";
                CoreLog.Warn($"自定义按键捕获：钩子安装失败（错误码 {Marshal.GetLastWin32Error()}）");
            }
            Activate();
            Focus();
        };
        Closed += (_, _) =>
        {
            if (_hook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
            }
        };
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = (int)wParam;
            // 只在按下时捕获（WM_KEYDOWN / WM_SYSKEYDOWN）
            if (msg is 0x0100 or 0x0104)
            {
                var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                int vk = (int)data.vkCode;

                // Esc = 取消
                if (vk == 0x1B)
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        CapturedKey = null;
                        Close();
                    });
                    return (IntPtr)1;   // 吞掉，避免 Esc 关掉父窗口
                }

                string? name = KeyName.NameOf(vk);
                Dispatcher.BeginInvoke(() =>
                {
                    CapturedVk = vk;
                    CapturedKey = name;

                    PromptText.Text = name == null ? $"未识别按键（VK 0x{vk:X2}）" : name;
                    DetailText.Text = name == null
                        ? "这个键没有内置名称，暂时不能绑定。换一个键试试。"
                        : $"虚拟键码 0x{vk:X2}　·　按「确定绑定」生效";
                    ConfirmButton.IsEnabled = name != null;
                });

                return (IntPtr)1;   // 吞掉，不让按键传到别的窗口
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(CapturedKey))
        {
            PromptText.Text = "先按一个键";
            return;
        }
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        CapturedKey = null;
        DialogResult = false;
        Close();
    }
}
