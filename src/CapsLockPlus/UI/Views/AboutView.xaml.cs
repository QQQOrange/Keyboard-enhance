using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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

    // ---------------- 配额 ----------------

    private bool _usageLoading;

    /// <summary>
    /// 刷新"本小时已用 / 总共"那一行。
    ///
    /// 匿名也能查（显示 60/小时），所以没填令牌时这行照样有内容 ——
    /// 用户一眼就能看出填令牌前后差了一个数量级。
    /// </summary>
    private async Task RefreshTokenUsageAsync()
    {
        if (_usageLoading) return;
        _usageLoading = true;
        try
        {
            TokenUsageText.Text = Loc.T("正在读取配额…");
            var info = await _updates.FetchRateLimitAsync(_host?.Settings.Global.GitHubToken);

            if (!info.Ok)
            {
                TokenUsageText.Text = Loc.T("配额暂时读不到（") + info.Message + "）";
                return;
            }

            string scope = UpdateService.HasToken(_host?.Settings.Global.GitHubToken)
                ? Loc.T("按你的账号")
                : Loc.T("匿名（按 IP）");
            string tail = info.Remaining > 0 ? $"还剩 {info.Remaining} 次" : Loc.T("已用完，整点恢复");
            TokenUsageText.Text = $"本小时已用 {info.Used} 次 / 共 {info.Limit} 次（{scope} · {tail}）";
        }
        finally
        {
            _usageLoading = false;
        }
    }

    // ---------------- 布局：全屏两列 / 窄窗口单列 ----------------

    /// <summary>
    /// 关于页切成两列的宽度阈值（DIP）。
    ///
    /// 1080 的来源：两列各 540 时，「更新卡」里的令牌行（标签 + 输入框 + 四个按钮）
    /// 还能排得开；再窄令牌输入框会被压到 200 以下，看不出填了什么。
    /// 低于它就退回单列（每张卡整行），这也是以前一直用的形态。
    /// </summary>
    private const double TwoColumnBreakpoint = 1080;

    /// <summary>上一次的形态；null = 还没算过（首次布局要应用一次）。</summary>
    private bool? _aboutNarrow;

    /// <summary>
    /// 窗口宽度变了：决定右列（信息卡 + 致谢卡）并排还是落到下面。
    ///
    /// 只改 Grid 的行列，不复制控件 —— 两张卡在两种形态下是同一批对象，
    /// 里面的文字、链接、事件都不受影响。
    /// </summary>
    private void OnAboutScrollSizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool narrow = e.NewSize.Width > 0 && e.NewSize.Width < TwoColumnBreakpoint;
        if (_aboutNarrow == narrow) return;
        _aboutNarrow = narrow;

        if (narrow)
        {
            /* 单列：更新卡占满第 2 行，右列整体落到第 3 行。
             * 顺序仍是"更新在上、信息在下"，和以前一模一样。 */
            Grid.SetColumnSpan(UpdateCard, 2);
            UpdateCard.Margin = new Thickness(0);

            Grid.SetRow(AboutRightColumn, 2);
            Grid.SetColumn(AboutRightColumn, 0);
            Grid.SetColumnSpan(AboutRightColumn, 2);
            AboutRightColumn.Margin = new Thickness(0, 16, 0, 0);
        }
        else
        {
            // 两列：左边更新（操作），右边信息 + 致谢
            Grid.SetColumnSpan(UpdateCard, 1);
            UpdateCard.Margin = new Thickness(0, 0, 8, 0);

            Grid.SetRow(AboutRightColumn, 1);
            Grid.SetColumn(AboutRightColumn, 1);
            Grid.SetColumnSpan(AboutRightColumn, 1);
            AboutRightColumn.Margin = new Thickness(8, 0, 0, 0);
        }
    }

    // ---------------- GitHub 个人访问令牌 ----------------

    /// <summary>
    /// 已保存令牌在输入框里的占位显示。
    ///
    /// 固定 8 个圆点，**不反映真实长度** —— 长度也是信息（20 位是老式 PAT、
    /// 93 位是 fine-grained），没必要漏。用户只需要知道"这里有东西、已经存好了"。
    /// </summary>
    internal const string TokenMask = "••••••••";

    /// <summary>
    /// 输入框焦点进来时，如果显示的是掩码就全选。
    ///
    /// 这样键盘用户 Tab 进来直接打字就替换掉；鼠标用户点进来后按退格也不会
    /// 一个一个删圆点（全选状态下按一次就清空）。
    /// </summary>
    private void OnTokenBoxGotFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (TokenBox.Text == TokenMask) TokenBox.SelectAll();
    }

    /// <summary>
    /// 掩码状态下开始打字 = 换一个新令牌：先把圆点清掉，再让这个字符落进去。
    ///
    /// 不做这一步的话，用户点进输入框直接敲字会得到「••••••••ghp_new」，
    /// 保存后令牌无效，而屏幕上看起来"明明填了"。
    /// </summary>
    private void OnTokenBoxPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (TokenBox.Text == TokenMask) TokenBox.Clear();
    }

    /// <summary>粘贴同理：掩码状态下粘进来的内容应当替换掉圆点，而不是接在后面。</summary>
    private void OnTokenBoxPasting(object sender, DataObjectPastingEventArgs e)
    {
        if (TokenBox.Text == TokenMask) TokenBox.Clear();
    }

    /// <summary>
    /// 刷新令牌状态那一行。
    ///
    /// 已保存时输入框显示掩码（不是空白）—— 空白会让人以为"没存上"，
    /// 于是又粘一遍。完整令牌永远不回填：掩码回填最容易被"点一下保存"
    /// 存成星号串，而且令牌摊在屏幕上截图就泄了。
    /// </summary>
    private void RefreshTokenStatus()
    {
        string saved = _host?.Settings.Global.GitHubToken ?? "";
        bool has = UpdateService.HasToken(saved);

        TokenBox.Text = has ? TokenMask : "";
        ClearTokenButton.IsEnabled = has;
        _ = RefreshTokenUsageAsync();

        /* 只回答"有没有"，一个字符都不回显 —— 截图/录屏都不会带走东西。
         * 想看头尾几位？那也算泄露，用户明确要求去掉。 */
        TokenStatusText.Text = has
            ? Loc.T("已保存（令牌不回显）　·　更新检查按账号限流，可以自动下载安装。")
              + Loc.T("输入新令牌会覆盖它。令牌会跟着备份和导出一起走。")
            : Loc.T("未填写　·　匿名访问每小时只有 60 次（同一网络下多台机器共用），容易撞上限。")
              + Loc.T("填了才能一键自动下载；不填也行 —— 发现新版本时只提示、并给出 GitHub 下载地址，")
              + Loc.T("程序内不提供下载按钮。只读公开仓库不需要勾任何权限。")
              + Loc.T("令牌会跟着加密备份和网盘同步一起走，换台机器恢复后不用重新填。");
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

        /* 掩码（或空）都表示"没输入新的"：验证已保存的那个。
         * 掩码是固定 8 个圆点，绝不能拿去请求 GitHub —— 那会得到 401，
         * 用户还以为自己存的令牌坏了。 */
        if (TokenBox.Text == TokenMask || token.Length == 0)
        {
            string saved = _host.Settings.Global.GitHubToken;
            if (!UpdateService.HasToken(saved))
            {
                TokenStatusText.Text = Loc.T("先把令牌粘进左边的输入框，再点「验证」。");
                return;
            }
            token = UpdateService.NormalizeToken(saved);
            usingSaved = true;
        }

        _verifying = true;
        VerifyTokenButton.IsEnabled = false;
        SaveTokenButton.IsEnabled = false;
        TokenStatusText.Text = usingSaved
            ? Loc.T("正在验证已保存的令牌…")
            : Loc.T("正在用这个令牌请求 GitHub…");

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
                ? "✓ " + result.Message + Loc.T("　（这就是已保存的令牌）")
                : "✓ " + result.Message + Loc.T("　—— 点「保存」把它存下来");
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

        /* 输入框里还是掩码：用户没动过。当作"已保存"，不写盘、不报错 ——
         * 报错会让人以为要重新填一遍。 */
        if (TokenBox.Text == TokenMask)
        {
            string cur = _host.Settings.Global.GitHubToken;
            TokenStatusText.Text = UpdateService.HasToken(cur)
                ? Loc.T("令牌没有改动，仍是已保存的那个（不回显）。")
                  + Loc.T("要换新的，直接在输入框里输入或粘贴即可。")
                : Loc.T("输入框里只有占位圆点，先把新令牌粘进来。");
            return;
        }

        string token = UpdateService.NormalizeToken(TokenBox.Text);
        if (token.Length == 0)
        {
            TokenStatusText.Text = Loc.T("输入框是空的 —— 要清掉已保存的令牌请点「清除」。");
            return;
        }

        _host.Settings.Global.GitHubToken = token;
        _host.SaveSettings();
        RefreshTokenStatus();

        /* 只提示不拦截：GitHub 未来可能启用新前缀，卡死格式等于把用户挡在外面。 */
        if (!UpdateService.LooksLikeGitHubToken(token))
        {
            TokenStatusText.Text = Loc.T("这个看起来不像 GitHub 令牌（通常以 ghp_ 或 github_pat_ 开头），")
                                   + Loc.T("但已经按你填的保存了 —— 如果检查更新报「令牌无效」，回头核对一下。");
            return;
        }

        _host.Notify(Loc.T("令牌已保存，更新检查改用你的账号配额（5000 次/小时）"));
    }

    private void OnClearToken(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        if (UpdateService.HasToken(_host.Settings.Global.GitHubToken) &&
            !AppDialog.ConfirmDanger(Window.GetWindow(this),
                Loc.T("清除后更新检查会回到匿名访问（每小时 60 次），发现新版本时也不再自动下载，")
                + Loc.T("只提示并给出下载地址。\n\n已保存的令牌会被删掉，需要重新填。"),
                Loc.T("清除 GitHub 令牌？"), Loc.T("清除")))
            return;

        _host.Settings.Global.GitHubToken = "";
        _host.SaveSettings();
        RefreshTokenStatus();
        _host.Notify(Loc.T("已清除令牌，更新检查回到匿名访问"));
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
            _host?.Notify(UiText.Failed(Loc.T("打开链接"), ex.Message));
        }
    }

    // ---------------- 自动更新 ----------------

    private void OnAutoUpdateChanged(object sender, RoutedEventArgs e)
    {
        if (_host == null || _suppress) return;
        _host.Settings.Global.AutoUpdate = AutoUpdateBox.IsChecked == true;
        _host.SaveSettings();
        _host.Notify(_host.Settings.Global.AutoUpdate ? Loc.T("已开启自动更新") : Loc.T("已关闭自动更新"));
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
        SetStatus(Loc.T("正在检查更新…"), "");
        try
        {
            var result = await _updates.CheckAsync(_host?.Settings.Global.GitHubToken);
            if (_host != null) _host.Settings.Global.LastUpdateCheck = DateTime.Now;
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
                SetStatus(head + Loc.T(" —— 没填 GitHub 令牌，不能自动下载，请点下面的地址用浏览器下载"),
                    Trim(result.Update.ReleaseNotes, 600));
            }
            else
            {
                ManualLinkPanel.Visibility = Visibility.Collapsed;
                DownloadButton.Content = Loc.T("下载并安装");
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
            SetStatus(Loc.T("下载完成，正在替换并重启…"), "");
            await Task.Delay(400);

            if (UpdateService.ApplyAndRestart(path))
            {
                _host?.ExitApp();
                return;
            }
            SetStatus(Loc.T("替换失败，请手动下载安装包"), "", isError: true);
            DownloadButton.Content = Loc.T("打开下载页");
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
