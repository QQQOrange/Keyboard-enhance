using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using CapsLockPlus.Core;
using CapsLockPlus.Core.Icons;
using CapsLockPlus.Core.Qbar;
using CapsLockPlus.Core.Settings;
using CapsLockPlus.Core.Windows;
using CapsLockPlus.UI.Theme;

namespace CapsLockPlus.UI.Windows;

public partial class QbarWindow : Window
{
    /// <summary>
    /// 字号测量的字形样本：全角汉字 + 带上下伸部的拉丁字母，
    /// 量出来的行高对中英文都够用。
    /// **刻意不做成可翻译文本** —— 加了译文行高就会跟着变，布局对不上。
    /// </summary>
    private const string GlyphSample = "汉字Agqy";

    private readonly AppHost _host;
    private List<QbarItem> _items = new();
    private QbarChip? _chip;
    private bool _closing;
    private bool _suppressTextChanged;
    private bool _imeApplied;
    private string _imeKey = "";
    private DateTime _imeAt = DateTime.MinValue;
    private int _imeRetry;
    private string _imeLoggedMode = "";
    private DispatcherTimer? _imeTimer;
    private DispatcherTimer? _imeVerify;

    public QbarWindow(AppHost host)
    {
        _host = host;
        InitializeComponent();
        Deactivated += OnDeactivated;
        Activated += (_, _) => FocusInputNow();
        PreviewKeyDown += OnWindowKeyDown;
        Results.SelectionChanged += (_, _) => UpdateHint();
        ApplyStyle();
    }

    // ---------------- 抢前台与输入焦点 ----------------

    private IntPtr _hwnd;
    private System.Windows.Threading.DispatcherTimer? _focusTimer;
    private int _focusTries;
    private DateTime _openedAt = DateTime.MinValue;

    /// <summary>
    /// 打开后的宽限期（毫秒）：这段时间里忽略 Deactivated。
    ///
    /// 面板刚弹出时正在跟系统抢前台，抢的过程中会短暂收到 Deactivated；
    /// 原来直接 HideQbar，用户看到的是"面板闪一下就没了，字也没打进去"。
    /// </summary>
    private const int OpenGraceMs = 320;

    /// <summary>
    /// 把输入框的焦点真正拿到手。
    ///
    /// ⚠ 这里不能只写 <c>Input.Focus()</c>：窗口本身没被激活时，
    /// WPF 的焦点只是"窗口内部的逻辑焦点"，键盘输入仍然去前台那个程序。
    /// 必须先把窗口激活（<see cref="WindowService.ForceForeground"/>），
    /// 再 Focus + Keyboard.Focus，才会真的把输入接管过来。
    /// </summary>
    private void FocusInputNow()
    {
        try
        {
            if (!IsActive) Activate();
            Focus();
            Keyboard.Focus(Input);
            Input.CaretIndex = Input.Text.Length;

            /* 输入法只在**焦点真正拿到之后**切：盲按 Shift 是"切换"，不是"确保"，
             * 现在已经是中文时再按一下反而变英文。这里用 ImeGuard 查布局后按需切。 */
            if (!_imeApplied && IsActive)
            {
                /* EnsureComposing 会同时检查“布局”和“输入法的中/英模式”。
                 * 只有真的成功才记账；失败时保持 false，等下一次激活/焦点
                 * 事件再试一次（中文输入法刚启动时 IMM 上下文可能还没就绪）。 */
                _imeApplied = ApplyImePreference();
                if (!_imeApplied) QueueImePreference(180);
            }
        }
        catch
        {
            // 焦点操作失败不该影响面板本身
        }
    }

    /// <summary>
    /// 按当前面板状态决定输入语言：
    ///   已有引擎标签 → 中文（用户接下来要打的是搜索内容）；
    ///   空面板       → 按“打开时切英文”选项，方便直接打 bd / gg 这类关键字。
    ///
    /// 只做“确保”，不做“切换”：已经是目标语言时什么都不发，避免把
    /// 用户手工切好的中/英状态又翻回去。
    /// </summary>
    private bool ApplyImePreference(bool force = false)
    {
        try
        {
            /* 空面板不碰输入法：用户系统当前是什么中/英状态，就保持什么状态。
             * 唯一要做的是把上一次“引擎标签”可能遗留的禁用状态恢复回来。 */
            if (_chip == null)
            {
                if (!InputMethod.GetIsInputMethodEnabled(Input))
                    InputMethod.SetIsInputMethodEnabled(Input, true);
                return true;
            }

            /* 只有“已经带出引擎标签”并且用户勾了“认出引擎后切回中文”
             * 时才切中文。 */ 
            string want = "zh:" + _chip.Key;
            if (!force && _imeKey == want && (DateTime.UtcNow - _imeAt).TotalMilliseconds < 800)
                return true;

            bool Done(bool ok)
            {
                if (ok)
                {
                    _imeKey = want;
                    _imeAt = DateTime.UtcNow;
                }
                return ok;
            }

            InputMethod.SetIsInputMethodEnabled(Input, true);
            if (!_host.Settings.Qbar.SwitchBackOnEngine)
            {
                // 用户关掉了“认出引擎后切回中文”：保持输入法现状。
                return Done(true);
            }

            return Done(ShiftImeTo(true));
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "切换输入法");
            return false;
        }
    }

    /// <summary>
    /// 用 Shift 把当前输入法拨到中文/英文模式，不切换键盘布局。
    /// 先读 WPF 的 ImeConversionMode，已经是目标状态就什么都不发；
    /// 读不到明确状态（DoNotCare）时也不盲按，交给下一轮重试。
    /// </summary>
    private bool ShiftImeTo(bool chinese)
    {
        InputMethod.SetPreferredImeState(Input, InputMethodState.On);
        var ime = InputMethod.Current;
        if (ime == null) return false;

        if (ime.ImeState != InputMethodState.On)
            ime.ImeState = InputMethodState.On;

        var mode = ime.ImeConversionMode;
        bool nowChinese = (mode & ImeConversionModeValues.Native) != 0;
        bool nowEnglish = (mode & ImeConversionModeValues.Alphanumeric) != 0;

        string actual = mode.ToString();
        if (_imeLoggedMode != actual)
        {
            _imeLoggedMode = actual;
            CoreLog.Info($"[Qbar] IME 当前={actual}，目标={(chinese ? "中文" : "英文")}");
        }

        if (chinese ? nowChinese : nowEnglish) return true;
        if (!nowChinese && !nowEnglish) return false;   // DoNotCare / 还没挂好

        Core.Input.InputSender.Tap(0x10);   // VK_SHIFT：只切中英模式，不换布局
        try
        {
            /* 搜狗/微软拼音对合成 Shift 的反应有延迟，而且 WPF 的
             * ImeConversionMode 有时要到下一次输入才刷新。这里再直接
             * 把 TSF 的转换模式拨到目标值作为兜底 —— 仍然不换键盘布局。 */
            var target = chinese ? ImeConversionModeValues.Native : ImeConversionModeValues.Alphanumeric;
            InputMethod.SetPreferredImeConversionMode(Input, target);
            ime.ImeConversionMode = target;
        }
        catch { }
        return true;
    }

    /// <summary>
    /// 焦点刚拿到时 TSF 有时还没挂好，立刻设 IME 会被随后到来的
    /// 焦点消息覆盖。延迟一拍再强制应用一次，只延后不阻塞面板显示。
    /// </summary>
    private void QueueImePreference(int delayMs = 140)
    {
        _imeRetry = 0;
        StartImeTimer(delayMs);
    }

    private void StartImeTimer(int delayMs)
    {
        _imeTimer?.Stop();
        _imeTimer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(Math.Max(30, delayMs)),
        };
        _imeTimer.Tick += (_, _) =>
        {
            _imeTimer?.Stop();
            if (!IsVisible) return;
            _imeApplied = ApplyImePreference(force: true);
            if (!_imeApplied && _imeRetry++ < 5)
                StartImeTimer(250);
            else if (_imeApplied)
                ScheduleImeVerify();
        };
        _imeTimer.Start();
    }

    /// <summary>
    /// TSF 有时在面板刚显示时先报 Alphanumeric，等焦点稳定/开始输入后又变回
    /// 中文（搜狗、微软拼音都见过）。这里在 700ms 后再核对一次：只读状态，
    /// 真的变回中文才补按一次 Shift；已经是目标状态就什么都不做。
    /// </summary>
    private void ScheduleImeVerify()
    {
        _imeVerify?.Stop();
        _imeVerify = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(700),
        };
        _imeVerify.Tick += (_, _) =>
        {
            _imeVerify?.Stop();
            if (IsVisible)
                _imeApplied = ApplyImePreference(force: true);
        };
        _imeVerify.Start();
    }

    /// <summary>
    /// 打开时抢前台：先试一次，没抢到就每 20ms 重试，最多约 400ms。
    ///
    /// 为什么要重试而不是"一次不成就算了"：
    /// 热键触发那一刻，系统还在处理上一个前台窗口的焦点切换，
    /// 第一次 SetForegroundWindow 常常被前台锁定拒掉，
    /// 隔几十毫秒再试就能成功 —— 这也解释了"有时候能打、有时候不能"。
    /// </summary>
    private void BeginForceFocus()
    {
        try { _hwnd = new WindowInteropHelper(this).Handle; }
        catch { _hwnd = IntPtr.Zero; }

        Topmost = true;
        FocusInputNow();

        if (_hwnd != IntPtr.Zero && WindowService.ForegroundIs(_hwnd)) return;

        _focusTries = 0;
        _focusTimer ??= new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Send)
        {
            Interval = TimeSpan.FromMilliseconds(20),
        };
        _focusTimer.Tick -= OnFocusTick;
        _focusTimer.Tick += OnFocusTick;
        _focusTimer.Start();
    }

    private void OnFocusTick(object? sender, EventArgs e)
    {
        if (!IsVisible || _closing) { StopFocusTimer(); return; }

        if (_hwnd == IntPtr.Zero)
        {
            try { _hwnd = new WindowInteropHelper(this).Handle; } catch { }   /* 预期内：探测/重试型，失败即视为无结果 */
        }

        if (_hwnd != IntPtr.Zero && WindowService.ForegroundIs(_hwnd))
        {
            FocusInputNow();
            StopFocusTimer();
            return;
        }

        if (++_focusTries > 20)      // 约 400ms
        {
            StopFocusTimer();
            CoreLog.Warn("Qbar 抢前台失败（前台锁定或被权限更高的程序占着），已放弃");
            return;
        }

        /* 前几次只做"常规抢"（SetForegroundWindow + AttachThreadInput）；
         * 反复失败才动前台锁定超时 —— 那一步会广播 WM_SETTINGCHANGE，
         * 每次重试都做一遍会把界面拖慢。 */
        bool aggressive = _focusTries >= 6;
        if (_hwnd != IntPtr.Zero) WindowService.ForceForeground(_hwnd, aggressive);
        FocusInputNow();
    }

    private void StopFocusTimer()
    {
        try
        {
            _focusTimer?.Stop();
            if (_focusTimer != null) _focusTimer.Tick -= OnFocusTick;
        }
        catch { }   /* 预期内：探测/重试型，失败即视为无结果 */
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (!ShouldHideOnDeactivate((int)(DateTime.Now - _openedAt).TotalMilliseconds)) return;
        HideQbar();
    }

    /// <summary>
    /// 收到 Deactivated 时该不该把面板收起来（纯函数，便于自检）。
    ///
    /// 打开后的头 <see cref="OpenGraceMs"/> 毫秒里一律不收：
    /// 那段时间正在跟系统抢前台，Deactivated 是抢的过程产生的，
    /// 收起来会让用户看到"面板闪一下就消失"。
    /// </summary>
    internal static bool ShouldHideOnDeactivate(int elapsedMs) => elapsedMs >= OpenGraceMs;

    // ---------------- 点击面板外面就收起 ----------------

    /// <summary>
    /// 打开期间盯着全局鼠标按键。
    ///
    /// 为什么除了 Deactivated 还要这个：任务栏这类"不换前台"的地方，
    /// 点了它前台窗口不变，Deactivated 根本不触发，面板会一直挂着。
    /// 用户要求"点任何地方都消失"，这个全局钩子就是兜底。
    ///
    /// 窗口**内部**（投影、透明边距）不归钩子管：那些点击会穿透到下
    /// 层窗口、由 Deactivated 收走，与加钩子之前的行为完全一致 ——
    /// 所以只做"矩形外就收"，不做花哨的命中测试。
    /// </summary>
    private void StartOutsideWatch()
    {
        // 先摘再挂：重复 Open() 不会叠出多个回调
        _host.Engine.MouseDownEvent -= OnGlobalMouseDown;
        _host.Engine.MouseDownEvent += OnGlobalMouseDown;
    }

    private void StopOutsideWatch() => _host.Engine.MouseDownEvent -= OnGlobalMouseDown;

    /// <summary>钩子线程回调：只做判断，收起动作丢回界面线程。</summary>
    private void OnGlobalMouseDown(int x, int y, int mouseMessage)
    {
        if (_hwnd == IntPtr.Zero) return;

        /* 先看这一击落在谁身上：本进程自己弹出的东西（输入框的右键
         * 菜单、提示气泡）虽然在面板矩形之外，也不算"外面" ——
         * 否则右键菜单里点「粘贴」会把面板一起收走。 */
        var hit = Core.Interop.NativeMethods.WindowFromPoint(new Core.Interop.NativeMethods.POINT { X = x, Y = y });
        if (hit != IntPtr.Zero)
        {
            Core.Interop.NativeMethods.GetWindowThreadProcessId(hit, out uint pid);
            if (pid == Core.Interop.NativeMethods.GetCurrentProcessId()) return;
        }

        if (!Core.Interop.NativeMethods.GetWindowRect(_hwnd, out var r)) return;
        if (!IsOutsidePanel(x, y, r.Left, r.Top, r.Right, r.Bottom)) return;
        // 与 Deactivated 共用同一套宽限期：刚打开时正在抢前台，别被误关
        if ((DateTime.Now - _openedAt).TotalMilliseconds < OpenGraceMs) return;
        Dispatcher.BeginInvoke(new Action(HideQbar));
    }

    /// <summary>
    /// 一次全局按键是否落在面板窗口之外（纯函数，便于自检）。
    /// 右/下边界取开区间，与 Windows 的命中测试一致：点在最右一列
    /// 或最下一行像素上，算窗外。
    /// </summary>
    internal static bool IsOutsidePanel(int x, int y, int left, int top, int right, int bottom)
        => x < left || x >= right || y < top || y >= bottom;

    private SolidColorBrush? _panelBrush;
    private SolidColorBrush? _borderBrush;
    private string _styleStamp = "";

    /// <summary>
    /// 判断面板底色是深是浅，并据此推出一整套前景色。
    ///
    /// 背景：XAML 里原先所有文字/分隔线/图标底色都是**硬编码的深色系**
    /// （#FFFFFF / #9FB4D8 / #1FFFFFFF…），一旦用户把面板底色改成浅色，
    /// 就变成"白字白底"，什么都看不见 —— 用户反馈的"只有深色文本框"就是这个。
    ///
    /// 现在改为按底色的**相对亮度**推导：
    ///   · 深底 → 白字 + 白色半透明分隔线（与原来一致）
    ///   · 浅底 → 深灰字 + 黑色半透明分隔线
    /// 这样无论用户把面板设成什么颜色，文字都可读。
    /// </summary>
    private static bool IsLightColor(Color c)
    {
        // sRGB 相对亮度（ITU-R BT.709 系数）
        double lum = (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;
        return lum > 0.55;
    }

    private static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t),
        (byte)(a.G + (b.G - a.G) * t),
        (byte)(a.B + (b.B - a.B) * t));

    /// <summary>
    /// sRGB 相对亮度（WCAG 定义：先线性化再按 0.2126/0.7152/0.0722 加权）。
    /// 和 <see cref="IsLightColor"/> 那个粗略估计不是一回事 —— 算对比度必须用这个。
    /// </summary>
    private static double RelativeLuminance(Color c)
    {
        static double Ch(byte v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Ch(c.R) + 0.7152 * Ch(c.G) + 0.0722 * Ch(c.B);
    }

    /// <summary>WCAG 对比度（1~21）。</summary>
    internal static double Contrast(Color a, Color b)
    {
        double la = RelativeLuminance(a), lb = RelativeLuminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>
    /// 在给定底色上挑可读性更高的文字色：纯白 还是 近黑。
    ///
    /// 用在"首字色块"上 —— 那块底是各家的品牌色，深浅完全不由我们定
    /// （百度蓝、必应青、维基灰……）。跟着面板明暗走就会踩雷：
    /// 浅色面板给深灰字，压在深蓝牌子上等于消失。
    /// </summary>
    internal static Color BestTextOn(Color background)
    {
        var white = Colors.White;

        /* 浅色牌子上用**纯黑**而不是面板那个 #161A23：
         * 谷歌红（#EA4335）、中灰（#808080）这类"中间调"底色，
         * 用 #161A23 只有 4.4:1，差一点点够不着正文可读的 4.5。 */
        var dark = Colors.Black;

        return Contrast(white, background) >= Contrast(dark, background) ? white : dark;
    }

    /// <summary>把颜色按给定不透明度叠到底色上（用于算出胶囊的实际底色）。</summary>
    internal static Color Overlay(Color baseColor, Color veil, double alpha) => Mix(baseColor, veil, alpha);

    /// <summary>面板上"主文字"该用什么色。浅底黑、深底白，胶囊里的引擎名也用它。</summary>
    internal static Color PrimaryOn(Color panel)
        => IsLightColor(panel) ? Color.FromRgb(0x16, 0x1A, 0x23) : Colors.White;

    /// <summary>把整套配色应用到面板里的每个元素。</summary>
    private void ApplyForegroundPalette(Color panelColor)
    {
        bool light = IsLightColor(panelColor);

        // 前景色阶：主文字 → 次级 → 弱化
        Color primary = PrimaryOn(panelColor);
        Color secondary = light ? Color.FromRgb(0x5B, 0x64, 0x78) : Color.FromRgb(0xC9, 0xD6, 0xEC);
        Color muted = light ? Color.FromRgb(0x8A, 0x93, 0xA6) : Color.FromRgb(0x9F, 0xB0, 0xCC);
        Color faint = light ? Color.FromRgb(0xA8, 0xB0, 0xBF) : Color.FromRgb(0x7F, 0x8D, 0xA8);

        // 半透明叠色（图标底、标签底、分隔线）：浅底用黑，深底用白
        Color veil = light ? Colors.Black : Colors.White;

        var bPrimary = Frozen(primary);
        var bSecondary = Frozen(secondary);
        var bMuted = Frozen(muted);
        var bFaint = Frozen(faint);
        var bVeil06 = Frozen(Color.FromArgb(0x0F, veil.R, veil.G, veil.B));
        var bVeil10 = Frozen(Color.FromArgb(0x1A, veil.R, veil.G, veil.B));
        var bVeil13 = Frozen(Color.FromArgb(0x22, veil.R, veil.G, veil.B));
        var bVeil20 = Frozen(Color.FromArgb(0x33, veil.R, veil.G, veil.B));

        ModeIcon.Foreground = bSecondary;
        Input.Foreground = bPrimary;
        Input.CaretBrush = bPrimary;

        /* 胶囊里的字是"当前用的是哪个引擎"的唯一提示，用主文字色。
         * 以前给的是次级灰：深色下偏蓝灰、浅色下偏浅灰，压在胶囊那层
         * 13% 叠色上就发糊 —— 用户在白天模式里一眼看不出写的什么。 */
        ChipLabel.Foreground = bPrimary;
        ChipClose.Foreground = bMuted;
        ChipBox.Background = bVeil13;
        ChipBox.BorderBrush = bVeil20;

        ChipInitialBox.Background = bVeil13;

        /* 首字块的底色是**品牌色**（每家深浅都不同），不能跟着面板走。
         * 以前固定用 bPrimary：浅色面板下是深灰字压深蓝牌子（如百度蓝），
         * 几乎看不见；深色面板下白字反而正常。改成按牌子亮度自己挑黑白。 */
        ChipInitial.Foreground = Frozen(BestTextOn(_chip?.Accent ?? Colors.SteelBlue));
        HintText.Foreground = bMuted;
        SepLine.Background = bVeil10;

        // 「当前分类」角标——它的底色是强调色，文字始终用白
        if (ModeIcon.Parent is Border modeBorder) modeBorder.Background = bVeil13;

        /* 结果列表项在 DataTemplate 里，逐个取出来改色。
         * 用 ItemContainerGenerator 拿容器，ContainerFromItem 在虚拟化下
         * 可能为空，所以这里遍历可视树更可靠。
         *
         * 有 x:Name 的都是模板外的固定元素，它们各自有明确语义，
         * 不该被"按字号猜"这一轮刷掉（ChipLabel 最典型）。 */
        /* 模板里的文字走 DynamicResource，这里直接把**窗口级资源**换掉：
         * 标题/副标题/角标各用各的角色，不再靠"字号 >= 13.5 就是标题"猜 ——
         * 那样任何字号微调都会让颜色错位，而且用户改显示缩放时特别明显。 */
        Resources["Brush.OverlayText"] = bPrimary;
        Resources["Brush.OverlayTextSecondary"] = bSecondary;
        Resources["Brush.OverlayTextTertiary"] = bFaint;

        // 再按具体名字覆盖一次（上面那轮会把模板外的也刷成近似色）
        ModeIcon.Foreground = bSecondary;
        Input.Foreground = bPrimary;
        ChipLabel.Foreground = bPrimary;
        ChipClose.Foreground = bMuted;
        HintText.Foreground = bMuted;

        foreach (var bd in FindAll<Border>(this))
        {
            // 图标底、徽标底这类小圆角块
            if (bd.CornerRadius.TopLeft is > 4 and < 10 && bd.Width is > 0 and < 30)
                bd.Background = bVeil10;
        }

        // 列表里的图标瓷砖（模板内，宽高都是 30）
        foreach (var bd in FindAll<Border>(this))
        {
            if (Math.Abs(bd.Width - 30) < 1 && Math.Abs(bd.Height - 30) < 1)
                bd.Background = bVeil06;
        }
    }

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    /// <summary>解析颜色字符串；非法值退回深色主题的卡片底色。</summary>
    private static Color ParseColor(string s)
    {
        try { return (Color)ColorConverter.ConvertFromString(s); }
        catch { return (Color)ColorConverter.ConvertFromString("#1C1F27"); }
    }

    /// <summary>
    /// 当前主题面板应使用的底色。
    ///
    /// 从应用资源里取 SurfaceAlt（卡片底），取不到就按"深色主题"兜底。
    /// 这样 Qbar 与主窗口的整体观感一致，而不是永远一块深色板。
    /// </summary>
    private Color ThemePanelColor()
    {
        try
        {
            if (Application.Current?.TryFindResource("Brush.SurfaceAlt") is SolidColorBrush br)
                return br.Color;
        }
        catch { }   /* 预期内：探测/重试型，失败即视为无结果 */
        return (Color)ColorConverter.ConvertFromString("#1C1F27");
    }

    /// <summary>主题标识，用于让样式缓存能在切换主题后失效。</summary>
    private static string ThemeStamp()
    {
        try
        {
            return Application.Current?.TryFindResource("Brush.SurfaceAlt") is SolidColorBrush br
                ? br.Color.ToString()
                : "?";
        }
        catch { return "?"; }
    }

    /// <summary>深度遍历可视树，收集指定类型的元素。</summary>
    private static IEnumerable<T> FindAll<T>(DependencyObject root) where T : DependencyObject
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit) yield return hit;
            foreach (var deeper in FindAll<T>(child)) yield return deeper;
        }
    }

    private void ApplyStyle()
    {
        var q = _host.Settings.Qbar;
        try
        {
            /* 面板底色跟随主题。
             *
             * 用户设置的 PanelColor 如果是"跟随主题"的保留值（空 / "auto"），
             * 就按当前主题的 SurfaceAlt 取色；否则用他指定的颜色。
             * 这样浅色主题下不会再出现"深色面板 + 白字看不见"。 */
            bool followTheme = string.IsNullOrWhiteSpace(q.PanelColor)
                               || q.PanelColor.Equals("auto", StringComparison.OrdinalIgnoreCase);
            Color effectivePanel = followTheme ? ThemePanelColor() : ParseColor(q.PanelColor);

            // 只有设置真的变了才重建画笔：Qbar 是高频开关的面板，
            // 每次 Open 都 new 一次 SolidColorBrush 属于无谓开销。
            string stamp = $"{q.AccentColor}|{effectivePanel}|{q.PanelOpacity}|{q.TextFontSize}|{q.LineHeight}|{q.ListCount}|{ThemeStamp()}";
            if (stamp == _styleStamp) return;
            _styleStamp = stamp;

            var accent = (Color)ColorConverter.ConvertFromString(q.AccentColor);
            _borderBrush = new SolidColorBrush(accent);
            _borderBrush.Freeze();
            Panel.BorderBrush = _borderBrush;

            var panelColor = effectivePanel;
            byte alpha = (byte)(double.TryParse(q.PanelOpacity, out double op) ? Math.Clamp(op, 0.3, 1) * 255 : 245);
            _panelBrush = new SolidColorBrush(Color.FromArgb(alpha, panelColor.R, panelColor.G, panelColor.B));
            _panelBrush.Freeze();
            Panel.Background = _panelBrush;

            // 整套前景色按底色亮度推导，保证任何底色下文字都可读
            ApplyForegroundPalette(panelColor);

            Input.FontSize = q.TextFontSize;
            // 字号变大时必须同步抬高输入框，否则上下缘会被裁掉。
            // 用真实字形高度来算（不是估系数），再留出安全余量。
            Input.MinHeight = Core.Text.TextMetrics.MinBoxHeight(q.TextFontSize, GlyphSample);
            Results.MaxHeight = q.LineHeight * q.ListCount + 12;
            SizeToContent = SizeToContent.Height;
        }
        catch (Exception ex)
        {
            CoreLog.Warn("Qbar 样式应用失败：" + ex.Message);
        }
    }

    /// <summary>设置页改了外观后调用，强制下次打开重建画笔。</summary>
    public void InvalidateStyle() => _styleStamp = "";

    /// <summary>
    /// 面板显示期间的 Esc 拦截：面板经常抢不到键盘焦点（前台锁定），
    /// 这时 Esc 会打进原来那个程序，面板纹丝不动 —— 用户看到的就是"Esc 关不掉"。
    /// 挂在引擎上就与焦点无关了。
    /// </summary>
    private Func<int, bool>? _escInterceptor;

    private void AttachEscInterceptor()
    {
        if (_host == null) return;

        _escInterceptor ??= vk =>
        {
            if (vk != 0x1B) return false;
            Dispatcher.BeginInvoke(new Action(() => HideQbar()));
            return true;
        };

        _host.Engine.KeyInterceptor = _escInterceptor;
    }

    private void DetachEscInterceptor()
    {
        if (_host == null || _escInterceptor == null) return;
        if (ReferenceEquals(_host.Engine.KeyInterceptor, _escInterceptor))
            _host.Engine.KeyInterceptor = null;
    }

    public void Open(string? prefill = null)
    {
        if (_closing) ReopenFromFade();
        _imeApplied = false;
        _imeKey = "";
        _imeAt = DateTime.MinValue;
        ApplyStyle();
        ClearChip();

        /* 每次打开都重取窗口列表：搜索用的缓存只有几百毫秒，
         * 但用户可能刚切过窗口，这里显式作废一次，第一眼结果更贴近现场。 */
        Core.Windows.WindowService.InvalidateWindowSearchCache();

        if (!IsVisible)
        {
            /* 顺序很重要：**先建句柄 → 再定位 → 最后 Show**。
             *
             * 定位要用到 HWND（才知道该落在哪块屏、那块屏的 DPI 是多少），
             * 但若先 Show 再挪，第一次打开会看到面板从默认位置"跳"一下。
             * EnsureHandle 只创建窗口句柄、不显示，正好满足"先有句柄又不闪"。 */
            try { _ = new WindowInteropHelper(this).EnsureHandle(); } catch { }   /* 预期内：探测/重试型，失败即视为无结果 */

            var fg = Core.Interop.NativeMethods.GetForegroundWindow();
            if (!Core.Windows.ScreenPlacement.Place(this, Core.Windows.ScreenPlacement.Anchor.UpperCenter,
                    target: fg))
            {
                // 拿不到显示器信息时退回旧算法（至少保证在主屏上可见）
                var wa = SystemParameters.WorkArea;
                Left = wa.Left + (wa.Width - Width) / 2;
                Top = wa.Top + wa.Height * 0.22;
            }

            Show();
        }

        /* 抢前台 + 抢输入焦点。
         *
         * 原实现是 Show() 之后直接 Activate()/Focus() —— 那两步在
         * Loc.T("我们不是前台进程")时会被系统拒掉（前台锁定），
         * 面板显示在最上面，但打字仍然打在原来的程序里。
         * 这就是用户说的"换出 Qbar 之后打的字不落在面板里"。 */
        _openedAt = DateTime.Now;
        AttachEscInterceptor();
        StartOutsideWatch();

        /* prefill 有两种形态：
         *   ① "bd "           —— 引擎关键字（由 qbar:<key> 动作传入）
         *   ② Loc.T("要搜索的内容")   —— 普通预填文本
         *
         * 形态 ① 必须**立刻变成标签**，否则它只是输入框里的一段文字：
         * 用户按 Caps+G 打开却看到输入框写着 "go "，还得自己再敲个空格
         * 才变标签 —— 等于"绑定没生效"。这里直接判定并转换。 */
        string pre = prefill ?? "";
        SetInputText(pre);
        bool chipped = pre.Length > 0 && TryMakeChip();
        if (chipped && _chip != null)
        {
            // 真的变成了引擎标签：清空输入，让用户直接打关键词。
            // 记下“面板是带着引擎标签打开的”，输入法必须回到中文，
            // 不能依赖用户另外勾了“选中引擎后切回中文”。
            SetInputText("");
            RunQuery();
        }
        else
        {
            /* 普通预填，或者网址预填：网址不再变标签，整条 URL 就留在输入框里，
             * 这里不能清 —— 清掉等于 qbar:bili 打开是个空面板。 */
            RunQuery();
        }

        /* 先把预填内容变成引擎标签，再抢焦点。
         * 反过来的话，FocusInputNow 可能在 _chip 还没建立时先按“空面板”
         * 应用一次输入法，并把 _imeApplied 置 true；等标签建立后
         * 就没有第二次机会了 —— 这正是“qbar:google 打开仍是英文”的竞态。 */
        BeginForceFocus();

        Input.Focus();
        Keyboard.Focus(Input);
        Input.CaretIndex = Input.Text.Length;

        /* 输入法不在打开瞬间盲按 Shift：此时面板还没拿到前台焦点，
         * Shift 会打给上一个窗口；而且“按一下”是切换而不是确保，
         * 当前已经是中文时反而被切成英文。统一交给 ImeGuard 按当前标签状态
         * 决定“中文还是英文”。 */
        _imeApplied = false;
        _imeKey = "";
        if (IsActive)
            _imeApplied = ApplyImePreference();
        QueueImePreference(120);

        PlayShowAnimation();
    }

    /// <summary>
    /// 离屏渲染用：打开并把某个关键字直接变成标签（相当于用户输入 "bd " 之后的状态）。
    /// </summary>
    public void OpenForRender(string keyword, string? tail = null)
    {
        Open("");
        SetInputText(keyword);
        if (!TryMakeChip()) RunQuery();
        if (!string.IsNullOrEmpty(tail)) SetInputText(tail);
        RunQuery();
    }

    /// <summary>
    /// 仅供 --render：Open 会把面板摆到屏幕**上方居中**（那是它给用户看的正常位置），
    /// 出图时用户并不知道会弹东西，看到的就是"突然冒出个面板"。
    ///
    /// 只改 WPF 的 Left/Top 不够：这个窗口是 Topmost + ShowActivated，
    /// 打开流程里位置还会被重算一次。直接下 Win32 命令挪到虚拟屏外面，
    /// 绕过属性重算；尺寸不变，RenderTargetBitmap 照样截得到内容。
    /// </summary>
    public void ParkOffScreenForRender()
    {
        Left = -32000;
        Top = -32000;

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        Core.Interop.NativeMethods.SetWindowPos(
            hwnd, IntPtr.Zero, -20000, -20000, 0, 0,
            Core.Interop.NativeMethods.SWP_NOSIZE
            | Core.Interop.NativeMethods.SWP_NOZORDER
            | Core.Interop.NativeMethods.SWP_NOACTIVATE);
    }

    private void PlayShowAnimation()
    {
        Panel.BeginAnimation(OpacityProperty, null);
        Panel.Opacity = 1;
        var slide = new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Slide.BeginAnimation(TranslateTransform.YProperty, slide);
    }

    /// <summary>
    /// 收起淡出进行中又被打开：当场作废这次收起。
    ///
    /// 没有这一步的话，旧淡出动画跑完时的 Completed 会把**刚打开**的
    /// 面板 Hide 掉 —— 现象是"点一下外面、紧接着再按热键，面板不出来"。
    /// </summary>
    private void ReopenFromFade()
    {
        Panel.BeginAnimation(OpacityProperty, null);
        Panel.Opacity = 1;
        _closing = false;
    }

    protected override void OnClosed(EventArgs e)
    {
        /* Alt+F4 / 系统关闭不会走 HideQbar；这里必须把全局 Esc 拦截和鼠标监听
         * 一起摘掉，否则 Qbar 消失后整个系统的 Esc 会被永久吞掉。 */
        _closing = true;
        DetachEscInterceptor();
        StopFocusTimer();
        StopOutsideWatch();
        base.OnClosed(e);
    }

    public void HideQbar()
    {
        if (!IsVisible || _closing) return;
        _imeTimer?.Stop();
        _imeVerify?.Stop();
        DetachEscInterceptor();
        StopFocusTimer();
        StopOutsideWatch();
        _closing = true;
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(90));
        fade.Completed += (_, _) =>
        {
            // 收起途中又被 Open() 打开（ReopenFromFade 把 _closing 清了）：
            // 这次收起作废，不能把新打开的面板 Hide 掉。
            if (!_closing) return;
            Hide();
            Panel.BeginAnimation(OpacityProperty, null);
            Panel.Opacity = 1;
            _closing = false;
        };
        Panel.BeginAnimation(OpacityProperty, fade);
    }

    // ---------------- 标签（Chip） ----------------

    /// <summary>
    /// 输入框整段等于一条快捷命令时直接执行（空格触发）。
    /// 返回 false 表示不是快捷命令，交给普通“变标签 / 搜索”流程。
    /// </summary>
    private bool TryRunQuickCommand()
    {
        var q = QbarService.FindQuickCommand(Input.Text);
        if (q == null) return false;
        ExecuteResult(QbarService.ToResult(q));
        return true;
    }

    /// <summary>
    /// 把 "bd" 变成标签。触发时机：输入空格 / Tab 时，
    /// 且当前整段文本恰好是一个已定义的关键字。
    /// </summary>
    private bool TryMakeChip()
    {
        if (_chip != null) return false;
        string text = Input.Text.Trim();
        if (text.Length == 0) return false;
        var chip = _host.Qbar.ResolveChip(text);
        if (chip == null) return false;

        /* 网址关键字（bili / gh / bd…）不再变成标签，直接把完整地址写进输入框。
         *
         * 标签的毛病是：地址被藏进那个小胶囊里，输入框被清空且变成只读 ——
         * 用户看不到自己要去哪，也接不上"打开后继续输路径"。
         * 把 URL 摊开在输入框里，回车就走普通的"打开这个网址"那条路，
         * 行为和手打网址完全一致（还看得见、能改）。 */
        if (chip.Kind == ChipKind.Web && !string.IsNullOrWhiteSpace(chip.Value))
        {
            ClearChip();
            SetInputText(chip.Value);
            Input.Focus();
            Input.CaretIndex = Input.Text.Length;
            RunQuery();
            return true;
        }

        SetChip(chip);
        SetInputText("");
        // 关键字已经变成引擎标签，立刻按“空关键词”刷新一次：
        // 网址类（如 bili）会直接显示首页，搜索类会显示待输入的提示，
        // 不会把上一个关键字留下的结果继续挂在下面。
        RunQuery();
        return true;
    }

    private void SetChip(QbarChip chip)
    {
        _chip = chip;
        ChipLabel.Text = chip.Label;

        /* 网址标签没有“后面的搜索词”这回事：bili + 空格 之后
         * 输入框里再打字也不会参与网址拼接，只会让人误以为还能搜。
         * 这里直接锁住输入框，只保留 Enter（打开）/ Backspace（退回关键字）
         * 这类窗口级按键；搜索/程序标签仍然可以继续输入。 */
        Input.IsReadOnly = chip.Kind == ChipKind.Web;
        Input.IsReadOnlyCaretVisible = chip.Kind != ChipKind.Web;

        if (chip.HasIcon)
        {
            ChipIcon.Source = chip.Icon;
            ChipIconBox.Visibility = Visibility.Visible;
            ChipInitialBox.Visibility = Visibility.Collapsed;
        }
        else
        {
            ChipIcon.Source = null;
            ChipIconBox.Visibility = Visibility.Collapsed;
            ChipInitialBox.Visibility = Visibility.Visible;
            ChipInitial.Text = chip.Initial;
            ChipInitialBox.Background = new SolidColorBrush(chip.Accent);

            /* 字色按品牌色自身亮度选：深牌子用白字、浅牌子用深字。
             * 这里必须跟着底色一起设 —— 否则换一个引擎后，
             * 牌子换了、字色还是上一家的，又会出现"看不见"。 */
            ChipInitial.Foreground = Frozen(BestTextOn(chip.Accent));

            /* 标签本身也要真实图标：启动程序从 exe/lnk 取，
             * 网站从 favicon 取；拿不到才保留首字。 */
            if (chip.Kind == ChipKind.Run)
            {
                var runIcon = IconCache.FromExecutable(chip.Value);
                if (runIcon != null) SetChipIcon(runIcon);
            }
            else if (chip.Kind == ChipKind.Web)
            {
                IconCache.LoadWebIconAsync(chip.Value, image =>
                {
                    if (image == null) return;
                    Dispatcher.BeginInvoke(() =>
                    {
                        if (ReferenceEquals(_chip, chip)) SetChipIcon(image);
                    });
                });
            }
        }

        ChipBox.Visibility = Visibility.Visible;
        ModeIcon.Text = chip.Kind switch
        {
            ChipKind.Search => "\uE721",
            ChipKind.Web => "\uE774",
            _ => "\uE756",
        };
        UpdateMode();

        /* 引擎认出来了，接下来要打的是搜索内容 —— 多半是中文。
         * 只在面板已经拿到焦点时切；否则留给 FocusInputNow 的首次应用，
         * 免得把键发到上一个窗口（这就是“带引擎打开却还是英文”的根因）。 */
        if (IsActive)
            _imeApplied = ApplyImePreference();
        QueueImePreference(100);
    }

    private void SetChipIcon(ImageSource image)
    {
        ChipIcon.Source = image;
        ChipIconBox.Visibility = Visibility.Visible;
        ChipInitialBox.Visibility = Visibility.Collapsed;
    }

    private void ClearChip()
    {
        _chip = null;
        Input.IsReadOnly = false;
        Input.IsReadOnlyCaretVisible = true;
        ChipBox.Visibility = Visibility.Collapsed;
        ChipIcon.Source = null;
        ModeIcon.Text = "\uE721";
    }

    /// <summary>移除标签，并把关键字放回输入框，方便直接改。</summary>
    private void RemoveChip(bool restoreText = true)
    {
        if (_chip == null) return;
        string key = _chip.Key;
        ClearChip();
        if (restoreText) SetInputText(key);
        else RunQuery();
        Input.Focus();
        Input.CaretIndex = Input.Text.Length;
        if (IsActive)
        {
            _imeApplied = ApplyImePreference();
            QueueImePreference(100);
        }
    }

    private void OnChipClose(object sender, RoutedEventArgs e) => RemoveChip();

    private void SetInputText(string text)
    {
        _suppressTextChanged = true;
        Input.Text = text;
        Input.CaretIndex = text.Length;
        _suppressTextChanged = false;
    }

    // ---------------- 输入与检索 ----------------

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { HideQbar(); e.Handled = true; return; }

        /* 网址标签会把输入框设为只读；某些 WPF/输入法组合下只读 TextBox
         * 收不到 PreviewKeyDown，退格就“卡死”在标签状态。窗口级再兜一层：
         * 输入框为空时退格 → 拆掉标签、把关键字放回输入框。 */
        if (e.Key == Key.Back && _chip != null && Input.Text.Length == 0)
        {
            RemoveChip();
            e.Handled = true;
        }
    }

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.P && Keyboard.Modifiers == ModifierKeys.Control)
        {
            PinSelected();
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.Escape:
                HideQbar();
                e.Handled = true;
                break;
            case Key.Space:
                /* 快捷命令优先：cmd / psh / hosts 这类词打完后按空格就直接执行，
                 * 不再要求“先变成标签、再按一次回车”。 */
                if (TryRunQuickCommand()) { e.Handled = true; break; }
                // 空格：如果整段正好是关键字，就转成标签（之后空格用于分词）
                if (TryMakeChip()) { e.Handled = true; break; }
                break;
            case Key.Tab:
                if (TryMakeChip()) { e.Handled = true; break; }
                CompleteSelected();
                e.Handled = true;
                break;
            case Key.Back:
                // 输入框已空时按退格 → 把标签还原成文字
                if (_chip != null && Input.Text.Length == 0)
                {
                    RemoveChip();
                    e.Handled = true;
                }
                break;
            case Key.Down:
                MoveSelection(1);
                e.Handled = true;
                break;
            case Key.Up:
                MoveSelection(-1);
                e.Handled = true;
                break;
            case Key.PageDown:
                MoveSelection(5);
                e.Handled = true;
                break;
            case Key.PageUp:
                MoveSelection(-5);
                e.Handled = true;
                break;
            case Key.Enter:
                /* 搜索/网址/程序这几类结果，回车先"补全成引擎 + 空格"，
                 * 第二次回车才真正执行。
                 *
                 * 原因：用户敲 `bd` 时匹配到的是百度**搜索引擎**这一条，
                 * 它的载荷是 `https://www.baidu.com/s?wd={q}` 这种模板。
                 * 直接执行会把没替换的 {q} 原样打开 —— 浏览器里出现
                 * "搜索 {q}" 这种结果。补全是这一行本来的意图：
                 * 变成「bd 」标签，接着打关键词即可。 */
                if (Results.SelectedItem is QbarItem sel && ShouldCompleteOnEnter(sel.Result))
                {
                    CompleteSelected();
                    e.Handled = true;
                    break;
                }
                ExecuteSelected();
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// 这一条结果回车时是否该"补全"而不是"执行"。
    ///
    /// 判据：归类里带标签（说明它是个"引擎 / 程序 / 网址前缀"），
    /// 且载荷里还有没填的占位符 —— 那它就不是一个可以直接打开的地址。
    /// </summary>
    private static bool ShouldCompleteOnEnter(QbarResult r)
        => r.Kind is QbarKind.Search or QbarKind.Web or QbarKind.Run
           && r.Badge.Length > 0
           && r.Payload.Contains("{q}", StringComparison.Ordinal);

    private void MoveSelection(int delta)
    {
        if (_items.Count == 0) return;
        int idx = Math.Clamp(Results.SelectedIndex + delta, 0, _items.Count - 1);
        Results.SelectedIndex = idx;
        Results.ScrollIntoView(Results.SelectedItem);
    }

    private void CompleteSelected()
    {
        if (Results.SelectedItem is not QbarItem item) return;
        // 补全成“关键字 + 空格”，下一次按键即可变成标签
        string completion = item.Result.Kind switch
        {
            QbarKind.Run or QbarKind.Web or QbarKind.Search when item.Result.Badge.Length > 0
                => item.Result.Badge + " ",
            _ => item.Result.Title,
        };
        SetInputText(completion);
        TryMakeChip();      // 成了标签就等于换了引擎，没成就只是段文字 —— 两种情况都要重搜
        RunQuery();
    }

    private void OnInputChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressTextChanged) return;
        RunQuery();
    }

    private void RunQuery()
    {
        var results = _host.Qbar.Query(Input.Text, _chip, Math.Max(6, _host.Settings.Qbar.ListCount + 2));
        _items = results.Select(r => new QbarItem(r, _host.Settings.Qbar.ShowIcons)).ToList();
        Results.ItemsSource = _items;
        if (_items.Count > 0) Results.SelectedIndex = 0;
        UpdateMode();
        UpdateHint();

        /* 结果行的文字颜色必须在**列表项生成之后**才能刷。
         *
         * ApplyStyle 里遍历可视树时，DataTemplate 还没实例化，
         * 结果就是行内标题保持 XAML 里写死的浅色 —— 浅色面板上几乎看不见。
         * 这里等布局完成再补一遍。 */
        Dispatcher.BeginInvoke(new Action(() => ApplyRowPalette()),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>给结果行里的文字重新上色（模板生成后调用）。</summary>
    private void ApplyRowPalette()
    {
        var q = _host.Settings.Qbar;
        bool followTheme = string.IsNullOrWhiteSpace(q.PanelColor)
                           || q.PanelColor.Equals("auto", StringComparison.OrdinalIgnoreCase);
        bool light = IsLightColor(followTheme ? ThemePanelColor() : ParseColor(q.PanelColor));

        Color primary = light ? Color.FromRgb(0x16, 0x1A, 0x23) : Colors.White;
        Color secondary = light ? Color.FromRgb(0x5B, 0x64, 0x78) : Color.FromRgb(0xC9, 0xD6, 0xEC);
        Color faint = light ? Color.FromRgb(0x8A, 0x93, 0xA6) : Color.FromRgb(0x7F, 0x8D, 0xA8);

        var bPrimary = Frozen(primary);
        var bSecondary = Frozen(secondary);
        var bFaint = Frozen(faint);

        // 只处理列表区域，别动输入框和提示行（它们已由 ApplyStyle 刷过）
        foreach (var tb in FindAll<TextBlock>(Results))
        {
            tb.Foreground = tb.FontSize switch
            {
                >= 13.5 => bPrimary,      // 行标题
                >= 11 => bSecondary,      // 副标题
                _ => bFaint,              // 徽标、角标
            };
        }
    }

    private void UpdateMode()
    {
        if (_chip != null)
        {
            HintText.Text = _chip.Kind switch
            {
                ChipKind.Search => Loc.T("Enter 搜索"),
                ChipKind.Web => Loc.T("Enter 打开"),
                _ => Loc.T("Enter 启动"),
            };
            return;
        }
        string text = Input.Text;
        string hint = Loc.T("Enter 执行");
        if (text.StartsWith('=')) hint = "Enter 复制结果";
        else if (text.StartsWith('/')) hint = "内置命令";
        else if (text.Contains(' ') && _host.Settings.Search.Any(s =>
                     s.Key.Equals(text.Split(' ')[0], StringComparison.OrdinalIgnoreCase)))
            hint = Loc.T("搜索");
        else if (text.Length > 0 && QbarService.LooksLikeMath(text)) hint = Loc.T("计算");
        HintText.Text = hint;
    }

    private void UpdateHint()
    {
        if (Results.SelectedItem is not QbarItem item) return;
        HintText.Text = item.Result.Kind switch
        {
            QbarKind.Math => Loc.T("Enter 复制结果"),
            QbarKind.Clipboard => Loc.T("Enter 粘贴"),
            QbarKind.Window => Loc.T("Enter 切换窗口"),
            QbarKind.Command => Loc.T("Enter 执行"),
            QbarKind.Search => _chip != null ? Loc.T("Enter 搜索") : Loc.T("Enter 打开搜索"),
            QbarKind.Web => Loc.T("Enter 打开网址"),
            QbarKind.Run => Loc.T("Enter 启动"),
            _ => Loc.T("Enter 执行"),
        };
    }

    /// <summary>Ctrl+P：把当前选中的文件/文件夹/网址/程序固定到 Qbar 空面板。</summary>
    private void PinSelected()
    {
        if (Results.SelectedItem is not QbarItem item) return;
        var r = item.Result;
        if (r.Kind is not (QbarKind.File or QbarKind.Run or QbarKind.Web))
        {
            _host.Notify(Loc.T("这条结果不能固定：请选择文件、文件夹、网址或程序"));
            return;
        }
        if (string.IsNullOrWhiteSpace(r.Payload)) return;

        var pinned = _host.Settings.Qbar.Pinned;
        if (pinned.Any(x => x.Value.Equals(r.Payload, StringComparison.OrdinalIgnoreCase)))
        {
            _host.Notify(Loc.T("这个目标已经固定过了"));
            return;
        }

        string title = string.IsNullOrWhiteSpace(r.Title)
            ? (System.IO.Path.GetFileName(r.Payload.TrimEnd('\\', '/')) is { Length: > 0 } name ? name : r.Payload)
            : r.Title;
        string key = title;
        int n = 2;
        while (pinned.Any(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase))) key = $"{title} {n++}";

        pinned.Add(new QEntry
        {
            Key = key,
            Value = r.Payload,
            Comment = title,
            IconPath = r.IconPath ?? "",
            Arguments = r.Arguments,
            RunAsAdmin = r.RunAsAdmin,
        });
        _host.SaveSettings();
        /* 固定是"立刻要生效"的动作：不能等 400ms 防抖。
         * 用户按完 Ctrl+P 可能马上就关面板/退程序，没 flush 的话
         * 这一条只活在内存里，重启后固定项就凭空消失了。 */
        _host.FlushSettings();
        _host.Notify(Loc.F("已固定到 Qbar：{0}", title));
    }

    private void ExecuteSelected()
    {
        if (_closing) return;
        if (Results.SelectedItem is not QbarItem item) return;
        ExecuteResult(item.Result);
    }

    /// <summary>
    /// 执行一条结果。`ExecuteSelected` 和“空格直接触发快捷命令”共用，
    /// 保证两条路径的收尾（隐藏面板、日志、异常提示）完全一致。
    /// </summary>
    private void ExecuteResult(QbarResult result)
    {
        /* 面板一开始淡出就不再执行：双击手势的两次点击间隔很短
         * （第二次抬起常常落在 90ms 淡出还没结束的时间里），没有这道
         * 闸门的话同一个结果会被执行两遍 —— 启动程序开两个窗口、
         * 打开网址开两个标签页。淡出期间的 Enter 同理。 */
        if (_closing) return;

        /* 记一行日志：这是"到底执行了几次"的唯一可靠观测量。
         * e2e 曾经靠"新起几个记事本进程"判断双击是否只执行一次，
         * 但 Win11 的记事本是标签页复用的 —— 执行两次也只多一个标签、
         * 不多进程，于是测试把"执行了"误判成"没执行"。
         * 日志不受系统行为影响，也不会被单实例程序带偏。 */
        CoreLog.Info($"[Qbar] 执行结果：{result.Title}");

        HideQbar();
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                _host.Qbar.Execute(result, BuildCallbacks());
            }
            catch (Exception ex)
            {
                CoreLog.Error("Qbar 执行失败", ex);
                _host.Notify(Loc.T("执行失败：") + ex.Message);
            }
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>
    /// 在文件结果上右键：给出文件管理器里最常用的那一组操作。
    ///
    /// 这里不直接调用 Explorer 的原生 IContextMenu —— Qbar 是无边框弹窗，
    /// 原生菜单的 owner 消息循环和它的关闭逻辑容易互相踩。先覆盖用户
    /// 明确要的“打开位置 / 复制 / 剪切 / 复制路径 / 属性”，行为稳定且可测。
    /// </summary>
    private void OnResultsRightClick(object sender, MouseButtonEventArgs e)
    {
        if (Results.ContainerFromElement(e.OriginalSource as DependencyObject) is not ListBoxItem row ||
            row.DataContext is not QbarItem item)
            return;

        var r = item.Result;
        string path = r.Payload;
        if (r.Kind != QbarKind.File || path.Length == 0) return;

        Results.SelectedItem = item;
        var menu = BuildFileMenu(r, path);
        Results.ContextMenu = menu;
        menu.PlacementTarget = row;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private ContextMenu BuildFileMenu(QbarResult result, string path)
    {
        var menu = new ContextMenu();
        bool exists = File.Exists(path) || Directory.Exists(path);

        MenuItem Add(string text, Action action, bool enabled = true)
        {
            var mi = new MenuItem { Header = Loc.T(text), IsEnabled = enabled };
            mi.Click += (_, _) => action();
            menu.Items.Add(mi);
            return mi;
        }

        Add("打开", () => ExecuteResult(result));
        Add("打开所在文件夹", () => OpenContainingFolder(path), exists);
        menu.Items.Add(new Separator());
        Add("复制", () => SetFileClipboard(path, cut: false), exists);
        Add("剪切", () => SetFileClipboard(path, cut: true), exists);
        Add("复制路径", () => CopyText(path));
        menu.Items.Add(new Separator());
        Add("属性", () => ShowProperties(path), exists);
        return menu;
    }

    private void OpenContainingFolder(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                string? parent = Directory.GetParent(path)?.FullName;
                Process.Start(new ProcessStartInfo("explorer.exe", parent is null ? $"\"{path}\"" : $"\"{parent}\"")
                {
                    UseShellExecute = true,
                });
                return;
            }

            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _host.Notify(Loc.T("打开文件夹失败：") + ex.Message);
        }
    }

    private void SetFileClipboard(string path, bool cut)
    {
        try
        {
            var data = new DataObject();
            data.SetFileDropList(new StringCollection { path });
            // Explorer 用这个 DWORD 区分“复制”和“剪切”：1=复制，2=剪切。
            data.SetData("Preferred DropEffect", new MemoryStream(cut ? new byte[] { 2, 0, 0, 0 } : new byte[] { 1, 0, 0, 0 }));
            Clipboard.SetDataObject(data, copy: true);
            _host.Notify(cut ? Loc.T("已剪切到剪贴板") : Loc.T("已复制到剪贴板"));
        }
        catch (Exception ex)
        {
            _host.Notify(Loc.T("写入剪贴板失败：") + ex.Message);
        }
    }

    private void CopyText(string text)
    {
        try { Clipboard.SetText(text); _host.Notify(Loc.T("路径已复制")); }
        catch (Exception ex) { _host.Notify(Loc.T("复制失败：") + ex.Message); }
    }

    private void ShowProperties(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = "properties" });
        }
        catch (Exception ex)
        {
            _host.Notify(Loc.T("打开属性失败：") + ex.Message);
        }
    }

    private QbarService.QbarCallbacks BuildCallbacks() => new(
        Notify: _host.Notify,
        CopyToClipboard: text =>
        {
            try { System.Windows.Clipboard.SetText(text); } catch { }   /* 预期内：清理动作，失败无副作用 */
        },
        Paste: text => _host.Clipboard.PasteText(text),
        OpenTarget: OpenTarget,
        RunTarget: RunTarget,
        RunCommand: RunCommand,
        RunAction: action => _host.InvokeAction(action, ""));

    private void OpenTarget(string target)
    {
        if (target.StartsWith("http", StringComparison.OrdinalIgnoreCase) ||
            target.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
        {
            _host.Context.OpenUrl(target);
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _host.Notify(Loc.T("打开失败：") + ex.Message);
        }
    }

    private void RunTarget(string target, bool asAdmin, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(target)
            {
                UseShellExecute = true,
                Arguments = args,
            };
            if (asAdmin) psi.Verb = "runas";
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            _host.Notify(Loc.T("启动失败：") + ex.Message);
        }
    }

    private void RunCommand(string command)
    {
        switch (command)
        {
            case "openSettings": _host.ShowMainWindow("settings"); break;
            case "openBindings": _host.ShowMainWindow("bindings"); break;
            case "openHelp": _host.ShowMainWindow("help"); break;
            case "openClipboard": _host.ShowClipboardHistory(); break;
            case "openMath": _host.ShowMathBoard(); break;
            case "touch": CreateDesktopFile(); break;
            case "reload": _host.ReloadAll(); break;
            case "toggleEnabled": _host.ToggleEnabled(); break;
            case "exit": _host.ExitApp(); break;
        }
    }

    /// <summary>
    /// 快捷命令 touch：在桌面新建一个空文件。
    /// 只收文件名（去掉任何目录部分），避免把文件写到用户没预期的地方。
    /// </summary>
    private void CreateDesktopFile()
    {
        string? name = TextPromptWindow.Ask(this, Loc.T("创建文件"), Loc.T("文件名"), "新建文件.txt");
        if (string.IsNullOrWhiteSpace(name)) return;

        name = System.IO.Path.GetFileName(name.Trim());
        if (name.Length == 0) return;

        string dir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        /* 桌面可以被用户重定向到 D:\Desktop 等位置，而 SpecialFolder 仍返回
         * C:\Users\...\Desktop。优先读 shell 的 User Shell Folders，保证
         * “桌面新建文件”真的出现在用户看到的桌面上。 */
        try
        {
            if (Microsoft.Win32.Registry.GetValue(
                    @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders",
                    "Desktop", null) is string redirected && redirected.Length > 0)
            {
                string expanded = Environment.ExpandEnvironmentVariables(redirected);
                if (System.IO.Directory.Exists(expanded)) dir = expanded;
            }
        }
        catch { }
        string path = System.IO.Path.Combine(dir, name);
        if (System.IO.File.Exists(path))
        {
            string stem = System.IO.Path.GetFileNameWithoutExtension(name);
            string ext = System.IO.Path.GetExtension(name);
            for (int i = 1; i < 1000 && System.IO.File.Exists(path); i++)
                path = System.IO.Path.Combine(dir, $"{stem} ({i}){ext}");
        }

        try
        {
            System.IO.File.WriteAllText(path, "");
            _host.Notify(Loc.T("已创建：") + path);
        }
        catch (Exception ex)
        {
            _host.Notify(Loc.T("创建失败：") + ex.Message);
        }
    }

    private void OnResultClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && FindAncestor<ListBoxItem>(d) != null)
            ExecuteSelected();
    }

    /* 原来这里还有一个 OnResultDoubleClick（XAML MouseDoubleClick 也挂着）：
     * 单击已经会执行并收起面板，它纯属重复，而且不像单击那样判断点没点
     * 中条目 —— 双击列表空白处也会把当前选中项执行一遍。
     * 已删除；Loc.T("双击只执行一次")由 ExecuteSelected 里的 _closing 闸门保证。 */

    private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null)
        {
            if (d is T t) return t;
            d = VisualTreeHelper.GetParent(d);
        }
        return null;
    }

    private sealed class QbarItem : INotifyPropertyChanged
    {
        private ImageSource? _iconSource;
        private bool _hasIcon;

        public QbarItem(QbarResult result, bool showIcons = true)
        {
            Result = result;
            var chip = result.Chip;

            /* 「显示图标」这个设置项之前**没人读** ——
             * 界面上能勾掉，但列表照样显示图标，属于典型表面实现。
             * 现在关掉时只显示文字，不再画图标。 */
            if (!showIcons)
            {
                // 什么都不画，靠文本区分
            }
            else if (chip is { HasIcon: true })
            {
                SetIcon(chip.Icon);
            }
            else if (chip != null)
            {
                Letter = chip.Initial;
                LetterBrush = new SolidColorBrush(chip.Accent);
                HasLetter = true;
                TryResolveIcon(result);
            }
            else
            {
                TryResolveIcon(result);
            }

            IconGlyph = result.Kind switch
            {
                QbarKind.Search => "\uE721",
                QbarKind.Web => "\uE774",
                QbarKind.Run => "\uE756",
                QbarKind.Window => "\uE737",
                QbarKind.Math => "\uE8EF",
                QbarKind.Clipboard => "\uE77F",
                QbarKind.File => "\uE8B7",
                _ => "\uE756",
            };
            Badge = result.Kind == QbarKind.Command ? result.Payload : result.KindLabel;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void TryResolveIcon(QbarResult result)
        {
            ImageSource? icon = null;
            if (!string.IsNullOrWhiteSpace(result.IconPath))
            {
                icon = IconCache.FromFile(result.IconPath);
            }
            if (icon == null && result.Kind is QbarKind.Run or QbarKind.File or QbarKind.Window)
            {
                icon = IconCache.FromExecutable(result.Payload);
            }
            if (icon == null && result.Kind == QbarKind.File)
            {
                icon = IconCache.FromFile(result.Payload);
            }
            if (icon != null)
            {
                SetIcon(icon);
            }
            else if (result.Kind == QbarKind.Web && !string.IsNullOrWhiteSpace(result.Payload))
            {
                /* favicon 必须联网，不能卡住 Qbar 的查询/打开。
                 * 先给字形，图标到了再通过 PropertyChanged 刷新这一行。 */
                string url = result.Payload;
                IconCache.LoadWebIconAsync(url, image =>
                {
                    if (image == null) return;
                    Application.Current?.Dispatcher.BeginInvoke(() => SetIcon(image));
                });
            }
        }

        private void SetIcon(ImageSource? icon)
        {
            if (icon == null) return;
            _iconSource = icon;
            _hasIcon = true;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IconSource)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasIcon)));
        }

        public QbarResult Result { get; }
        public string IconGlyph { get; }
        public string Badge { get; }
        public ImageSource? IconSource => _iconSource;
        public bool HasIcon => _hasIcon;
        public bool HasLetter { get; }
        public string Letter { get; } = "";
        public Brush LetterBrush { get; } = Brushes.Transparent;
        public string Title => Result.Title;
        public string Subtitle => Result.Subtitle;
    }
}
