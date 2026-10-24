using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using CapsLockPlus.Core;
using CapsLockPlus.UI.Windows;
using CapsLockPlus.Core.Settings;
using CapsLockPlus.Core.Sync;
using Microsoft.Win32;

namespace CapsLockPlus.UI.Views;

/// <summary>
/// 「同步与备份」页：WebDAV 自动同步 + 加密/明文导入导出。
/// </summary>
public partial class BackupView : UserControl
{
    private AppHost? _host;
    private bool _suppress;
    private readonly List<GroupVm> _groups = new();

    public BackupView() => InitializeComponent();

    public void Initialize(AppHost host)
    {
        _host = host;
        BuildGroups();
        Refresh();
        host.SyncCompleted += OnSyncCompleted;
    }

    public void Refresh()
    {
        if (_host == null) return;
        var s = _host.Settings.Sync;

        _suppress = true;
        UrlBox.Text = s.WebDavUrl;
        UserBox.Text = s.WebDavUser;
        PassBox.Password = s.WebDavPass;
        BackupPassBox.Password = s.BackupPassword;
        OnStartBox.IsChecked = s.SyncOnStart;
        OnExitBox.IsChecked = s.SyncOnExit;
        AutoBackupBox.IsChecked = s.AutoBackup;
        AutoMinutesBox.Text = s.AutoBackupMinutes.ToString();
        KeepBox.Text = s.KeepLocalBackups.ToString();
        DaysBox.Text = s.CleanupDays.ToString();
        _suppress = false;

        UpdatePasswordWarning();
        UpdateVaultStatus();
        BackupDirText.Text = SyncService.BackupDir;
        FallbackHint.Visibility = SyncService.BackupDirIsFallback
            ? Visibility.Visible
            : Visibility.Collapsed;
        RefreshLocalSummary();
        RefreshDataLocation();

        SyncStatus.Text = s.LastSyncAt == DateTime.MinValue
            ? "还没同步过"
            : $"上次同步：{s.LastSyncAt:yyyy-MM-dd HH:mm}　{s.LastSyncInfo}";
    }

    /// <summary>
    /// 数据目录 + 加密状态。
    ///
    /// 用户要求"所有用户数据都加密"，那就得能当场看到它到底加没加密 ——
    /// 只说一句"已加密"是不够的，要指出是哪种算法、密钥怎么来的。
    /// </summary>
    private void RefreshDataLocation()
    {
        DataDirText.Text = AppPaths.DataDir + "\n" + AppPaths.DataDirNote;

        if (!DataVault.IsEnabled)
        {
            DataEncText.Text = "⚠ 本地数据还是明文 —— 设一个「备份密码」就会立刻转成密文（AES-256-GCM）。";
            DataEncText.Foreground = PrivateFg;
            return;
        }

        DataEncText.Text = DataVault.IsUnlocked
            ? "✓ 本地数据已加密（AES-256-GCM，密钥由备份密码派生）"
            : "✓ 本地数据已加密，但本次会话还没解锁 —— 修改不会被保存，解锁后恢复正常。";
        DataEncText.Foreground = (Brush)FindResource("Brush.TextTertiary");
    }

    /// <summary>本地备份的一句话概况。</summary>
    private void RefreshLocalSummary()
    {
        var list = SyncService.ListLocal();
        if (list.Count == 0)
        {
            LocalSummary.Text = $"还没有本地备份。点「现在就备份一次」会生成一份 {SyncService.Extension} 文件。";
            return;
        }
        long total = list.Sum(x => x.Size);
        int enc = list.Count(x => x.Encrypted);
        LocalSummary.Text = $"本地 {list.Count} 份（{enc} 份已加密，共 {total / 1024.0:0.#} KB）"
                            + $"，最近一份 {list[0].Time:MM-dd HH:mm}。";
    }

    private void BuildGroups()
    {
        _groups.Clear();
        foreach (var g in ExportImport.Items())
        {
            _groups.Add(new GroupVm
            {
                Id = g.Id,
                Name = g.Name,
                Desc = g.Desc,
                Private = g.Private,
                Checked = g.DefaultChecked,
            });
        }
        GroupList.ItemsSource = null;
        GroupList.ItemsSource = _groups;
        RefreshExportLock();   // 初始状态：非隐私组默认勾选，开关应可用
    }

    private static readonly Brush PrivateFg =
        new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E5484D"));

    private void UpdatePasswordWarning()
    {
        bool noPassword = string.IsNullOrEmpty(BackupPassBox.Password);
        NoPasswordWarning.Visibility = noPassword ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------------- 同步设置 ----------------

    private void OnUrlChanged(object sender, TextChangedEventArgs e)
    {
        if (_host == null || _suppress) return;
        _host.Settings.Sync.WebDavUrl = UrlBox.Text.Trim();
        _host.SaveSettings();
    }

    private void OnUserChanged(object sender, TextChangedEventArgs e)
    {
        if (_host == null || _suppress) return;
        _host.Settings.Sync.WebDavUser = UserBox.Text.Trim();
        _host.SaveSettings();
    }

    private void OnPassChanged(object sender, RoutedEventArgs e)
    {
        if (_host == null || _suppress) return;
        _host.Settings.Sync.WebDavPass = PassBox.Password;
        _host.SaveSettings();
    }

    /// <summary>
    /// 备份密码变化。
    ///
    /// 这个密码不只是"给云端备份加密"—— 一旦设置，**本地数据也立刻转为密文**：
    /// settings.json 里的搜索记录、宏、窗口绑定路径、常用邮箱这些
    /// 都不再以明文躺在磁盘上。
    ///
    /// 注意：PasswordChanged 每敲一个字符就触发一次，而应用加密要跑
    /// 60 万次 PBKDF2（约 0.3~0.6 秒）。直接执行会让打字一顿一顿的，
    /// 所以先防抖 700ms，停手后再真正应用。
    /// </summary>
    private void OnBackupPassChanged(object sender, RoutedEventArgs e)
    {
        if (_host == null || _suppress) return;
        _host.Settings.Sync.BackupPassword = BackupPassBox.Password;
        UpdatePasswordWarning();

        _passTimer?.Stop();
        _passTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _passTimer.Tick += (_, _) =>
        {
            _passTimer?.Stop();
            _passTimer = null;
            ApplyVaultPassword();
        };
        _passTimer.Start();
    }

    private DispatcherTimer? _passTimer;

    /// <summary>把密码变更真正落到加密层：启用 / 换密钥 / 关闭。</summary>
    private void ApplyVaultPassword()
    {
        if (_host == null) return;
        string pass = _host.Settings.Sync.BackupPassword ?? "";

        try
        {
            if (string.IsNullOrWhiteSpace(pass))
            {
                if (!DataVault.IsEnabled) return;
                DataVault.Disable();
                SettingsStore.Save(_host.Settings);       // 写回明文
                RemovePlaintextBackups();
                ShowVaultStatus("已关闭本地加密：数据恢复为明文存储。", ok: false);
                return;
            }

            bool firstTime = !DataVault.IsEnabled;
            DataVault.Enable(pass);

            /* 必须**立刻**落盘：AppHost 的 SaveSettings 有防抖，
             * 万一用户此时关掉窗口，明文就留在磁盘上了。 */
            SettingsStore.Save(_host.Settings);
            RemovePlaintextBackups();

            ShowVaultStatus(firstTime
                ? "已启用本地加密：设置、宏、按键绑定、窗口绑定全部改为 AES-256-GCM 密文存储，"
                  + "文件拷到别的电脑也打不开。日常启动不需要输密码（密钥用 Windows DPAPI 缓存）。"
                : "密码已更新，本地数据已用新密钥重新加密。", ok: true);
        }
        catch (Exception ex)
        {
            ShowVaultStatus("加密失败：" + ex.Message, ok: false);
            CoreLog.Warn("[加密] 应用密码失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 清掉可能残留的明文副本。
    ///
    /// settings.json.bak 是"上一次保存前的副本"，启用加密之前写下的那些
    /// 就是明文 —— 留着等于把加密白做了。实现挪到 DataVault，
    /// 这样启动时也能顺手清一遍（不只是"用户点了密码框"那一刻）。
    /// </summary>
    private static void RemovePlaintextBackups() =>
        DataVault.PurgePlaintextCopies(AppPaths.SettingsFile);

    private void ShowVaultStatus(string text, bool ok)
    {
        VaultStatusText.Text = text;
        VaultStatusBar.Background = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(ok ? "#221D9E75" : "#22E5484D"));
        VaultStatusBar.BorderBrush = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(ok ? "#551D9E75" : "#55E5484D"));
        VaultStatusText.Foreground = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(ok ? "#0F6E56" : "#E5484D"));
        VaultStatusBar.Visibility = Visibility.Visible;
    }

    /// <summary>刷新时同步加密状态（老用户可能早就设过密码，但那时还没加密本地数据）。</summary>
    private void UpdateVaultStatus()
    {
        if (DataVault.IsEnabled)
            ShowVaultStatus("本地数据已加密（AES-256-GCM）：设置、宏、按键绑定、窗口绑定都以密文存储。", ok: true);
        else
            VaultStatusBar.Visibility = Visibility.Collapsed;
    }

    private void OnOnStartChanged(object sender, RoutedEventArgs e)
    {
        if (_host == null || _suppress) return;
        _host.Settings.Sync.SyncOnStart = OnStartBox.IsChecked == true;
        _host.SaveSettings();
    }

    private void OnOnExitChanged(object sender, RoutedEventArgs e)
    {
        if (_host == null || _suppress) return;
        _host.Settings.Sync.SyncOnExit = OnExitBox.IsChecked == true;
        _host.SaveSettings();
    }

    // ---------------- 自动备份 / 保留策略 ----------------

    private void OnAutoBackupChanged(object sender, RoutedEventArgs e)
    {
        if (_host == null || _suppress) return;

        bool on = AutoBackupBox.IsChecked == true;
        _host.Settings.Sync.AutoBackup = on;

        if (on && string.IsNullOrWhiteSpace(_host.Settings.Sync.BackupPassword))
        {
            /* 没密码就没法加密，自动备份起来也只能失败 ——
             * 与其每 60 分钟静默失败一次，不如把开关退回去并说清楚。 */
            _suppress = true;
            AutoBackupBox.IsChecked = false;
            _suppress = false;
            _host.Settings.Sync.AutoBackup = false;
            SyncStatus.Text = "✗ 自动备份需要先设「备份密码」—— 备份必须是密文。";
            return;
        }

        _host.SaveSettings();
        _host.RestartAutoBackupTimer();
        SyncStatus.Text = on
            ? $"✓ 已开启自动备份：每 {_host.Settings.Sync.AutoBackupMinutes} 分钟一次（配置没变化会跳过）"
            : "已关闭自动备份。";
    }

    private void OnAutoMinutesChanged(object sender, RoutedEventArgs e)
    {
        if (_host == null || _suppress) return;
        int before = _host.Settings.Sync.AutoBackupMinutes;
        int minutes = ParseInt(AutoMinutesBox.Text, 60, 5, 1440);
        AutoMinutesBox.Text = minutes.ToString();
        if (minutes == before) return;

        _host.Settings.Sync.AutoBackupMinutes = minutes;
        _host.SaveSettings();
        if (_host.Settings.Sync.AutoBackup) _host.RestartAutoBackupTimer();
    }

    private void OnKeepChanged(object sender, RoutedEventArgs e)
    {
        if (_host == null || _suppress) return;
        int before = _host.Settings.Sync.KeepLocalBackups;
        int keep = ParseInt(KeepBox.Text, SyncService.DefaultKeep, 1, 200);
        KeepBox.Text = keep.ToString();
        if (keep == before) return;

        _host.Settings.Sync.KeepLocalBackups = keep;
        _host.SaveSettings();

        int removed = SyncService.PruneLocal(keep, _host.Settings.Sync.CleanupDays);
        RefreshLocalSummary();
        SyncStatus.Text = removed > 0
            ? $"✓ 已只保留最近 {keep} 份，清掉 {removed} 份旧备份"
            : $"✓ 保留份数改为 {keep}，暂时没有需要清理的";
    }

    private void OnDaysChanged(object sender, RoutedEventArgs e)
    {
        if (_host == null || _suppress) return;
        int before = _host.Settings.Sync.CleanupDays;
        int days = ParseInt(DaysBox.Text, 0, 0, 3650);
        DaysBox.Text = days.ToString();
        if (days == before) return;

        _host.Settings.Sync.CleanupDays = days;
        _host.SaveSettings();

        int removed = SyncService.PruneLocal(_host.Settings.Sync.KeepLocalBackups, days);
        RefreshLocalSummary();
        SyncStatus.Text = days == 0
            ? "✓ 已关闭按天数清理，只按份数保留"
            : removed > 0
                ? $"✓ 已清掉 {removed} 份 {days} 天前的备份（最新一份永远保留）"
                : $"✓ 清理天数改为 {days} 天，暂时没有超期的备份";
    }

    /// <summary>解析整数并夹到范围内；解析不出来就用兜底值。</summary>
    private static int ParseInt(string text, int fallback, int min, int max)
    {
        if (!int.TryParse((text ?? "").Trim(), out int v)) return fallback;
        return Math.Clamp(v, min, max);
    }

    // ---------------- 目录 ----------------

    private void OnOpenBackupDir(object sender, RoutedEventArgs e) =>
        SyncService.OpenFolder(SyncService.BackupDir);

    private void OnOpenDataDir(object sender, RoutedEventArgs e) =>
        SyncService.OpenFolder(AppPaths.DataDir);

    // ---------------- 同步操作 ----------------

    private void OnSyncCompleted(SyncResult r)
    {
        Dispatcher.BeginInvoke(() =>
        {
            SyncStatus.Text = $"{DateTime.Now:HH:mm:ss}　{r.Message}";
            Refresh();
        });
    }

    private async void OnTestClick(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        SyncStatus.Text = "正在连接…";
        var r = await _host.Sync.TestAsync();
        SyncStatus.Text = (r.Ok ? UiText.OkMark : UiText.FailMark) + r.Message;
    }

    private async void OnUploadClick(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        if (string.IsNullOrWhiteSpace(_host.Settings.Sync.WebDavUrl))
        {
            SyncStatus.Text = "✗ 请先填 WebDAV 地址";
            return;
        }
        SyncStatus.Text = "正在上传…";
        var r = await _host.RunSyncNowAsync(upload: true);
        SyncStatus.Text = (r.Ok ? UiText.OkMark : UiText.FailMark) + r.Message;
        Refresh();
    }

    private void OnRestoreClick(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;

        /* 恢复不再是"点一下就拿最新那份覆盖"：
         * 先拉清单让用户挑，再让他选覆盖还是合并。 */
        var dlg = new UI.Windows.RestoreWindow(_host) { Owner = Window.GetWindow(this) };
        bool? ok = dlg.ShowDialog();
        Refresh();
        if (ok == true)
        {
            SyncStatus.Text = "✓ " + (dlg.Result?.Summary ?? "已恢复");
            _host.Notify("已从备份恢复配置");
        }
    }

    private void OnLocalBackupClick(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        try
        {
            string payload = _host.Sync.BuildBackupPayload(out _);
            string path = _host.Sync.WriteLocalBackup(payload);
            SyncStatus.Text = $"✓ 已备份到 {System.IO.Path.GetFileName(path)}（已加密）";
            RefreshLocalSummary();
        }
        catch (Exception ex)
        {
            SyncStatus.Text = "✗ " + ex.Message;
        }
    }

    // ---------------- 备份维护 ----------------

    /// <summary>维护操作进行中：避免连点两个按钮同时删。</summary>
    private bool _maintaining;

    /// <summary>
    /// 从云端恢复：RestoreWindow 默认就停在"云端备份"页签，
    /// 列表里每行都有时间 / 名称 / 大小，够用户认出是哪一份。
    /// </summary>
    private void OnRestoreFromCloud(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;

        if (!_host.Sync.Configured)
        {
            AppDialog.Warn(Window.GetWindow(this),
                "还没填 WebDAV 地址，云端没有可恢复的备份。\n\n"
                + "先在上面填好地址与账号，点「测连接」确认能用。",
                "云端未配置");
            return;
        }

        MaintStatus.Text = "正在读取云端备份清单…";
        var win = new CapsLockPlus.UI.Windows.RestoreWindow(_host)
        {
            Owner = Window.GetWindow(this),
        };

        bool restored = win.ShowDialog() == true;
        MaintStatus.Text = restored
            ? "✓ " + (win.Result?.Summary ?? "已从云端恢复")
            : "";
        if (restored) RefreshLocalSummary();
    }

    private async void OnCleanupOld(object sender, RoutedEventArgs e) =>
        await CleanupAsync(
            "清理 7 天前的备份",
            "删除本地和云端**早于 7 天**的备份。\n\n"
            + $"· 本地目录：{SyncService.BackupDir}\n"
            + (_host?.Sync.Configured == true
                ? "· 云端：同样按时间清理\n"
                : "· 云端：未配置，这次只清本地\n")
            + "\n最新一份始终保留；当前正在用的配置不受影响。",
            () => _host!.Sync.CleanupOlderThanAsync(7));

    private async void OnCleanupKeep(object sender, RoutedEventArgs e) =>
        await CleanupAsync(
            "只保留最近 10 份备份",
            "本地和云端都只保留**最新 10 份**，更旧的删掉。\n\n"
            + (_host?.Sync.Configured == true
                ? "· 本地与云端一起处理\n"
                : "· 云端未配置，这次只清本地\n")
            + "\n最新一份始终保留；当前正在用的配置不受影响。",
            () => _host!.Sync.KeepNewestAsync(10));

    private async void OnCleanupRedundant(object sender, RoutedEventArgs e) =>
        await CleanupAsync(
            "清除云端冗余数据",
            "只清理**云端**目录里这些东西：\n\n"
            + "· 上传中断的残留（.tmp / .part / .crdownload / ~$ 开头）\n"
            + "· 0 字节或小于 64 字节的文件\n"
            + "· 内容完全重复的备份（只留最新那份）\n"
            + "· 打不开的：既不是加密备份，也不是明文导出\n\n"
            + "本地备份目录不会被动 —— 那里可能有你自己的文件。",
            () => _host!.Sync.CleanupRedundantAsync());

    /// <summary>三个清理按钮共用：确认 → 执行 → 结果弹窗 + 页面内状态。</summary>
    private async Task CleanupAsync(string title, string confirmText,
        Func<Task<SyncService.CleanupResult>> action)
    {
        if (_host == null || _maintaining) return;

        if (!AppDialog.ConfirmDanger(Window.GetWindow(this), confirmText, title, "开始清理")) return;

        _maintaining = true;
        MaintStatus.Text = "正在清理…";
        try
        {
            var result = await action();
            MaintStatus.Text = "✓ " + result.Summary;
            RefreshLocalSummary();
            AppDialog.Info(Window.GetWindow(this), result.Summary, title + " · 完成", DialogKind.Success);
        }
        catch (Exception ex)
        {
            CoreLog.Warn($"{title}失败：{ex.Message}");
            MaintStatus.Text = "✗ " + ex.Message;
            AppDialog.Error(Window.GetWindow(this), ex.Message, title + " · 失败");
        }
        finally
        {
            _maintaining = false;
        }
    }

    // ---------------- 导出 ----------------

    private void OnGroupToggled(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: string id } box) return;
        var vm = _groups.FirstOrDefault(g => g.Id == id);
        if (vm != null) vm.Checked = box.IsChecked == true;
        RefreshExportLock();
    }

    /// <summary>
    /// 勾了隐私组就强制加密：锁死开关，并把它掰回"加密"。
    ///
    /// 这是硬约束不是建议 —— 隐私组里有网盘密码、备份密码，
    /// 明文文件被转发一次就等于把凭据送出去。
    /// 界面锁一层，<see cref="ExportImport.WritePlain"/> 里再兜一层。
    /// </summary>
    private void RefreshExportLock()
    {
        var priv = ExportImport.PrivateNames(_groups.Where(g => g.Checked).Select(g => g.Id));
        bool mustEncrypt = priv.Count > 0;

        if (mustEncrypt)
        {
            if (ExportEncryptedBox.IsChecked != true)
            {
                ExportEncryptedBox.IsChecked = true;
                OnExportModeChanged(this, new RoutedEventArgs());   // 同步徽标与说明
            }
            ExportEncryptedBox.IsEnabled = false;
            ExportEncryptedBox.ToolTip =
                $"勾选了隐私内容（{string.Join("、", priv)}），导出必须加密 —— "
                + "这些组里有网盘密码、备份密码这类凭据，明文文件被转发就等于把凭据送出去。";
        }
        else
        {
            ExportEncryptedBox.IsEnabled = true;
            ExportEncryptedBox.ToolTip = "加密导出：用备份密码把文件变成密文";
        }
    }

    private void OnSelectNone(object sender, RoutedEventArgs e)
    {
        foreach (var g in _groups) g.Checked = false;
        RefreshExportLock();
        Rebind();
    }

    private void OnSelectSafe(object sender, RoutedEventArgs e)
    {
        foreach (var g in _groups) g.Checked = !g.Private;
        Rebind();
    }

    private void OnSelectAll(object sender, RoutedEventArgs e)
    {
        foreach (var g in _groups) g.Checked = true;
        RefreshExportLock();
        Rebind();
    }

    private void Rebind()
    {
        GroupList.ItemsSource = null;
        GroupList.ItemsSource = _groups;
    }

    /// <summary>导出模式切换：加密 / 明文。</summary>
    private void OnExportModeChanged(object sender, RoutedEventArgs e)
    {
        bool enc = ExportEncryptedBox.IsChecked == true;

        /* 徽标 + 文案一起改：用户要求"明确展示当前的加密状态"，
         * 只靠一个开关的位置不够直观 —— 开关在右边、说明在左边，
         * 扫一眼分不清现在是哪种。 */
        ExportStateText.Text = enc ? "已加密" : "明文";
        ExportStateText.Foreground = (System.Windows.Media.Brush)FindResource(
            enc ? "Brush.Success" : "Brush.Warning");
        ExportStateBadge.Background = (System.Windows.Media.Brush)FindResource(
            enc ? "Brush.SuccessSoft" : "Brush.WarningSoft");

        ExportModeHint.Text = enc
            ? "用备份密码加密，含 GitHub 令牌 / 全部配置。换台机器导入时输入同一个密码即可。"
            : "不加密，任何人拿到都能读 —— 里面的 GitHub 令牌也看得见，别公开分享。"
              + "为免更严重的泄露，仍会剔除网盘密码与备份密码。";
    }

    private void OnExportClick(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;

        var selected = _groups.Where(g => g.Checked).Select(g => g.Id).ToList();
        if (selected.Count == 0)
        {
            AppDialog.Info(Window.GetWindow(this), "一项都没勾，没什么可导出的。", "导出配置");
            return;
        }

        bool encrypted = ExportEncryptedBox.IsChecked == true;
        string password = _host.Settings.Sync.BackupPassword ?? "";

        if (encrypted && string.IsNullOrWhiteSpace(password))
        {
            AppDialog.Info(Window.GetWindow(this),
                "还没有「备份密码」，没法加密导出。\n\n" +
                "可以先去上面的「备份密码」里设一个（本地数据也会一起变成密文），\n" +
                "或者关掉「加密导出」改用明文导出。",
                "需要先设密码");
            BackupPassBox.Focus();
            return;
        }

        /* 勾了隐私组就必须加密 —— 开关正常情况下已被锁死，这里兜底防绕过。 */
        var privNow = ExportImport.PrivateNames(selected);
        if (!encrypted && privNow.Count > 0)
        {
            AppDialog.Warn(Window.GetWindow(this),
                $"勾选了隐私内容（{string.Join("、", privNow)}），只能用加密导出。\n\n"
                + "这些组里有网盘密码、备份密码这类凭据，明文文件被转发就等于把凭据送出去。",
                "必须加密导出");
            return;
        }

        // 明文导出：说清楚文件里到底有什么（现在含令牌了）
        if (!encrypted)
        {
            bool hasToken = Core.Update.UpdateService.HasToken(_host.Settings.Global.GitHubToken);
            string extra = hasToken
                ? "\n\n注意：这份文件里包含你的 GitHub 个人访问令牌，以及全部已勾选的配置。"
                : "";
            if (!AppDialog.Confirm(Window.GetWindow(this),
                    "这次是明文导出，谁拿到文件都能读到内容。" + extra
                    + "\n\n（网盘密码与备份密码仍会剔除）确定要继续吗？",
                    "明文导出", DialogKind.Warn, okText: "继续导出")) return;
        }

        var dlg = new SaveFileDialog
        {
            Title = encrypted ? "导出配置（加密）" : "导出配置（明文）",
            FileName = $"KeyboardEnhance配置({DateTime.Now:yyyy-MM-dd HH-mm-ss}){ExportImport.Extension}",
            Filter = $"KeyboardEnhance 配置|*{ExportImport.Extension}|所有文件|*.*",
            InitialDirectory = SyncService.BackupDir,
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            int n = encrypted
                ? ExportImport.WriteEncrypted(dlg.FileName, _host.Settings, selected, password)
                : ExportImport.WritePlain(dlg.FileName, _host.Settings, selected);

            string what = encrypted
                ? $"已导出（加密，{n} 个设置项，含令牌与全部字段）"
                : $"已导出（明文，{n} 个设置项，含令牌）";
            _host.Notify(what);
            ShowImportResult($"已导出到：{dlg.FileName}\n共 {n} 个设置项，包含 {selected.Count} 个分组。\n"
                             + (encrypted
                                 ? "文件是密文，导入时需要同一个备份密码。"
                                 : "这是明文文件：里面的 GitHub 令牌与配置都能被直接读到，别公开分享。"), true);
        }
        catch (Exception ex)
        {
            _host.Notify(UiText.Failed("导出配置", ex.Message));
            ShowImportResult("导出失败：" + ex.Message, false);
        }
    }

    // ---------------- 导入 ----------------

    private void OnImportClick(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;

        var dlg = new OpenFileDialog
        {
            Title = "导入配置（加密或明文）",
            Filter = $"KeyboardEnhance 配置|*{ExportImport.Extension}|所有文件|*.*",
            InitialDirectory = SyncService.BackupDir,
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            string text = File.ReadAllText(dlg.FileName);
            string password = _host.Settings.Sync.BackupPassword ?? "";

            List<string> groups;
            System.Text.Json.Nodes.JsonObject data;
            try
            {
                (groups, data) = ExportImport.Parse(text, password);
            }
            catch (BackupPasswordException)
            {
                /* 这份导出是别的密码加密的（或者本地还没设密码）。
                 * 不能直接失败 —— 让他输一次密码继续，和恢复备份的体验保持一致。 */
                var prompt = new UI.Windows.PasswordPromptWindow(
                    pass =>
                    {
                        try { ExportImport.Parse(text, pass); return true; }
                        catch { return false; }
                    },
                    "这份导出是加密的",
                    "请输入导出它时用的备份密码。",
                    "用这个密码导入")
                {
                    Owner = Window.GetWindow(this),
                };
                if (prompt.ShowDialog() != true) return;
                (groups, data) = ExportImport.Parse(text, prompt.Password ?? "");
            }

            if (groups.Count == 0)
            {
                ShowImportResult("这份导出里没有认得的分组，可能来自别的版本。", false);
                return;
            }

            var names = groups.Select(g => ExportImport.FindGroup(g)?.Name ?? g).ToList();
            if (!AppDialog.Confirm(Window.GetWindow(this),
                    $"将导入以下内容（会覆盖当前对应的项）：\n\n　{string.Join("\n　", names)}\n\n继续吗？",
                    "导入配置", DialogKind.Question, okText: "导入")) return;

            ExportImport.Apply(_host.Settings, groups, data);
            _host.SaveSettings();
            _host.ReloadAll();

            ShowImportResult($"导入成功：{names.Count} 个分组\n　{string.Join("、", names)}", true);
            _host.Notify($"已导入 {names.Count} 个分组的配置");
        }
        catch (Exception ex)
        {
            ShowImportResult("导入失败：" + ex.Message, false);
            _host.Notify("导入失败：" + ex.Message);
        }
    }

    private void ShowImportResult(string text, bool ok)
    {
        ImportResultText.Text = text;
        ImportResultText.Foreground = ok
            ? (Brush)FindResource("Brush.TextSecondary")
            : PrivateFg;
        ImportResult.Visibility = Visibility.Visible;
    }

    /// <summary>界面上一个可勾选分组。</summary>
    private sealed class GroupVm : System.ComponentModel.INotifyPropertyChanged
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public string Desc { get; init; } = "";
        public bool Private { get; init; }

        private bool _checked;
        public bool Checked
        {
            get => _checked;
            set
            {
                _checked = value;
                PropertyChanged?.Invoke(this, new(nameof(Checked)));
            }
        }

        /// <summary>隐私项才显示红色标记。</summary>
        public Visibility PrivateVisibility => Private ? Visibility.Visible : Visibility.Collapsed;

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }
}