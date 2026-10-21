using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using CapsLockPlus.Core;
using CapsLockPlus.Core.Settings;
using CapsLockPlus.Core.Sync;
using Microsoft.Win32;

namespace CapsLockPlus.UI.Views;

/// <summary>
/// 「同步与备份」页：WebDAV 自动同步 + 明文导入导出。
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

        SyncStatus.Text = s.LastSyncAt == DateTime.MinValue
            ? "还没同步过"
            : $"上次同步：{s.LastSyncAt:yyyy-MM-dd HH:mm}　{s.LastSyncInfo}";
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
    /// 就是明文 —— 留着等于把加密白做了。
    /// </summary>
    private static void RemovePlaintextBackups()
    {
        foreach (var suffix in new[] { ".bak", ".broken", ".tmp" })
        {
            try
            {
                string p = AppPaths.SettingsFile + suffix;
                if (!File.Exists(p)) continue;
                string text = File.ReadAllText(p);
                if (!DataVault.LooksEncrypted(text) && text.TrimStart().StartsWith('{'))
                    File.Delete(p);
            }
            catch { }
        }
    }

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
        SyncStatus.Text = (r.Ok ? "✓ " : "✗ ") + r.Message;
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
        SyncStatus.Text = (r.Ok ? "✓ " : "✗ ") + r.Message;
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

    // ---------------- 导出 ----------------

    private void OnGroupToggled(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: string id } box) return;
        var vm = _groups.FirstOrDefault(g => g.Id == id);
        if (vm != null) vm.Checked = box.IsChecked == true;
    }

    private void OnSelectNone(object sender, RoutedEventArgs e)
    {
        foreach (var g in _groups) g.Checked = false;
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
        Rebind();
    }

    private void Rebind()
    {
        GroupList.ItemsSource = null;
        GroupList.ItemsSource = _groups;
    }

    private void OnExportClick(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;

        var selected = _groups.Where(g => g.Checked).Select(g => g.Id).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show(Window.GetWindow(this), "一项都没勾，没什么可导出的。",
                "导出配置", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 勾了隐私项就再确认一次 —— 这个文件是明文的，可能要被发给别人
        var priv = _groups.Where(g => g.Checked && g.Private).Select(g => g.Name).ToList();
        if (priv.Count > 0)
        {
            var ok = MessageBox.Show(Window.GetWindow(this),
                $"你勾选了隐私内容：\n\n　{string.Join("\n　", priv)}\n\n" +
                "导出的文件是**明文**的，谁拿到都能读到这些内容。\n确定要继续吗？",
                "导出隐私内容", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (ok != MessageBoxResult.OK) return;
        }

        var dlg = new SaveFileDialog
        {
            Title = "导出按键增强配置（明文）",
            FileName = $"按键增强配置({DateTime.Now:yyyy-MM-dd HH-mm-ss}){ExportImport.Extension}",
            Filter = $"按键增强明文配置|*{ExportImport.Extension}|JSON|*.json",
            InitialDirectory = SyncService.BackupDir,
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            int n = ExportImport.WritePlain(dlg.FileName, _host.Settings, selected);
            _host.Notify($"已导出（明文，{n} 个设置项，可直接发给别人导入）");
            ShowImportResult($"已导出到：{dlg.FileName}\n共 {n} 个设置项，包含 {selected.Count} 个分组。\n" +
                             "这是明文文件，可以直接发给别人导入。", true);
        }
        catch (Exception ex)
        {
            _host.Notify("导出失败：" + ex.Message);
            ShowImportResult("导出失败：" + ex.Message, false);
        }
    }

    // ---------------- 导入 ----------------

    private void OnImportClick(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;

        var dlg = new OpenFileDialog
        {
            Title = "导入按键增强配置（明文）",
            Filter = $"按键增强明文配置|*{ExportImport.Extension}|JSON|*.json|所有文件|*.*",
            InitialDirectory = SyncService.BackupDir,
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var (groups, data) = ExportImport.ReadPlain(dlg.FileName);
            if (groups.Count == 0)
            {
                ShowImportResult("这份导出里没有认得的分组，可能来自别的版本。", false);
                return;
            }

            var names = groups.Select(g => ExportImport.FindGroup(g)?.Name ?? g).ToList();
            var confirm = MessageBox.Show(Window.GetWindow(this),
                $"将导入以下内容（会覆盖当前对应的项）：\n\n　{string.Join("\n　", names)}\n\n继续吗？",
                "导入配置", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.OK) return;

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