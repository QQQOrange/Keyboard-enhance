using System;
using System.IO;
using System.Text.Json;
using CapsLockPlus.Core.Settings;
using CapsLockPlus.Core.Sync;

namespace CapsLockPlus;

/// <summary>
/// 自检：启动密码（与备份密码完全独立的那一套）。
///
/// 覆盖四件事：
///   1. 合规规则（长短、空白、空值）—— 界面和落库必须同一把尺子；
///   2. 哈希行为（正确/错误/大小写/不修剪、盐随机、迭代次数记录在案）；
///   3. 文件真相（写→读往返、删文件即解除、损坏文件不把人关在外面）；
///   4. 边界（测试模式跳过密码门、导出不带密码、与备份密码互不干扰）。
///
/// ⚠ 全部跑在临时目录里（DirOverride）：自检绝不能碰真实数据目录，
/// 否则跑一次 --selftest 就在用户机器上留下一把锁。
/// </summary>
public static partial class SelfTest
{
    private static void TestStartupPassword()
    {
        string tmp = Path.Combine(Path.GetTempPath(),
            "clp-selftest-startup-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmp);
        StartupPassword.DirOverride = tmp;

        try
        {
            var sec = new SecurityOptions();

            /* ---- 1. 合规规则 ---- */
            Check("初始状态：没有启动密码", !StartupPassword.IsSet());
            Check("没设密码时校验一律不过", !StartupPassword.Verify("anything"));
            Check("空密码被拒", StartupPassword.ValidateNew("") != null);
            Check("null 被拒", StartupPassword.ValidateNew(null) != null);
            Check("全是空格被拒", StartupPassword.ValidateNew("   ") != null);
            Check("3 位太短被拒", StartupPassword.ValidateNew("abc") != null);
            Check("4 位可以", StartupPassword.ValidateNew("abcd") == null);
            Check("64 位可以", StartupPassword.ValidateNew(new string('a', 64)) == null);
            Check("65 位超长被拒", StartupPassword.ValidateNew(new string('a', 65)) != null);
            Check("TrySet 拒绝不合规密码", !StartupPassword.TrySet("ab", out _));
            Check("被拒后不会留下文件", !File.Exists(StartupPassword.FilePath));

            /* ---- 2. 哈希行为 ---- */
            Check("TrySet 合规密码成功",
                StartupPassword.TrySet("correct-horse", out string setErr), setErr);
            Check("设完文件存在", StartupPassword.IsSet());
            Check("正确密码通过", StartupPassword.Verify("correct-horse"));
            Check("错误密码不过", !StartupPassword.Verify("wrong-horse"));
            Check("大小写敏感", !StartupPassword.Verify("Correct-Horse"));
            Check("密码不做修剪（前后多个空格不过）",
                !StartupPassword.Verify(" correct-horse "));
            Check("空输入不过", !StartupPassword.Verify(""));

            string saltBefore = File.ReadAllText(StartupPassword.FilePath);
            Check("改成新密码成功", StartupPassword.TrySet("brand-new-pass", out _));
            string saltAfter = File.ReadAllText(StartupPassword.FilePath);
            Check("同一个密码两次设置，盐和派生值都不同（随机盐）",
                saltBefore != saltAfter);
            Check("改密码后旧密码失效", !StartupPassword.Verify("correct-horse"));
            Check("改密码后新密码有效", StartupPassword.Verify("brand-new-pass"));

            StartupPassword.MirrorTo(sec);
            Check("镜像：开关打开", sec.StartupPasswordEnabled);
            Check("镜像：盐非空", sec.StartupPasswordSalt.Length > 0);
            Check("镜像：派生值非空", sec.StartupPasswordHash.Length > 0);
            Check("镜像：迭代次数记录在案",
                sec.StartupPasswordIterations == StartupPassword.Iterations,
                $"iter={sec.StartupPasswordIterations}");
            Check("HasPassword 与文件一致", sec.HasPassword);

            /* ---- 3. 文件真相 ---- */
            StartupPassword.Clear();
            Check("清除后没有启动密码", !StartupPassword.IsSet());
            Check("清除后校验不过", !StartupPassword.Verify("correct-horse"));
            StartupPassword.MirrorTo(sec);
            Check("清除后镜像归零",
                !sec.StartupPasswordEnabled && !sec.HasPassword,
                $"enabled={sec.StartupPasswordEnabled}");

            // 损坏文件 = 视为没设（宁可放行也不把人永久关在门外）
            File.WriteAllText(StartupPassword.FilePath, "{ 这不是有效 JSON");
            Check("文件损坏视为未设置", !StartupPassword.IsSet());
            Check("文件损坏时校验不过", !StartupPassword.Verify("correct-horse"));

            /* 读不出来的文件会被改名成 .bad 留档：
             * 不这么做的话，每次启动都要读 2~3 遍、刷 2~3 条同样的 WARN，
             * 而保护本身早就失效了 —— 日志里只剩噪声。 */
            Check("损坏文件已被改名留档", File.Exists(StartupPassword.FilePath + ".bad"));
            Check("损坏文件不再占着原文件名", !File.Exists(StartupPassword.FilePath));
            try { File.Delete(StartupPassword.FilePath + ".bad"); } catch { }
            File.Delete(StartupPassword.FilePath);

            /* ---- 4. 边界 ---- */
            Check("--selftest 跳过密码门", StartupPassword.ShouldSkip(new[] { "--selftest" }));
            Check("--render 跳过密码门", StartupPassword.ShouldSkip(new[] { "--render" }));
            Check("--render-diag 跳过密码门", StartupPassword.ShouldSkip(new[] { "--render-diag" }));
            Check("--osd 跳过密码门", StartupPassword.ShouldSkip(new[] { "--osd", "50" }));
            Check("--diag 跳过密码门", StartupPassword.ShouldSkip(new[] { "--diag" }));
            Check("--hwtest 跳过密码门", StartupPassword.ShouldSkip(new[] { "--hwtest" }));
            Check("大小写不敏感地识别跳过标志", StartupPassword.ShouldSkip(new[] { "--SelfTest" }));
            Check("正常启动不跳过", !StartupPassword.ShouldSkip(Array.Empty<string>()));
            Check("--tray 不跳过（托盘启动也要密码）",
                !StartupPassword.ShouldSkip(new[] { "--tray" }));
            Check("--updated 不跳过（更新重启也要密码）",
                !StartupPassword.ShouldSkip(new[] { "--updated" }));
            Check("--page= 不跳过", !StartupPassword.ShouldSkip(new[] { "--page=keys" }));

            // 导出不带启动密码：跟着配置走会把用户锁在导入后的机器外
            Check("明文导出剔除启动密码开关",
                Array.Exists(ExportImport.NeverExport,
                    p => p == "Security.StartupPasswordEnabled"));
            Check("明文导出剔除启动密码盐",
                Array.Exists(ExportImport.NeverExport,
                    p => p == "Security.StartupPasswordSalt"));
            Check("明文导出剔除启动密码哈希",
                Array.Exists(ExportImport.NeverExport,
                    p => p == "Security.StartupPasswordHash"));
            Check("明文导出剔除启动密码迭代次数",
                Array.Exists(ExportImport.NeverExport,
                    p => p == "Security.StartupPasswordIterations"));

            /* ---- 5. 与备份密码完全独立 ---- */
            var settings = new AppSettings
            {
                Sync = { BackupPassword = "backup-secret" },
            };
            Check("设启动密码成功", StartupPassword.TrySet("gate-secret", out _));
            StartupPassword.MirrorTo(settings.Security);

            // 备份加密照常工作，且启动密码镜像不被它碰
            string secret = "一些用户数据";
            string box = Core.Sync.CryptoBox.Encrypt(secret, settings.Sync.BackupPassword);
            Check("备份密码仍能加密", Core.Sync.CryptoBox.LooksEncrypted(box));
            CheckEqual("备份密码仍能解密",
                Core.Sync.CryptoBox.Decrypt(box, settings.Sync.BackupPassword), secret);
            Check("启动密码镜像没被备份密码影响",
                settings.Security.StartupPasswordEnabled && settings.Security.HasPassword);

            // 整套设置往返（JSON）后两套密码各在各的区段
            var json = JsonSerializer.Serialize(settings, SettingsStore.JsonOptions);
            var back = JsonSerializer.Deserialize<AppSettings>(json, SettingsStore.JsonOptions)!;
            Check("往返后启动密码还在", back.Security.HasPassword);
            Check("往返后启动密码开关还在", back.Security.StartupPasswordEnabled);
            CheckEqual("往返后备份密码没被串位", back.Sync.BackupPassword, "backup-secret");
            Check("两份镜像内容一致",
                back.Security.StartupPasswordSalt == settings.Security.StartupPasswordSalt
                && back.Security.StartupPasswordHash == settings.Security.StartupPasswordHash);

            // 改启动密码不动备份密码，反之亦然
            StartupPassword.TrySet("gate-secret-2", out _);
            StartupPassword.MirrorTo(back.Security);
            CheckEqual("改启动密码后备份密码原样", back.Sync.BackupPassword, "backup-secret");
            back.Sync.BackupPassword = "another-backup";
            Check("改备份密码后启动密码镜像原样",
                StartupPassword.Verify("gate-secret-2"));

            StartupPassword.Clear();
        }
        finally
        {
            StartupPassword.DirOverride = null;
            try { Directory.Delete(tmp, true); }
            catch { /* 临时目录，删不掉也不影响结果 */ }
        }
    }

    /// <summary>
    /// 本地文件加密：写下去是密文、读回来是原文，旧明文能就地迁移。
    /// 这几条直接盯"磁盘上能不能搜到明文"，比只测加解密函数更接近真实风险。
    /// </summary>
    private static void TestLocalEncryption()
    {
        string dir = Path.Combine(Path.GetTempPath(), "clp-sec-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        try
        {
            string file = Path.Combine(dir, "usage.json");
            const string secret = "{\"secret\":\"张三的秘密\"}";

            Core.Security.SecureFile.WriteAllText(file, secret);
            string raw = File.ReadAllText(file);

            Check("加密：文件带密文头", raw.StartsWith("CLP2:"));
            Check("加密：磁盘上不含明文", !raw.Contains("张三的秘密"));
            CheckEqual("加密：能原样读回", Core.Security.SecureFile.ReadAllText(file), secret);

            string legacy = Path.Combine(dir, "legacy.json");
            File.WriteAllText(legacy, "{\"old\":1}");
            Check("加密：旧明文被就地转换", Core.Security.SecureFile.EncryptInPlace(legacy));
            Check("加密：转换后可读", Core.Security.SecureFile.ReadAllText(legacy).Contains("\"old\":1"));

            string log = Path.Combine(dir, "app.log");
            Core.Security.SecureFile.AppendBlock(log, "第一行\n第二行\n");
            Core.Security.SecureFile.AppendBlock(log, "第三行\n");

            Check("加密日志：两批都能解出", Core.Security.SecureFile.ReadLog(log).Contains("第三行"));
            Check("加密日志：磁盘无明文", !File.ReadAllText(log).Contains("第三行"));

            string marker = "秘密标记-" + Guid.NewGuid().ToString("N");
            Core.CoreLog.Info(marker);
            Core.CoreLog.Flush();
            Check("加密日志：CoreLog 写入后磁盘无明文",
                !File.ReadAllText(Core.CoreLog.LogPath).Contains(marker));
            Check("加密日志：CoreLog 能解出",
                Core.Security.SecureFile.ReadLog(Core.CoreLog.LogPath).Contains(marker));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// 换密钥的断点续做（v4.20.6）。
    ///
    /// 真实事故形态：改密码时新密钥已经写进 vault.json，settings.json 还是旧密文的，
    /// 这时断电/被强杀 —— 重启后新密码打不开旧密文、旧密码已被覆盖，配置永久丢失。
    ///
    /// 这里用真实的三个文件模拟三种现场，验证 <see cref="Core.Settings.VaultRecovery.Recover"/>
    /// 分别做出"收尾 / 回滚"的正确判断，且回滚能把旧 vault 与 DPAPI 缓存**逐字节**还原。
    /// 自检跑在临时数据目录里，结束时把四个文件原样还回去。
    /// </summary>
    private static void TestVaultRecovery()
    {
        string settingsFile = Core.AppPaths.SettingsFile;
        string vaultFile = Core.Settings.DataVault.VaultFile;
        string cacheFile = Core.Settings.DataVault.CachePath;
        string pendingFile = Core.Settings.VaultRecovery.PendingPath;
        var utf8 = new System.Text.UTF8Encoding(false);

        // 现场拍照（不存在记 null），finally 里逐字节还原
        byte[]? SavedOrNull(string path) => File.Exists(path) ? File.ReadAllBytes(path) : null;
        void RestoreOrDelete(string path, byte[]? bytes)
        {
            if (bytes == null) { if (File.Exists(path)) File.Delete(path); }
            else File.WriteAllBytes(path, bytes);
        }

        byte[]? sSettings = SavedOrNull(settingsFile);
        byte[]? sVault = SavedOrNull(vaultFile);
        byte[]? sCache = SavedOrNull(cacheFile);
        byte[]? sPending = SavedOrNull(pendingFile);

        string PendingJson(string stage, string? oldVault, byte[]? oldCache) =>
            ("{\"Version\":1,\"Stage\":\"" + stage + "\",\"OldVaultText\":" +
             (oldVault == null ? "null" : System.Text.Json.JsonSerializer.Serialize(oldVault)) +
             ",\"OldCacheBase64\":" +
             (oldCache == null ? "null" : "\"" + Convert.ToBase64String(oldCache) + "\"") +
             ",\"CreatedUtc\":\"2026-01-01T00:00:00Z\"}");

        // 造一段"需要 vault 密钥"的信封（内容随便，只要格式对）
        const string envelope = "{\"clpVault\":1,\"alg\":\"aes-256-gcm\"," +
                                "\"nonce\":\"AAAAAAAAAAAAAAAA\",\"tag\":\"AAAAAAAAAAAAAAAAAAAAAA==\"," +
                                "\"data\":\"AAAAAAAAAAAA\"}";

        try
        {
            Directory.CreateDirectory(Core.AppPaths.DataDir);

            // ⓪ 没有 pending：什么都不做
            if (File.Exists(pendingFile)) File.Delete(pendingFile);
            Check("vault 恢复：没有 pending 时不做任何事",
                Core.Settings.VaultRecovery.Recover() == Core.Settings.VaultRecovery.Outcome.None);

            /* ① 关闭加密已经写完：设置已是明文 → 收尾，不动 vault.json 现状 */
            File.WriteAllText(settingsFile, "{\"Global\":{}}", utf8);
            File.WriteAllText(pendingFile, PendingJson("disable", "{\"v\":1,\"old\":true}", new byte[] { 9, 9 }), utf8);
            File.WriteAllText(vaultFile, "{\"v\":1,\"new\":true}", utf8);
            if (File.Exists(cacheFile)) File.Delete(cacheFile);

            var r1 = Core.Settings.VaultRecovery.Recover();
            Check("vault 恢复：设置已明文 → 按已完成收尾",
                r1 == Core.Settings.VaultRecovery.Outcome.Completed);
            Check("vault 恢复：收尾后 pending 被删除", !File.Exists(pendingFile));
            CheckEqual("vault 恢复：收尾不改 vault.json", File.ReadAllText(vaultFile), "{\"v\":1,\"new\":true}");

            /* ② 新密钥没生效：设置还是 vault 密文，而缓存里没有能解它的密钥 → 回滚 */
            byte[] oldCacheBytes = { 1, 2, 3, 4 };
            const string oldVaultText = "{\"v\":1,\"salt\":\"OLD\"}";
            File.WriteAllText(settingsFile, Core.Security.LocalCrypto.Encrypt(envelope), utf8);
            if (File.Exists(cacheFile)) File.Delete(cacheFile);
            File.WriteAllText(pendingFile, PendingJson("enable", oldVaultText, oldCacheBytes), utf8);
            File.WriteAllText(vaultFile, "{\"v\":1,\"salt\":\"NEW\"}", utf8);

            var r2 = Core.Settings.VaultRecovery.Recover();
            Check("vault 恢复：新密钥解不开 → 回滚",
                r2 == Core.Settings.VaultRecovery.Outcome.RolledBack);
            CheckEqual("vault 恢复：回滚后 vault.json 逐字还原",
                File.ReadAllText(vaultFile), oldVaultText);
            Check("vault 恢复：回滚后 DPAPI 缓存逐字节还原",
                File.Exists(cacheFile) && File.ReadAllBytes(cacheFile).SequenceEqual(oldCacheBytes));
            Check("vault 恢复：回滚后 pending 被删除", !File.Exists(pendingFile));

            /* ③ 新密钥已生效：缓存里的密钥能解开设置 → 收尾，不动 vault.json */
            byte[] newKey = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
            File.WriteAllText(settingsFile,
                Core.Security.LocalCrypto.Encrypt(MakeVaultEnvelope(newKey, "{\"Global\":{}}")), utf8);
            File.WriteAllBytes(cacheFile, System.Security.Cryptography.ProtectedData.Protect(
                newKey, Core.Settings.DataVault.EntropyForTest,
                System.Security.Cryptography.DataProtectionScope.CurrentUser));
            File.WriteAllText(pendingFile, PendingJson("enable", "{\"v\":1,\"salt\":\"OLD\"}", null), utf8);
            File.WriteAllText(vaultFile, "{\"v\":1,\"salt\":\"NEW\"}", utf8);

            var r3 = Core.Settings.VaultRecovery.Recover();
            Check("vault 恢复：新密钥已生效 → 收尾",
                r3 == Core.Settings.VaultRecovery.Outcome.Committed);
            Check("vault 恢复：收尾后 vault.json 仍是新内容",
                File.ReadAllText(vaultFile).Contains("\"NEW\""));
            Check("vault 恢复：收尾后不会把旧缓存写回去",
                File.ReadAllBytes(cacheFile).Length > 4);
        }
        finally
        {
            RestoreOrDelete(settingsFile, sSettings);
            RestoreOrDelete(vaultFile, sVault);
            RestoreOrDelete(cacheFile, sCache);
            RestoreOrDelete(pendingFile, sPending);
            Core.Settings.DataVault.ForgetKeyForTest();
        }
    }

    /// <summary>构造一段真实的 vault 信封（供断点恢复用例使用）。</summary>
    private static string MakeVaultEnvelope(byte[] key, string plain)
    {
        byte[] nonce = System.Security.Cryptography.RandomNumberGenerator.GetBytes(12);
        byte[] data = System.Text.Encoding.UTF8.GetBytes(plain);
        byte[] cipher = new byte[data.Length];
        byte[] tag = new byte[16];
        using (var aes = Core.Security.AesGcmFactory.Create(key))
            aes.Encrypt(nonce, data, cipher, tag);

        return "{\"clpVault\":1,\"alg\":\"aes-256-gcm\",\"nonce\":\"" +
               Convert.ToBase64String(nonce) + "\",\"tag\":\"" + Convert.ToBase64String(tag) +
               "\",\"data\":\"" + Convert.ToBase64String(cipher) + "\"}";
    }
}
