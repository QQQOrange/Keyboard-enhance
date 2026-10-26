using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using CapsLockPlus.Core.Update;
using CapsLockPlus.Core.Windows;

namespace CapsLockPlus.UI.Windows;

/// <summary>启动时发现新版本的提示窗：下载 → 替换 → 重启，全程不离开当前页面。</summary>
public partial class UpdatePromptWindow : Window
{
    private readonly AppHost _host;
    private readonly UpdateInfo _info;
    private readonly UpdateService _service = new();

    /// <summary>正在下载：界面已切成"只剩取消"。</summary>
    private bool _busy;

    /// <summary>窗口已经关了：异步回来的代码不许再碰控件。</summary>
    private bool _closed;

    /// <summary>这一次下载的取消源；空闲时为 null。</summary>
    private CancellationTokenSource? _cts;

    /// <summary>只能用浏览器下载（没令牌 / 没有匹配的包）——「下载并安装」永久隐藏。</summary>
    private bool _manualOnly;

    /// <summary>没填令牌时唯一能用的下载入口：点它交给浏览器。</summary>
    private string _manualUrl = "";

    public UpdatePromptWindow(AppHost host, UpdateInfo info)
    {
        _host = host;
        _info = info;
        InitializeComponent();
        TitleText.Text = Loc.F("发现新版本 {0}", info.Version);
        SubText.Text = Loc.F("当前 {0}", UpdateService.CurrentVersion) +
                       (info.SizeText.Length > 0 ? Loc.F("　·　{0}", info.SizeText) : "");
        NotesText.Text = string.IsNullOrWhiteSpace(info.ReleaseNotes)
            ? Loc.T("（该版本没有填写更新说明）")
            : info.ReleaseNotes;
        StatusText.Text = "";

        /* 两种情况下**程序内不提供下载**：
         *
         * ① 没填令牌 —— 自动下载会直接拉一个几 MB 的 exe 并尝试替换自己，这是整条
         *    流程里最"重"的一步；没填令牌就把选择权交回去，用户在浏览器里看得见
         *    GitHub 域名也更放心。
         * ② 这个 release 里没有与本机系统/架构匹配的包 —— 下错了要么装不上，要么
         *    像之前那样在 Win11 上白下 60 多兆的 Win7 自包含包。 */
        bool noToken = !UpdateService.HasToken(host.Settings.Global.GitHubToken);
        if (noToken || !info.MatchedSystem)
        {
            EnableManualDownload(
                string.IsNullOrWhiteSpace(info.DownloadUrl) ? info.PageUrl : info.DownloadUrl,
                info.MatchedSystem
                    ? Loc.T("没有填写 GitHub 个人访问令牌，这一版不能自动下载。点下面的地址用浏览器下载：")
                    : Loc.T("这一版没有与你系统匹配的安装包，程序里不自动下载。点下面的地址到下载页自己挑一个："));
        }
    }

    /// <summary>
    /// 只能手动下载时的形态：**藏掉「下载并安装」**，只留一个可点的下载地址。
    ///
    /// 用户明确要求：没填令牌就算检测到新版本，也不能在程序里点下载。
    /// 所以这里不是"把按钮改成打开下载页"，而是程序内根本不提供下载入口 ——
    /// 必须点链接去浏览器，看得见 GitHub 的域名再下载。
    /// </summary>
    private void EnableManualDownload(string url, string hint)
    {
        _manualOnly = true;
        DownloadButton.Visibility = Visibility.Collapsed;

        LinkHint.Text = hint;
        DownloadLinkText.Text = url;
        LinkCard.Visibility = Visibility.Visible;
        _manualUrl = url;
    }

    // ---------------- 呈现到桌面 ----------------

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        BringToDesktopFront();
    }

    /// <summary>
    /// 让它**真的出现在桌面上**，并在任务栏留一项。
    ///
    /// 启动时是静默检查更新，本进程一般不是前台进程：<c>Show()</c> 内部调的
    /// <c>SetForegroundWindow</c> 会被 Windows 的**前台锁定**拒绝 —— 窗口建出来了，
    /// 却压在别的窗口底下；而当时 <c>ShowInTaskbar="False"</c>，任务栏上一点痕迹
    /// 都没有，用户的感觉就是"根本没弹出更新提示"。
    ///
    /// 三步补偿：① 先置顶（顶层窗口不受前台锁定影响，一定可见）；
    /// ② 再硬抢前台（拿到键盘焦点，Esc / 回车才有反应）；
    /// ③ 1.2 秒后撤掉置顶，别长期压着用户正在看的窗口。
    /// </summary>
    private void BringToDesktopFront()
    {
        try
        {
            Topmost = true;
            Activate();

            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (!WindowService.ForceForeground(hwnd, allowUnlockTimeout: true))
                Core.CoreLog.Warn("更新提示窗没能抢到前台，已置顶显示");

            var drop = new DispatcherTimer(DispatcherPriority.Normal)
            {
                Interval = TimeSpan.FromSeconds(1.2),
            };
            drop.Tick += (_, _) =>
            {
                drop.Stop();
                if (!_closed) Topmost = false;
            };
            drop.Start();
        }
        catch (Exception ex)
        {
            Core.CoreLog.Warn("更新提示窗置前失败：" + ex.Message);
        }
    }

    // ---------------- 关闭 / 取消 ----------------

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (_busy) RequestCancel();
            else Close();
            return;
        }
        base.OnPreviewKeyDown(e);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // 关窗就等于放弃这次更新：先掐断下载。
        // 临时文件由 DownloadAsync 的取消分支自己删，这里不用管。
        _closed = true;
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }   /* 预期内：已在收尾 */
        base.OnClosing(e);
    }

    private void OnCancelDownload(object sender, RoutedEventArgs e) => RequestCancel();

    /// <summary>
    /// 请求取消：只发信号，真正的收尾（删临时文件、恢复按钮）在
    /// <see cref="RunDownloadAsync"/> 的 finally 里做 —— 那里才知道下载循环退到哪一步。
    /// </summary>
    private void RequestCancel()
    {
        if (_cts is not { IsCancellationRequested: false }) return;
        CancelButton.IsEnabled = false;   // 防连点
        StatusText.Text = Loc.T("正在取消…");
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }     /* 预期内：已在收尾 */
    }

    private void OnLater(object sender, RoutedEventArgs e) => Close();

    private void OnSkip(object sender, RoutedEventArgs e)
    {
        _host.Settings.Global.SkippedVersion = _info.Version;
        _host.SaveSettings();
        _host.Notify(Loc.F("已跳过 {0}，下次有新版本还会提醒", _info.Version));
        Close();
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
                Loc.T("没能自动打开浏览器。\n\n请手动访问：\n") + url,
                Loc.T("打开下载页失败"));
        }
    }

    // ---------------- 下载 ----------------

    private void OnDownload(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _ = RunDownloadAsync();
    }

    /// <summary>
    /// 下载 → 校验 → 替换 → 重启。
    ///
    /// 下载期间界面只剩一个「取消」：原来三个按钮全被禁用、窗口又是无标题栏的，
    /// 慢网下用户既不能中断也不能关窗，只能干等。
    /// </summary>
    private async Task RunDownloadAsync()
    {
        if (_busy) return;
        _busy = true;
        _cts = new CancellationTokenSource();
        CancellationToken ct = _cts.Token;

        SetBusyUi(true);
        Progress.Value = 0;
        Progress.Visibility = Visibility.Visible;
        StatusText.Text = Loc.T("正在下载…");

        try
        {
            var progress = new Progress<double>(p =>
            {
                if (_closed) return;
                Progress.Value = p * 100;
                StatusText.Text = Loc.F("正在下载… {0:0}%", p * 100);
            });

            var (ok, path, error, canceled) = await _service.DownloadAsync(_info, progress, ct);
            if (_closed) return;

            if (canceled)
            {
                StatusText.Text = Loc.T("已取消下载，可重新下载");
                Progress.Visibility = Visibility.Collapsed;
                return;
            }

            if (!ok)
            {
                StatusText.Text = error;
                Progress.Visibility = Visibility.Collapsed;
                return;
            }

            Progress.Value = 100;
            StatusText.Text = Loc.T("正在替换并重启…");
            await Task.Delay(400, ct);
            if (_closed) return;

            if (UpdateService.ApplyAndRestart(path))
            {
                _closed = true;   // 主程序马上退出，后续不许再碰界面
                _host.ExitApp();
                return;
            }

            StatusText.Text = Loc.T("替换失败，请到 Releases 手动下载");
            ShowOpenPageFallback();
        }
        catch (OperationCanceledException)
        {
            if (!_closed)
            {
                StatusText.Text = Loc.T("已取消下载，可重新下载");
                Progress.Visibility = Visibility.Collapsed;
            }
        }
        catch (Exception ex)
        {
            Core.CoreLog.Error("更新下载异常", ex);
            if (!_closed)
            {
                StatusText.Text = Loc.F("下载失败：{0}", ex.Message);
                Progress.Visibility = Visibility.Collapsed;
            }
        }
        finally
        {
            _busy = false;
            _cts?.Dispose();
            _cts = null;
            if (!_closed) SetBusyUi(false);
        }
    }

    /// <summary>替换失败后的退路：按钮变成「打开下载页」，别让用户卡在这里。</summary>
    private void ShowOpenPageFallback()
    {
        _manualOnly = false;
        SetBusyUi(false);
        DownloadButton.Content = Loc.T("打开下载页");
        DownloadButton.Visibility = Visibility.Visible;
        DownloadButton.IsEnabled = true;
        DownloadButton.Click -= OnDownload;
        DownloadButton.Click += OnOpenPageClick;
    }

    private void OnOpenPageClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(_info.PageUrl) { UseShellExecute = true });
            Close();
        }
        catch (Exception ex)
        {
            Core.CoreLog.Swallow(ex, "打开更新下载页");
            AppDialog.Warn(Application.Current?.MainWindow,
                Loc.T("没能自动打开浏览器。\n\n请手动访问：\n") + _info.PageUrl,
                Loc.T("打开下载页失败"));
        }
    }

    /// <summary>
    /// 下载中与空闲两种形态的按钮排布。
    ///
    /// 下载中把「跳过 / 稍后 / 下载」全藏起来、只留「取消」：这三个动作在流还在写
    /// 文件的时候点下去都不安全（关窗会留下没人清理的临时文件、重复点下载会开两条流）。
    /// </summary>
    private void SetBusyUi(bool busy)
    {
        SkipButton.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        LaterButton.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        DownloadButton.Visibility = busy || _manualOnly ? Visibility.Collapsed : Visibility.Visible;
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;

        DownloadButton.IsEnabled = !busy;
        SkipButton.IsEnabled = !busy;
        LaterButton.IsEnabled = !busy;
        CancelButton.IsEnabled = busy;
    }
}
