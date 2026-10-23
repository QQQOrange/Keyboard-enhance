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
        "guide", "keys", "qbar", "clipboard", "macros",
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
