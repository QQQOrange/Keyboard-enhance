using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using CapsLockPlus.Core;
using CapsLockPlus.Core.Localization;
using CapsLockPlus.UI.Windows;
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
        /* 音量下限跟系统步进走：系统一步就是 2%，设 1% 也只能走 2%，
         * 与其让用户设一个"永远达不到"的值，不如把下限抬到 2%。 */
        int sysStep = Math.Max(1, Core.Windows.VolumeService.StepPercent());
        VolumeStepSlider.Minimum = sysStep;
        VolumeStepSlider.Value = Math.Max(g.VolumeStep, sysStep);
        BrightnessStepSlider.Value = g.BrightnessStep;
        UpdateVolumeStepText(g.VolumeStep);
        UpdateBrightnessStepText(g.BrightnessStep);
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
        BindLanguageBox(g.Language);
        DataPathText.Text = $"设置文件：{AppPaths.SettingsFile}\n日志：{CoreLog.LogPath}";
        RefreshStartupPwState();
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
        _host.Notify(enabled ? Loc.T("已设置开机自启") : Loc.T("已取消开机自启"));
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
        AdminBadgeText.Text = admin ? Loc.T("管理员") : Loc.T("普通权限");
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

        AdminStateText.Text = $"当前：{(running ? Loc.T("管理员权限") : Loc.T("普通权限"))}运行　·　" +
                              $"设置：下次启动以{(wanted ? Loc.T("管理员") : Loc.T("普通"))}权限运行";

        /* 开机自启 + 管理员 = 每次登录都要点一次 UAC。
         * 这是 Windows 的规矩（注册表 Run 项没法声明"要高权限"），
         * 与其让用户莫名其妙，不如直说。 */
        if (wanted && _host?.Settings.Global.StartWithWindows == true)
            AdminStateText.Text += Loc.T("\n开机自启时会弹一次 UAC 确认 —— 关掉「开机自动启动」就不会弹。");

        RestartElevatedButton.Visibility = running == wanted ? Visibility.Collapsed : Visibility.Visible;
        // 已经是管理员时不能再"提权重启"，按钮只用于"降回普通权限"
        RestartElevatedButton.Content = wanted ? Loc.T("立即重启以生效") : Loc.T("立即重启（回到普通权限）");
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
            bool ok = AppDialog.Confirm(Window.GetWindow(this),
                Loc.T("以管理员身份启动后，「选中文字 → 直接搜索」会失效。\n\n") +
                Loc.T("原因：Windows 禁止管理员进程向普通权限的浏览器 / 编辑器\n") +
                Loc.T("注入输入（UIPI 安全限制），取词通道会全部被拦掉。\n\n") +
                Loc.T("好处：对着「以管理员身份运行」的程序按快捷键才会生效。\n") +
                Loc.T("（锁屏状态下任何用户态程序都无法使用快捷键，\n") +
                Loc.T(" 这与是否管理员无关。）\n\n") +
                Loc.T("确定要开启吗？"),
                Loc.T("开启管理员模式"), DialogKind.Warn, okText: Loc.T("开启"));
            if (!ok)
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

        bool ask = AppDialog.Confirm(Window.GetWindow(this),
            enable
                ? Loc.T("已保存。下次启动会以管理员身份运行（会弹一次 UAC 确认）。\n\n现在就重启吗？")
                : Loc.T("已保存。下次启动会以普通权限运行。\n\n现在就重启吗？"),
            Loc.T("需要重启才生效"), DialogKind.Question, okText: Loc.T("现在重启"), cancelText: Loc.T("稍后"));
        if (ask) RestartApp();
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
            AppDialog.Warn(Window.GetWindow(this),
                Loc.T("提权失败：") + (err ?? Loc.T("未知原因")) + Loc.T("\n\n（多半是在 UAC 对话框里点了「否」）"),
                Loc.T("没能以管理员身份重启"));
            return;
        }
        _host.ExitApp(restart: true);
    }

    /* ---- 启动密码（与备份密码完全独立的一套） ----------------------------
     *
     * 开启：先弹"设置新密码"（两遍确认），落库后才算开；
     * 关闭：先验当前密码，证明了才让关 —— 否则路过的人顺手一拨就解开了；
     * 修改：先验旧密码，再设新密码。
     *
     * 密码的存取全在 Core.Settings.StartupPassword（文件是真相），
     * 这里只做流程；设置里的 Security 区段是镜像，供界面显示与随设置持久化。
     */

    /// <summary>刷新开关、状态行与「修改密码」按钮（真相以 startup.json 为准）。</summary>
    private void RefreshStartupPwState()
    {
        if (_host == null) return;

        StartupPassword.MirrorTo(_host.Settings.Security);
        bool on = _host.Settings.Security.StartupPasswordEnabled;

        _suppress = true;
        StartupPwBox.IsChecked = on;
        _suppress = false;

        StartupPwStateText.Text = on
            ? Loc.T("已开启：下次启动软件时先要输入密码。忘记密码可删除数据目录里的 startup.json 解除保护（其他设置不受影响）。")
            : Loc.T("未开启：启动软件不需要密码。");

        ChangeStartupPwButton.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnStartupPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_host == null || _suppress) return;

        bool enable = StartupPwBox.IsChecked == true;
        var sec = _host.Settings.Security;

        if (enable)
        {
            var dlg = new NewPasswordWindow(
                Loc.T("设置启动密码"),
                Loc.T("设好后，每次启动本软件都要先输入这个密码才能进入主界面。"));
            dlg.ShowDialog();
            if (dlg.Password == null)
            {
                RevertStartupPwBox(false);
                return;
            }

            if (!StartupPassword.TrySet(dlg.Password, out string err))
            {
                AppDialog.Error(Window.GetWindow(this), err, Loc.T("设置启动密码"));
                RevertStartupPwBox(false);
                return;
            }

            StartupPassword.MirrorTo(sec);
            Save();
            RefreshStartupPwState();
            _host.Notify(Loc.T("已设置启动密码，下次启动软件时生效"));
        }
        else
        {
            // 关保护要先证明是机主：路过的人不能顺手一拨就解开
            var verify = new StartupPasswordWindow(
                p => StartupPassword.Verify(p),
                Loc.T("关闭启动密码"),
                Loc.T("请输入当前的启动密码。验证通过后才能关闭保护。"),
                Loc.T("关闭保护"), Loc.T("取消"));
            verify.ShowDialog();
            if (!verify.Passed)
            {
                RevertStartupPwBox(true);
                return;
            }

            StartupPassword.Clear();
            StartupPassword.MirrorTo(sec);
            Save();
            RefreshStartupPwState();
            _host.Notify(Loc.T("已关闭启动密码"));
        }
    }

    /// <summary>修改启动密码：验旧 → 设新。</summary>
    private void OnChangeStartupPassword(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;

        var verify = new StartupPasswordWindow(
            p => StartupPassword.Verify(p),
            Loc.T("修改启动密码"),
            Loc.T("请输入当前的启动密码。"),
            Loc.T("下一步"), Loc.T("取消"));
        verify.ShowDialog();
        if (!verify.Passed) return;

        var dlg = new NewPasswordWindow(
            Loc.T("修改启动密码"),
            Loc.T("设个好记的新密码。下次启动就要用它了。"));
        dlg.ShowDialog();
        if (dlg.Password == null) return;

        if (!StartupPassword.TrySet(dlg.Password, out string err))
        {
            AppDialog.Error(Window.GetWindow(this), err, Loc.T("修改启动密码"));
            return;
        }

        StartupPassword.MirrorTo(_host.Settings.Security);
        Save();
        RefreshStartupPwState();
        _host.Notify(Loc.T("已修改启动密码"));
    }

    /// <summary>把开关拨回真实状态（用户取消流程时用，避免界面撒谎）。</summary>
    private void RevertStartupPwBox(bool on)
    {
        _suppress = true;
        StartupPwBox.IsChecked = on;
        _suppress = false;
    }

    private void OnFeedbackChanged(object sender, RoutedEventArgs e)
    {
        if (_host == null || _suppress) return;
        _host.Settings.Global.ShowActionFeedback = FeedbackBox.IsChecked == true;
        Save();
        if (FeedbackBox.IsChecked == true)
            _host.Notify(Loc.T("已开启按键反馈：按 Caps+S 试试，角落会显示「左移」"));
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

    /// <summary>
    /// 音量一次调多少。
    ///
    /// 音量走系统音量键，而系统步进是固定的（通常 2%），所以设 5% 时
    /// 实际是"这次 4%、下次 6%"交替 —— 长期平均正好 5%。
    /// 这一点必须在界面上说清楚，否则用户会觉得"设了 5 却总是差 1"。
    /// </summary>
    private void OnVolumeStepChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_host == null || _suppress) return;
        int v = (int)Math.Round(VolumeStepSlider.Value);
        _host.Settings.Global.VolumeStep = v;
        UpdateVolumeStepText(v);
        Save();
    }

    private void OnBrightnessStepChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_host == null || _suppress) return;
        int v = (int)Math.Round(BrightnessStepSlider.Value);
        _host.Settings.Global.BrightnessStep = v;
        UpdateBrightnessStepText(v);
        Save();
    }

    private void UpdateVolumeStepText(int v)
    {
        if (VolumeStepText == null) return;
        int sys = Core.Windows.VolumeService.StepPercent();
        VolumeStepText.Text = (v % sys == 0
            ? $"{v}%（按住时按系统 {sys}% 细调）"
            : $"{v}%（系统步进 {sys}%，按整数倍补偿，长期平均正好 {v}%）")
            + Loc.T(" · 单个键可在「按键映射 → 编辑绑定」里单独设步进");
    }

    private void UpdateBrightnessStepText(int v)
    {
        if (BrightnessStepText == null) return;
        BrightnessStepText.Text = $"{v}%（按住时按 1% 细调）"
            + Loc.T(" · 单个键可在「按键映射 → 编辑绑定」里单独设步进");
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

    /// <summary>
    /// 把内置语言包填进下拉框。条目直接来自 <see cref="Loc.Available"/> ——
    /// 往 Assets/Lang 丢一个新 json，这里就自动多一项，不用改代码。
    /// </summary>
    private void BindLanguageBox(string? current)
    {
        if (!ReferenceEquals(LanguageBox.ItemsSource, Loc.Available))
            LanguageBox.ItemsSource = Loc.Available;

        string code = Loc.Normalize(current);
        LanguageBox.SelectedItem = Loc.Available.FirstOrDefault(l => l.Code == code)
                                   ?? Loc.Available.FirstOrDefault();
    }

    /// <summary>
    /// 切界面语言：**不重启**。
    ///   ① <see cref="Loc.Apply"/> 换字典并通知所有 {loc:T} 绑定 → XAML 自己重画；
    ///   ② <c>Load()</c> 重跑一遍 —— 代码里 set 的文本（数字/步长/路径这些
    ///      带单位的串）不会自己变，得重算；
    ///   ③ 存盘，下次启动直接是这门语言。
    /// </summary>
    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_host == null || _suppress) return;
        if (LanguageBox.SelectedItem is not LocLanguage lang) return;
        if (string.Equals(_host.Settings.Global.Language, lang.Code, StringComparison.OrdinalIgnoreCase))
            return;

        _host.Settings.Global.Language = lang.Code;
        Save();

        /* 顺序有讲究：
         * 1) 先 Loc.Apply —— 换字典并把所有 {loc:T} 令牌作废，XAML 上的文字**自己**重画；
         * 2) 再 Refresh —— 代码里拼出来的文本（Loc.T("设置文件：…")这类）刷新时才会重算；
         * 3) 最后重建常驻界面 —— 托盘菜单、动作目录、提示气泡是长生命周期对象，
         *    它们的文本在构造时就定死了，只能靠事件重建。
         * Refresh() 内部自己管 _suppress，这里不要在外层再动它。 */
        Loc.Apply(lang.Code);
        Refresh();
        _host.NotifyLocalizedSurfaces();
    }

    private void OnImportLegacy(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        var dlg = new OpenFileDialog
        {
            Title = Loc.T("选择旧版 CapsLock+settings.ini"),
            Filter = Loc.T("CapsLock+ 设置文件|*.ini|所有文件|*.*"),
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
                msg += Loc.T("\n\n以下项目需要手动确认：\n") + string.Join("\n", result.Warnings.Take(8));
            AppDialog.Info(Window.GetWindow(this), msg, Loc.T("导入旧版设置"), DialogKind.Success);
        }
        catch (Exception ex)
        {
            AppDialog.Error(Window.GetWindow(this), Loc.T("导入失败：") + ex.Message, Loc.T("导入旧版设置"));
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
            _host?.Notify(Loc.T("打开失败：") + ex.Message);
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
            _host?.Notify(Loc.T("打开日志失败：") + ex.Message);
        }
    }

    private void OnReload(object sender, RoutedEventArgs e) => _host?.ReloadAll();

    private void OnResetAll(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        if (!AppDialog.ConfirmDanger(Window.GetWindow(this),
                Loc.T("把全部设置恢复成默认值？\n（按键映射、Qbar 条目、窗口绑定、外观都会被重置）\n\n") +
                Loc.T("启动密码不受影响 —— 要关掉它请用上面的开关（需要验证当前密码）。"),
                Loc.T("重置全部设置"), okText: Loc.T("恢复默认"))) return;
        _host.Settings = DefaultSettings.Create();
        _host.SaveSettings();
        _host.ReloadAll();
        Refresh();
        _host.Notify(Loc.T("已恢复默认设置"));
    }
}
