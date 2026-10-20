using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using CapsLockPlus.Core;
using CapsLockPlus.Core.Input;
using KeyModifiers = CapsLockPlus.Core.Input.KeyModifiers;

namespace CapsLockPlus.UI.Windows;

/// <summary>
/// 自定义按键捕获窗口（支持组合键）。
///
/// 打开后用户直接按目标组合键，例如 Ctrl+Shift+K、Alt+D。
/// 用全局低级键盘钩子捕获：修饰键按下时实时高亮，
/// 松开非修饰键的那一刻定格组合并等待确认。
///
/// 为什么用钩子而不是 PreviewKeyDown：WPF 键盘事件拿不到
/// 浏览器键、媒体键等扩展键的原始 VK，而这些恰是需要自定义的场景。
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

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    /// <summary>某个虚拟键当前是否处于按下状态。</summary>
    private static bool IsKeyDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    // 修饰键的虚拟键码
    private const int VK_CTRL_A = 0xA2, VK_CTRL_B = 0xA3;   // 左/右 Ctrl
    private const int VK_SHIFT_A = 0xA0, VK_SHIFT_B = 0xA1;
    private const int VK_ALT_A = 0xA4, VK_ALT_B = 0xA5;
    private const int VK_LWIN = 0x5B, VK_RWIN = 0x5C;
    private const int VK_ESC = 0x1B;

    /// <summary>捕获到的组合键（KeyName 形式，如 "ctrl+shift+k"）；取消时为空。</summary>
    public string? CapturedCombo { get; private set; }

    /// <summary>捕获到的主键名（不含修饰键）。</summary>
    public string? CapturedKey => CapturedCombo?.Contains('+') == true
        ? CapturedCombo[(CapturedCombo.LastIndexOf('+') + 1)..]
        : CapturedCombo;

    /// <summary>捕获到的修饰键。</summary>
    public KeyModifiers CapturedMods { get; private set; }

    /// <summary>是否捕获到了组合键（含修饰键才算组合）。</summary>
    public bool IsCombo => CapturedMods != KeyModifiers.None;

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

    private static bool IsModifier(int vk) => vk is
        VK_CTRL_A or VK_CTRL_B or VK_SHIFT_A or VK_SHIFT_B or
        VK_ALT_A or VK_ALT_B or VK_LWIN or VK_RWIN or 0x11 or 0x10 or 0x12;

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return CallNextHookEx(_hook, nCode, wParam, lParam);

        int msg = (int)wParam;
        bool down = msg is 0x0100 or 0x0104;      // WM_KEYDOWN / WM_SYSKEYDOWN
        bool up = msg is 0x0101 or 0x0105;        // WM_KEYUP / WM_SYSKEYUP
        if (!down && !up) return CallNextHookEx(_hook, nCode, wParam, lParam);

        var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
        int vk = (int)data.vkCode;

        // Esc 取消
        if (down && vk == VK_ESC)
        {
            Dispatcher.BeginInvoke(() =>
            {
                CapturedCombo = null;
                DialogResult = false;
                Close();
            });
            return (IntPtr)1;
        }

        if (IsModifier(vk))
        {
            // 修饰键：按下实时刷新高亮，松开刷新一次
            if (down || up)
            {
                Dispatcher.BeginInvoke(UpdateModifierBadges);
            }
            return (IntPtr)1;
        }

        if (down)
        {
            // 非修饰键按下 = 定格组合
            string? name = KeyName.NameOf(vk);
            var mods = ReadModifiers();
            string combo = BuildCombo(mods, name);

            Dispatcher.BeginInvoke(() =>
            {
                CapturedMods = mods;
                CapturedCombo = name == null ? null : combo;

                if (name == null)
                {
                    PromptText.Text = $"未识别按键（VK 0x{vk:X2}）";
                    DetailText.Text = "这个键没有内置名称，暂时不能绑定。换一个键试试。";
                    ConfirmButton.IsEnabled = false;
                    return;
                }

                PromptText.Text = combo;
                DetailText.Text = mods == KeyModifiers.None
                    ? $"单键 {name}　·　按「确定绑定」生效"
                    : $"组合键 {combo}　·　按「确定绑定」生效";
                ConfirmButton.IsEnabled = true;
            });

            return (IntPtr)1;
        }

        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private static KeyModifiers ReadModifiers()
    {
        var m = KeyModifiers.None;
        if (IsKeyDown(VK_CTRL_A) || IsKeyDown(VK_CTRL_B) || IsKeyDown(0x11)) m |= KeyModifiers.Ctrl;
        if (IsKeyDown(VK_SHIFT_A) || IsKeyDown(VK_SHIFT_B) || IsKeyDown(0x10)) m |= KeyModifiers.Shift;
        if (IsKeyDown(VK_ALT_A) || IsKeyDown(VK_ALT_B) || IsKeyDown(0x12)) m |= KeyModifiers.Alt;
        if (IsKeyDown(VK_LWIN) || IsKeyDown(VK_RWIN)) m |= KeyModifiers.Win;
        return m;
    }

    private static string BuildCombo(KeyModifiers mods, string? key)
    {
        var parts = new List<string>();
        if (mods.HasFlag(KeyModifiers.Ctrl)) parts.Add("ctrl");
        if (mods.HasFlag(KeyModifiers.Shift)) parts.Add("shift");
        if (mods.HasFlag(KeyModifiers.Alt)) parts.Add("alt");
        if (mods.HasFlag(KeyModifiers.Win)) parts.Add("win");
        parts.Add(key ?? "?");
        return string.Join("+", parts);
    }

    private void UpdateModifierBadges()
    {
        SetBadge(CtrlBadge, ReadModifiers().HasFlag(KeyModifiers.Ctrl));
        SetBadge(ShiftBadge, ReadModifiers().HasFlag(KeyModifiers.Shift));
        SetBadge(AltBadge, ReadModifiers().HasFlag(KeyModifiers.Alt));
        SetBadge(WinBadge, ReadModifiers().HasFlag(KeyModifiers.Win));
    }

    private static void SetBadge(Border badge, bool on)
    {
        if (on)
        {
            badge.Background = (Brush)Application.Current.FindResource("Brush.AccentSoft");
            badge.BorderBrush = (Brush)Application.Current.FindResource("Brush.Accent");
            if (badge.Child is TextBlock tb)
                tb.Foreground = (Brush)Application.Current.FindResource("Brush.Accent");
        }
        else
        {
            badge.Background = (Brush)Application.Current.FindResource("Brush.Surface");
            badge.BorderBrush = (Brush)Application.Current.FindResource("Brush.Border");
            if (badge.Child is TextBlock tb)
                tb.Foreground = (Brush)Application.Current.FindResource("Brush.TextTertiary");
        }
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(CapturedCombo))
        {
            PromptText.Text = "先按一个组合键";
            return;
        }
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        CapturedCombo = null;
        DialogResult = false;
        Close();
    }
}
