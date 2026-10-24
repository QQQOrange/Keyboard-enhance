using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using CapsLockPlus.Core;
using CapsLockPlus.Core.Actions;
using CapsLockPlus.Core.Icons;
using CapsLockPlus.Core.Input;
using CapsLockPlus.Core.Interop;
using CapsLockPlus.Core.Qbar;
using CapsLockPlus.Core.Settings;
using CapsLockPlus.Core.Text;
using CapsLockPlus.Core.Update;
using CapsLockPlus.Core.Windows;

namespace CapsLockPlus;

/// <summary>
/// 自检：不依赖界面，验证核心逻辑。
/// 用 `KeyboardEnhance.exe --selftest` 运行，退出码 0 表示全部通过。
/// </summary>
public static partial class SelfTest
{

    /* 设置模型、迁移、备份/同步/加密、动作目录。
     * 与 SelfTest.cs 同属 partial 类，只按业务域分文件。 */

    /// <summary>
    /// GitHub 的资产摘要是 "sha256:xxx" 形式，若不去掉前缀，
    /// 每次下载都会误判“校验失败”，导致自动更新永远无法完成。
    /// </summary>
    private static void TestUpdateDigest()
    {
        const string hash = "b4b7ae5a70fe98b4b89ea279ca4e4be99e94633b53c7d7dacab2d2847877938e";
        CheckEqual("去掉 sha256: 前缀", UpdateService.NormalizeDigest("sha256:" + hash), hash);
        CheckEqual("兼容大写前缀", UpdateService.NormalizeDigest("SHA256:" + hash.ToUpperInvariant()), hash.ToUpperInvariant());
        CheckEqual("兼容空格分隔", UpdateService.NormalizeDigest("sha256 " + hash), hash);
        CheckEqual("兼容等号分隔", UpdateService.NormalizeDigest("sha256=" + hash), hash);
        CheckEqual("纯摘要原样返回", UpdateService.NormalizeDigest(hash), hash);
        CheckEqual("空值返回空串", UpdateService.NormalizeDigest(null), "");
        CheckEqual("空摘要返回空串", UpdateService.NormalizeDigest("   "), "");
        // 只校验格式合法，不写死具体版本号，避免每次发版都要改测试
        Check("版本号是三段式",
            System.Text.RegularExpressions.Regex.IsMatch(UpdateService.CurrentVersion, @"^\d+\.\d+\.\d+$"),
            UpdateService.CurrentVersion);
    }

    /// <summary>
    /// GitHub 个人访问令牌：洗涤、掩码、"填没填"的判定。
    ///
    /// 这几条都不联网，验的是"用户粘进来的那串东西"能不能被正确处理 ——
    /// 带前缀、带换行、带引号是真实输入里最常见的三种情况，
    /// 处理错了的表现都是"令牌无效"，用户根本查不出来。
    /// </summary>
    private static void TestGitHubToken()
    {
        // ---- 洗涤：把不该在令牌里的东西剥掉 ----
        CheckEqual("干净令牌原样返回",
            UpdateService.NormalizeToken("ghp_abcdefghijklmnopqrstuvwxyz0123456789"), "ghp_abcdefghijklmnopqrstuvwxyz0123456789");
        CheckEqual("去首尾空白",
            UpdateService.NormalizeToken("  ghp_abc123  "), "ghp_abc123");
        CheckEqual("去 Bearer 前缀",
            UpdateService.NormalizeToken("Bearer ghp_abc123"), "ghp_abc123");
        CheckEqual("去小写 bearer 前缀",
            UpdateService.NormalizeToken("bearer ghp_abc123"), "ghp_abc123");
        CheckEqual("去 token 前缀",
            UpdateService.NormalizeToken("token ghp_abc123"), "ghp_abc123");
        CheckEqual("叠着的前缀一起剥",
            UpdateService.NormalizeToken("Authorization: Bearer ghp_abc123"), "ghp_abc123");
        CheckEqual("只取换行前的那一段",
            UpdateService.NormalizeToken("ghp_abc123\r\n第二行"), "ghp_abc123");
        CheckEqual("粘多行时取第一段",
            UpdateService.NormalizeToken("ghp_abc123 extra"), "ghp_abc123");
        CheckEqual("null 洗成空串", UpdateService.NormalizeToken(null), "");
        CheckEqual("空白洗成空串", UpdateService.NormalizeToken("   \r\n  "), "");

        // ---- 填没填：这决定了"能不能自动下载" ----
        Check("有令牌 → 可以自动下载", UpdateService.HasToken("ghp_abc123"));
        Check("空令牌 → 不能自动下载", !UpdateService.HasToken(""));
        Check("只有空格 → 也算没填", !UpdateService.HasToken("   "));
        Check("null → 算没填", !UpdateService.HasToken(null));

        // ---- 掩码：界面上回显时不能暴露完整令牌 ----
        /* 掩码只回答"有没有"，一个真实字符都不露 —— 头尾几位同样算泄露
         * （截图、录屏、远程协助都会带走）。 */
        string mask = UpdateService.MaskToken("ghp_abcdefghijklmnopqrstuvwxyz");
        CheckEqual("掩码是固定 8 个点", mask, new string('•', 8));
        Check("掩码里没有令牌前缀", !mask.Contains("ghp"), mask);
        Check("掩码里没有令牌片段", !mask.Contains("abcd") && !mask.Contains("wxyz"), mask);
        CheckEqual("短令牌也是 8 个点", UpdateService.MaskToken("ghp_x"), new string('•', 8));
        CheckEqual("长度不同但掩码相同（不泄露长度）",
            UpdateService.MaskToken("ghp_short"), UpdateService.MaskToken("github_pat_" + new string('a', 80)));
        CheckEqual("空令牌的掩码是空串", UpdateService.MaskToken(null), "");

        // ---- 前缀粗判（只用于提示，不拦截保存）----
        Check("ghp_ 是 GitHub 令牌", UpdateService.LooksLikeGitHubToken("ghp_x"));
        Check("github_pat_ 是 GitHub 令牌", UpdateService.LooksLikeGitHubToken("github_pat_x"));
        Check("gho_ 是 GitHub 令牌", UpdateService.LooksLikeGitHubToken("gho_x"));
        Check("随便一串不是 GitHub 令牌", !UpdateService.LooksLikeGitHubToken("hello"));

        // ---- 帮助文档地址：集中配置，界面上的问号读的就是它 ----
        Check("帮助地址指向 GitHub 官方中文文档",
            UpdateService.TokenHelpUrl.StartsWith("https://docs.github.com/zh/", StringComparison.Ordinal)
            && UpdateService.TokenHelpUrl.Contains("personal-access-tokens"),
            UpdateService.TokenHelpUrl);

        /* 令牌**不再**被排除：用户要求"备份与导出都必须带令牌"。
         * 真正要守的是"隐私组必须加密"，那条在 TestExportGroups 里。 */
        bool tokenExcluded = Core.Sync.ExportImport.NeverExport.Any(p =>
            p.Equals("Global.GitHubToken", StringComparison.OrdinalIgnoreCase));
        Check("令牌不排除在导出外（备份与导出一致）", !tokenExcluded);

        // ---- 归一化：配置里写坏了的令牌会被洗掉，而不是带着报 401 ----
        var s = DefaultSettings.Create();
        s.Global.GitHubToken = "  Bearer ghp_abc123\r\n  ";
        SettingsStore.Normalize(s);
        CheckEqual("读配置时也洗一遍令牌", s.Global.GitHubToken, "ghp_abc123");
    }

    /// <summary>
    /// GitHub 令牌在备份 / 同步里的边界。
    ///
    /// 用户要求"个人密钥也要纳入备份与同步"，但边界必须写死清楚，
    /// 否则就是拿着凭据到处跑：
    ///   · **加密备份 / WebDAV 同步** → 带（换台机器恢复后能直接用）
    ///   · **明文导出** → 不带（那种文件会被发给别人）
    ///   · **覆盖恢复** → 备份有就用备份的，备份没有就保留本机的
    ///   · **合并恢复** → 本机空才取备份的（多设备各用各的，不互相覆盖）
    /// </summary>
    private static void TestTokenInBackup()
    {
        const string token = "ghp_backup0123456789abcdefghijklmn";
        const string pass = "备份口令-for-token";

        var s = DefaultSettings.Create();
        s.Sync.BackupPassword = pass;
        s.Global.GitHubToken = token;

        var svc = new Core.Sync.SyncService(() => s);
        string payload = svc.BuildBackupPayload(out bool encrypted);

        // ① 备份一律密文，且密文里翻不到令牌
        Check("备份一定是加密的", encrypted);
        Check("备份文件里不含令牌明文", !payload.Contains(token),
            payload.Length > 40 ? payload[..40] : payload);

        // ② 解密后能看到它 —— 这是"跨设备恢复"的前提
        string json = Core.Sync.CryptoBox.Decrypt(payload, pass);
        Check("解密后备份里有令牌", json.Contains(token));

        // ③ 换台机器（本机没令牌）整份恢复 → 令牌被带过来
        var fresh = DefaultSettings.Create();
        fresh.Sync.BackupPassword = pass;
        var outcome = new Core.Sync.SyncService(() => fresh).RestoreFromText(payload);
        CheckEqual("整份恢复能带上令牌", fresh.Global.GitHubToken, token);
        Check("整份恢复会标明令牌来自备份", outcome.TokenAdopted);

        // ④ 覆盖恢复但备份里没有令牌 → 保留本机的（不能把能用的令牌弄没）
        var noToken = DefaultSettings.Create();
        noToken.Sync.BackupPassword = pass;
        noToken.Global.GitHubToken = "";
        string payloadNoToken = new Core.Sync.SyncService(() => noToken).BuildBackupPayload(out _);

        var keep = DefaultSettings.Create();
        keep.Sync.BackupPassword = pass;
        keep.Global.GitHubToken = "ghp_localkeep0123456789";
        var outcome2 = new Core.Sync.SyncService(() => keep).RestoreFromText(payloadNoToken);
        CheckEqual("备份没令牌时保留本机的", keep.Global.GitHubToken, "ghp_localkeep0123456789");
        Check("这种情况不标记为采用了备份令牌", !outcome2.TokenAdopted);

        // ⑤ 合并恢复：本机空 → 取备份的
        var mergeEmpty = DefaultSettings.Create();
        mergeEmpty.Sync.BackupPassword = pass;
        mergeEmpty.Global.GitHubToken = "";
        new Core.Sync.SyncService(() => mergeEmpty)
            .RestoreFromText(payload, Core.Sync.RestoreMode.Merge);
        CheckEqual("合并时本机空 → 取备份的令牌", mergeEmpty.Global.GitHubToken, token);

        // ⑥ 合并恢复：本机有值 → 保留本机（多设备不互相踩）
        var mergeOwn = DefaultSettings.Create();
        mergeOwn.Sync.BackupPassword = pass;
        mergeOwn.Global.GitHubToken = "ghp_device2_own_token";
        new Core.Sync.SyncService(() => mergeOwn)
            .RestoreFromText(payload, Core.Sync.RestoreMode.Merge);
        CheckEqual("合并时本机有值 → 保留本机的令牌",
            mergeOwn.Global.GitHubToken, "ghp_device2_own_token");

        /* ⑦ 明文导出**也要带令牌**（用户明确要求：备份与导出两条路径一致）。
         * 代价是明文文件里能读到它，所以界面上必须标清楚"当前是明文"——
         * 但那是提示的事，不是悄悄把它摘掉。 */
        var node = System.Text.Json.JsonSerializer
            .SerializeToNode(s, SettingsStore.JsonOptions)!.AsObject();
        Core.Sync.ExportImport.Sanitize(node);
        Check("明文导出保留 GitHub 令牌", node.ToJsonString().Contains(token));

        // ⑦b 网盘密码与备份密码仍然要摘（那两个泄露的后果比令牌重得多）
        var s2 = DefaultSettings.Create();
        s2.Sync.WebDavPass = "webdav-secret";
        s2.Sync.BackupPassword = "backup-secret";
        var node2 = System.Text.Json.JsonSerializer
            .SerializeToNode(s2, SettingsStore.JsonOptions)!.AsObject();
        Core.Sync.ExportImport.Sanitize(node2);
        string plain2 = node2.ToJsonString();
        Check("明文导出仍摘掉网盘密码", !plain2.Contains("webdav-secret"));
        Check("明文导出仍摘掉备份密码", !plain2.Contains("backup-secret"));
    }

    /// <summary>
    /// 导出 → 换台机器 → 导入 的完整往返。
    ///
    /// 用户报过"导出的文件在另一台电脑上根本导入不了"。这条把整条链路跑一遍：
    /// 加密导出 → 新机器（没有备份密码）解析 → 要求输密码 → 输对 → 应用，
    /// 逐段确认到底卡在哪一环，而不是靠猜。
    /// </summary>
    private static void TestExportRoundTrip()
    {
        var src = DefaultSettings.Create();
        src.Global.GitHubToken = "ghp_roundtrip0123456789";
        src.Sync.BackupPassword = "导出时的密码";
        src.Triggers.Add(new Core.Settings.TriggerKey { Key = "capslock", LayerId = 2 });

        string tmp = Path.Combine(Path.GetTempPath(),
            "ke_rt_" + Guid.NewGuid().ToString("N")[..6] + Core.Sync.ExportImport.Extension);
        try
        {
            var all = Core.Sync.ExportImport.Groups.Select(g => g.Id).ToList();
            int n = Core.Sync.ExportImport.WriteEncrypted(tmp, src, all, "导出时的密码");
            Check("加密导出写出了设置项", n > 0, n.ToString());

            string text = File.ReadAllText(tmp);

            // ① 新机器：备份密码是空的 → 必须抛"密码问题"，界面据此弹输入框
            var fresh = DefaultSettings.Create();
            bool asksForPassword = false;
            try { Core.Sync.ExportImport.Parse(text, ""); }
            catch (Core.Sync.BackupPasswordException) { asksForPassword = true; }
            Check("新机器空密码时会要求输入密码（而不是报文件损坏）", asksForPassword);

            // ② 输入正确密码 → 能解析出分组
            var (groups, data, wasBackup) = Core.Sync.ExportImport.Parse(text, "导出时的密码");
            Check("解析出分组", groups.Count > 0, string.Join(",", groups));
            Check("导出文件不被认成备份", !wasBackup);

            // ③ 先看导出文件本身：层与触发键的引用在文件里对不对
            Check("文件里 Layers 是完整 10 层",
                data["Layers"] is System.Text.Json.Nodes.JsonArray la && la.Count == src.Layers.Count,
                data["Layers"]?.ToJsonString()?.Length.ToString() ?? "null");
            /* 触发键有两层各一条（默认给的是第 1 层），所以不能只看第 0 条。 */
            bool hasLayer2 = data["Triggers"] is System.Text.Json.Nodes.JsonArray ta
                && ta.Any(t => t!["LayerId"]!.GetValue<int>() == 2);
            Check("文件里有绑到第 2 层的触发键", hasLayer2,
                data["Triggers"]?.ToJsonString() ?? "null");

            // ④ 应用后内容真的过来了
            Core.Sync.ExportImport.Apply(fresh, groups, data);
            CheckEqual("令牌带过来了", fresh.Global.GitHubToken, "ghp_roundtrip0123456789");
            CheckEqual("层数一致", fresh.Layers.Count, src.Layers.Count);
            CheckEqual("触发键一致", fresh.Triggers.Count, src.Triggers.Count);
            Check("导入后仍有一条绑在第 2 层",
                fresh.Triggers.Any(t => t.LayerId == 2),
                string.Join(",", fresh.Triggers.Select(t => t.LayerId)));

            /* ⑤ 备份文件也能直接导入（用户分不清 .keyboardenhance 和 .clpconf）。
             *   备份是整份 AppSettings，没有 kind/items 包装。 */
            string backupText = Core.Sync.CryptoBox.Encrypt(
                System.Text.Json.JsonSerializer.Serialize(src, SettingsStore.JsonOptions),
                "导出时的密码");
            var (bGroups, bData, bWasBackup) = Core.Sync.ExportImport.Parse(backupText, "导出时的密码");
            Check("备份文件被认成备份", bWasBackup);
            Check("备份文件覆盖全部组", bGroups.Count == Core.Sync.ExportImport.Groups.Length,
                $"{bGroups.Count}/{Core.Sync.ExportImport.Groups.Length}");
            Check("备份文件里有 Layers", bData.ContainsKey("Layers"));

            // ⑥ 错误密码必须继续报"密码问题"，不能变成"文件损坏"
            bool wrongPw = false;
            try { Core.Sync.ExportImport.Parse(text, "错的密码"); }
            catch (Core.Sync.BackupPasswordException) { wrongPw = true; }
            catch { }
            Check("密码错了报的是密码问题", wrongPw);
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }

    /// <summary>
    /// 清理规则：按天数、按份数、以及"什么算冗余"。
    ///
    /// 本地部分用临时目录真删真数（同一套 <c>PruneFolder</c> 代码路径）；
    /// 云端只是把同一套规则作用在 PROPFIND 结果上，判定规则在这里验。
    /// 重点守 **"最新一份永远留"** —— 半月没备份时按天数会把唯一那份也删掉。
    /// </summary>
    private static void TestBackupCleanup()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ke_cleanup_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            // 3 份 10 天前 + 12 份近期
            for (int i = 0; i < 15; i++)
            {
                /* 文件名精确到秒 —— 前 3 份必须错开分钟，否则会被同一个名字覆盖，
                 * 那就变成"造了 15 份其实只有 13 个文件"。 */
                DateTime when = i < 3
                    ? DateTime.Now.AddDays(-10).AddMinutes(-i * 7)
                    : DateTime.Now.AddHours(-i);
                string path = Path.Combine(dir, Core.Sync.SyncService.BackupName(when));
                File.WriteAllText(path, "payload-" + i);
                File.SetLastWriteTime(path, when);
            }
            CheckEqual("造了 15 份备份", Directory.GetFiles(dir).Length, 15);

            // ① 按 7 天清理：3 份旧的走掉
            int byDays = Core.Sync.SyncService.PruneFolder(dir, keep: int.MaxValue, days: 7);
            CheckEqual("按 7 天清理删掉 3 份", byDays, 3);
            CheckEqual("剩 12 份", Directory.GetFiles(dir).Length, 12);

            // ② 只保留 10 份
            int byKeep = Core.Sync.SyncService.PruneFolder(dir, keep: 10, days: 0);
            CheckEqual("保留 10 份时删掉 2 份", byKeep, 2);
            CheckEqual("剩 10 份", Directory.GetFiles(dir).Length, 10);

            // ③ 只剩一份时，再按天数清也不会删它
            Core.Sync.SyncService.PruneFolder(dir, keep: 1, days: 0);
            CheckEqual("最后留 1 份", Directory.GetFiles(dir).Length, 1);
            int last = Core.Sync.SyncService.PruneFolder(dir, keep: 1, days: 1);
            CheckEqual("唯一一份过期也不删", last, 0);
            CheckEqual("仍然还在", Directory.GetFiles(dir).Length, 1);

            // ④ 冗余判定：哪些算"上传残留"
            Check("识别 .tmp 残留", Core.Sync.SyncService.IsTempArtifact("KeyboardEnhance(1).keyboardenhance.tmp"));
            Check("识别 .part 残留", Core.Sync.SyncService.IsTempArtifact("backup.part"));
            Check("识别 .crdownload", Core.Sync.SyncService.IsTempArtifact("x.crdownload"));
            Check("识别 ~$ 前缀", Core.Sync.SyncService.IsTempArtifact("~$settings.json"));
            Check("识别 .bak 旧副本", Core.Sync.SyncService.IsTempArtifact("settings.json.bak"));
            Check("正常备份名不算残留",
                !Core.Sync.SyncService.IsTempArtifact(Core.Sync.SyncService.BackupName(DateTime.Now)));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// 老用户的设置文件里只有旧引擎，升级后必须自动补齐新引擎，
    /// 否则"扩充了引擎"对老用户等于没发生。
    /// </summary>
    private static void TestEngineMigration()
    {
        var old = DefaultSettings.Create();
        old.BuiltinEnginesVersion = 0;          // 模拟旧版设置文件
        old.Search.RemoveAll(e => !e.Key.Equals("default", StringComparison.OrdinalIgnoreCase)
                                   && e.Key is not ("bd" or "tb" or "zh" or "gh" or "tr"));
        int before = old.Search.Count;

        SettingsStore.MigrateBuiltinEnginesForTest(old);

        Check("迁移后条目变多", old.Search.Count > before, $"{before} → {old.Search.Count}");
        Check("迁移后版本号已更新",
            old.BuiltinEnginesVersion == DefaultSettings.BuiltinEnginesVersion);
        Check("补齐了 go 引擎", old.Search.Any(e => e.Key == "go"));
        Check("补齐了 bili 引擎", old.Search.Any(e => e.Key == "bili"));
        Check("用户原有的 bd 未被覆盖", old.Search.Any(e => e.Key == "bd"));

        // 一次又一次迁移应当是幂等的
        int after = old.Search.Count;
        SettingsStore.MigrateBuiltinEnginesForTest(old);
        CheckEqual("迁移是幂等的", old.Search.Count, after);
    }

    /// <summary>
    /// 数据目录必须**跟着软件走**，迁移要只搬用户数据，加密导出要带全字段。
    ///
    /// 这三条都是"用户看不见但一错就很难受"的地方：
    ///   · 数据留在 %LocalAppData% → 拷走软件文件夹等于没拷配置；
    ///   · 迁移时把 logs / backups 一起搬 → 新目录塞满诊断垃圾，
    ///     而且备份会散落在两处；
    ///   · 加密导出还偷偷剔除密码 → 换台机器恢复完还得重填一遍。
    /// </summary>
    private static void TestDataLocationAndEncryptedExport()
    {
        // ---------- 目录约定 ----------
        CheckEqual("用户数据目录名", AppPaths.DataFolderName, "KeyboardEnhance Data");
        CheckEqual("备份目录名", Core.Sync.SyncService.BackupFolderName, "KeyboardEnhance Back");
        Check("老备份目录名仍有记录（迁移要用）",
            Core.Sync.SyncService.LegacyBackupFolderName.Length > 0,
            Core.Sync.SyncService.LegacyBackupFolderName);

        /* 自检为了不碰真实数据，会用 CAPSLOCKPLUS_DATA 指到临时目录 ——
         * 那种情况下"在软件旁边"当然不成立，跳过而不是判失败。 */
        bool envOverride = !string.IsNullOrWhiteSpace(
            Environment.GetEnvironmentVariable("CAPSLOCKPLUS_DATA"));
        Check("数据目录在软件旁边（环境变量覆盖 / 回落时跳过）",
            envOverride || AppPaths.DataDirIsFallback
            || AppPaths.DataDir.StartsWith(AppPaths.AppDir, StringComparison.OrdinalIgnoreCase),
            $"data={AppPaths.DataDir} app={AppPaths.AppDir} "
            + $"override={envOverride} fallback={AppPaths.DataDirIsFallback}");

        // ---------- 迁移：拷什么、不拷什么 ----------
        string root = Path.Combine(AppPaths.DataDir, "selftest-migrate");
        string from = Path.Combine(root, "old");
        string to = Path.Combine(root, "new");
        try
        {
            Directory.CreateDirectory(Path.Combine(from, "icons"));
            Directory.CreateDirectory(Path.Combine(from, "logs"));
            Directory.CreateDirectory(Path.Combine(from, "backups"));

            File.WriteAllText(Path.Combine(from, "settings.json"), "old-settings");
            File.WriteAllText(Path.Combine(from, "vault.json"), "vault");
            File.WriteAllText(Path.Combine(from, "vault.cache"), "cache");
            File.WriteAllText(Path.Combine(from, "settings.json.tmp"), "tmp");
            File.WriteAllText(Path.Combine(from, "selftest.log"), "log");
            File.WriteAllText(Path.Combine(from, "icons", "a.svg"), "<svg/>");
            File.WriteAllText(Path.Combine(from, "logs", "app.log"), "log");
            File.WriteAllText(Path.Combine(from, "backups", "x.clpbak"), "bak");

            int n = AppPaths.CopyTreeForTest(from, to);

            Check("迁移带上了设置", File.Exists(Path.Combine(to, "settings.json")));
            Check("迁移带上了加密元数据", File.Exists(Path.Combine(to, "vault.json")));
            Check("迁移带上了密钥缓存", File.Exists(Path.Combine(to, "vault.cache")));
            Check("迁移带上了图标", File.Exists(Path.Combine(to, "icons", "a.svg")));
            Check("迁移不拷日志", !Directory.Exists(Path.Combine(to, "logs")));
            Check("迁移不拷备份（备份目录单独搬）", !Directory.Exists(Path.Combine(to, "backups")));
            Check("迁移不拷临时文件", !File.Exists(Path.Combine(to, "settings.json.tmp")));
            Check("迁移不拷自检产物", !File.Exists(Path.Combine(to, "selftest.log")));
            CheckEqual("迁移数不含被跳过的文件", n, 4);   // settings / vault.json / vault.cache / icons

            // 再跑一次：目标已有的文件绝不能被旧数据盖掉
            File.WriteAllText(Path.Combine(to, "settings.json"), "new-settings");
            AppPaths.CopyTreeForTest(from, to);
            CheckEqual("迁移不覆盖目标已有文件",
                File.ReadAllText(Path.Combine(to, "settings.json")), "new-settings");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }

        // ---------- 导出：勾了哪组就是哪组的全部字段 ----------
        var s = DefaultSettings.Create();
        s.Sync.WebDavUrl = "https://dav.example.com/dav/";
        s.Sync.WebDavUser = "me@example.com";
        s.Sync.WebDavPass = "SuperSecretDavPass";
        s.Sync.BackupPassword = "SuperSecretBackupPass";

        var all = Core.Sync.ExportImport.Groups.Select(g => g.Id).ToList();

        // 明文模式：仍然剔除凭据（这条老行为不能退）
        var (plainText, _) = Core.Sync.ExportImport.Build(s, all);
        Check("明文导出不含网盘密码", !plainText.Contains("SuperSecretDavPass"));
        Check("明文导出不含备份密码", !plainText.Contains("SuperSecretBackupPass"));

        // 加密模式：勾了就带全部字段
        var (fullText, _) = Core.Sync.ExportImport.Build(s, all, includeSecrets: true);
        Check("加密导出带上网盘密码", fullText.Contains("SuperSecretDavPass"));
        Check("加密导出带上备份密码", fullText.Contains("SuperSecretBackupPass"));

        // ---------- 落盘 + 回读（真实文件） ----------
        string dir = Path.Combine(AppPaths.DataDir, "selftest-export-enc");
        string file = Path.Combine(dir, "enc" + Core.Sync.ExportImport.Extension);
        Directory.CreateDirectory(dir);
        try
        {
            int n = Core.Sync.ExportImport.WriteEncrypted(file, s, all, "export-pw");
            Check("加密导出写出了文件", File.Exists(file) && new FileInfo(file).Length > 0);
            Check("加密导出的文件确实是密文",
                Core.Sync.CryptoBox.LooksEncrypted(File.ReadAllText(file)));
            Check("加密导出写入了设置项", n > 0, n.ToString());

            var (groups, data, _) = Core.Sync.ExportImport.ReadPlain(file, "export-pw");
            Check("加密导出能读回来", groups.Count == all.Count, $"{groups.Count}/{all.Count}");

            var dst = DefaultSettings.Create();
            Core.Sync.ExportImport.Apply(dst, groups, data);
            CheckEqual("加密导出能恢复网盘地址", dst.Sync.WebDavUrl, "https://dav.example.com/dav/");
            CheckEqual("加密导出能恢复网盘密码", dst.Sync.WebDavPass, "SuperSecretDavPass");
            CheckEqual("加密导出能恢复备份密码", dst.Sync.BackupPassword, "SuperSecretBackupPass");

            // 密码不对 → 必须报"密码问题"，而不是"文件坏了"
            bool passwordRejected = false;
            try { Core.Sync.ExportImport.ReadPlain(file, "wrong-pw"); }
            catch (Core.Sync.BackupPasswordException) { passwordRejected = true; }
            Check("加密导出用错密码会被判为密码问题", passwordRejected);

            bool emptyRejected = false;
            try { Core.Sync.ExportImport.ReadPlain(file, ""); }
            catch (Core.Sync.BackupPasswordException) { emptyRejected = true; }
            Check("加密导出没密码时提示要密码", emptyRejected);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }

        /* ---------- 明文副本必须清掉 ----------
         *
         * `.bak` 是上次保存前的副本。启用加密前写下的那份是明文，
         * 和正牌设置长得一模一样 —— 用户拷走文件夹时根本不会注意。
         * 但**密文**副本要留着：那是有效的回退点。 */
        string purgeDir = Path.Combine(AppPaths.DataDir, "selftest-purge");
        string settingsFile = Path.Combine(purgeDir, "settings.json");
        Directory.CreateDirectory(purgeDir);
        try
        {
            File.WriteAllText(settingsFile, "{\"clpVault\":1}");
            File.WriteAllText(settingsFile + ".bak", "{\"Keys\":[]}");        // 明文 → 删
            File.WriteAllText(settingsFile + ".tmp", "{\"clpVault\":1}");     // 密文 → 留
            File.WriteAllText(settingsFile + ".broken", "这不是 JSON");        // 非 JSON → 留

            int removed = DataVault.PurgePlaintextCopies(settingsFile);
            CheckEqual("只删明文副本", removed, 1);
            Check("明文 .bak 被删掉", !File.Exists(settingsFile + ".bak"));
            Check("密文 .tmp 保留", File.Exists(settingsFile + ".tmp"));
            Check("非 JSON 的 .broken 保留", File.Exists(settingsFile + ".broken"));
        }
        finally
        {
            try { Directory.Delete(purgeDir, true); } catch { }
        }
    }

    private static void TestSettings()
    {
        var original = SettingsStore.Load();
        var copy = original.Clone();
        copy.Global.MouseSpeed = 7;
        var json = System.Text.Json.JsonSerializer.Serialize(copy, SettingsStore.JsonOptions);
        var back = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json, SettingsStore.JsonOptions)!;
        CheckEqual("设置可序列化往返", back.Global.MouseSpeed, 7);
        Check("默认设置含按键绑定", DefaultSettings.Create().Keys.Count > 100);
        Check("默认设置含搜索指令", DefaultSettings.Create().Search.Count >= 5);
        Check("Capslox 与经典方案不同",
            DefaultSettings.CapsloxScheme().First(k => k.Combo == "caps_z").Action !=
            DefaultSettings.ClassicScheme().First(k => k.Combo == "caps_z").Action);
        var normalized = DefaultSettings.Create();
        normalized.Keys.Add(new KeyBinding { Combo = "caps_q", Action = "moveLeft" });
        SettingsStore.Normalize(normalized);
        Check("重复绑定被去重", normalized.Keys.Count(k => k.Combo == "caps_q") == 1);
        Check("每个默认绑定都有对应动作", DefaultSettings.Create().Keys.All(k =>
            k.Action.Length > 0));
    }

    private static void TestLegacyImport()
    {
        // 不要写系统临时目录：某些环境下 %TEMP% 不可写（组策略 / 沙箱），
        // 之前这里抛出的未捕获异常会让 --selftest 整个挂住。统一写到数据目录。
        string path = Path.Combine(AppPaths.DataDir, "selftest_legacy.ini");
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            string content = string.Join("\r\n", new[]
            {
                "[Global]",
                "autostart=1",
                "mouseSpeed=6",
                "[Keys]",
                "caps_q=keyFunc_qbar",
                "caps_a=keyFunc_moveWordLeft(3)",
                // 这一行是旧的固定窗口槽位：现在会被**跳过**（不迁移），
                // 所以下面的条目数断言要按"实际能迁移的"来算。
                "caps_1=keyFunc_winbind_activate(1)",
                "caps_lalt_z=keyFunc_putWinToBottom",
                "caps_f3=keyFunc_translate",
                "[QSearch]",
                "default=https://www.bing.com/search?q={q}",
                "bd=https://www.baidu.com/s?wd={q}",
                "[QRun]",
                "ie=C:\\Program Files\\Internet Explorer\\iexplore.exe",
                "*runas ",
                "[QWeb]",
                "gh=https://github.com",
                "[TabHotString]",
                "clp=CapsLock+",
            });
            File.WriteAllText(path, content, new UnicodeEncoding(false, true));
            var result = LegacyIniImporter.Import(path);
            // Keys 统计的是**按键绑定**条数。样本里 5 个按键中，
            // 旧的窗口槽位（caps_1=keyFunc_winbind_activate(1)）会被跳过，
            // 所以应当正好迁移 4 个。
            CheckEqual("能识别 UTF-16 的旧设置", result.Keys, 4);
            CheckEqual("迁移 Qbar 绑定", result.Settings.FindBinding("caps_q")?.Action, "qbar");
            CheckEqual("迁移带参数的动作", result.Settings.FindBinding("caps_a")?.Arg, "3");
            // 旧窗口槽位已移除：导入时这一项被跳过（不再产生 winbindActivateN）
            Check("旧窗口槽位不再迁移成 winbindActivateN",
                result.Settings.FindBinding("caps_1")?.Action?.StartsWith("winbindActivate") != true,
                result.Settings.FindBinding("caps_1")?.Action ?? "(无)");
            CheckEqual("迁移置底动作", result.Settings.FindBinding("caps_lalt_z")?.Action, "winSendToBottom");
            // 翻译功能已整块移除：旧 ini 里的 keyfunc_translate 迁移成"什么都不做"
            CheckEqual("已移除的翻译动作迁移成 doNothing", result.Settings.FindBinding("caps_f3")?.Action, "doNothing");
            Check("迁移搜索指令", result.Settings.Search.Any(s => s.Key == "bd"));
            Check("迁移程序条目", result.Settings.Run.Any(r => r.Key == "ie"));
            Check("迁移网址条目", result.Settings.Web.Any(w => w.Key == "gh"));
            Check("迁移热字串", result.Settings.HotStrings.Any(h => h.Key == "clp"));
            CheckEqual("迁移开机自启", result.Settings.Global.StartWithWindows, true);
            CheckEqual("迁移鼠标速度", result.Settings.Global.MouseSpeed, 6);
        }
        catch (Exception ex)
        {
            Check("旧版 ini 迁移（读取/写入）", false, ex.Message);
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }

    private static void TestActionCatalog()
    {
        var settings = DefaultSettings.Create();
        var queue = new ActionQueue();
        var ctx = new ActionContext
        {
            Settings = () => settings,
            Clipboard = new ClipboardService(),
            Windows = new WindowActions(),
            WindowBindings = new WindowBindingService(settings),
            Mouse = new MouseService(),
            Notify = _ => { },
        };
        var catalog = ActionCatalog.Create(ctx);
        Check("动作数量 > 80", catalog.Items.Count > 80, catalog.Items.Count.ToString());
        Check("所有动作 ID 唯一", catalog.Items.Select(i => i.Id).Distinct().Count() == catalog.Items.Count);
        Check("每个动作都有说明", catalog.Items.All(i => i.Description.Length > 0));
        Check("未知动作安全返回", catalog.Invoke("no_such_action", "") == false);

        // 每个默认绑定都能在动作目录里找到
        var missing = settings.Keys.Where(k => k.IsBound && catalog.Get(k.Action) == null)
            .Select(k => k.Combo + "=" + k.Action).ToList();
        Check("默认绑定全部可解析", missing.Count == 0, string.Join(", ", missing.Take(6)));

        // 每个预设动作都被默认绑定或可选
        Check("不绑定动作存在", catalog.Get("doNothing") != null);

        /* 「系统功能」分类：媒体键、音量、亮度、计算器归到一起。
         * 之前这些散在「系统」里，和"打开网址/运行程序"混着，不好找。 */
        var sysFn = catalog.Items.Where(i => i.Category == "系统功能").ToList();
        Check("系统功能分类至少有 10 项", sysFn.Count >= 10, sysFn.Count.ToString());

        foreach (var id in new[]
                 {
                     "mediaPlayPause", "mediaPrev", "mediaNext", "volumeUp", "volumeDown",
                     "volumeMute", "openCalculator", "brightnessUp", "brightnessDown",
                     "lockScreen", "screenshot",
                 })
        {
            Check($"系统功能含 {id}", catalog.Get(id) != null);
        }

        /* 「智能直达」分类取代了原来的「系统」：只留一个动作。
         *
         * 原来的「系统」下并列着「打开网址」「运行程序」「直达链接」，
         * 前两个要预先填死地址/程序，和「直达链接」分类里预填的
         * 直达网址 / 直达文件夹 / 直达文件语义重复，用户不知道该在哪填。
         * 现在预填的归「直达链接」，选中的归「智能直达」，一个动作就够。 */
        var smart = catalog.Items.Where(i => i.Category == "智能直达").ToList();
        CheckEqual("「智能直达」分类只有 1 个动作", smart.Count, 1);
        CheckEqual("「智能直达」里的动作是 smartOpen", smart.FirstOrDefault()?.Id, "smartOpen");
        Check("「智能直达」不带预填参数", catalog.Get("smartOpen")?.NeedsArg == false);

        // 旧动作不能从表里删掉（老配置还绑着），但必须隐藏
        Check("openUrl 已从界面隐藏", catalog.Get("openUrl")?.Hidden == true);
        Check("runProgram 已从界面隐藏", catalog.Get("runProgram")?.Hidden == true);
        Check("界面上不再有「系统」分类",
            catalog.Items.Where(i => !i.Hidden).All(i => i.Category != "系统"),
            string.Join(",", catalog.Items.Where(i => !i.Hidden && i.Category == "系统").Select(i => i.Id)));

        queue.Dispose();
    }

    /// <summary>
    /// 备份加密：往返、错密码、篡改、明文兼容。
    /// 这些是"每次备份都加密"这条需求的底线，必须逐条验。
    /// </summary>
    private static void TestCrypto()
    {
        const string plain = "{\"hello\":\"世界\",\"n\":42}";
        const string pw = "口令123";

        string enc = Core.Sync.CryptoBox.Encrypt(plain, pw);
        Check("加密后能被识别为密文", Core.Sync.CryptoBox.LooksEncrypted(enc));
        Check("密文里不出现原文", !enc.Contains("世界"), "密文泄露了明文");
        CheckEqual("同口令能解回原文", Core.Sync.CryptoBox.Decrypt(enc, pw), plain);

        // 同一内容两次加密必须不同（盐/nonce 随机）
        string enc2 = Core.Sync.CryptoBox.Encrypt(plain, pw);
        Check("两次加密结果不同（盐随机）", enc != enc2);

        // 错密码必须失败，且报人话
        bool wrongRejected = false;
        try { Core.Sync.CryptoBox.Decrypt(enc, "错误密码"); }
        catch (InvalidOperationException e)
        {
            wrongRejected = e.Message.Contains("密码");
        }
        Check("错误密码被拒绝且提示说人话", wrongRejected);

        // 空口令 = 不加密，原样返回（兼容"不要密码"的路径）
        CheckEqual("空口令不加密", Core.Sync.CryptoBox.Encrypt(plain, ""), plain);
        Check("明文不会被当成密文", !Core.Sync.CryptoBox.LooksEncrypted(plain));
        CheckEqual("明文原样放行", Core.Sync.CryptoBox.Decrypt(plain, "任意密码"), plain);

        // 只有空格的口令不能静默降级成明文
        bool spaceRejected = false;
        try { Core.Sync.CryptoBox.Encrypt(plain, "   "); }
        catch (InvalidOperationException) { spaceRejected = true; }
        Check("全空格密码被拒绝（不静默降级）", spaceRejected);

        // 密文被改一个字节必须解不开（AEAD 认证）
        var node = System.Text.Json.Nodes.JsonNode.Parse(enc)!.AsObject();
        byte[] data = Convert.FromBase64String(node["data"]!.GetValue<string>());
        data[0] ^= 0xFF;
        node["data"] = Convert.ToBase64String(data);
        bool tamperRejected = false;
        try { Core.Sync.CryptoBox.Decrypt(node.ToJsonString(), pw); }
        catch (InvalidOperationException) { tamperRejected = true; }
        Check("篡改过的密文被拒绝", tamperRejected);

        // KDF 参数写进文件（换成荒谬值要被挡，避免解压炸弹式卡死）
        var bomb = System.Text.Json.Nodes.JsonNode.Parse(enc)!.AsObject();
        bomb["iterations"] = 999_999_999;
        bool bombRejected = false;
        try { Core.Sync.CryptoBox.Decrypt(bomb.ToJsonString(), pw); }
        catch (InvalidOperationException) { bombRejected = true; }
        Check("荒谬的 KDF 参数被拒绝", bombRejected);
    }

    /// <summary>
    /// 明文导出 / 导入：往返、拒绝加密备份、**只应用勾选的组**。
    /// </summary>
    /// <summary>
    /// 导出范围必须和"勾了什么"一致。
    ///
    /// 用户报过：勾了「同步与备份设置」和「按键映射触发键与层」，导出文件里没有这两块。
    /// 前者是组的 Keys 与组名不符（只写了 Triggers，漏了 Layers），
    /// 后者要确认 Sync 整块真的进了文件、而且加密导出不摘密码字段。
    /// </summary>
    private static void TestExportGroups()
    {
        var s = DefaultSettings.Create();
        s.Sync.WebDavUrl = "https://dav.example.com/dav/";
        s.Sync.WebDavUser = "tester";
        s.Sync.SyncOnStart = true;
        s.Sync.BackupPassword = "导出密码";
        s.Triggers.Add(new Core.Settings.TriggerKey { Key = "capslock", LayerId = 2 });

        // ---- ① 触发键与层：组名承诺了"层"，文件里就必须有 Layers ----
        var (t1, _) = Core.Sync.ExportImport.Build(s, new[] { "triggers" }, includeSecrets: true);
        Check("导出含 Triggers", t1.Contains("\"Triggers\""), t1.Length.ToString());
        Check("导出含 Layers（组名承诺了『层』）", t1.Contains("\"Layers\""),
            t1.Length > 300 ? t1[..300] : t1);
        Check("触发键内容真的写进去了", t1.Contains("capslock"));

        // ---- ② 同步与备份设置：整块 Sync 都要在，密码字段也不例外（加密导出）----
        var (t2, _) = Core.Sync.ExportImport.Build(s, new[] { "syncSettings" }, includeSecrets: true);
        Check("导出含 Sync", t2.Contains("\"Sync\""), t2.Length.ToString());
        Check("Sync 里带上了服务器地址", t2.Contains("dav.example.com"));
        Check("Sync 里带上了用户名", t2.Contains("tester"));
        Check("Sync 里带上了同步开关", t2.Contains("\"SyncOnStart\""));
        Check("加密导出保留密码字段", t2.Contains("\"BackupPassword\""));

        // ---- ③ 明文导出仍然要摘凭据（安全边界不能被"修 bug"顺手破坏）----
        var (t3, _) = Core.Sync.ExportImport.Build(s, new[] { "syncSettings" }, includeSecrets: false);
        Check("明文导出摘掉网盘密码", !t3.Contains("\"WebDavPass\""));
        Check("明文导出摘掉备份密码", !t3.Contains("\"BackupPassword\""));

        // ---- ④ 勾了隐私组就不许明文导出（用户明确要求：强制加密）----
        string tmp = Path.Combine(Path.GetTempPath(), "ke_export_priv.clpconf");
        bool plainRejected = false;
        try
        {
            Core.Sync.ExportImport.WritePlain(tmp, s, new[] { "syncSettings" });
        }
        catch (InvalidOperationException)
        {
            plainRejected = true;
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
        Check("勾了隐私组时明文导出被拒绝", plainRejected);
        CheckEqual("能报出是哪个隐私组",
            string.Join(",", Core.Sync.ExportImport.PrivateNames(new[] { "syncSettings" })), "同步与备份设置");
        Check("不勾隐私组时不拦",
            Core.Sync.ExportImport.PrivateNames(new[] { "appearance", "keys" }).Count == 0);

        // ---- ⑤ 不勾任何组：文件里不该有配置内容 ----
        var (t4, count4) = Core.Sync.ExportImport.Build(s, Array.Empty<string>(), includeSecrets: true);
        CheckEqual("空勾选导出的设置项为 0", count4, 0);
        Check("空勾选不含 Triggers", !t4.Contains("\"Triggers\""));
    }

    private static void TestExportImport()
    {        var src = DefaultSettings.Create();
        src.Sync.WebDavUrl = "https://dav.example.com/dav/";
        src.Sync.BackupPassword = "同步密码";

        // ---- 隐私标记必须正确 ----
        var syncGroup = Core.Sync.ExportImport.FindGroup("syncSettings");
        var keysGroup = Core.Sync.ExportImport.FindGroup("keys");
        Check("同步设置被标为隐私", syncGroup?.Private == true);
        Check("按键映射不是隐私", keysGroup?.Private == false);
        Check("隐私项默认不勾选", syncGroup?.DefaultChecked == false);
        Check("非隐私项默认勾选", keysGroup?.DefaultChecked == true);

        // ---- 只勾"按键映射"：导出内容不得包含任何隐私键 ----
        var (text, count) = Core.Sync.ExportImport.Build(src, new[] { "keys" });
        Check("导出的 JSON 是合法结构", text.Contains("\"kind\": \"clp-config\""));
        Check("只勾按键映射时不包含同步密码", !text.Contains("同步密码"));
        Check("只勾按键映射时不包含 WebDAV 地址", !text.Contains("dav.example.com"));
        Check("确实写入了设置项", count > 0, count.ToString());

        // ---- 往返：导出再导入，按键映射能恢复 ----
        var (groups, data, _) = Core.Sync.ExportImport.Parse(text);
        CheckEqual("解析出 1 个组", groups.Count, 1);
        var dst = DefaultSettings.Create();
        int beforeKeys = dst.Keys.Count;
        Core.Sync.ExportImport.Apply(dst, groups, data);
        Check("导入后按键映射被替换", dst.Keys.Count > 0, $"{beforeKeys} -> {dst.Keys.Count}");
        CheckEqual("导入不碰同步设置（没勾）", dst.Sync.WebDavUrl, "");

        // ---- 关键安全属性：文件里塞了隐私键，但没勾对应组，也不能生效 ----
        var evil = """
        {"kind":"clp-config","items":["keys"],
         "data":{"Keys":[],"Sync":{"WebDavUrl":"https://attacker/dav/","WebDavPass":"x"}}}
        """;
        var (eg, ed, _) = Core.Sync.ExportImport.Parse(evil);
        var victim = DefaultSettings.Create();
        victim.Sync.WebDavUrl = "https://my-own/dav/";
        Core.Sync.ExportImport.Apply(victim, eg, ed);
        CheckEqual("只勾 keys 时无法改掉 WebDAV 地址（防投毒）",
            victim.Sync.WebDavUrl, "https://my-own/dav/");
        CheckEqual("只勾 keys 时无法改掉备份密码", victim.Sync.BackupPassword, "");

        /* ---- 加密导出：现在**能**导入了（要给密码），但密码不对必须报密码问题 ----
         *
         * 老行为是"明文导入一律拒绝加密文件"。用户后来要求加密导出能带走全部字段，
         * 那导入侧就得跟着支持 —— 否则导出的东西自己读不回来。
         * 关键是不能把"密码不对"退化成"文件坏了"，否则用户会去怀疑文件。 */
        string encrypted = Core.Sync.CryptoBox.Encrypt(text, "pw");
        var (encGroups, _, _) = Core.Sync.ExportImport.Parse(encrypted, "pw");
        Check("加密导出能被正确密码导入", encGroups.Count == 1 && encGroups[0] == "keys",
            string.Join(",", encGroups));

        bool wrongPw = false;
        try { Core.Sync.ExportImport.Parse(encrypted, "not-the-pw"); }
        catch (Core.Sync.BackupPasswordException) { wrongPw = true; }
        Check("加密导出用错密码被判为密码问题", wrongPw);

        /* ---- 既不是导出、也不是备份的 JSON 必须拒 ----
         * 注意别用 {"Keys":[]} 这种来测：它**确实**是一份合法配置（的片段），
         * 现在会被当成"整份备份"接受。要测拒绝就用完全无关的 JSON。 */
        bool junk = false;
        try { Core.Sync.ExportImport.Parse("{\"foo\":1,\"bar\":[]}"); }
        catch (InvalidOperationException) { junk = true; }
        Check("看不出配置结构的 JSON 被拒绝", junk);

        // ---- items 类型不对要拒 ----
        bool badItems = false;
        try { Core.Sync.ExportImport.Parse("{\"kind\":\"clp-config\",\"items\":{},\"data\":{\"a\":1}}"); }
        catch (InvalidOperationException) { badItems = true; }
        Check("items 不是数组时被拒绝", badItems);

        // ---- data 为空要拒 ----
        bool emptyData = false;
        try { Core.Sync.ExportImport.Parse("{\"kind\":\"clp-config\",\"items\":[\"keys\"],\"data\":{}}"); }
        catch (InvalidOperationException e) { emptyData = e.Message.Contains("没有任何数据"); }
        Check("data 为空时被拒绝", emptyData);
    }

    /// <summary>
    /// WebDAV：地址整理、PROPFIND 解析、备份文件名。
    /// </summary>
    private static void TestWebDav()
    {
        // 裸地址补 https 与末尾斜杠
        CheckEqual("裸域名补协议",
            Core.Sync.WebDavClient.NormalizeInput("dav.jianguoyun.com/dav"),
            "https://dav.jianguoyun.com/dav/");
        /* 云端子目录名：用**可执行文件名**，不是显示名。
         * 显示名是「按键增强 / Keyboard Enhance」，带空格和中文，
         * 当目录名既不好打也不好排在网盘里。 */
        CheckEqual("云端子目录用可执行文件名",
            Core.Sync.WebDavClient.DefaultSubdir, "KeyboardEnhance");
        Check("云端子目录里没有空格", !Core.Sync.WebDavClient.DefaultSubdir.Contains(' '));

        // 已知网盘根地址会自动补子目录（发请求时用 NormalizeBase）
        CheckEqual("已知根地址补子目录",
            Core.Sync.WebDavClient.NormalizeBase("https://dav.jianguoyun.com/dav/"),
            "https://dav.jianguoyun.com/dav/KeyboardEnhance/");

        // 老配置里存的是「根地址 + 旧子目录」，必须还原成纯根地址，否则会多套一层
        CheckEqual("老配置里的旧子目录会被剥掉",
            Core.Sync.WebDavClient.NormalizeInput("https://dav.jianguoyun.com/dav/CapsLockPlus/"),
            "https://dav.jianguoyun.com/dav/");
        CheckEqual("新子目录同样会被剥掉",
            Core.Sync.WebDavClient.NormalizeInput("https://dav.jianguoyun.com/dav/KeyboardEnhance/"),
            "https://dav.jianguoyun.com/dav/");
        // 配置里存的是纯根地址（不含子目录）
        CheckEqual("配置里不存子目录",
            Core.Sync.WebDavClient.NormalizeInput("https://dav.jianguoyun.com/dav/CapsLockPlus/"),
            "https://dav.jianguoyun.com/dav/");
        // 用户自己的路径原样保留，不再多套一层
        CheckEqual("用户自定义路径不套子目录",
            Core.Sync.WebDavClient.NormalizeBase("https://dav.jianguoyun.com/dav/我的备份/"),
            "https://dav.jianguoyun.com/dav/我的备份/");

        /* 「目录还不存在」这个判断必须准。
         *
         * 它是"点测连接时自动建目录"这条修复的开关：
         * 判对了才会去打 MKCOL；判错了（比如把 401 也算进来）
         * 就会在密码错误时反复重试建目录，把真正的错误盖住。 */
        Check("404 判定为「目录不存在」",
            new Core.Sync.WebDavStatusException(
                System.Net.HttpStatusCode.NotFound, "x").IsMissingDirectory);
        Check("409 判定为「目录不存在」",
            new Core.Sync.WebDavStatusException(
                System.Net.HttpStatusCode.Conflict, "x").IsMissingDirectory);
        Check("401 不算「目录不存在」",
            !new Core.Sync.WebDavStatusException(
                System.Net.HttpStatusCode.Unauthorized, "x").IsMissingDirectory);
        Check("403 不算「目录不存在」",
            !new Core.Sync.WebDavStatusException(
                System.Net.HttpStatusCode.Forbidden, "x").IsMissingDirectory);

        Check("空地址不算错误（未启用）", Core.Sync.WebDavClient.Validate("") == null);
        Check("非法地址报错", Core.Sync.WebDavClient.Validate("not a url") != null);

        // PROPFIND 解析
        const string xml = """
        <?xml version="1.0"?>
        <D:multistatus xmlns:D="DAV:">
          <D:response>
            <D:href>/dav/CapsLockPlus/</D:href>
            <D:propstat><D:prop><D:resourcetype><D:collection/></D:resourcetype>
              <D:getlastmodified>Tue, 20 Oct 2026 05:00:00 GMT</D:getlastmodified>
            </D:prop></D:propstat>
          </D:response>
          <D:response>
            <D:href>/dav/CapsLockPlus/capslock-backup.json</D:href>
            <D:propstat><D:prop><D:resourcetype/>
              <D:getlastmodified>Tue, 20 Oct 2026 06:30:00 GMT</D:getlastmodified>
              <D:getcontentlength>2048</D:getcontentlength>
            </D:prop></D:propstat>
          </D:response>
        </D:multistatus>
        """;
        var files = Core.Sync.WebDavClient.ParsePropfind(xml);
        CheckEqual("目录项被跳过，只剩 1 个文件", files.Count, 1);
        CheckEqual("解析出文件名", files[0].Name, "capslock-backup.json");
        CheckEqual("解析出文件大小", files[0].Size, 2048L);

        // 单独验日期解析：中文区域下最容易踩"认不出 Mon/Oct"的坑
        bool dateOk = Core.Sync.WebDavClient.ParseHttpDate(
            "Tue, 20 Oct 2026 05:00:00 GMT", out var parsed);
        Check("HTTP 日期能被解析", dateOk, dateOk ? parsed.ToString() : "TryParseExact 失败");
        Check("解析出的年份正确", dateOk && parsed.Year == 2026, parsed.ToString());

        // 星期写错也要能解开（有些代理会发错的星期）
        bool wrongDayOk = Core.Sync.WebDavClient.ParseHttpDate(
            "Mon, 20 Oct 2026 05:00:00 GMT", out var parsed2);
        Check("星期写错的 HTTP 日期也能解析", wrongDayOk && parsed2.Year == 2026,
            wrongDayOk ? parsed2.ToString() : "解析失败");
        Check("空日期字符串安全返回 false",
            !Core.Sync.WebDavClient.ParseHttpDate("", out _));

        Check("解析出修改时间", files[0].Modified.Year == 2026, files[0].Modified.ToString());

        // 备份文件名格式
        string name = Core.Sync.SyncService.BackupName(new DateTime(2026, 10, 20, 13, 45, 9));
        CheckEqual("备份文件名格式", name, "KeyboardEnhance(2026-10-20 13-45-09).keyboardenhance");
    }

    /// <summary>
    /// 备份的命名、保留策略、合并恢复、导出脱敏。
    ///
    /// 这几条都是"看起来能用、但会悄悄毁掉用户数据"的地方：
    ///   * 保留策略算错 → 把唯一一份备份删了；
    ///   * 合并当成覆盖 → 丢掉本机独有的键位；
    ///   * 导出忘了脱敏 → 明文把网盘密码发给别人。
    /// </summary>
    private static void TestBackupPolicy()
    {
        var ext = Core.Sync.SyncService.Extension;
        CheckEqual("备份后缀是 .keyboardenhance", ext, ".keyboardenhance");
        Check("认得自己的备份名",
            Core.Sync.SyncService.IsBackupName("KeyboardEnhance(2026-10-20 13-45-09)" + ext));
        Check("也认得旧版 .clpbak",
            Core.Sync.SyncService.IsBackupName("capslock+(2026-10-20 13-45-09).clpbak"));
        Check("别的文件不算备份", !Core.Sync.SyncService.IsBackupName("settings.json"));

        // ---- 保留策略：份数上限 ----
        string dir = Path.Combine(AppPaths.DataDir, "selftest-prune");
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);

            var baseTime = new DateTime(2026, 10, 20, 12, 0, 0);
            for (int i = 0; i < 12; i++)
            {
                string n = Core.Sync.SyncService.BackupName(baseTime.AddMinutes(i));
                string p = Path.Combine(dir, n);
                File.WriteAllText(p, "x");
                File.SetLastWriteTime(p, baseTime.AddMinutes(i));
            }
            int removed = Core.Sync.SyncService.PruneFolder(dir, keep: 10, days: 0);
            var left = Core.Sync.SyncService.ListLocal(dir);
            CheckEqual("保留 10 份：清掉 2 份", removed, 2);
            CheckEqual("保留 10 份：剩 10 份", left.Count, 10);
            Check("保留的是最新的那批",
                left[0].Name.Contains("12-11-00"), left[0].Name);

            // ---- 保留策略：按天数清，但永远留最新 ----
            foreach (var f in Directory.GetFiles(dir)) File.Delete(f);
            var old = baseTime.AddDays(-30);
            string oldFile = Path.Combine(dir, Core.Sync.SyncService.BackupName(old));
            File.WriteAllText(oldFile, "x");
            File.SetLastWriteTime(oldFile, old);

            int removed2 = Core.Sync.SyncService.PruneFolder(dir, keep: 10, days: 7);
            CheckEqual("只有一份超期备份时不会被删空", removed2, 0);
            Check("超期但那唯一一份还在", File.Exists(oldFile));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }

        // ---- 合并恢复：远端覆盖同项、本地独有保留、单值设置保留本机 ----
        var local = DefaultSettings.Create();
        local.Sync.WebDavPass = "本机网盘密码";
        local.Sync.BackupPassword = "本机备份密码";
        var layer1 = local.Layers.First(l => l.Id == 1);
        layer1.Keys.Clear();
        layer1.Keys.Add(new Core.Settings.KeyBinding { Combo = "caps_q", Action = "qbar" });
        layer1.Keys.Add(new Core.Settings.KeyBinding { Combo = "caps_w", Action = "qbar" });

        var remote = DefaultSettings.Create();
        remote.Sync.WebDavPass = "别人的网盘密码";
        remote.Sync.BackupPassword = "别人的备份密码";
        var rl1 = remote.Layers.First(l => l.Id == 1);
        rl1.Keys.Clear();
        rl1.Keys.Add(new Core.Settings.KeyBinding { Combo = "caps_q", Action = "copy" });
        rl1.Keys.Add(new Core.Settings.KeyBinding { Combo = "caps_e", Action = "qbar" });

        var (merged, added, replaced, _) =
            Core.Sync.BackupMerge.Merge(local, remote);   // 令牌规则单独在 TestTokenInBackup 里验
        var ml1 = merged.Layers.First(l => l.Id == 1);
        var q = ml1.Keys.FirstOrDefault(k => k.Combo == "caps_q");

        Check("合并后同名组合键被远端覆盖", q?.Action == "copy", q?.Action ?? "缺失");
        Check("合并后远端独有的键被加进来", ml1.Keys.Any(k => k.Combo == "caps_e"));
        Check("合并后本地独有的键仍在", ml1.Keys.Any(k => k.Combo == "caps_w"));
        Check("合并计数：新增 1 项", added == 1, added.ToString());
        Check("合并计数：覆盖 1 项", replaced == 1, replaced.ToString());
        CheckEqual("合并不会把别人的网盘密码带进来",
            merged.Sync.WebDavPass, "本机网盘密码");
        CheckEqual("合并不会覆盖本机的备份密码",
            merged.Sync.BackupPassword, "本机备份密码");

        // ---- 导出脱敏：勾了"同步与备份设置"也不许带密码 ----
        var s = DefaultSettings.Create();
        s.Sync.WebDavUrl = "https://dav.example.com/dav/";
        s.Sync.WebDavUser = "me@example.com";
        s.Sync.WebDavPass = "SuperSecretDavPass";
        s.Sync.BackupPassword = "SuperSecretBackupPass";

        var (text, _) = Core.Sync.ExportImport.Build(s,
            Core.Sync.ExportImport.Groups.Select(g => g.Id));
        Check("导出里保留网盘地址", text.Contains("dav.example.com"));
        Check("导出里保留网盘用户名", text.Contains("me@example.com"));
        Check("导出绝不包含网盘密码", !text.Contains("SuperSecretDavPass"));
        Check("导出绝不包含备份密码", !text.Contains("SuperSecretBackupPass"));

        // 未勾选同步组时也顺手确认一下：没有 Sync 字段
        var (onlyMacros, _) = Core.Sync.ExportImport.Build(s, new[] { "macros" });
        Check("只导宏时不带同步设置", !onlyMacros.Contains("WebDavUrl"));
    }

    /// <summary>
    /// 备份一律加密：没设备份密码时必须拒绝，而不是偷偷明文落盘。
    /// </summary>
    private static void TestSyncPayload()
    {
        var s = DefaultSettings.Create();
        var svc = new Core.Sync.SyncService(() => s);

        // 没设密码 → 必须拒绝
        s.Sync.BackupPassword = "";
        bool refused = false;
        string why = "";
        try { svc.BuildBackupPayload(out _); }
        catch (InvalidOperationException e) { refused = true; why = e.Message; }
        Check("没设备份密码时拒绝备份", refused);
        Check("拒绝时说明了原因", why.Contains("备份密码"), why);

        // 设了密码 → 必须加密
        s.Sync.BackupPassword = "我的备份密码";
        s.Sync.WebDavPass = "本机网盘密码";
        string encPayload = svc.BuildBackupPayload(out bool enc2);
        Check("设置密码后备份被加密", enc2);
        Check("加密备份确实是密文", Core.Sync.CryptoBox.LooksEncrypted(encPayload));
        Check("密文里不含明文密钥", !encPayload.Contains("我的备份密码"));

        // 往返恢复（覆盖）
        var target = DefaultSettings.Create();
        target.Sync.BackupPassword = "我的备份密码";
        target.Sync.WebDavPass = "目标机器自己的密码";
        var svc2 = new Core.Sync.SyncService(() => target);
        var outcome = svc2.RestoreFromText(encPayload);
        Check("恢复后层结构完整", target.Layers.Count == 10);
        Check("恢复后触发键完整", target.Triggers.Count >= 1);
        CheckEqual("恢复方式记录为覆盖", outcome.Summary.Contains("覆盖") ? "覆盖" : "", "覆盖");

        /* 备份里带着"另一台机器"的网盘密码时，覆盖恢复也不能把它灌进来：
         * 那等于把同步目标换成别人的网盘，下次同步就把配置传过去了。 */
        CheckEqual("覆盖恢复不会带走备份里的网盘密码",
            target.Sync.WebDavPass, "目标机器自己的密码");
        CheckEqual("覆盖恢复不会覆盖本机的备份密码",
            target.Sync.BackupPassword, "我的备份密码");

        // 恢复时密码不对必须失败（而且是可识别的备份密码异常）
        var wrong = DefaultSettings.Create();
        wrong.Sync.BackupPassword = "错的密码";
        var svc3 = new Core.Sync.SyncService(() => wrong);
        bool failed = false;
        try { svc3.RestoreFromText(encPayload); }
        catch (Core.Sync.BackupPasswordException) { failed = true; }
        Check("密码不对时恢复失败", failed);

        // 旧密码加密的备份：换密码后仍能用当时的密码解开
        Check("用当时的密码能认出这份备份",
            Core.Sync.SyncService.CanDecrypt(encPayload, "我的备份密码"));
        Check("用错的密码认不出来",
            !Core.Sync.SyncService.CanDecrypt(encPayload, "错的密码"));
    }

    /// <summary>
    /// 导出/导入的**真实磁盘往返**：走 WritePlain / ReadPlain，
    /// 验证文件编码、路径、大小限制这些纯字符串测不到的部分。
    /// </summary>
    private static void TestExportImportOnDisk()
    {
        string dir = Path.Combine(AppPaths.DataDir, "selftest-export");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "roundtrip" + Core.Sync.ExportImport.Extension);

        try
        {
            var src = DefaultSettings.Create();
            src.Macros.Clear();
            src.Macros.Add(new Core.Settings.Macro
            {
                Id = "t1",
                Name = "测试宏",
                Steps = { new Core.Settings.MacroStep { Kind = "delay", DelayMs = 123 } },
            });

            // 导出「宏」这一组到真实文件
            int n = Core.Sync.ExportImport.WritePlain(file, src, new[] { "macros" });
            Check("导出写出了文件", File.Exists(file));
            Check("文件非空", new FileInfo(file).Length > 0, n.ToString());
            Check("导出的文件不是加密的",
                !Core.Sync.CryptoBox.LooksEncrypted(File.ReadAllText(file)));

            // 读回来
            var (groups, data, _) = Core.Sync.ExportImport.ReadPlain(file);
            CheckEqual("读回 1 个组", groups.Count, 1);
            CheckEqual("组名正确", groups[0], "macros");

            // 导入到另一份设置
            var dst = DefaultSettings.Create();
            dst.Macros.Clear();
            Core.Sync.ExportImport.Apply(dst, groups, data);
            CheckEqual("导入后宏数量正确", dst.Macros.Count, 1);
            CheckEqual("导入后宏名正确", dst.Macros[0].Name, "测试宏");
            CheckEqual("导入后宏步骤正确", dst.Macros[0].Steps.Count, 1);

            // 超过大小上限要拒：这里只验小文件能读通
            string small = Path.Combine(dir, "small.clpconf");
            File.WriteAllText(small, "{\"kind\":\"clp-config\",\"items\":[\"macros\"],\"data\":{\"Macros\":[]}}");
            var readBack = Core.Sync.ExportImport.ReadPlain(small);
            Check("再读一份小文件也正常", readBack.Groups.Count == 1);
            File.Delete(small);

            // 不是 JSON 的文件要给出人话错误
            string bad = Path.Combine(dir, "bad.clpconf");
            File.WriteAllText(bad, "这不是 JSON");
            bool rejected = false;
            try { Core.Sync.ExportImport.ReadPlain(bad); }
            catch (InvalidOperationException e) { rejected = e.Message.Contains("JSON"); }
            Check("非 JSON 文件被拒绝且说明原因", rejected);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }

        // 本地备份能真正落盘
        var s = DefaultSettings.Create();
        s.Sync.BackupPassword = "pw";
        var svc = new Core.Sync.SyncService(() => s);
        string payload = svc.BuildBackupPayload(out bool enc);
        Check("备份载荷已加密", enc);
        string path = svc.WriteLocalBackup(payload);
        Check("本地备份文件已写出", File.Exists(path));
        Check("备份文件扩展名正确",
            path.EndsWith(Core.Sync.SyncService.Extension, StringComparison.OrdinalIgnoreCase), path);
        Check("备份目录存在", Directory.Exists(Core.Sync.SyncService.BackupDir));
        Check("备份放在软件目录下的专属文件夹里",
            Path.GetFileName(Core.Sync.SyncService.BackupDir)
                .Contains("KeyboardEnhance", StringComparison.OrdinalIgnoreCase)
            || Core.Sync.SyncService.BackupDirIsFallback,
            Core.Sync.SyncService.BackupDir);

        // 别把自检产物留在用户的备份目录里
        try { File.Delete(path); } catch { }
    }

    /// <summary>
    /// 动作表按分类分组后，**每个分类下都要有可选项**（两级下拉才不会是空的）。
    /// </summary>
    private static void TestActionCategories()
    {
        var settings = DefaultSettings.Create();
        var ctx = new Core.Actions.ActionContext
        {
            Settings = () => settings,
            Clipboard = new Core.Actions.ClipboardService(),
            Windows = new Core.Actions.WindowActions(),
            WindowBindings = new Core.Actions.WindowBindingService(settings),
            Mouse = new Core.Actions.MouseService(),
            Notify = _ => { },
        };
        var cat = Core.Actions.ActionCatalog.Create(ctx);

        var categories = cat.Items.Select(i => i.Category).Distinct().ToList();
        Check("动作表有多个分类", categories.Count >= 5, categories.Count.ToString());
        Check("没有空分类",
            categories.All(c => cat.Items.Any(i => i.Category == c)));
        Check("分类名都不为空", categories.All(c => !string.IsNullOrWhiteSpace(c)));

        // 每个分类下的功能都要有标题和描述（界面上要显示）
        Check("所有动作都有标题", cat.Items.All(i => !string.IsNullOrWhiteSpace(i.Title)));
        Check("所有动作都有描述", cat.Items.All(i => i.Description != null));

        // 常用分类必须存在
        foreach (string want in new[] { "基础", "光标移动", "选择", "编辑", "宏", "窗口绑定" })
            Check($"分类「{want}」存在", categories.Contains(want),
                string.Join(",", categories));
    }

    /// <summary>
    /// 单实例逻辑。
    ///
    /// 这里曾经出过严重问题：提权重启时旧进程与新进程抢互斥体，
    /// 新进程误判“已有实例”而退出，导致两个进程都没了、钩子没装上，
    /// 表现就是“按任何键都没反应”。所以必须校验：
    ///   * 首次获取成功；
    ///   * 重复获取被正确判为“已有实例”；
    ///   * 释放后又能重新获取（= 重启流程不会互相卡死）。
    /// </summary>
    /// <summary>
    /// 「恢复默认」的数据层：只重置第 1 层、保留自定义组合键、其它层不动，
    /// 并且 <c>Keys</c> 与 <c>Layers[0].Keys</c> 必须始终是**同一个 List**。
    ///
    /// 背景：这段以前写成 <c>settings.Keys = preset</c>，把引用换掉了。
    /// 引擎与界面都按层读 <c>Layers[0].Keys</c>，于是：
    ///   · 点「恢复默认」键盘上什么都没变（看着像按钮坏了）；
    ///   · 落盘重读之后新预设被 NormalizeLayers 反向覆盖，彻底丢失。
    /// 下面几条专门盯着这个引用同一性和"只动第 1 层"的边界。
    /// </summary>
    private static void TestResetLayerOne()
    {
        var s = DefaultSettings.Create();
        var layer1 = s.Layers.First(l => l.Id == 1);
        var layer3 = s.Layers.First(l => l.Id == 3);

        // 弄乱：改一条默认键、加一条自定义组合键、在别的层加一条
        layer1.Keys.First(k => k.Combo == "caps_q").Action = "doNothing";
        layer1.Keys.Add(new KeyBinding { Combo = "caps_ctrl_alt_shift_f24", Action = "copy1", Note = "我的复制" });
        layer3.Keys.Add(new KeyBinding { Combo = "caps_z", Action = "undoRedo", Note = "第3层" });

        Check("重置前 Keys 与 Layers[0].Keys 是同一份 List",
            ReferenceEquals(s.Keys, layer1.Keys));

        DefaultSettings.ResetLayerOne(s, "capslox");

        Check("重置后 Keys 与 Layers[0].Keys 仍是同一份 List",
            ReferenceEquals(s.Keys, layer1.Keys),
            "换掉引用 = 引擎读不到新预设");

        CheckEqual("恢复默认：改过的默认键被恢复",
            layer1.Keys.First(k => k.Combo == "caps_q").Action, "qbar");

        Check("恢复默认：用户自己的组合键被保留",
            layer1.Keys.Any(k => k.Combo == "caps_ctrl_alt_shift_f24" && k.Note == "我的复制"),
            $"共 {layer1.Keys.Count} 条");

        Check("恢复默认：其它层完全不动",
            layer3.Keys.Any(k => k.Combo == "caps_z" && k.Note == "第3层"),
            $"第 3 层 {layer3.Keys.Count} 条");

        var dupes = layer1.Keys.GroupBy(k => k.Combo, StringComparer.OrdinalIgnoreCase)
                               .Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Check("恢复默认：不会留下重复组合键", dupes.Count == 0, string.Join(",", dupes));

        // 重置是幂等的：连点两次结果一致
        int countOnce = layer1.Keys.Count;
        DefaultSettings.ResetLayerOne(s, "capslox");
        Check("恢复默认可重复执行（幂等）", layer1.Keys.Count == countOnce,
            $"{countOnce} → {layer1.Keys.Count}");
    }

    private static void TestSingleInstance()
    {
        // 用一个独立名称测试，避免与真实运行的实例互相干扰
        var name = "CapsLockPlus.SelfTest." + Guid.NewGuid().ToString("N");
        var first = new Mutex(true, name, out bool createdFirst);
        Check("首次可获取单实例锁", createdFirst);

        var second = new Mutex(true, name, out bool createdSecond);
        Check("重复获取会被识别为已有实例", !createdSecond);
        try { second.Dispose(); } catch { }

        first.ReleaseMutex();
        first.Dispose();

        // 释放之后应当能重新获取——这正是提权重启能成功接管的前提
        var third = new Mutex(true, name, out bool createdThird);
        Check("释放后可重新获取（重启不卡死）", createdThird);
        try { third.ReleaseMutex(); third.Dispose(); } catch { }
    }
}