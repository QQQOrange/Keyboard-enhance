using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CapsLockPlus.Core;
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
        _suppress = false;

        UpdatePasswordWarning();
        BackupDirText.Text = $"备份目录：{SyncService.BackupDir}";
        SyncStatus.Text = s.LastSyncAt == DateTime.MinValue
            ? "还没同步过"
            : $"上次同步：{s.LastSyncAt:yyyy-MM-dd HH:mm}　{s.LastSyncInfo}";
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

    private void OnBackupPassChanged(object sender, RoutedEventArgs e)
    {
        if (_host == null || _suppress) return;
        _host.Settings.Sync.BackupPassword = BackupPassBox.Password;
        _host.SaveSettings();
        UpdatePasswordWarning();
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

    private async void OnDownloadClick(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        if (string.IsNullOrWhiteSpace(_host.Settings.Sync.WebDavUrl))
        {
            SyncStatus.Text = "✗ 请先填 WebDAV 地址";
            return;
        }

        var confirm = MessageBox.Show(Window.GetWindow(this),
            "会用云端备份覆盖当前配置。\n恢复前会自动把当前配置另存一份到本地备份目录，\n万一恢复错了还能退回去。\n\n继续吗？",
            "从云端恢复", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK) return;

        SyncStatus.Text = "正在下载…";
        var r = await _host.RunSyncNowAsync(upload: false);
        SyncStatus.Text = (r.Ok ? "✓ " : "✗ ") + r.Message;
        Refresh();
        _host.Notify(r.Ok ? "已从云端恢复配置" : "恢复失败：" + r.Message);
    }

    private void OnLocalBackupClick(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        try
        {
            string payload = _host.Sync.BuildBackupPayload(out bool encrypted);
            string path = _host.Sync.WriteLocalBackup(payload);
            SyncStatus.Text = $"✓ 已备份到 {path}" + (encrypted ? "（已加密）" : "（未加密）");
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
            Title = "导出 CapsLock+ 配置（明文）",
            FileName = $"capslock+配置({DateTime.Now:yyyy-MM-dd HH-mm-ss}){ExportImport.Extension}",
            Filter = $"CapsLock+ 明文配置|*{ExportImport.Extension}|JSON|*.json",
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
            Title = "导入 CapsLock+ 配置（明文）",
            Filter = $"CapsLock+ 明文配置|*{ExportImport.Extension}|JSON|*.json|所有文件|*.*",
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