using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using CapsLockPlus.Core;
using CapsLockPlus.Core.Settings;
using CapsLockPlus.Core.Update;

namespace CapsLockPlus.UI.Views;

public partial class AboutView : UserControl
{
    private readonly UpdateService _updates = new();
    private AppHost? _host;
    private UpdateInfo? _pending;
    private bool _busy;
    private bool _suppress;

    public AboutView() => InitializeComponent();

    public void Initialize(AppHost host)
    {
        _host = host;
        Refresh();
    }

    public void Refresh()
    {
        if (_host == null) return;
        string version = UpdateService.CurrentVersion;
        VersionBadge.Text = "v" + version;
        CurrentVersionText.Text = version;
        AboutVersion.Text = version;
        BuildInfo.Text = $".NET {Environment.Version.ToString(3)} · " +
                         $"{System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}";
        AboutGitHubLink.Text = UpdateService.GitHubUrl;
        AboutDataPath.Text = AppPaths.SettingsFile;

        _suppress = true;
        AutoUpdateBox.IsChecked = _host.Settings.Global.AutoUpdate;
        _suppress = false;
    }

    // ---------------- 链接 ----------------

    private void OnOpenGitHub(object sender, RoutedEventArgs e) => OpenUrl(UpdateService.GitHubUrl);

    private void OnOpenGitHub(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
        OpenUrl(UpdateService.GitHubUrl);

    private void OnOpenUpstream(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
        OpenUrl("https://github.com/wo52616111/capslock-plus");

    private void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _host?.Notify("打开链接失败：" + ex.Message);
        }
    }

    // ---------------- 自动更新 ----------------

    private void OnAutoUpdateChanged(object sender, RoutedEventArgs e)
    {
        if (_host == null || _suppress) return;
        _host.Settings.Global.AutoUpdate = AutoUpdateBox.IsChecked == true;
        _host.SaveSettings();
        _host.Notify(_host.Settings.Global.AutoUpdate ? "已开启自动更新" : "已关闭自动更新");
    }

    // ---------------- 手动检查 ----------------

    private async void OnCheckUpdate(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        CheckButton.IsEnabled = false;
        DownloadButton.Visibility = Visibility.Collapsed;
        SetStatus("正在检查更新…", "");
        try
        {
            var result = await _updates.CheckAsync();
            _host?.Settings.Global.LastUpdateCheck = DateTime.Now;
            _host?.SaveSettings();

            if (!result.Success)
            {
                SetStatus(result.Message, "", isError: true);
                return;
            }
            if (!result.IsNewer || result.Update == null)
            {
                SetStatus($"已是最新版本（{UpdateService.CurrentVersion}）", "");
                return;
            }

            _pending = result.Update;
            SetStatus($"发现新版本 {result.Update.Version}" +
                      (result.Update.SizeText.Length > 0 ? $"（{result.Update.SizeText}）" : ""),
                Trim(result.Update.ReleaseNotes, 600));
            DownloadButton.Visibility = Visibility.Visible;
        }
        finally
        {
            _busy = false;
            CheckButton.IsEnabled = true;
        }
    }

    // ---------------- 下载并安装 ----------------

    private async void OnDownloadInstall(object sender, RoutedEventArgs e)
    {
        if (_busy || _pending == null) return;
        _busy = true;
        DownloadButton.IsEnabled = false;
        CheckButton.IsEnabled = false;
        Progress.Visibility = Visibility.Visible;
        Progress.Value = 0;
        SetStatus($"正在下载 {_pending.Version}…", "");

        try
        {
            var progress = new Progress<double>(p => Progress.Value = p * 100);
            var (ok, path, error) = await _updates.DownloadAsync(_pending, progress);
            if (!ok)
            {
                SetStatus(error, "", isError: true);
                Progress.Visibility = Visibility.Collapsed;
                return;
            }

            Progress.Value = 100;
            SetStatus("下载完成，正在替换并重启…", "");
            await Task.Delay(400);

            if (UpdateService.ApplyAndRestart(path))
            {
                _host?.ExitApp();
                return;
            }
            SetStatus("替换失败，请手动下载安装包", "", isError: true);
            DownloadButton.Content = "打开下载页";
            DownloadButton.Click -= OnDownloadInstall;
            DownloadButton.Click += (_, _) => OpenUrl(UpdateService.ReleasesUrl);
        }
        finally
        {
            _busy = false;
            DownloadButton.IsEnabled = true;
            CheckButton.IsEnabled = true;
        }
    }

    private void SetStatus(string text, string notes, bool isError = false)
    {
        StatusCard.Visibility = Visibility.Visible;
        StatusText.Text = text;
        StatusText.Foreground = (System.Windows.Media.Brush)FindResource(
            isError ? "Brush.Danger" : "Brush.TextPrimary");
        ReleaseNotes.Text = notes;
        ReleaseNotes.Visibility = notes.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string Trim(string text, int max) =>
        string.IsNullOrEmpty(text) ? "" : (text.Length <= max ? text : text[..max] + "…");
}
