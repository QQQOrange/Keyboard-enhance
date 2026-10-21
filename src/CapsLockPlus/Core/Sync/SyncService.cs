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

/// <summary>恢复方式。</summary>
public enum RestoreMode
{
    /// <summary>整份替换：云端有什么本地就是什么。</summary>
    Overwrite,

    /// <summary>合并：按主键对齐，云端同项覆盖，本地独有保留。</summary>
    Merge,
}

/// <summary>恢复结果。</summary>
public sealed record RestoreOutcome(RestoreMode Mode, int Added, int Replaced, string Summary);

/// <summary>本地备份文件。</summary>
public sealed record LocalBackup(string Path, string Name, DateTime Time, long Size, bool Encrypted)
{
    /// <summary>界面上的一句话：`10-21 22:30 · 116 KB · 已加密`</summary>
    public string Caption =>
        $"{Time:MM-dd HH:mm} · {Size / 1024.0:0.#} KB · {(Encrypted ? "已加密" : "明文")}";
}

/// <summary>
/// 备份与 WebDAV 同步服务。
///
/// 备份的几条硬规矩：
///   * **一律加密**：备份里装着翻译密钥、网盘密码、宏、搜索记录，
///     没有"先明文传上去以后再说"这种选项。没设备份密码就拒绝备份；
///   * **落盘位置跟着软件走**：exe 同级新建 `KeyboardEnhance 备份/`，
///     装到 Program Files 这类只读目录时自动回落到数据目录；
///   * **不会无限堆**：默认只留最近 10 份，并且清掉 7 天前的；
///   * **恢复前先自保**：覆盖恢复前先把当前配置另存一份，恢复错了能退回。
/// </summary>
public sealed class SyncService
{
    /// <summary>备份文件后缀。用户要求：以 keyboardenhance 为后缀。</summary>
    public const string Extension = ".keyboardenhance";

    /// <summary>老版本的后缀，仍然能读能恢复。</summary>
    public const string LegacyExtension = ".clpbak";

    /// <summary>exe 同级新建的备份文件夹名。</summary>
    public const string BackupFolderName = "KeyboardEnhance 备份";

    /// <summary>默认保留份数。</summary>
    public const int DefaultKeep = 10;

    /// <summary>默认清理多少天之前的备份（0 = 不按天数清）。</summary>
    public const int DefaultCleanDays = 7;

    /// <summary>同步互斥：同一时刻只允许一个云操作，避免上传下载互相踩。</summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private readonly Func<AppSettings> _settings;

    public SyncService(Func<AppSettings> settings) => _settings = settings;

    // ---------------- 本地目录 ----------------

    private static string? _backupDirCache;

    /// <summary>
    /// 本地备份目录：**软件所在目录**下的 `KeyboardEnhance 备份`。
    ///
    /// 为什么不放 %LocalAppData%：用户要的是"备份就在软件旁边"，
    /// 拷走整个文件夹就带走了软件和全部备份，不用满硬盘找。
    /// 但 Program Files 之类位置不可写，所以先探测一次，失败就回落。
    /// </summary>
    public static string BackupDir => _backupDirCache ??= ResolveBackupDir();

    /// <summary>当前用的是不是回落目录（界面提示用）。</summary>
    public static bool BackupDirIsFallback { get; private set; }

    /// <summary>软件所在目录（界面上的「打开软件目录」用）。</summary>
    public static string AppDir => AppPaths.AppDir;

    /// <summary>用资源管理器打开一个目录（不存在就先建）。</summary>
    public static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{path}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception e)
        {
            CoreLog.Warn($"打开目录失败（{path}）：{e.Message}");
        }
    }

    private static string ResolveBackupDir()
    {
        string primary = Path.Combine(AppPaths.AppDir, BackupFolderName);
        try
        {
            Directory.CreateDirectory(primary);
            // 真写一个文件再删掉：只测 CreateDirectory 骗不过 UAC 虚拟化
            string probe = Path.Combine(primary, ".writable");
            File.WriteAllText(probe, "1");
            File.Delete(probe);
            BackupDirIsFallback = false;
            return primary;
        }
        catch (Exception e)
        {
            CoreLog.Warn($"软件目录不可写，备份改放数据目录：{e.Message}");
            BackupDirIsFallback = true;
            string fallback = Path.Combine(AppPaths.DataDir, "backups");
            Directory.CreateDirectory(fallback);
            return fallback;
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
    /// 生成备份内容 —— **一定是密文**。
    ///
    /// 没设备份密码时直接抛异常，由调用方提示用户去设。
    /// 这里不做"没密码就明文"的妥协：备份会被传到网盘、
    /// 会被拷到 U 盘、会被丢进聊天窗口，明文等于把凭据送人。
    /// </summary>
    public string BuildBackupPayload(out bool encrypted)
    {
        var s = _settings();
        string password = s.Sync.BackupPassword ?? "";
        if (string.IsNullOrWhiteSpace(password))
            throw new InvalidOperationException(
                "还没设备份密码。备份一律加密，请先在「备份密码」里设一个再备份。");

        encrypted = true;
        string json = JsonSerializer.Serialize(s, SettingsStore.JsonOptions);
        return CryptoBox.Encrypt(json, password);
    }

    /// <summary>不抛异常版本，供自动备份这类"失败就算了"的场景用。</summary>
    public bool TryBuildBackupPayload(out string payload, out string error)
    {
        try
        {
            payload = BuildBackupPayload(out _);
            error = "";
            return true;
        }
        catch (Exception e)
        {
            payload = "";
            error = e.Message;
            return false;
        }
    }

    /// <summary>
    /// 把备份文本还原进设置。
    /// </summary>
    /// <param name="text">备份文件内容。</param>
    /// <param name="mode">覆盖 or 合并。</param>
    /// <param name="password">解密口令；null = 用当前设置里的备份密码。</param>
    public RestoreOutcome RestoreFromText(string text, RestoreMode mode = RestoreMode.Overwrite,
        string? password = null)
    {
        var s = _settings();
        string pass = password ?? s.Sync.BackupPassword ?? "";

        string json;
        try
        {
            json = CryptoBox.Decrypt(text, pass);
        }
        catch (Exception e)
        {
            // 统一成一种异常，界面据此弹出"输入当时的密码"
            throw new BackupPasswordException(e.Message, e);
        }

        var parsed = JsonSerializer.Deserialize<AppSettings>(json, SettingsStore.JsonOptions)
            ?? throw new InvalidOperationException("备份内容解析失败");

        SettingsStore.Normalize(parsed);

        if (mode == RestoreMode.Merge)
        {
            var (merged, added, replaced) = BackupMerge.Merge(s, parsed);
            SettingsStore.CopyInto(s, merged);
            CoreLog.Info($"已合并备份：新增 {added} 项，覆盖 {replaced} 项");
            return new RestoreOutcome(mode, added, replaced,
                $"已合并：新增 {added} 项，覆盖 {replaced} 项（本地独有的内容保留）");
        }

        /* 覆盖恢复也要**保留本机的同步与加密设置**。
         *
         * 理由不是"省事"，是安全：
         *   * 备份可能来自别人的机器 / 另一台电脑，里面的 WebDavUrl + 密码
         *     指向的是**别人的网盘**。整份灌进来，下次同步就把本机配置
         *     （含翻译密钥、宏、搜索记录）传到那个账号上去了；
         *   * 备份密码更不能跟着走 —— 本地 vault 的密钥是按本机密码派生的，
         *     换成备份里的密码，磁盘上的密文立刻解不开，等于把自己锁在外面。
         *
         * 所以恢复只恢复"内容"（按键、层、宏、搜索项、窗口绑定、外观），
         * 不碰"这台机器的身份"。 */
        var localSync = s.Sync;
        SettingsStore.CopyInto(s, parsed);
        s.Sync = localSync;

        CoreLog.Info("已从备份恢复配置（覆盖，同步与加密设置保留本机）");
        return new RestoreOutcome(mode, 0, 0,
            "已整份恢复（覆盖，网盘与备份密码保留本机）");
    }

    /// <summary>这份备份能不能用这个口令解开（只试解密，不动任何设置）。</summary>
    public static bool CanDecrypt(string text, string password)
    {
        try
        {
            CryptoBox.Decrypt(text, password);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ---------------- 本地备份 ----------------

    /// <summary>写一份本地备份，返回文件路径。</summary>
    public string WriteLocalBackup(string payload, bool prune = true)
    {
        string path = Path.Combine(BackupDir, BackupName(DateTime.Now));
        File.WriteAllText(path, payload, System.Text.Encoding.UTF8);
        CoreLog.Info($"本地备份已写：{path}");
        if (prune) PruneLocal();
        return path;
    }

    /// <summary>不抛异常地写一份本地备份（自动备份 / 恢复前自保用）。</summary>
    public string? TryWriteLocalBackup()
    {
        try
        {
            if (!TryBuildBackupPayload(out string payload, out _)) return null;
            return WriteLocalBackup(payload);
        }
        catch (Exception e)
        {
            CoreLog.Warn("本地备份失败：" + e.Message);
            return null;
        }
    }

    /// <summary>列出本地备份（新的在前）。新旧两种后缀都收。</summary>
    public static List<LocalBackup> ListLocal(string? dir = null)
    {
        var list = new List<LocalBackup>();
        try
        {
            var info = new DirectoryInfo(dir ?? BackupDir);
            if (!info.Exists) return list;
            foreach (var f in info.GetFiles())
            {
                if (!IsBackupName(f.Name)) continue;
                bool enc = true;
                try { enc = CryptoBox.LooksEncrypted(File.ReadAllText(f.FullName)); }
                catch { }
                list.Add(new LocalBackup(f.FullName, f.Name, f.LastWriteTime, f.Length, enc));
            }
        }
        catch (Exception e)
        {
            CoreLog.Warn("列本地备份失败：" + e.Message);
        }
        return list.OrderByDescending(x => x.Time).ToList();
    }

    /// <summary>这个文件名是不是本软件的备份。</summary>
    public static bool IsBackupName(string name) =>
        (name.StartsWith("KeyboardEnhance(", StringComparison.OrdinalIgnoreCase)
         && name.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
        || (name.StartsWith("capslock+(", StringComparison.OrdinalIgnoreCase)
            && name.EndsWith(LegacyExtension, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 按保留策略清理本地备份。
    ///
    /// 两条规则同时生效，但**永远至少留最新一份** ——
    /// 用户把"保留 10 份"和"清理 7 天前"都设上、又半个月没备份时，
    /// 按天数会连唯一那份一起删掉，那就成了"备份功能帮我清空了备份"。
    /// </summary>
    public static int PruneLocal(int? keep = null, int? days = null) =>
        PruneFolder(BackupDir, keep, days);

    /// <summary>清理指定目录（自检走同一条代码路径）。</summary>
    public static int PruneFolder(string dir, int? keep = null, int? days = null)
    {
        try
        {
            var s = ListLocal(dir);
            return PruneList(s, keep, days, x => (x.Time, () => File.Delete(x.Path), x.Name));
        }
        catch (Exception e)
        {
            CoreLog.Warn("清理旧备份失败：" + e.Message);
            return 0;
        }
    }

    /// <summary>清理的公共逻辑（本地与云端共用同一套规则）。</summary>
    private static int PruneList<T>(List<T> items, int? keep, int? days,
        Func<T, (DateTime Time, Action Delete, string Name)> info)
    {
        int keepN = Math.Max(1, keep ?? DefaultKeep);
        int dayN = days ?? DefaultCleanDays;
        var ordered = items
            .Select(x => (Item: x, Info: info(x)))
            .OrderByDescending(x => x.Info.Time)
            .ToList();

        int removed = 0;
        for (int i = 0; i < ordered.Count; i++)
        {
            bool tooMany = i >= keepN;
            bool tooOld = dayN > 0 && i > 0 &&
                          (DateTime.Now - ordered[i].Info.Time).TotalDays > dayN;
            if (!tooMany && !tooOld) continue;
            try
            {
                ordered[i].Info.Delete();
                removed++;
                CoreLog.Info($"清理旧备份：{ordered[i].Info.Name}");
            }
            catch (Exception e)
            {
                CoreLog.Warn($"删不掉 {ordered[i].Info.Name}：{e.Message}");
            }
        }
        return removed;
    }

    /// <summary>备份文件名：KeyboardEnhance(2026-10-21 22-30-00).keyboardenhance</summary>
    public static string BackupName(DateTime when) =>
        $"KeyboardEnhance({when:yyyy-MM-dd HH-mm-ss}){Extension}";

    // ---------------- 手动操作 ----------------

    /// <summary>上传一份备份到云端（同时写一份本地）。</summary>
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
            WriteLocalBackup(payload);

            // ② 再把同一份字节推上去
            var client = Client();
            string stampName = BackupName(DateTime.Now);
            await client.UploadAsync(stampName, payload, ct).ConfigureAwait(false);
            // 稳定文件名永远指向最新一份，启动同步时不必列目录
            await client.UploadAsync(WebDavClient.LatestFile, payload, ct).ConfigureAwait(false);

            s.Sync.LastSyncAt = DateTime.Now;
            s.Sync.LastSyncInfo = $"已上传 {stampName}";

            long kb = payload.Length / 1024;
            CoreLog.Info($"WebDAV 上传完成：{stampName}（已加密，{kb}KB）");

            // ③ 云端也按同一套策略清理，不然网盘会越堆越满
            int pruned = await PruneCloudAsync(client, ct).ConfigureAwait(false);

            return SyncResult.Done($"已上传 {stampName}（已加密，{kb}KB）"
                                   + (pruned > 0 ? $"，顺手清掉 {pruned} 份旧备份" : ""));
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

    /// <summary>从云端下载**最新**那份并覆盖恢复（保留旧入口，界面走恢复对话框）。</summary>
    public async Task<SyncResult> DownloadAsync(CancellationToken ct = default)
    {
        if (!Configured) return SyncResult.Fail("还没填 WebDAV 地址");
        if (!await Gate.WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false))
            return SyncResult.Fail("已有同步在进行，稍后再试");

        try
        {
            var client = Client();
            string? text = await client.DownloadAsync(WebDavClient.LatestFile, ct)
                .ConfigureAwait(false);
            string usedName = WebDavClient.LatestFile;

            if (text == null)
            {
                // 兼容老版本：稳定文件名换了，回落列目录找最新
                var files = await ListBackupsAsync(ct).ConfigureAwait(false);
                var newest = files.FirstOrDefault();
                if (newest == null) return SyncResult.Fail("云端还没有任何备份");
                text = await client.DownloadAsync(newest.Name, ct).ConfigureAwait(false);
                usedName = newest.Name;
            }

            if (text == null) return SyncResult.Fail("云端备份读不出来");

            // 恢复前先备份当前配置，万一恢复错了还能退回去
            TryWriteLocalBackup();
            var outcome = RestoreFromText(text);

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

    // ---------------- 备份列表 ----------------

    /// <summary>
    /// 列出云端的**历史备份**。
    ///
    /// 过滤掉 `keyboardenhance-latest.keyboardenhance` 这个稳定别名 ——
    /// 它和最新那份时间戳备份内容完全一样，列出来会让用户以为有两份。
    /// </summary>
    public async Task<List<RemoteFile>> ListBackupsAsync(CancellationToken ct = default)
    {
        var client = Client();
        var all = await client.ListAsync(ct).ConfigureAwait(false);
        return all
            .Where(f => IsBackupName(f.Name))
            .OrderByDescending(f => f.Modified)
            .ToList();
    }

    /// <summary>下载指定名字的云端备份。</summary>
    public Task<string?> DownloadByNameAsync(string name, CancellationToken ct = default) =>
        Client().DownloadAsync(name, ct);

    /// <summary>删掉一份云端备份。</summary>
    public async Task<SyncResult> DeleteCloudAsync(string name, CancellationToken ct = default)
    {
        if (!Configured) return SyncResult.Fail("还没填 WebDAV 地址");
        try
        {
            await Client().DeleteAsync(name, ct).ConfigureAwait(false);
            return SyncResult.Done($"已删除云端备份 {name}");
        }
        catch (Exception e)
        {
            return SyncResult.Fail(e.Message);
        }
    }

    /// <summary>按保留策略清理云端旧备份；返回删掉几份。</summary>
    private async Task<int> PruneCloudAsync(WebDavClient client, CancellationToken ct)
    {
        try
        {
            var s = _settings().Sync;
            var files = (await client.ListAsync(ct).ConfigureAwait(false))
                .Where(f => IsBackupName(f.Name))
                .ToList();
            if (files.Count == 0) return 0;

            int keep = Math.Max(1, s.KeepLocalBackups);
            int days = s.CleanupDays;
            int removed = 0;

            var ordered = files.OrderByDescending(f => f.Modified).ToList();
            for (int i = 0; i < ordered.Count; i++)
            {
                bool tooMany = i >= keep;
                bool tooOld = days > 0 && i > 0 &&
                              (DateTimeOffset.Now - ordered[i].Modified).TotalDays > days;
                if (!tooMany && !tooOld) continue;
                try
                {
                    await client.DeleteAsync(ordered[i].Name, ct).ConfigureAwait(false);
                    removed++;
                }
                catch (Exception e)
                {
                    CoreLog.Warn($"云端删不掉 {ordered[i].Name}：{e.Message}");
                }
            }
            return removed;
        }
        catch (Exception e)
        {
            // 清理失败不影响同步本身
            CoreLog.Warn("清理云端旧备份失败：" + e.Message);
            return 0;
        }
    }

    // ---------------- 自动备份 ----------------

    /// <summary>上一次自动备份时的配置指纹，用于"没变化就跳过"。</summary>
    private string _lastAutoHash = "";

    /// <summary>
    /// 配置指纹。
    ///
    /// 必须**排除** LastSyncAt / LastSyncInfo —— 它们每次备份后都会变，
    /// 把它们算进来，指纹永远不同，"没变化就跳过"就形同虚设，
    /// 结果是每隔几分钟往网盘塞一份只差一个时间戳的备份。
    /// </summary>
    private string StateHash()
    {
        var snapshot = _settings().Clone();
        snapshot.Sync.LastSyncAt = DateTime.MinValue;
        snapshot.Sync.LastSyncInfo = "";
        string json = JsonSerializer.Serialize(snapshot, SettingsStore.JsonOptions);
        byte[] hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(hash);
    }

    /// <summary>
    /// 自动备份一次：本地必写；配了网盘就顺手推上去。
    ///
    /// 不弹任何提示 —— 它每隔一段时间就跑一次，弹窗会变成骚扰。
    /// 配置跟上次比没有变化时直接跳过，免得空堆备份。
    /// </summary>
    public async Task<SyncResult> AutoBackupAsync(CancellationToken ct = default)
    {
        var s = _settings();
        if (!TryBuildBackupPayload(out _, out string err)) return SyncResult.Fail(err);

        string hash = StateHash();
        if (hash == _lastAutoHash)
            return SyncResult.Done("配置没有变化，跳过这次自动备份");

        if (!Configured)
        {
            string? path = TryWriteLocalBackup();
            if (path == null) return SyncResult.Fail("自动备份失败");
            _lastAutoHash = hash;
            s.Sync.LastSyncAt = DateTime.Now;
            s.Sync.LastSyncInfo = "自动备份（本地）";
            return SyncResult.Done($"已自动备份到本地：{Path.GetFileName(path)}");
        }

        var r = await UploadAsync(ct).ConfigureAwait(false);
        if (r.Ok) _lastAutoHash = hash;
        return r;
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
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var task = UploadAsync(cts.Token);
            /* ① 用 CancellationToken 控制硬上限：到点后 HTTP 请求真的会中断，
             *    而不是像 task.Wait(timeout) 那样只放弃等待、请求还在后台跑
             *    （孤儿任务会在进程退出时抛未观察异常）。
             * ② ContinueWith 观察异常，保证即便仍出现晚到的失败也不会是"未观察"的。 */
            _ = task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            if (!task.Wait(TimeSpan.FromSeconds(9)))
            {
                cts.Cancel();
                CoreLog.Warn("退出同步超时（8 秒），放弃这次上传");
                return SyncResult.Fail("退出同步超时，本次未上传");
            }
            return task.IsCompletedSuccessfully ? task.Result : SyncResult.Fail("退出同步失败");
        }
        catch (Exception e)
        {
            CoreLog.Warn("退出同步失败：" + e.Message);
            return SyncResult.Fail(e.Message);
        }
    }
}

/// <summary>备份解密失败（多半是密码不对）。界面据此弹出"输入当时的密码"。</summary>
public sealed class BackupPasswordException : Exception
{
    public BackupPasswordException(string message, Exception? inner = null)
        : base(message, inner) { }
}
