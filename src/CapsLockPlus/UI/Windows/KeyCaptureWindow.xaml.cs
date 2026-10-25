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

    /// <summary>
    /// 触发键（层前缀键）的虚拟键码集合。
    ///
    /// 为什么需要它：用户日常用的键**全都是"触发键 + 某键"**（默认 CapsLock + J）。
    /// 但 CapsLock 本身不是修饰键，捕获窗口会把它当成"目标键"立刻定格成
    /// capslock —— 于是按 Caps+J 录到的永远是 CapsLock，怎么都绑不到
    /// 那个已有的 J。这正是"自定义按键绑不了已有按键"的根本原因。
    ///
    /// 现在：按到前缀键 ⇒ 只当作"开始录组合"，继续等真正的目标键；
    /// 单独按一下前缀键就松手 ⇒ 仍允许把它自己当目标（绑短按用）。
    /// </summary>
    public HashSet<int> PrefixKeys { get; init; } = new();

    /// <summary>当前按住的触发键（0 = 没有）。</summary>
    private int _prefixVk;

    /// <summary>按住触发键期间是否又按了别的键。</summary>
    private bool _prefixUsed;

    /// <summary>可当前缀用的触发键（已排除修饰键）。</summary>
    private HashSet<int> _prefixEligible = new();

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
            /* PrefixKeys 是 init 属性，对象初始化器在构造函数**之后**才赋值，
             * 所以不能写在构造函数里 —— 到这里才读得到。
             * 修饰键要排除：右 Alt 之类被设成触发键时仍按修饰键语义处理。 */
            _prefixEligible = PrefixKeys.Where(v => v != 0 && !IsModifier(v)).ToHashSet();

            _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
            if (_hook == IntPtr.Zero)
            {
                PromptText.Text = Loc.T("无法安装键盘钩子");
                DetailText.Text = Loc.T("请检查是否有安全软件拦截全局钩子。");
                CoreLog.Warn($"自定义按键捕获：钩子安装失败（错误码 {Marshal.GetLastWin32Error()}）");
            }
            Activate();
            Focus();
            // 用户可能是"已经按着 Ctrl 才点开对话框"，那几个 down 事件我们没收到，
            // 先播种一次初始状态，之后完全靠钩子事件维护。
            SeedModifiers();
            UpdateModifierBadges();
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

    /// <summary>
    /// 修饰键跟踪器。
    ///
    /// 不用 <see cref="GetAsyncKeyState"/> 判断修饰键 —— 低级钩子回调里
    /// 异步按键状态还没更新，查到的永远是上一个键的状态，
    /// 表现就是"能识别按键但录不到组合键"。
    /// 这里完全靠钩子的 down/up 事件维护。
    /// </summary>
    private readonly ModifierTracker _tracker = new();

    /// <summary>当前按住的修饰键（从钩子事件推导）。</summary>
    private KeyModifiers ReadModifiers() => _tracker.Current;

    private void TrackModifier(int vk, bool down) => _tracker.Track(vk, down);

    private static bool IsModifier(int vk) => ModifierTracker.IsModifier(vk);

    /// <summary>
    /// 窗口刚打开时补一次初始状态：用户可能**已经按着** Ctrl 才点开对话框，
    /// 那几个键的 down 事件我们没收到。这里用 GetAsyncKeyState 做一次性播种，
    /// 之后就完全靠钩子事件维护（那时它反而不可靠）。
    /// </summary>
    private void SeedModifiers() => _tracker.Seed(IsKeyDown);

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
            // 修饰键：先把按住状态记下来（这是唯一可靠的来源），再刷新高亮。
            // **抬起**时如果还没定案，就把这个修饰键本身作为目标
            // （方便单独绑定 Ctrl/Shift/Alt/Win）。
            TrackModifier(vk, down);

            if (down)
            {
                Dispatcher.BeginInvoke(UpdateModifierBadges);
            }
            else
            {
                Dispatcher.BeginInvoke(() =>
                {
                    UpdateModifierBadges();
                    if (string.IsNullOrEmpty(CapturedCombo))
                    {
                        // 只按了一个修饰键就松手：允许把它当目标键
                        string? modName = KeyName.NameOf(vk);
                        if (modName != null)
                        {
                            CapturedMods = KeyModifiers.None;
                            CapturedCombo = modName;
                            PromptText.Text = KeyName.Display(modName);
                            DetailText.Text = Loc.T("单独绑定这个修饰键　·　按「确定绑定」生效");
                            ConfirmButton.IsEnabled = true;
                        }
                    }
                });
            }
            return (IntPtr)1;
        }

        /* 触发键（层前缀）必须在这里拦下来。
         *
         * 默认触发键是 CapsLock —— 它**不是修饰键**，如果不拦，
         * 流程会直接落到下面"非修饰键按下 = 定格组合"，把用户按下的
         * 前缀键录成目标键本身。于是「Caps + J」永远录成 capslock，
         * 用户想改的那个已有按键（J）怎么都选不中 —— 这就是
         * Loc.T("自定义按键绑不了已有按键")的真正原因。
         *
         * 只对**非修饰键**做前缀处理：右 Alt 这类既可能被设成触发键、
         * 本身又是修饰键的键，仍走原来的修饰键语义，
         * 否则"按住右 Alt 再按 K"会被误判成普通按键。
         */
        if (_prefixEligible.Contains(vk))
        {
            if (down)
            {
                _prefixVk = vk;
                _prefixUsed = false;
                string? pName = KeyName.NameOf(vk);
                Dispatcher.BeginInvoke(() =>
                {
                    PromptText.Text = $"{KeyName.Display(pName ?? "")} + ?";
                    DetailText.Text = Loc.T("触发键只作组合前缀，不会占用它　·　再按一个键");
                });
            }
            else
            {
                if (!_prefixUsed && _prefixVk == vk)
                {
                    // 单独按了一下触发键就松手：允许把它自己当目标（改短按行为用）
                    string? modName = KeyName.NameOf(vk);
                    if (modName != null)
                    {
                        Dispatcher.BeginInvoke(() =>
                        {
                            CapturedMods = KeyModifiers.None;
                            CapturedCombo = modName;
                            PromptText.Text = KeyName.Display(modName);
                            DetailText.Text = Loc.T("单独绑定这个触发键　·　按「确定绑定」生效");
                            ConfirmButton.IsEnabled = true;
                        });
                    }
                }
                _prefixVk = 0;
                _prefixUsed = false;
            }
            return (IntPtr)1;
        }

        if (down)
        {
            // 有前缀键按着 → 这个键才是真正的目标键
            if (_prefixVk != 0) _prefixUsed = true;

            /* 非修饰键按下 = 定格组合。
             *
             * 未知键不再拒绝：给它一个 `vk0xXX` 的兜底名字。
             * 键名表是白名单，输入法键 / 厂商自定义键根本不在表里，
             * 以前用户按下去只看到"暂时不能绑定"，等于功能缺失；
             * 而 KeyName.VkOf 早就支持这个格式（宏录制一直在用）。 */
            string? known = KeyName.NameOf(vk);
            string name = known ?? KeyName.GeneratedName(vk);
            var mods = ReadModifiers();
            string combo = BuildCombo(mods, name);

            Dispatcher.BeginInvoke(() =>
            {
                CapturedMods = mods;
                CapturedCombo = combo;

                PromptText.Text = combo;
                DetailText.Text = known != null
                    ? (mods == KeyModifiers.None
                        ? $"单键 {name}　·　按「确定绑定」生效"
                        : $"组合键 {combo}　·　按「确定绑定」生效")
                    : $"未收录的键（VK 0x{vk:X2}），按 {name} 记录　·　按「确定绑定」生效";
                ConfirmButton.IsEnabled = true;
            });

            return (IntPtr)1;
        }

        return CallNextHookEx(_hook, nCode, wParam, lParam);
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
            PromptText.Text = Loc.T("先按一个组合键");
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
