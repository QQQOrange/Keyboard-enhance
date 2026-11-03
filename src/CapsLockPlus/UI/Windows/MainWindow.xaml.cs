using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using CapsLockPlus.UI.Theme;
using CapsLockPlus.UI.Views;

namespace CapsLockPlus.UI.Windows;

public partial class MainWindow : Window
{
    private readonly AppHost _host;
    private readonly Dictionary<string, UserControl> _pages = new();
    private string _current = "";
    private string? _pendingPage;

    /// <summary>
    /// 基础最小尺寸（= XAML 里写的那两个值）。
    ///
    /// 在构造函数里从 XAML 读出来存着，而不是另写一份常量：
    /// 两份数字一旦不同步，"离开按键映射页之后窗口缩不回原来的大小"
    /// 这种问题会非常难查。
    /// </summary>
    private readonly double _baseMinWidth;
    private readonly double _baseMinHeight;

    /* 按键映射页会把窗口撑大（要装下整把键盘）。撑大之后必须能缩回去：
     * WPF 在 MinWidth 变小时**不会**自动缩窗口，于是"进过一次按键映射页，
     * 窗口就永远变宽了" —— 用户还得自己拖回去，而且窗口尺寸如果被记住，
     * 下次启动也是宽的。这里记下撑大前的尺寸，离开那一页时还原。 */
    private bool _grownForKeys;
    private double _grownWidth;
    private double _grownHeight;
    private double _restoreWidth;
    private double _restoreHeight;

    /// <summary>当前主窗口实例（AppHost 需要用它导航到某个页面）。</summary>
    public static MainWindow? Instance { get; private set; }

    public MainWindow(AppHost host)
    {
        _host = host;
        Instance = this;
        InitializeComponent();

        _baseMinWidth = MinWidth;
        _baseMinHeight = MinHeight;

        NavList.SelectedIndex = 0;
        Loaded += OnLoaded;
        StateChanged += OnStateChanged;
        Closing += OnClosing;
        Closed += OnClosed;
        _host.EnabledChanged += OnEnabledChanged;
        _host.SettingsChanged += UpdateStatus;
    }

    /// <summary>
    /// 窗口真的关掉之后（用户关掉了「关闭时最小化到托盘」）：
    /// 退订宿主事件、清掉静态实例。
    ///
    /// 不退订的话，宿主的事件会一直握着这个已经关闭的窗口 ——
    /// 之后再新建一个主窗口，同一个事件上就挂着两个处理者
    /// （死窗口 + 活窗口），既泄漏又会让状态刷新做两遍。
    /// </summary>
    private void OnClosed(object? sender, EventArgs e)
    {
        _host.EnabledChanged -= OnEnabledChanged;
        _host.SettingsChanged -= UpdateStatus;
        if (ReferenceEquals(Instance, this)) Instance = null;

        // 页面里也有订阅了宿主事件的（例如「同步与备份」），
        // 走 Unloaded 让它们自己退订 —— 关闭窗口时 WPF 不保证会发 Unloaded，
        // 所以这里显式通知一次。
        foreach (var page in _pages.Values)
        {
            if (page is FrameworkElement fe) fe.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        WindowChrome.Apply(this, ThemeManager.Current == ThemeManager.Mode.Dark ||
                                 (ThemeManager.Current == ThemeManager.Mode.System && ThemeManager.IsDarkSystem));
        WindowChrome.EnableRounded(this);
        UpdateStatus();
        // 首次运行且没有指定页面时，自动停在“快速上手”
        if (_pendingPage == null && !_host.Settings.Global.OnboardingDone)
        {
            Navigate("guide");
        }
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        BorderThickness = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (ShouldExitOnClose(_host.Settings.Global.CloseToTray, _host.Exiting))
        {
            /* 用户把「关闭时最小化到托盘」关掉了 —— 这是"点 × 就退出程序"的意思
             * （设置项注释与界面文案都是这么写的）。
             *
             * 以前这里什么都不做：窗口关掉了，但 ShutdownMode 是 OnExplicitShutdown，
             * 没有任何人调 Shutdown —— 于是**托盘图标和键盘钩子都还在跑**。
             * 用户以为自己退了程序，结果按住 CapsLock 依旧满屏快捷键。
             *
             * 现在交给 ExitApp 统一收尾（落盘、统计、托盘、退出同步都走那一条路），
             * 并取消这次 Closing，避免窗口先关、退出流程再动它。 */
            e.Cancel = true;
            _host.ExitApp();
            return;
        }

        if (!_host.Exiting)
        {
            e.Cancel = true;
            Hide();
        }
    }

    /// <summary>
    /// 点 × 时该不该真的退出。
    ///
    /// 拆成纯函数是为了能被自检钉住：这里一旦判错，症状是
    /// "窗口没了但程序还在后台接管键盘"，用户很难联想到设置项。
    /// </summary>
    internal static bool ShouldExitOnClose(bool closeToTray, bool exiting)
        => !closeToTray && !exiting;

    /// <summary>
    /// 侧栏页面的**唯一顺序表**：下标 = NavList 里 ListBoxItem 的位置。
    ///
    /// 这张表必须和 MainWindow.xaml 里 NavList 的项**同序**。
    /// v4.5.10 就吃过一次亏：删掉「翻译」页时只改了 XAML、没改这里的映射，
    /// 结果点「宏」高亮跑到「触发键与层」、点「关于」拿到越界下标。
    /// 现在索引统一由这张表算出来，不再散落在一个 switch 里。
    /// </summary>
    internal static readonly string[] NavOrderForTest =
    {
        "guide", "keys", "hotstrings", "qbar", "clipboard", "macros",
        "layers", "backup", "settings", "test", "help", "about",
    };

    /// <summary>页面名 → 侧栏下标；未知页面退回第一项（和原来 switch 的 _ => 0 一致）。</summary>
    private static int NavIndexOf(string page)
    {
        int i = Array.IndexOf(NavOrderForTest, page);
        return i >= 0 ? i : 0;
    }

    public void Navigate(string page)
    {
        _pendingPage = page;
        int index = NavIndexOf(page);
        if (NavList.SelectedIndex != index) NavList.SelectedIndex = index;
        else ShowPage(page);
    }

    /// <summary>按需创建页面：只有真正打开过的页面才会构建可视化树，启动更快、内存更省。</summary>
    public UserControl GetPage(string page)
    {
        if (_pages.TryGetValue(page, out var cached)) return cached;

        UserControl view = page switch
        {
            "keys" => new KeysView(),
            "hotstrings" => new HotstringView(),
            "qbar" => new QbarSettingsView(),
            "clipboard" => new ClipboardView(),
            "macros" => new MacrosView(),
            "layers" => new LayersView(),
            "backup" => new BackupView(),
            "settings" => new SettingsView(),
            "test" => new KeyTestView(),
            "help" => new HelpView(),
            "about" => new AboutView(),
            _ => new GuideView(),
        };

        switch (view)
        {
            case GuideView g: g.Initialize(_host); break;
            case KeysView k: k.Initialize(_host); break;
            case HotstringView hs: hs.Initialize(_host); break;
            case QbarSettingsView q: q.Initialize(_host); break;
            case ClipboardView c: c.Initialize(_host); break;
            case MacrosView m: m.Initialize(_host); break;
            case LayersView lv: lv.Initialize(_host); break;
            case BackupView bv: bv.Initialize(_host); break;
            case SettingsView s: s.Initialize(_host); break;
            case KeyTestView kt: kt.Initialize(_host); break;
            case HelpView h: h.Initialize(_host); break;
            case AboutView a: a.Initialize(_host); break;
        }

        _pages[page] = view;
        return view;
    }

    private void ShowPage(string page)
    {
        if (_current == page && PageHost.Content != null) return;
        _current = page;

        var view = GetPage(page);
        PageHost.Content = view;
        ApplyPageMinimum(view);

        (PageTitle.Text, PageBadge.Text) = page switch
        {
            "guide" => (Loc.T("快速上手"), Loc.T("3 分钟学会")),
            "keys" => (Loc.T("按键映射"), Loc.T("可视化编辑")),
            "hotstrings" => (Loc.T("文本替换"), Loc.T("触发词 · 空格展开")),
            "qbar" => (Loc.T("Qbar 面板"), Loc.T("搜索 · 启动 · 计算")),
            "clipboard" => (Loc.T("剪贴板"), Loc.T("独立剪贴板")),
            "macros" => (Loc.T("宏"), Loc.T("录制并绑定")),
            "layers" => (Loc.T("触发键与层"), Loc.T("多触发键 · 10 层")),
            "backup" => (Loc.T("同步与备份"), Loc.T("WebDAV · 导入导出")),
            "settings" => (Loc.T("设置"), Loc.T("全局选项")),
            "test" => (Loc.T("测试"), Loc.T("按键 · 切层 · 诊断")),
            "help" => (Loc.T("快捷键速查"), Loc.T("随时查看")),
            "about" => (Loc.T("关于"), Loc.T("版本与更新")),
            _ => ("CapsLock+", ""),
        };

        switch (view)
        {
            case GuideView g: g.Refresh(); break;
            case KeysView k: k.Refresh(); break;
            case ClipboardView c: c.Refresh(); break;
            case SettingsView s: s.Refresh(); break;
            case KeyTestView kt: kt.Refresh(); break;
            case QbarSettingsView q: q.Refresh(); break;
            case MacrosView mv: mv.Reload(); break;
            case LayersView lvw: lvw.Reload(); break;
            case BackupView bvw: bvw.Refresh(); break;
            case HelpView h: h.Refresh(); break;
            case AboutView a: a.Refresh(); break;
        }

        // 轻量淡入，切换页面不会“硬闪”。
        // 用 FillBehavior.Stop 并且不持有动画对象，避免动画结束后仍在每帧刷新。
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140))
        {
            FillBehavior = FillBehavior.Stop,
        };
        fade.Completed += (_, _) => PageHost.BeginAnimation(OpacityProperty, null);
        PageHost.Opacity = 1;
        PageHost.BeginAnimation(OpacityProperty, fade);
        UpdateStatus();
    }

    private void OnNavSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NavList.SelectedItem is ListBoxItem item && item.Tag is string tag)
            ShowPage(tag);
    }

    /// <summary>
    /// 页面能缩到多小，由页面自己说了算。
    ///
    /// 按键映射页要完整显示一把 108 键键盘：窗口太窄只能把键帽压糊，
    /// 所以它按用户选的档位报一个"装得下"的尺寸，这里设成 MinWidth/MinHeight。
    ///   · 换大档 → MinWidth 变大，WPF 会顺手把当前窗口撑到装得下；
    ///   · 换小档 → MinWidth 变小，窗口可以再缩回去；
    ///   · 换到别的页 → 回到基础值，不让键盘页的尺寸绑住整个程序。
    ///
    /// 屏幕本身比要求还小时（1366 笔记本选"特大"）夹到工作区大小：
    /// 否则窗口会宽到屏幕外面，右边那排按钮点都点不到。
    /// </summary>
    internal void ApplyPageMinimum(UserControl view)
    {
        if (view is KeysView keys)
        {
            var (needW, needH) = keys.RequiredWindowSize();
            var wa = SystemParameters.WorkArea;
            double targetW = Math.Min(needW, Math.Max(480, wa.Width));
            double targetH = Math.Min(needH, Math.Max(400, wa.Height));

            /* 记下"撑大之前"的尺寸，离开这一页时还原。
             *
             * 顺序很关键：必须在**改 MinWidth 之前**判断，因为一改 MinWidth
             * 窗口就会立刻被撑大，之后再比就永远是"已经够大"，还原逻辑不会触发。
             * 也只在窗口确实被顶大时记一次；用户自己拖大了就不动他。 */
            if (!_grownForKeys && WindowState == WindowState.Normal
                && (Width < targetW - 0.5 || Height < targetH - 0.5))
            {
                _grownForKeys = true;
                _restoreWidth = Width;
                _restoreHeight = Height;
                _grownWidth = Math.Max(Width, targetW);
                _grownHeight = Math.Max(Height, targetH);
            }

            MinWidth = targetW;
            MinHeight = targetH;

            /* 窗口被 MinWidth 撑大时是往**右边/下边**长的，Left 不动。
             * 如果原来窗口居中，长完就会有一部分跑到屏幕外 ——
             * 表现是右边那排按钮和「恢复默认」点不到。这里把它推回工作区。 */
            if (WindowState == WindowState.Normal)
            {
                if (Left + MinWidth > wa.Right) Left = Math.Max(wa.Left, wa.Right - MinWidth);
                if (Top + MinHeight > wa.Bottom) Top = Math.Max(wa.Top, wa.Bottom - MinHeight);
            }
            return;
        }

        MinWidth = _baseMinWidth;
        MinHeight = _baseMinHeight;

        /* 离开按键映射页：把窗口缩回进入前的尺寸。
         *
         * 只在"当前尺寸还是我们撑出来的那个"时还原 ——
         * 如果用户在这期间自己拖过大小（或最大化过），就尊重他的选择。 */
        if (_grownForKeys)
        {
            _grownForKeys = false;
            if (WindowState == WindowState.Normal
                && Math.Abs(Width - _grownWidth) < 1.5
                && Math.Abs(Height - _grownHeight) < 1.5)
            {
                Width = Math.Max(_baseMinWidth, _restoreWidth);
                Height = Math.Max(_baseMinHeight, _restoreHeight);
            }
        }
    }

    private void UpdateStatus()
    {
        bool enabled = _host.Engine.Enabled;
        StatusText.Text = enabled ? Loc.T("正在运行") : Loc.T("已暂停");
        StatusDot.Fill = (System.Windows.Media.Brush)FindResource(enabled ? "Brush.Success" : "Brush.Warning");
        HookText.Text = _host.HookAlive ? Loc.T("键盘钩子正常") : Loc.T("键盘钩子未生效");
        PauseButton.Content = enabled ? Loc.T("暂停") : Loc.T("恢复");
        PauseButton.Foreground = (System.Windows.Media.Brush)FindResource(enabled ? "Brush.TextSecondary" : "Brush.Accent");
    }

    private void OnEnabledChanged(bool enabled) => UpdateStatus();

    private void OnPauseClick(object sender, RoutedEventArgs e) => _host.ToggleEnabled();

    private void OnThemeClick(object sender, RoutedEventArgs e)
    {
        var next = ThemeManager.Current switch
        {
            ThemeManager.Mode.Light => ThemeManager.Mode.Dark,
            ThemeManager.Mode.Dark => ThemeManager.Mode.System,
            _ => ThemeManager.Mode.Light,
        };
        _host.Settings.Global.Theme = ThemeManager.ToSetting(next);
        ThemeManager.Apply(next);
        _host.SaveSettings();
        bool dark = next == ThemeManager.Mode.Dark ||
                    (next == ThemeManager.Mode.System && ThemeManager.IsDarkSystem);
        WindowChrome.Apply(this, dark);
        _host.Notify(next switch
        {
            ThemeManager.Mode.Light => Loc.T("已切换到浅色主题"),
            ThemeManager.Mode.Dark => Loc.T("已切换到深色主题"),
            _ => Loc.T("已跟随系统主题"),
        });
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        if (_host.Settings.Global.CloseToTray) Hide();
        else Close();
    }

    public void ReloadFromSettings()
    {
        /* 先记住当前停在哪一页，再清缓存。
         *
         * 早先是先 `_current = ""` 再问"该显示哪页"，于是永远落到默认的
         * 「快速上手」：改完语言、按 Caps+F5 重新加载、恢复默认设置、
         * 导入配置、恢复备份之后，用户都被弹回第一页，
         * 刚操作的设置页（连着他改的那个下拉框）直接消失。 */
        string keep = _current.Length > 0 ? _current : _pendingPage ?? "guide";

        foreach (var page in _pages.Keys.ToList()) _pages.Remove(page);
        _current = "";
        if (IsVisible) ShowPage(keep);
        UpdateStatus();
    }
}
