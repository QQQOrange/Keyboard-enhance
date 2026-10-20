using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using CapsLockPlus.Core.Settings;

namespace CapsLockPlus.Core.Sync;

/// <summary>一次同步的结果，用于给用户提示。</summary>
public sealed record SyncResult(bool Ok, string Message)
{
    public static SyncResult Fail(string msg) => new(false, msg);
    public static SyncResult Done(string msg) => new(true, msg);
}

/// <summary>
/// WebDAV 同步服务。
///
/// 行为：
///   * **启动时**：比云端与本机哪份新，谁新用谁；云端还没有就直接推一份上去；
///   * **退出时**：把当前配置推上去（受超时限制，网盘不通也不该卡住退出）；
///   * **手动**：上传 / 下载 / 测连接。
///
/// 每次上传的内容都按备份密码加密（没设密码则明文，但仍会明确告知）。
/// </summary>
public sealed class SyncService
{
    /// <summary>同步互斥：同一时刻只允许一个云操作，避免上传下载互相踩。</summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private readonly Func<AppSettings> _settings;

    public SyncService(Func<AppSettings> settings) => _settings = settings;

    /// <summary>本地备份目录。</summary>
    public static string BackupDir
    {
        get
        {
            string dir = Path.Combine(AppPaths.DataDir, "backups");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private WebDavClient Client(int timeoutSeconds = 20)
    {
        var s = _settings().Sync;
        return new WebDavClient(new WebDavOptions
        {
            Url = s.WebDavUrl,
            User = s.WebDavUser,
            Pass = s.WebDavPass,
            TimeoutSeconds = timeoutSeconds,
        });
    }

    /// <summary>云端是否配置可用。</summary>
    public bool Configured
    {
        get
        {
            var s = _settings().Sync;
            return !string.IsNullOrWhiteSpace(s.WebDavUrl);
        }
    }

    // ---------------- 备份内容 ----------------

    /// <summary>
    /// 生成备份内容（加密后的文本）。
    ///
    /// 加密用的口令来自设置里的备份密码；没设密码就是明文，
    /// 这时调用方应当明确告诉用户"这份备份没加密"。
    /// </summary>
    public string BuildBackupPayload(out bool encrypted)
    {
        var s = _settings();
        // 备份是"整份配置"，所以直接序列化整个 AppSettings
        string json = JsonSerializer.Serialize(s, SettingsStore.JsonOptions);
        string password = s.Sync.BackupPassword;
        if (string.IsNullOrEmpty(password))
        {
            encrypted = false;
            return json;
        }
        encrypted = true;
        return CryptoBox.Encrypt(json, password);
    }

    /// <summary>把备份文本还原进设置。返回是否成功。</summary>
    public bool RestoreFromText(string text)
    {
        var s = _settings();
        string json = CryptoBox.Decrypt(text, s.Sync.BackupPassword);
        var parsed = JsonSerializer.Deserialize<AppSettings>(json, SettingsStore.JsonOptions);
        if (parsed == null) throw new InvalidOperationException("备份内容解析失败");

        SettingsStore.Normalize(parsed);
        SettingsStore.CopyInto(s, parsed);
        CoreLog.Info("已从备份恢复配置");
        return true;
    }

    // ---------------- 本地备份 ----------------

    /// <summary>写一份本地备份，返回文件路径。</summary>
    public string WriteLocalBackup(string payload)
    {
        string name = BackupName(DateTime.Now);
        string path = Path.Combine(BackupDir, name);
        File.WriteAllText(path, payload, System.Text.Encoding.UTF8);
        CoreLog.Info($"本地备份已写：{path}");
        PruneLocalBackups(keep: 20);
        return path;
    }

    /// <summary>只保留最近 N 份本地备份，避免越积越多。</summary>
    private static void PruneLocalBackups(int keep)
    {
        try
        {
            var files = new DirectoryInfo(BackupDir)
                .GetFiles("*.clpbak")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Skip(keep)
                .ToList();
            foreach (var f in files) f.Delete();
        }
        catch (Exception e)
        {
            CoreLog.Warn("清理旧备份失败：" + e.Message);
        }
    }

    /// <summary>备份文件名：capslock+(2026-10-20 13-45-00).clpbak</summary>
    public static string BackupName(DateTime when) =>
        $"capslock+({when:yyyy-MM-dd HH-mm-ss}).clpbak";

    // ---------------- 手动操作 ----------------

    /// <summary>上传一份备份到云端。</summary>
    public async Task<SyncResult> UploadAsync(CancellationToken ct = default)
    {
        if (!Configured) return SyncResult.Fail("还没填 WebDAV 地址");
        if (!await Gate.WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false))
            return SyncResult.Fail("已有同步在进行，稍后再试");

        try
        {
            var s = _settings();
            string payload = BuildBackupPayload(out bool encrypted);

            // ① 先本地
            string local = WriteLocalBackup(payload);

            // ② 再把同一份字节推上去
            var client = Client();
            string stampName = BackupName(DateTime.Now);
            await client.UploadAsync(stampName, payload, ct).ConfigureAwait(false);
            // 稳定文件名永远指向最新一份，下载时不必列目录
            await client.UploadAsync(WebDavClient.LatestFile, payload, ct).ConfigureAwait(false);

            s.Sync.LastSyncAt = DateTime.Now;
            s.Sync.LastSyncInfo = $"已上传 {stampName}";

            string enc = encrypted ? "已加密" : "**未加密**";
            long kb = payload.Length / 1024;
            CoreLog.Info($"WebDAV 上传完成：{stampName}（{enc}，{kb}KB）");
            _ = local;
            return SyncResult.Done($"已上传 {stampName}（{enc}，{kb}KB）");
        }
        catch (Exception e)
        {
            CoreLog.Warn("WebDAV 上传失败：" + e.Message);
            return SyncResult.Fail(e.Message);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>从云端下载最新备份并恢复。</summary>
    public async Task<SyncResult> DownloadAsync(CancellationToken ct = default)
    {
        if (!Configured) return SyncResult.Fail("还没填 WebDAV 地址");
        if (!await Gate.WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false))
            return SyncResult.Fail("已有同步在进行，稍后再试");

        try
        {
            var client = Client();

            // 先试稳定文件名；没有就列目录找最新的一份
            string? text = await client.DownloadAsync(WebDavClient.LatestFile, ct)
                .ConfigureAwait(false);
            string usedName = WebDavClient.LatestFile;

            if (text == null)
            {
                var files = await client.ListAsync(
                    new HttpClient(), ct).ConfigureAwait(false);
                var newest = files.FirstOrDefault();
                if (newest == null) return SyncResult.Fail("云端还没有任何备份");
                text = await client.DownloadAsync(newest.Name, ct).ConfigureAwait(false);
                usedName = newest.Name;
            }

            if (text == null) return SyncResult.Fail("云端备份读不出来");

            // 恢复前先备份当前配置，万一恢复错了还能退回去
            WriteLocalBackup(BuildBackupPayload(out _));
            RestoreFromText(text);

            var s = _settings();
            s.Sync.LastSyncAt = DateTime.Now;
            s.Sync.LastSyncInfo = $"已恢复 {usedName}";

            CoreLog.Info($"WebDAV 恢复完成：{usedName}");
            return SyncResult.Done($"已从 {usedName} 恢复配置（恢复前的配置已另存一份）");
        }
        catch (Exception e)
        {
            CoreLog.Warn("WebDAV 恢复失败：" + e.Message);
            return SyncResult.Fail(e.Message);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>测试连接。</summary>
    public async Task<SyncResult> TestAsync(CancellationToken ct = default)
    {
        if (!Configured) return SyncResult.Fail("还没填 WebDAV 地址");
        try
        {
            var client = Client(15);
            string msg = await client.ProbeAsync(ct).ConfigureAwait(false);
            return SyncResult.Done(msg);
        }
        catch (Exception e)
        {
            return SyncResult.Fail(e.Message);
        }
    }

    // ---------------- 自动同步 ----------------

    /// <summary>
    /// 启动时同步：比云端与本机哪份新，谁新用谁。
    ///
    /// 云端压根没有备份 → 直接推一份上去。
    /// </summary>
    public async Task<SyncResult> SyncOnStartAsync(CancellationToken ct = default)
    {
        var s = _settings();
        if (!s.Sync.SyncOnStart) return SyncResult.Done("");
        if (!Configured) return SyncResult.Fail("已开启启动同步，但没填 WebDAV 地址");

        try
        {
            var client = Client();
            var remoteAt = await client.GetModifiedAsync(WebDavClient.LatestFile, ct)
                .ConfigureAwait(false);

            if (remoteAt == null)
            {
                var up = await UploadAsync(ct).ConfigureAwait(false);
                return up.Ok
                    ? SyncResult.Done("云端还没有备份，已把本机配置推上去")
                    : up;
            }

            DateTime localAt = SettingsStore.LastWriteTimeUtc.ToLocalTime();
            var remoteLocal = remoteAt.Value.ToLocalTime();

            if (remoteLocal > localAt.AddSeconds(2))
            {
                // 云端更新 → 拉下来
                var down = await DownloadAsync(ct).ConfigureAwait(false);
                return down.Ok
                    ? SyncResult.Done($"云端备份更新（{remoteLocal:MM-dd HH:mm}），已恢复")
                    : down;
            }

            // 本机更新或一样新 → 推上去
            var res = await UploadAsync(ct).ConfigureAwait(false);
            return res.Ok
                ? SyncResult.Done($"本机配置更新，已上传（云端那份是 {remoteLocal:MM-dd HH:mm}）")
                : res;
        }
        catch (Exception e)
        {
            CoreLog.Warn("启动同步失败：" + e.Message);
            return SyncResult.Fail(e.Message);
        }
    }

    /// <summary>
    /// 退出时同步：把当前配置推上去。
    ///
    /// 超时特意调短（8 秒）—— 网盘不通时不该让用户关不掉软件。
    /// </summary>
    public SyncResult SyncOnExit()
    {
        var s = _settings();
        if (!s.Sync.SyncOnExit) return SyncResult.Done("");
        if (!Configured) return SyncResult.Done("");

        try
        {
            var task = UploadAsync();
            // 同步等，但有硬上限：关软件不能卡住
            if (!task.Wait(TimeSpan.FromSeconds(8)))
            {
                CoreLog.Warn("退出同步超时（8 秒），放弃这次上传");
                return SyncResult.Fail("退出同步超时，本次未上传");
            }
            return task.Result;
        }
        catch (Exception e)
        {
            CoreLog.Warn("退出同步失败：" + e.Message);
            return SyncResult.Fail(e.Message);
        }
    }
}