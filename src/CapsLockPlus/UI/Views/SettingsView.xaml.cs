using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using CapsLockPlus.Core;
using CapsLockPlus.Core.Settings;
using CapsLockPlus.UI.Theme;
using Microsoft.Win32;

namespace CapsLockPlus.UI.Views;

public partial class SettingsView : UserControl
{
    private AppHost? _host;
    private bool _suppress;

    public SettingsView() => InitializeComponent();

    public void Initialize(AppHost host)
    {
        _host = host;
        Refresh();
    }

    public void Refresh()
    {
        if (_host == null) return;
        _suppress = true;
        var g = _host.Settings.Global;
        RefreshPermission();
        StartupBox.IsChecked = StartupManager.IsEnabled();
        TrayBox.IsChecked = g.CloseToTray;
        OpenOnStartBox.IsChecked = g.OpenWindowOnStartup;
        ToastBox.IsChecked = g.ShowToast;
        UpdateToastBox.IsChecked = g.ShowUpdateToast;
        FeedbackBox.IsChecked = g.ShowActionFeedback;
        DoubleTapBox.IsChecked = g.DoubleTapCaps;
        RepeatBox.IsChecked = g.RepeatWhenHeld;
        MouseSpeedSlider.Value = g.MouseSpeed;
        MouseSpeedText.Text = $"{g.MouseSpeed}（Caps+LAlt+滚轮 可快速调整）";
        TapCapsBox.SelectedIndex = g.TapCapsAction switch
        {
            "esc" => 1,
            "doNothing" => 2,
            _ => 0,
        };
        TextInputBox.SelectedIndex = (g.TextInputMode ?? "auto").ToLowerInvariant() switch
        {
            "keys" => 1,
            "unicode" => 2,
            _ => 0,
        };
        ThemeBox.SelectedIndex = g.Theme switch
        {
            "dark" => 1,
            "system" => 2,
            _ => 0,
        };
        DataPathText.Text = $"设置文件：{AppPaths.SettingsFile}\n日志：{CoreLog.LogPath}";
        _suppress = false;
    }

    private void Save()
    {
        _host?.SaveSettings();
    }

    private void OnStartupChanged(object sender, RoutedEventArgs e)
    {
        if (_host == null || _suppress) return;
        bool enabled = StartupBox.IsChecked == true;
        StartupManager.SetEnabled(enabled);
        _host.Settings.Global.StartWithWindows = enabled;
        Save();
        RefreshAdminState();      // 管理员模式下自启会弹 UAC，提示要跟着变
        _host.Notify(enabled ? "已设置开机自启" : "已取消开机自启");
    }

    private void OnTrayChanged(object sender, RoutedEventArgs e)
    {
        if (_host == null || _suppress) return;
        _host.Settings.Global.CloseToTray = TrayBox.IsChecked == true;
        Save();
    }

    private void OnOpenOnStartChanged(object sender, RoutedEventArgs e)
    {
        if (_host == null || _suppress) return;
        _host.Settings.Global.OpenWindowOnStartup = OpenOnStartBox.IsChecked == true;
        Save();
    }

    private void OnToastChanged(object sender, RoutedEventArgs e)
    {
        if (_host == null || _suppress) return;
        _host.Settings.Global.ShowToast = ToastBox.IsChecked == true;
        Save();
    }

    private void OnUpdateToastChanged(object sender, RoutedEventArgs e)
    {
        if (_host == null || _suppress) return;
        _host.Settings.Global.ShowUpdateToast = UpdateToastBox.IsChecked == true;
        Save();
    }

    /// <summary>刷新权限显示。</summary>
    private void RefreshPermission()
    {
        bool admin = Core.ElevationHelper.IsElevated;
        AdminBadge.Visibility = admin ? Visibility.Visible : Visibility.Collapsed;
        AdminBadgeText.Text = admin ? "管理员" : "普通权限";
        if (RunAsAdminBox != null) RunAsAdminBox.IsChecked = _host?.Settings.Global.RunAsAdmin ?? false;
        PermissionText.Text = Core.ElevationHelper.PermissionSummary();
        RefreshAdminState();
    }

    /// <summary>
    /// 状态行：「设置成什么」和「现在实际是什么」分开写。
    ///
    /// 两者不一致时把「立即重启以生效」露出来 ——
    /// 否则用户改完开关以为已经生效，实际上要等下次启动，
    /// 中间会一直以为功能坏了（这个软件之前就吃过这个亏）。
    /// </summary>
    private void RefreshAdminState()
    {
        if (AdminStateText == null) return;

        bool running = Core.ElevationHelper.IsElevated;
        bool wanted = _host?.Settings.Global.RunAsAdmin ?? false;

        AdminStateText.Text = $"当前：{(running ? "管理员权限" : "普通权限")}运行　·　" +
                              $"设置：下次启动以{(wanted ? "管理员" : "普通")}权限运行";

        /* 开机自启 + 管理员 = 每次登录都要点一次 UAC。
         * 这是 Windows 的规矩（注册表 Run 项没法声明"要高权限"），
         * 与其让用户莫名其妙，不如直说。 */
        if (wanted && _host?.Settings.Global.StartWithWindows == true)
            AdminStateText.Text += "\n开机自启时会弹一次 UAC 确认 —— 关掉「开机自动启动」就不会弹。";

        RestartElevatedButton.Visibility = running == wanted ? Visibility.Collapsed : Visibility.Visible;
        // 已经是管理员时不能再"提权重启"，按钮只用于"降回普通权限"
        RestartElevatedButton.Content = wanted ? "立即重启以生效" : "立即重启（回到普通权限）";
    }

    /// <summary>
    /// 「以管理员身份启动」开关。
    ///
    /// ⚠ 保存后需要**重启**才生效（启动时才决定是否提权）。
    /// 并在开启时明确告知后果：取词功能会失效。
    ///
    /// 不再强制重启：以前保存完直接 `ExitApp(restart: true)`，
    /// 用户可能正在做别的事，程序突然消失又弹 UAC 很吓人。
    /// 现在只保存，然后问一句要不要现在重启。
    /// </summary>
    private void OnRunAsAdminChanged(object sender, RoutedEventArgs e)
    {
        if (_host == null || _suppress) return;
        bool enable = RunAsAdminBox.IsChecked == true;

        if (enable)
        {
            var ok = MessageBox.Show(Window.GetWindow(this),
                "以管理员身份启动后，「选中文字 → 直接搜索」会失效。\n\n" +
                "原因：Windows 禁止管理员进程向普通权限的浏览器 / 编辑器\n" +
                "注入输入（UIPI 安全限制），取词通道会全部被拦掉。\n\n" +
                "好处：对着「以管理员身份运行」的程序按快捷键才会生效。\n" +
                "（锁屏状态下任何用户态程序都无法使用快捷键，\n" +
                " 这与是否管理员无关。）\n\n" +
                "确定要开启吗？",
                "开启管理员模式", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (ok != MessageBoxResult.OK)
            {
                _suppress = true;
                RunAsAdminBox.IsChecked = false;
                _suppress = false;
                return;
            }
        }

        _host.Settings.Global.RunAsAdmin = enable;
        Save();
        RefreshAdminState();

        var ask = MessageBox.Show(Window.GetWindow(this),
            enable
                ? "已保存。下次启动会以管理员身份运行（会弹一次 UAC 确认）。\n\n现在就重启吗？"
                : "已保存。下次启动会以普通权限运行。\n\n现在就重启吗？",
            "需要重启才生效", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (ask == MessageBoxResult.Yes) RestartApp();
    }

    /// <summary>设置页里的「立即重启以生效」按钮。</summary>
    private void OnRestartForElevation(object sender, RoutedEventArgs e) => RestartApp();

    /// <summary>
    /// 重启程序。
    ///
    /// 目标权限是管理员、当前不是时，先显式拉起提权实例再退出 ——
    /// 否则普通权限重启一次还是普通权限，用户会以为开关没生效。
    /// </summary>
    private void RestartApp()
    {
        if (_host == null) return;
        bool wanted = _host.Settings.Global.RunAsAdmin;
        if (wanted && !Core.ElevationHelper.IsElevated)
        {
            if (Core.ElevationHelper.RelaunchElevated(Environment.GetCommandLineArgs().Skip(1).ToArray(), out string? err))
            {
                _host.ExitApp(restart: false);
                return;
            }
            MessageBox.Show(Window.GetWindow(this),
                "提权失败：" + (err ?? "未知原因") + "\n\n（多半是在 UAC 对话框里点了「否」）",
                "没能以管理员身份重启", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _host.ExitApp(restart: true);
    }

    private void OnFeedbackChanged(object sender, RoutedEventArgs e)
    {
        if (_host == null || _suppress) return;
        _host.Settings.Global.ShowActionFeedback = FeedbackBox.IsChecked == true;
        Save();
        if (FeedbackBox.IsChecked == true)
            _host.Notify("已开启按键反馈：按 Caps+S 试试，角落会显示「左移」");
    }

    private void OnDoubleTapChanged(object sender, RoutedEventArgs e)
    {
        if (_host == null || _suppress) return;
        bool on = DoubleTapBox.IsChecked == true;
        _host.Settings.Global.DoubleTapCaps = on;

        /* ⚠ 必须同步到每个触发键上。
         *
         * Global.DoubleTapCaps / TapCapsAction 只在"首次生成触发键"时被读一次
         * （见 SettingsStore.NormalizeLayers）。老配置里 Triggers 早就存在，
         * 所以光改全局字段的话，这个开关**永远不生效** ——
         * 属于典型的"界面上能改，实际没用"。
         *
         * 真正的开关在「触发键与层」页，这里做同步，保证两处一致。 */
        foreach (var t in _host.Settings.Triggers)
        {
            t.DoubleTapAction = on
                ? (_host.Settings.Global.DoubleTapCapsAction.Length > 0
                    ? _host.Settings.Global.DoubleTapCapsAction
                    : "qbar")
                : "";
        }

        Save();
    }

    private void OnRepeatChanged(object sender, RoutedEventArgs e)
    {
        if (_host == null || _suppress) return;
        _host.Settings.Global.RepeatWhenHeld = RepeatBox.IsChecked == true;
        Save();
    }

    private void OnMouseSpeedChanged(object sender, RoutedEventArgs e)
    {
        if (_host == null || _suppress) return;
        int v = (int)MouseSpeedSlider.Value;
        _host.Settings.Global.MouseSpeed = v;
        _host.Context.Mouse.Speed = v;
        MouseSpeedText.Text = $"{v}（Caps+LAlt+滚轮 可快速调整）";
        Save();
    }

    /// <summary>
    /// 「输入文本」用哪种方式打字。
    ///
    /// 之所以做成设置项而不是每个绑定单独选：<c>typeText</c> 的参数就是文字本身，
    /// 改成带模式的结构会破坏所有老配置。全局设置既能解决密码框，也不动存量数据。
    /// </summary>
    private void OnTextInputModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_host == null || _suppress) return;
        if (TextInputBox.SelectedItem is ComboBoxItem { Tag: string tag })
        {
            _host.Settings.Global.TextInputMode = tag;
            Save();
        }
    }

    private void OnTapCapsChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_host == null || _suppress) return;
        if (TapCapsBox.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            _host.Settings.Global.TapCapsAction = tag;

            // 同步到所有触发键 —— 否则老配置里改这里完全不生效
            // （全局字段只在首次生成触发键时被读取一次）。
            foreach (var t in _host.Settings.Triggers) t.TapAction = tag;

            Save();
        }
    }

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_host == null || _suppress) return;
        if (ThemeBox.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            _host.Settings.Global.Theme = tag;
            ThemeManager.Apply(ThemeManager.Parse(tag));
            Save();
            if (Window.GetWindow(this) is { } w)
            {
                bool dark = tag == "dark" || (tag == "system" && ThemeManager.IsDarkSystem);
                WindowChrome.Apply(w, dark);
            }
        }
    }

    private void OnImportLegacy(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        var dlg = new OpenFileDialog
        {
            Title = "选择旧版 CapsLock+settings.ini",
            Filter = "CapsLock+ 设置文件|*.ini|所有文件|*.*",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var result = LegacyIniImporter.Import(dlg.FileName);
            _host.Settings = result.Settings;
            _host.SaveSettings();
            _host.ReloadAll();
            Refresh();
            string msg = $"导入完成：\n· 按键 {result.Keys} 项\n· 搜索指令 {result.Search} 条\n· 程序 {result.Run} 个\n" +
                         $"· 网址 {result.Web} 个\n· 文字替换 {result.HotStrings} 条";
            if (result.Warnings.Count > 0)
                msg += "\n\n以下项目需要手动确认：\n" + string.Join("\n", result.Warnings.Take(8));
            MessageBox.Show(Window.GetWindow(this), msg, "导入旧版设置", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this), "导入失败：" + ex.Message, "导入旧版设置",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnOpenSettingsFile(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            Process.Start(new ProcessStartInfo(AppPaths.SettingsFile) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _host?.Notify("打开失败：" + ex.Message);
        }
    }

    private void OnOpenLog(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CoreLog.LogPath)!);
            if (!File.Exists(CoreLog.LogPath)) File.WriteAllText(CoreLog.LogPath, "");
            Process.Start(new ProcessStartInfo(CoreLog.LogPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _host?.Notify("打开日志失败：" + ex.Message);
        }
    }

    private void OnReload(object sender, RoutedEventArgs e) => _host?.ReloadAll();

    private void OnResetAll(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        var r = MessageBox.Show(Window.GetWindow(this),
            "把全部设置恢复成默认值？\n（按键映射、Qbar 条目、窗口绑定、外观都会被重置）",
            "重置全部设置", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (r != MessageBoxResult.OK) return;
        _host.Settings = DefaultSettings.Create();
        _host.SaveSettings();
        _host.ReloadAll();
        Refresh();
        _host.Notify("已恢复默认设置");
    }
}
