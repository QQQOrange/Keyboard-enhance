using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using CapsLockPlus.Core;
using CapsLockPlus.Core.Icons;
using CapsLockPlus.Core.Qbar;
using CapsLockPlus.Core.Windows;
using CapsLockPlus.UI.Theme;

namespace CapsLockPlus.UI.Windows;

public partial class QbarWindow : Window
{
    private readonly AppHost _host;
    private List<QbarItem> _items = new();
    private QbarChip? _chip;
    private bool _closing;
    private bool _suppressTextChanged;

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
        }
        catch
        {
            // 焦点操作失败不该影响面板本身
        }
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
            try { _hwnd = new WindowInteropHelper(this).Handle; } catch { }
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
        catch { }
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

    /// <summary>把整套配色应用到面板里的每个元素。</summary>
    private void ApplyForegroundPalette(Color panelColor)
    {
        bool light = IsLightColor(panelColor);

        // 前景色阶：主文字 → 次级 → 弱化
        Color primary = light ? Color.FromRgb(0x16, 0x1A, 0x23) : Colors.White;
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

        ChipLabel.Foreground = bSecondary;
        ChipClose.Foreground = bMuted;
        ChipBox.Background = bVeil13;
        ChipBox.BorderBrush = bVeil20;

        ChipInitialBox.Background = bVeil13;
        ChipInitial.Foreground = bPrimary;
        HintText.Foreground = bMuted;
        SepLine.Background = bVeil10;

        // 「当前分类」角标——它的底色是强调色，文字始终用白
        if (ModeIcon.Parent is Border modeBorder) modeBorder.Background = bVeil13;

        /* 结果列表项在 DataTemplate 里，逐个取出来改色。
         * 用 ItemContainerGenerator 拿容器，ContainerFromItem 在虚拟化下
         * 可能为空，所以这里遍历可视树更可靠。 */
        foreach (var tb in FindAll<TextBlock>(this))
        {
            string? name = tb.Name;
            // 模板内的元素没有 x:Name，按字号/绑定特征分类
            if (tb.FontSize >= 13.5) tb.Foreground = bPrimary;
            else if (tb.FontSize >= 11) tb.Foreground = bSecondary;
            else tb.Foreground = bFaint;
            _ = name;
        }

        // 再按具体名字覆盖一次（上面那轮会把模板外的也刷成近似色）
        ModeIcon.Foreground = bSecondary;
        Input.Foreground = bPrimary;
        ChipLabel.Foreground = bSecondary;
        ChipClose.Foreground = bMuted;
        HintText.Foreground = bMuted;
        ChipInitial.Foreground = bPrimary;

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
        catch { }
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
            Input.MinHeight = Core.Text.TextMetrics.MinBoxHeight(q.TextFontSize, "汉字Agqy");
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

    public void Open(string? prefill = null)
    {
        ApplyStyle();
        ClearChip();
        if (!IsVisible)
        {
            var wa = SystemParameters.WorkArea;
            Left = wa.Left + (wa.Width - Width) / 2;
            Top = wa.Top + wa.Height * 0.22;
            Show();
        }

        /* 抢前台 + 抢输入焦点。
         *
         * 原实现是 Show() 之后直接 Activate()/Focus() —— 那两步在
         * "我们不是前台进程"时会被系统拒掉（前台锁定），
         * 面板显示在最上面，但打字仍然打在原来的程序里。
         * 这就是用户说的"换出 Qbar 之后打的字不落在面板里"。 */
        _openedAt = DateTime.Now;
        BeginForceFocus();

        /* prefill 有两种形态：
         *   ① "bd "           —— 引擎关键字（由 qbar:<key> 动作传入）
         *   ② "要搜索的内容"   —— 普通预填文本
         *
         * 形态 ① 必须**立刻变成标签**，否则它只是输入框里的一段文字：
         * 用户按 Caps+G 打开却看到输入框写着 "go "，还得自己再敲个空格
         * 才变标签 —— 等于"绑定没生效"。这里直接判定并转换。 */
        string pre = prefill ?? "";
        SetInputText(pre);
        if (pre.Length > 0 && TryMakeChip())
        {
            // 已变成引擎标签：清空输入，让用户直接打关键词
            SetInputText("");
            RunQuery();
        }
        else if (pre.Length == 0)
        {
            RunQuery();
        }
        else
        {
            RunQuery();       // 普通预填：按内容正常搜
        }

        Input.Focus();
        Keyboard.Focus(Input);
        Input.CaretIndex = Input.Text.Length;
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

    public void HideQbar()
    {
        if (!IsVisible || _closing) return;
        StopFocusTimer();
        _closing = true;
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(90));
        fade.Completed += (_, _) =>
        {
            Hide();
            Panel.BeginAnimation(OpacityProperty, null);
            Panel.Opacity = 1;
            _closing = false;
        };
        Panel.BeginAnimation(OpacityProperty, fade);
    }

    // ---------------- 标签（Chip） ----------------

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
        SetChip(chip);
        SetInputText("");
        return true;
    }

    private void SetChip(QbarChip chip)
    {
        _chip = chip;
        ChipLabel.Text = chip.Label;

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
        }

        ChipBox.Visibility = Visibility.Visible;
        ModeIcon.Text = chip.Kind switch
        {
            ChipKind.Search => "\uE721",
            ChipKind.Web => "\uE774",
            _ => "\uE756",
        };
        UpdateMode();
    }

    private void ClearChip()
    {
        _chip = null;
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
        if (e.Key == Key.Escape) { HideQbar(); e.Handled = true; }
    }

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                HideQbar();
                e.Handled = true;
                break;
            case Key.Space:
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
                ExecuteSelected();
                e.Handled = true;
                break;
        }
    }

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
        if (TryMakeChip()) RunQuery();
        else RunQuery();
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
                ChipKind.Search => "Enter 搜索",
                ChipKind.Web => "Enter 打开",
                _ => "Enter 启动",
            };
            return;
        }
        string text = Input.Text;
        string hint = "Enter 执行";
        if (text.StartsWith('=')) hint = "Enter 复制结果";
        else if (text.StartsWith('/')) hint = "内置命令";
        else if (text.Contains(' ') && _host.Settings.Search.Any(s =>
                     s.Key.Equals(text.Split(' ')[0], StringComparison.OrdinalIgnoreCase)))
            hint = "搜索";
        else if (text.Length > 0 && QbarService.LooksLikeMath(text)) hint = "计算";
        HintText.Text = hint;
    }

    private void UpdateHint()
    {
        if (Results.SelectedItem is not QbarItem item) return;
        HintText.Text = item.Result.Kind switch
        {
            QbarKind.Math => "Enter 复制结果",
            QbarKind.Clipboard => "Enter 粘贴",
            QbarKind.Window => "Enter 切换窗口",
            QbarKind.Command => "Enter 执行",
            QbarKind.Search => _chip != null ? "Enter 搜索" : "Enter 打开搜索",
            QbarKind.Web => "Enter 打开网址",
            QbarKind.Run => "Enter 启动",
            _ => "Enter 执行",
        };
    }

    private void ExecuteSelected()
    {
        if (Results.SelectedItem is not QbarItem item) return;
        var result = item.Result;
        HideQbar();
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                var cb = new QbarService.QbarCallbacks(
                    Notify: _host.Notify,
                    CopyToClipboard: text =>
                    {
                        try { System.Windows.Clipboard.SetText(text); } catch { }
                    },
                    Paste: text => _host.Clipboard.PasteText(text),
                    OpenTarget: OpenTarget,
                    RunTarget: RunTarget,
                    RunCommand: RunCommand);
                _host.Qbar.Execute(result, cb);
            }
            catch (Exception ex)
            {
                CoreLog.Error("Qbar 执行失败", ex);
                _host.Notify("执行失败：" + ex.Message);
            }
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

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
            _host.Notify("打开失败：" + ex.Message);
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
            _host.Notify("启动失败：" + ex.Message);
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
            case "reload": _host.ReloadAll(); break;
            case "toggleEnabled": _host.ToggleEnabled(); break;
            case "exit": _host.ExitApp(); break;
        }
    }

    private void OnResultClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && FindAncestor<ListBoxItem>(d) != null)
            ExecuteSelected();
    }

    private void OnResultDoubleClick(object sender, MouseButtonEventArgs e) => ExecuteSelected();

    private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null)
        {
            if (d is T t) return t;
            d = VisualTreeHelper.GetParent(d);
        }
        return null;
    }

    private sealed class QbarItem
    {
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
                IconSource = chip.Icon;
                HasIcon = true;
            }
            else if (chip != null)
            {
                Letter = chip.Initial;
                LetterBrush = new SolidColorBrush(chip.Accent);
                HasLetter = true;
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

        public QbarResult Result { get; }
        public string IconGlyph { get; }
        public string Badge { get; }
        public ImageSource? IconSource { get; }
        public bool HasIcon { get; }
        public bool HasLetter { get; }
        public string Letter { get; } = "";
        public Brush LetterBrush { get; } = Brushes.Transparent;
        public string Title => Result.Title;
        public string Subtitle => Result.Subtitle;
    }
}
