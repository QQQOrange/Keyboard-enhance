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

    /// <summary>没填令牌时唯一能用的下载入口：点它交给浏览器。</summary>
    private string _manualUrl = "";

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

        /* 没填令牌就**只提示、不自动下载**。
         *
         * 自动下载会直接拉一个几 MB 的 exe 并尝试替换自己 —— 这是整条流程里
         * 最"重"的一步。没填令牌时把选择权交回去：摆出下载地址，用户自己在
         * 浏览器里下，看到是 GitHub 的域名也更放心。填了令牌才走自动更新。 */
        if (!UpdateService.HasToken(host.Settings.Global.GitHubToken))
        {
            EnableManualDownload(string.IsNullOrWhiteSpace(info.DownloadUrl)
                ? info.PageUrl
                : info.DownloadUrl);
        }
    }

    /// <summary>
    /// 没填令牌时的形态：**藏掉「下载并安装」**，只留一个可点的下载地址。
    ///
    /// 用户明确要求：没填令牌就算检测到新版本，也不能在程序里点下载。
    /// 所以这里不是"把按钮改成打开下载页"，而是程序内根本不提供下载入口 ——
    /// 必须点链接去浏览器，看得见 GitHub 的域名再下载。
    /// </summary>
    private void EnableManualDownload(string url)
    {
        DownloadButton.Visibility = Visibility.Collapsed;

        DownloadLinkText.Text = url;
        LinkCard.Visibility = Visibility.Visible;
        _manualUrl = url;
    }

    private void OnOpenLinkClick(object sender, RoutedEventArgs e)
    {
        if (_manualUrl.Length > 0) OpenDownloadLink(_manualUrl);
    }

    private void OpenDownloadLink(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            Close();
        }
        catch (Exception ex)
        {
            // 浏览器没起来（默认浏览器未关联 / 被策略拦）：把地址告诉用户，别只是关窗
            Core.CoreLog.Swallow(ex, "打开更新下载页");
            AppDialog.Warn(Application.Current?.MainWindow,
                "没能自动打开浏览器。\n\n请手动访问：\n" + url,
                "打开下载页失败");
        }
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
                catch (Exception ex)
                {
                    // 浏览器没起来（默认浏览器未关联 / 被策略拦）：告诉用户，别只是关窗
                    Core.CoreLog.Swallow(ex, "打开更新下载页");
                    AppDialog.Warn(Application.Current?.MainWindow,
                        "没能自动打开浏览器。\n\n请手动访问：\n" + _info.PageUrl,
                        "打开下载页失败");
                }
                Close();
            };
        }
        finally
        {
            _busy = false;
        }
    }
}
