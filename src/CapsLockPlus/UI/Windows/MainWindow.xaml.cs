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

    /// <summary>当前主窗口实例（AppHost 需要用它导航到某个页面）。</summary>
    public static MainWindow? Instance { get; private set; }

    public MainWindow(AppHost host)
    {
        _host = host;
        Instance = this;
        InitializeComponent();

        NavList.SelectedIndex = 0;
        Loaded += OnLoaded;
        StateChanged += OnStateChanged;
        Closing += OnClosing;
        _host.EnabledChanged += OnEnabledChanged;
        _host.SettingsChanged += UpdateStatus;
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
        if (_host.Settings.Global.CloseToTray && !_host.Exiting)
        {
            e.Cancel = true;
            Hide();
        }
    }

    public void Navigate(string page)
    {
        _pendingPage = page;
        /* 导航索引必须和 NavList 里 ListBoxItem 的**顺序**一致。
         * 「窗口绑定」独立页已移除（改到按键映射里作为动作），
         * 所以后面的索引整体前移一位。 */
        int index = page switch
        {
            "guide" => 0,
            "keys" => 1,
            "qbar" => 2,
            "clipboard" => 3,
            "macros" => 4,
            "layers" => 5,
            "backup" => 6,
            "settings" => 7,
            "test" => 8,
            "help" => 9,
            "about" => 10,
            _ => 0,
        };
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

        (PageTitle.Text, PageBadge.Text) = page switch
        {
            "guide" => ("快速上手", "3 分钟学会"),
            "keys" => ("按键映射", "可视化编辑"),
            "qbar" => ("Qbar 面板", "搜索 · 启动 · 计算"),
            "clipboard" => ("剪贴板", "独立剪贴板"),
            "macros" => ("宏", "录制并绑定"),
            "layers" => ("触发键与层", "多触发键 · 10 层"),
            "backup" => ("同步与备份", "WebDAV · 导入导出"),
            "settings" => ("设置", "全局选项"),
            "test" => ("测试", "按键 · 切层 · 诊断"),
            "help" => ("快捷键速查", "随时查看"),
            "about" => ("关于", "版本与更新"),
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

    private void UpdateStatus()
    {
        bool enabled = _host.Engine.Enabled;
        StatusText.Text = enabled ? "正在运行" : "已暂停";
        StatusDot.Fill = (System.Windows.Media.Brush)FindResource(enabled ? "Brush.Success" : "Brush.Warning");
        HookText.Text = _host.HookAlive ? "键盘钩子正常" : "键盘钩子未生效";
        PauseButton.Content = enabled ? "暂停" : "恢复";
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
            ThemeManager.Mode.Light => "已切换到浅色主题",
            ThemeManager.Mode.Dark => "已切换到深色主题",
            _ => "已跟随系统主题",
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
        foreach (var page in _pages.Keys.ToList()) _pages.Remove(page);
        _current = "";
        if (IsVisible) ShowPage(_pendingPage ?? "guide");
        UpdateStatus();
    }
}
