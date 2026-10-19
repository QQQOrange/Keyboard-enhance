using System.Diagnostics;
using System.Windows;
using CapsLockPlus.Core.Update;

namespace CapsLockPlus.UI.Windows;

/// <summary>启动时发现新版本的提示窗：下载 → 替换 → 重启，全程不离开当前页面。</summary>
public partial class UpdatePromptWindow : Window
{
    private readonly AppHost _host;
    private readonly UpdateInfo _info;
    private readonly UpdateService _service = new();
    private bool _busy;

    public UpdatePromptWindow(AppHost host, UpdateInfo info)
    {
        _host = host;
        _info = info;
        InitializeComponent();
        TitleText.Text = $"发现新版本 {info.Version}";
        SubText.Text = $"当前 {UpdateService.CurrentVersion}" +
                       (info.SizeText.Length > 0 ? $"　·　{info.SizeText}" : "");
        NotesText.Text = string.IsNullOrWhiteSpace(info.ReleaseNotes)
            ? "（该版本没有填写更新说明）"
            : info.ReleaseNotes;
        StatusText.Text = "";
    }

    private void OnLater(object sender, RoutedEventArgs e) => Close();

    private void OnSkip(object sender, RoutedEventArgs e)
    {
        _host.Settings.Global.SkippedVersion = _info.Version;
        _host.SaveSettings();
        _host.Notify($"已跳过 {_info.Version}，下次有新版本还会提醒");
        Close();
    }

    private async void OnDownload(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        DownloadButton.IsEnabled = false;
        SkipButton.IsEnabled = false;
        LaterButton.IsEnabled = false;
        Progress.Visibility = Visibility.Visible;
        StatusText.Text = "正在下载…";

        try
        {
            var progress = new Progress<double>(p =>
            {
                Progress.Value = p * 100;
                StatusText.Text = $"正在下载… {p * 100:0}%";
            });
            var (ok, path, error) = await _service.DownloadAsync(_info, progress);
            if (!ok)
            {
                StatusText.Text = error;
                LaterButton.IsEnabled = true;
                return;
            }

            Progress.Value = 100;
            StatusText.Text = "正在替换并重启…";
            await Task.Delay(400);

            if (UpdateService.ApplyAndRestart(path))
            {
                _host.ExitApp();
                return;
            }

            StatusText.Text = "替换失败，请到 Releases 手动下载";
            LaterButton.IsEnabled = true;
            DownloadButton.Content = "打开下载页";
            DownloadButton.IsEnabled = true;
            DownloadButton.Click -= OnDownload;
            DownloadButton.Click += (_, _) =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo(_info.PageUrl) { UseShellExecute = true });
                }
                catch { }
                Close();
            };
        }
        finally
        {
            _busy = false;
        }
    }
}
