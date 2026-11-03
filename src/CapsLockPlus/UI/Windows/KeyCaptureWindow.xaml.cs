using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
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

    /// <summary>捕获结果（非模态回调用）。</summary>
    public readonly record struct CaptureResult(string? Combo, string? Key, Core.Input.KeyModifiers Mods);

    /// <summary>
    /// 统一的"录一个键"入口：**非模态**打开，结果通过回调返回。
    ///
    /// 为什么不建议直接 new + ShowDialog：ShowDialog 会阻塞界面线程，
    /// 万一窗口没显示出来（透明窗口 + Owner 不可见时出现过），用户看到的就是
    /// "整个程序卡死、识别按键的页面也不出现"，而且没有任何日志。
    /// </summary>
    public static void Capture(Action<CaptureResult> onCaptured, IEnumerable<int>? prefixKeys = null,
                              string? title = null)
    {
        if (KeyCaptureSink.Current != null)
        {
            CoreLog.Info("已经有捕获窗在等待按键，忽略这次重复打开");
            return;
        }

        var dlg = new KeyCaptureWindow { PrefixKeys = prefixKeys == null ? new HashSet<int>() : new HashSet<int>(prefixKeys) };
        if (!string.IsNullOrWhiteSpace(title)) dlg.Title = title!;
        dlg.Closed += (_, _) =>
        {
            try { onCaptured(new CaptureResult(dlg.CapturedCombo, dlg.CapturedKey, dlg.CapturedMods)); }
            catch (Exception ex) { CoreLog.Swallow(ex, "捕获键回调"); }
        };
        CoreLog.Info("打开键位捕获窗（非模态）");
        dlg.Show();
        dlg.Activate();
    }

    public KeyCaptureWindow()
    {
        InitializeComponent();
        _proc = HookCallback;

        /* 第三条来源：WPF 自己的预览事件。
         *
         * 窗口是模态 + Topmost + 打开即激活，只要它还拿着键盘焦点，
         * 每个按键都会先走 PreviewKeyDown —— 这条路**不依赖任何全局钩子**，
         * 所以钩子被系统摘掉时它照样能用。 */
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewKeyUp += OnPreviewKeyUp;
        IsVisibleChanged += (_, e) => { if (e.NewValue is true) Keyboard.Focus(this); };

        Loaded += (_, _) =>
        {
            /* PrefixKeys 是 init 属性，对象初始化器在构造函数**之后**才赋值，
             * 所以不能写在构造函数里 —— 到这里才读得到。
             * 修饰键要排除：右 Alt 之类被设成触发键时仍按修饰键语义处理。 */
            _prefixEligible = PrefixKeys.Where(v => v != 0 && !IsModifier(v)).ToHashSet();

            /* 第二条来源：Raw Input（不受钩子超时影响）。
             * 订阅之后即使钩子死了，按键也照样进来。 */
            KeyCaptureSink.Current = (vk, down) =>
            {
                RawHits++;
                if (Environment.GetEnvironmentVariable("CLP_CAPTURE_LOG") == "1")
                    CoreLog.Info($"[捕获] raw vk=0x{vk:X2} down={down}");
                Dispatcher.BeginInvoke(() => HandleKey(vk, down));
            };

            _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
            if (_hook == IntPtr.Zero)
            {
                /* 不再把这当成"功能不可用"：钩子只是三路来源之一，
                 * 另外两路（Raw Input / WPF 预览）足够覆盖普通按键。
                 * 提示要如实说清"现在还能用"，否则用户会以为坏了。 */
                PromptText.Text = Loc.T("等待按键…");
                DetailText.Text = Loc.T("全局钩子没装上（可能被安全软件拦截），已改用窗口内捕获——功能正常。");
                CoreLog.Warn($"自定义按键捕获：钩子安装失败（错误码 {Marshal.GetLastWin32Error()}），改用 Raw Input + 窗口内捕获");
            }
            Activate();
            Focus();
            Keyboard.Focus(this);
            // 用户可能是"已经按着 Ctrl 才点开对话框"，那几个 down 事件我们没收到，
            // 先播种一次初始状态，之后完全靠事件维护。
            SeedModifiers();
            UpdateModifierBadges();

            /* 3 秒还没收到任何按键：把"三路来源各自收到几次"写进日志，并给用户一句人话。
             * 这条日志是下次排查的抓手 —— 哪一路是 0，问题就在哪一路。 */
            _noInputTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _noInputTimer.Tick += (_, _) =>
            {
                _noInputTimer!.Stop();
                if (HookHits + RawHits + WpfHits > 0) return;
                CoreLog.Warn($"录键 3 秒没收到任何按键：钩子={HookHits} RawInput={RawHits} 窗口事件={WpfHits} " +
                             $"（管理员={Core.ElevationHelper.IsElevated}）");
                DetailText.Text = Loc.T("还没收到按键。可以再按一次；如果一直没反应，把日志发给我们（日志里记了三路来源的计数）。");
            };
            _noInputTimer.Start();
        };
        Closed += (_, _) =>
        {
            _noInputTimer?.Stop();
            KeyCaptureSink.Current = null;
            if (_hook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
            }
        };
    }

    /* ---------------- 三路输入汇合到这里 ---------------- */

    /// <summary>上一次处理的按键，用来给多路来源去重（同一个物理按键会被钩子 + Raw Input + WPF 各送一次）。</summary>
    /// <summary>
    /// 非模态打开时（Recommend：不用 ShowDialog），<c>DialogResult</c> 一设就抛
    /// InvalidOperationException。这里统一兜住 —— 调用方看 CapturedCombo 就够了。
    /// </summary>
    private void TrySetResult(bool value)
    {
        try { DialogResult = value; } catch (InvalidOperationException) { /* 非模态 */ }
    }

    private DispatcherTimer? _noInputTimer;

    private int _lastVk;
    private bool _lastDown;
    private long _lastTick;
    private bool _closed;

    /// <summary>
    /// 一帧内同一个 (vk, 方向) 的重复事件直接丢掉。
    ///
    /// 阈值取 40ms：真实自动重复最快约 30ms 一次，而三路来源之间相差不到 5ms，
    /// 所以既能吃掉重复，又不会把用户的第二次按键当成重复。
    /// </summary>
    private bool Duplicate(int vk, bool down)
    {
        long now = Environment.TickCount64;
        if (vk == _lastVk && down == _lastDown && now - _lastTick < 40) return true;
        _lastVk = vk;
        _lastDown = down;
        _lastTick = now;
        return false;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        WpfHits++;
        if (Environment.GetEnvironmentVariable("CLP_CAPTURE_LOG") == "1")
            CoreLog.Info($"[捕获] wpf key={e.Key}");
        e.Handled = true;
        // Alt 在 WPF 里走 Key.System，真正的键在 SystemKey 上
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        int vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0) return;
        if (e.IsRepeat) return;
        HandleKey(vk, true);
    }

    private void OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        WpfHits++;
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        int vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0) return;
        HandleKey(vk, false);
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
        if (nCode >= 0)
        {
            HookHits++;
            if (Environment.GetEnvironmentVariable("CLP_CAPTURE_LOG") == "1")
                CoreLog.Info("[捕获] hook 事件");
        }
        if (nCode < 0) return CallNextHookEx(_hook, nCode, wParam, lParam);

        int msg = (int)wParam;
        bool down = msg is 0x0100 or 0x0104;      // WM_KEYDOWN / WM_SYSKEYDOWN
        bool up = msg is 0x0101 or 0x0105;        // WM_KEYUP / WM_SYSKEYUP
        if (!down && !up) return CallNextHookEx(_hook, nCode, wParam, lParam);

        var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
        // 吞掉按键：捕获期间这些键不该漏给前台（Alt+Tab / Win 之类会当场切走窗口）
        return HandleKey((int)data.vkCode, down)
            ? (IntPtr)1
            : CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /* ---- 三路来源计数器（--capture-test 诊断用） ---- */

    /// <summary>低级钩子收到的事件数。</summary>
    internal int HookHits { get; set; }

    /// <summary>Raw Input 送进来的事件数。</summary>
    internal int RawHits { get; private set; }

    /// <summary>窗口内 PreviewKey 事件数。</summary>
    internal int WpfHits { get; private set; }

    /// <summary>
    /// 三路输入（低级钩子 / Raw Input / WPF 预览）汇合后的唯一处理点。
    /// 返回值 = 是否吞掉这个按键（只有钩子那一路用得到）。
    /// </summary>
    private bool HandleKey(int vk, bool down)
    {
        if (_closed || vk == 0) return false;
        if (Duplicate(vk, down)) return true;

        // Esc 取消
        if (down && vk == VK_ESC)
        {
            _closed = true;
            Dispatcher.BeginInvoke(() =>
            {
                CapturedCombo = null;
                TrySetResult(false);
                Close();
            });
            return true;
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
            return true;
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
            return true;
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
                        ? Loc.F("单键 {0}　·　按「确定绑定」生效", name)
                        : Loc.F("组合键 {0}　·　按「确定绑定」生效", combo))
                    : Loc.F("未收录的键（VK 0x{0:X2}），按 {1} 记录　·　按「确定绑定」生效", vk, name);
                ConfirmButton.IsEnabled = true;
            });

            return true;
        }

        // 非修饰键抬起：不吞（否则某些程序收不到 up，会"粘键"）
        return false;
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
        TrySetResult(true);
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        CapturedCombo = null;
        TrySetResult(false);
        Close();
    }
}
