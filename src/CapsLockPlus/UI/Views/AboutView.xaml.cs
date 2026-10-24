using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using CapsLockPlus.Core;
using CapsLockPlus.Core.Settings;
using CapsLockPlus.Core.Update;
using CapsLockPlus.UI.Windows;

namespace CapsLockPlus.UI.Views;

public partial class AboutView : UserControl
{
    private readonly UpdateService _updates = new();
    private AppHost? _host;
    private UpdateInfo? _pending;
    private bool _busy;
    private bool _suppress;

    /// <summary>
    /// 非空表示"这一轮只给下载页、不自动下载"（没填 GitHub 令牌时）。
    /// 存着地址而不是每次都重算，是为了让按钮点击时用的链接和屏幕上显示的一致。
    /// </summary>
    private string _manualUrl = "";

    /// <summary>
    /// 构造时就刷一次令牌状态：状态行不该是空的。
    /// 此时 <c>_host</c> 还是 null，<see cref="RefreshTokenStatus"/> 会按"未填写"来写 ——
    /// 这正是用户第一次打开这一页看到的东西。
    /// </summary>
    public AboutView()
    {
        InitializeComponent();
        RefreshTokenStatus();
    }

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

        RefreshTokenStatus();
    }

    // ---------------- GitHub 个人访问令牌 ----------------

    /// <summary>
    /// 刷新令牌状态那一行。
    ///
    /// 输入框里**不回填**完整令牌：掩码回填最容易被"点一下保存"存成星号串，
    /// 而且令牌摊在屏幕上截图就泄了。已保存的只在这里显示头尾几位。
    /// </summary>
    private void RefreshTokenStatus()
    {
        string saved = _host?.Settings.Global.GitHubToken ?? "";
        bool has = UpdateService.HasToken(saved);

        TokenBox.Text = "";
        ClearTokenButton.IsEnabled = has;

        TokenStatusText.Text = has
            ? $"已保存 {UpdateService.MaskToken(saved)}　·　更新检查按账号限流（5000 次/小时），可以自动下载安装。"
              + "输入新令牌会覆盖它。令牌会跟着加密备份和网盘同步一起走（明文导出不含）。"
            : "未填写　·　匿名访问每小时只有 60 次（同一网络下多台机器共用），容易撞上限。"
              + "填了才能一键自动下载；不填也行 —— 发现新版本时只提示、并给出 GitHub 下载地址，"
              + "程序内不提供下载按钮。只读公开仓库不需要勾任何权限。"
              + "令牌会跟着加密备份和网盘同步一起走，换台机器恢复后不用重新填。";
    }

    /// <summary>验证中：两个按钮都锁住，避免连点时把状态文字刷来刷去。</summary>
    private bool _verifying;

    /// <summary>
    /// 验证令牌：用输入框里的令牌真请求一次 GitHub，确认它能用。
    ///
    /// 输入框空但之前存过令牌时，验证的是**已保存的那个** —— 用户想知道
    /// "存进去的那个还有效吗"是最常见的诉求，不该逼他先手动贴一遍。
    /// </summary>
    private async void OnVerifyToken(object sender, RoutedEventArgs e)
    {
        if (_host == null || _verifying) return;

        string token = UpdateService.NormalizeToken(TokenBox.Text);
        bool usingSaved = false;
        if (token.Length == 0)
        {
            string saved = _host.Settings.Global.GitHubToken;
            if (!UpdateService.HasToken(saved))
            {
                TokenStatusText.Text = "先把令牌粘进左边的输入框，再点「验证」。";
                return;
            }
            token = UpdateService.NormalizeToken(saved);
            usingSaved = true;
        }

        _verifying = true;
        VerifyTokenButton.IsEnabled = false;
        SaveTokenButton.IsEnabled = false;
        TokenStatusText.Text = usingSaved
            ? "正在验证已保存的令牌…"
            : "正在用这个令牌请求 GitHub…";

        try
        {
            var result = await _updates.VerifyTokenAsync(token);

            if (!result.Ok)
            {
                TokenStatusText.Text = "✗ " + result.Message;
                return;
            }

            /* 验证通过后要不要顺手保存：只有"输入框里的新令牌"才提示保存，
             * 验证的是已保存的那个就直接说"还能用"，别再让人点一次。 */
            TokenStatusText.Text = usingSaved
                ? "✓ " + result.Message + "　（这就是已保存的令牌）"
                : "✓ " + result.Message + "　—— 点「保存」把它存下来";
        }
        finally
        {
            _verifying = false;
            VerifyTokenButton.IsEnabled = true;
            SaveTokenButton.IsEnabled = true;
        }
    }

    private void OnSaveToken(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;

        string token = UpdateService.NormalizeToken(TokenBox.Text);
        if (token.Length == 0)
        {
            TokenStatusText.Text = "输入框是空的 —— 要清掉已保存的令牌请点「清除」。";
            return;
        }

        _host.Settings.Global.GitHubToken = token;
        _host.SaveSettings();
        RefreshTokenStatus();

        /* 只提示不拦截：GitHub 未来可能启用新前缀，卡死格式等于把用户挡在外面。 */
        if (!UpdateService.LooksLikeGitHubToken(token))
        {
            TokenStatusText.Text = "这个看起来不像 GitHub 令牌（通常以 ghp_ 或 github_pat_ 开头），"
                                   + "但已经按你填的保存了 —— 如果检查更新报「令牌无效」，回头核对一下。";
            return;
        }

        _host.Notify("令牌已保存，更新检查改用你的账号配额（5000 次/小时）");
    }

    private void OnClearToken(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        if (UpdateService.HasToken(_host.Settings.Global.GitHubToken) &&
            !AppDialog.ConfirmDanger(Window.GetWindow(this),
                "清除后更新检查会回到匿名访问（每小时 60 次），发现新版本时也不再自动下载，"
                + "只提示并给出下载地址。\n\n已保存的令牌会被删掉，需要重新填。",
                "清除 GitHub 令牌？", "清除"))
            return;

        _host.Settings.Global.GitHubToken = "";
        _host.SaveSettings();
        RefreshTokenStatus();
        _host.Notify("已清除令牌，更新检查回到匿名访问");
    }

    private void OnOpenTokenPage(object sender, RoutedEventArgs e) =>
        OpenUrl(UpdateService.TokenCreateUrl);

    /// <summary>
    /// 问号按钮：在浏览器里打开"什么是个人访问令牌"的官方文档。
    /// 地址取自 <see cref="UpdateService.TokenHelpUrl"/>，不要在这里写死。
    /// </summary>
    private void OnOpenTokenHelp(object sender, RoutedEventArgs e) =>
        OpenUrl(UpdateService.TokenHelpUrl);

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
            _host?.Notify(UiText.Failed("打开链接", ex.Message));
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
        ManualLinkPanel.Visibility = Visibility.Collapsed;
        UpdateLinkText.Text = "";
        _manualUrl = "";
        SetStatus("正在检查更新…", "");
        try
        {
            var result = await _updates.CheckAsync(_host?.Settings.Global.GitHubToken);
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
            string head = $"发现新版本 {result.Update.Version}" +
                          (result.Update.SizeText.Length > 0 ? $"（{result.Update.SizeText}）" : "");

            /* 没填令牌：只提示 + 给下载地址，**程序内不提供下载入口**。
             * 判断和 UpdatePromptWindow 用同一个 HasToken，两处行为必须一致。 */
            if (!UpdateService.HasToken(_host?.Settings.Global.GitHubToken))
            {
                ShowManualLink(string.IsNullOrWhiteSpace(result.Update.DownloadUrl)
                    ? result.Update.PageUrl
                    : result.Update.DownloadUrl);
                SetStatus(head + " —— 没填 GitHub 令牌，不能自动下载，请点下面的地址用浏览器下载",
                    Trim(result.Update.ReleaseNotes, 600));
            }
            else
            {
                ManualLinkPanel.Visibility = Visibility.Collapsed;
                DownloadButton.Content = "下载并安装";
                DownloadButton.Visibility = Visibility.Visible;
                SetStatus(head, Trim(result.Update.ReleaseNotes, 600));
            }
        }
        finally
        {
            _busy = false;
            CheckButton.IsEnabled = true;
        }
    }

    // ---------------- 下载并安装 ----------------

    /// <summary>
    /// 没填令牌时的"只能点链接"形态。
    ///
    /// 关键是**把「下载并安装」整个藏起来**：不是改成"打开下载页"，
    /// 而是程序内根本不提供下载入口 —— 用户必须点链接去浏览器，
    /// 看清楚下载地址再决定。没令牌时连自动下载都不该发生。
    /// </summary>
    internal void ShowManualLink(string url)
    {
        _manualUrl = url;
        UpdateLinkText.Text = url;
        ManualLinkPanel.Visibility = Visibility.Visible;
        DownloadButton.Visibility = Visibility.Collapsed;
    }

    private void OnOpenUpdateLink(object sender, RoutedEventArgs e)
    {
        if (_manualUrl.Length > 0) OpenUrl(_manualUrl);
    }

    /// <summary>没填令牌时这个按钮是「打开下载页」：只跳浏览器，不动文件系统。</summary>
    private async void OnDownloadInstall(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        /* 没填令牌时这个按钮是「打开下载页」：只跳浏览器，不动文件系统。 */
        if (_manualUrl.Length > 0)
        {
            OpenUrl(_manualUrl);
            return;
        }

        if (_pending == null) return;
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
