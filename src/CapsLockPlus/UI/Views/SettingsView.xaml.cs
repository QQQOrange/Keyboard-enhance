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
        ElevateButton.Visibility = admin ? Visibility.Collapsed : Visibility.Visible;
        PermissionText.Text = Core.ElevationHelper.PermissionSummary();
    }

    private void OnElevateClick(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;

        var ok = MessageBox.Show(Window.GetWindow(this),
            "将以管理员身份重新启动 CapsLock+。\n\n" +
            "这样做的好处：对着「以管理员身份运行」的程序按快捷键才会生效。\n" +
            "（注意：锁屏状态下无法使用快捷键 —— 这是 Windows 的安全限制，\n" +
            " 任何用户态程序都做不到，管理员权限也不行。）\n\n继续吗？",
            "以管理员身份重启", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (ok != MessageBoxResult.OK) return;

        if (Core.ElevationHelper.RelaunchElevated(Environment.GetCommandLineArgs()[1..], out var err))
        {
            _host.ExitApp();
        }
        else
        {
            MessageBox.Show(Window.GetWindow(this),
                "提权失败：" + (err ?? "未知原因"), "无法提权",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
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
