using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using CapsLockPlus.Core;
using CapsLockPlus.Core.Sync;

namespace CapsLockPlus.UI.Windows;

/// <summary>
/// 「从备份恢复」对话框。
///
/// 流程刻意做成"先看清单、再选、再决定怎么恢复"：
///   * 以前的「从云端恢复」点一下就直接拿最新那份覆盖，用户既不知道
///     云端有几份、也不知道会覆盖掉什么；
///   * 现在先 PROPFIND 拉清单（云端 / 本地两个来源），
///     选中某一份后才执行，且可以在"合并"和"覆盖"之间选。
///
/// 恢复失败（多半是密码不对）时会请用户输入**这份备份当时用的密码**：
/// 老备份可能是旧密码加密的，用当前的备份密码解不开很正常。
/// </summary>
public partial class RestoreWindow : Window
{
    private readonly AppHost? _host;
    private readonly ObservableCollection<BackupRow> _rows = new();
    private bool _cloud = true;
    private bool _loading;

    public RestoreWindow() : this(null) { }

    public RestoreWindow(AppHost? host)
    {
        InitializeComponent();
        _host = host;
        BackupList.ItemsSource = _rows;
        Loaded += async (_, _) => await LoadAsync().ConfigureAwait(true);
    }

    /// <summary>恢复成功后为 true，调用方据此刷新界面。</summary>
    public RestoreOutcome? Result { get; private set; }

    // ---------------- 来源切换 ----------------

    private async void OnCloudTab(object sender, RoutedEventArgs e)
    {
        if (_cloud) return;
        _cloud = true;
        StyleTabs();
        await LoadAsync().ConfigureAwait(true);
    }

    private async void OnLocalTab(object sender, RoutedEventArgs e)
    {
        if (!_cloud) return;
        _cloud = false;
        StyleTabs();
        await LoadAsync().ConfigureAwait(true);
    }

    private void StyleTabs()
    {
        CloudTab.Style = (Style)FindResource(_cloud ? "Button.Primary" : "Button.Base");
        LocalTab.Style = (Style)FindResource(_cloud ? "Button.Base" : "Button.Primary");
        DeleteButton.Visibility = _cloud ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnRefresh(object sender, RoutedEventArgs e) =>
        await LoadAsync().ConfigureAwait(true);

    // ---------------- 加载清单 ----------------

    private async Task LoadAsync()
    {
        if (_loading) return;
        if (_host == null)
        {
            StatusText.Text = "没有可用的配置宿主，无法读取备份。";
            return;
        }

        _loading = true;
        _rows.Clear();
        try
        {
            bool cloudReady = _cloud &&
                              !string.IsNullOrWhiteSpace(_host.Settings.Sync.WebDavUrl);

            /* 没配网盘时别停在"还没填地址"这种死路上 ——
             * 直接切到本地备份，用户想恢复还是能恢复。 */
            if (_cloud && !cloudReady)
            {
                _cloud = false;
                StyleTabs();
            }

            if (cloudReady)
            {
                await LoadCloudRowsAsync().ConfigureAwait(true);
            }
            else
            {
                LoadLocalRows();
            }

            if (_rows.Count > 0) BackupList.SelectedIndex = 0;
        }
        finally
        {
            _loading = false;
        }
    }

    private async Task LoadCloudRowsAsync()
    {
        if (_host == null) return;

        StatusText.Text = "正在读取云端备份…";
        List<RemoteFile> files;
        try
        {
            files = await _host.Sync.ListBackupsAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusText.Text = "读取云端失败：" + ex.Message;
            return;
        }

        foreach (var f in files)
        {
            _rows.Add(new BackupRow
            {
                Time = f.Modified == DateTimeOffset.MinValue
                    ? "—"
                    : f.Modified.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                Name = f.Name,
                Size = f.Size > 0 ? $"{f.Size / 1024.0:0.#} KB" : "—",
                State = "—",
                Cloud = true,
            });
        }

        StatusText.Text = _rows.Count == 0
            ? "云端还没有备份。"
            : $"云端共 {_rows.Count} 份备份（保留策略：最近 {_host!.Settings.Sync.KeepLocalBackups} 份"
              + $" / {_host!.Settings.Sync.CleanupDays} 天）。";
    }

    private void LoadLocalRows()
    {
        var files = SyncService.ListLocal();
        foreach (var f in files)
        {
            _rows.Add(new BackupRow
            {
                Time = f.Time.ToString("yyyy-MM-dd HH:mm"),
                Name = f.Name,
                Size = $"{f.Size / 1024.0:0.#} KB",
                State = f.Encrypted ? "已加密" : "明文",
                Cloud = false,
                Path = f.Path,
            });
        }

        StatusText.Text = _rows.Count == 0
            ? $"本地还没有备份。备份目录：{SyncService.BackupDir}"
            : $"本地共 {_rows.Count} 份备份，目录：{SyncService.BackupDir}";
    }

    // ---------------- 删除 ----------------

    private async void OnDelete(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        if (BackupList.SelectedItem is not BackupRow row) return;

        if (row.Cloud)
        {
            if (!AppDialog.ConfirmDanger(this,
                    $"从云端删除这份备份？\n\n　{row.Name}\n\n删了就找不回来了。",
                    "删除云端备份", okText: "删除")) return;

            StatusText.Text = "正在删除…";
            var r = await _host.Sync.DeleteCloudAsync(row.Name).ConfigureAwait(true);
            StatusText.Text = (r.Ok ? "✓ " : "✗ ") + r.Message;
            if (r.Ok) await LoadAsync().ConfigureAwait(true);
            return;
        }

        try
        {
            if (!AppDialog.ConfirmDanger(this,
                    $"删除这份本地备份？\n\n　{row.Name}\n　{row.Path}\n\n删了就找不回来了。",
                    "删除本地备份", okText: "删除")) return;
            if (!string.IsNullOrEmpty(row.Path)) File.Delete(row.Path);
            StatusText.Text = "✓ 已删除 " + row.Name;
            await LoadAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusText.Text = "✗ 删除失败：" + ex.Message;
        }
    }

    // ---------------- 恢复 ----------------

    private async void OnRestore(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        if (BackupList.SelectedItem is not BackupRow row)
        {
            StatusText.Text = "先在上面选一份备份。";
            return;
        }

        var mode = OverwriteRadio.IsChecked == true ? RestoreMode.Overwrite : RestoreMode.Merge;

        if (mode == RestoreMode.Overwrite)
        {
            if (!AppDialog.Confirm(this,
                    "覆盖恢复会把当前配置整份换成备份里的内容。\n" +
                    "恢复之前会自动把当前配置另存一份到本地备份目录，恢复错了还能退回去。\n\n" +
                    $"确定用「{row.Name}」覆盖吗？",
                    "覆盖恢复", DialogKind.Warn, okText: "覆盖恢复")) return;
        }

        RestoreButton.IsEnabled = false;
        try
        {
            StatusText.Text = row.Cloud ? "正在下载备份…" : "正在读取备份…";
            string? text = row.Cloud
                ? await _host.Sync.DownloadByNameAsync(row.Name).ConfigureAwait(true)
                : (string.IsNullOrEmpty(row.Path) ? null : File.ReadAllText(row.Path, System.Text.Encoding.UTF8));

            if (string.IsNullOrEmpty(text))
            {
                StatusText.Text = "✗ 这份备份读不出来。";
                return;
            }

            // 恢复前先自保（没设备份密码时这一步会跳过，下面会提示）
            string? selfBackup = _host.Sync.TryWriteLocalBackup();

            string password = _host.Settings.Sync.BackupPassword ?? "";
            if (!SyncService.CanDecrypt(text, password))
            {
                /* 当前密码解不开 —— 说明这份是旧密码加密的。
                 *
                 * 这里不能直接失败：用户改过密码之后，老备份全都会被判"密码错误"，
                 * 而它们恰恰是最该能恢复的东西。所以请他输入"这份备份当时的密码"。 */
                var prompt = new PasswordPromptWindow(
                    pass => SyncService.CanDecrypt(text, pass),
                    "这份备份用的是旧密码",
                    "当前备份密码解不开它 —— 请填生成这份备份时用的密码。",
                    "用这个密码恢复")
                {
                    Owner = this,
                };
                if (prompt.ShowDialog() != true)
                {
                    StatusText.Text = "已取消恢复。";
                    return;
                }
                password = prompt.Password ?? "";
            }

            StatusText.Text = mode == RestoreMode.Merge ? "正在合并…" : "正在恢复…";
            var outcome = _host.Sync.RestoreFromText(text, mode, password);

            // 立刻落盘：恢复完不保存等于白恢复
            _host.SaveSettings();
            _host.ReloadAll();

            Result = outcome;
            StatusText.Text = "✓ " + outcome.Summary;

            var msg = outcome.Summary
                      + (selfBackup != null ? $"\n\n恢复前的配置已另存：{System.IO.Path.GetFileName(selfBackup)}" : "");
            AppDialog.Success(this, msg, "恢复完成");
            DialogResult = true;
            Close();
        }
        catch (BackupPasswordException ex)
        {
            StatusText.Text = "✗ 解不开这份备份：" + ex.Message;
        }
        catch (Exception ex)
        {
            CoreLog.Warn("恢复备份失败：" + ex);
            StatusText.Text = "✗ 恢复失败：" + ex.Message;
        }
        finally
        {
            RestoreButton.IsEnabled = true;
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

}

/// <summary>
/// 清单里的一行。
///
/// 必须 public：WPF 的绑定引擎反射不到非公开嵌套类型上的属性，
/// 列表会一行行显示成类型全名
/// （`CapsLockPlus.UI.Windows.RestoreWindow+Row`）。
/// </summary>
public sealed class BackupRow
{
    public string Time { get; init; } = "";
    public string Name { get; init; } = "";
    public string Size { get; init; } = "";
    public string State { get; init; } = "";
    public bool Cloud { get; init; }
    public string? Path { get; init; }
}
