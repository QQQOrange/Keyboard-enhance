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
        StartupBox.IsChecked = StartupManager.IsEnabled();
        TrayBox.IsChecked = g.CloseToTray;
        OpenOnStartBox.IsChecked = g.OpenWindowOnStartup;
        ToastBox.IsChecked = g.ShowToast;
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

    private void OnDoubleTapChanged(object sender, RoutedEventArgs e)
    {
        if (_host == null || _suppress) return;
        _host.Settings.Global.DoubleTapCaps = DoubleTapBox.IsChecked == true;
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
