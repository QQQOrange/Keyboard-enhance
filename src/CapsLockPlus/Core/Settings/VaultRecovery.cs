using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CapsLockPlus.Core.Security;

namespace CapsLockPlus.Core.Settings;

/// <summary>
/// 换密钥 / 关加密的「断点续做」。
///
/// ## 为什么需要它
///
/// `vault.json`（密钥元数据）与 `settings.json`（用那把密钥加密的配置）是**两个文件**，
/// 它们没法在一次原子操作里同时更新。于是"启用加密 / 改密码 / 关闭加密"天然存在一个
/// 中间态：新密钥已经写进 vault.json，而 settings.json 还是旧密钥的密文 ——
/// 这时断电、蓝屏、被强杀，重启后新密钥打不开旧密文，旧密钥又已经被覆盖，
/// **配置永久读不出来**。
///
/// 进程内已经有两段式回滚（Enable → Save → Commit/Rollback），但那只覆盖"Save 抛异常"，
/// 覆盖不了"进程在中途直接消失"。所以这里再补一层落盘保护：
///
///   1. 改 vault 之前，先把**旧 vault.json 内容 + 旧 DPAPI 缓存**写进 `vault.pending.json`；
///   2. 操作正常结束（Commit / Rollback）就删掉 pending；
///   3. 下次启动时（<see cref="Recover"/>）如果发现 pending，就判断"新密钥到底生效了没有"：
///        · settings.json 已经是明文 → 关加密已完成，收尾；
///        · 新密钥能解开 settings.json → 换密钥已完成，收尾；
///        · 新密钥解不开 → 回滚到 pending 里的旧密钥，让"旧密钥 + 旧密文"重新一致。
///
/// pending 里**不含密码**，只有 vault.json 的公开元数据与 DPAPI 包裹过的缓存，
/// 和 vault.cache 本身是同一级别的敏感度。
/// </summary>
public static class VaultRecovery
{
    /// <summary>一次恢复尝试的结论（日志与自检用）。</summary>
    public enum Outcome
    {
        /// <summary>没有未完成的操作。</summary>
        None,

        /// <summary>新密钥已经生效，只是没来得及清理现场。</summary>
        Committed,

        /// <summary>新密钥没生效，已回滚到操作前的密钥。</summary>
        RolledBack,

        /// <summary>关闭加密已经写完（设置已是明文），收尾即可。</summary>
        Completed,

        /// <summary>无法安全判断（本机密钥也坏了等），现场原样保留，交给只读保护。</summary>
        Failed,
    }

    public static string PendingPath => Path.Combine(AppPaths.DataDir, "vault.pending.json");

    private sealed class PendingState
    {
        public int Version { get; set; } = 1;
        public string Stage { get; set; } = "";          // enable / disable
        public string? OldVaultText { get; set; }
        public string? OldCacheBase64 { get; set; }
        public string CreatedUtc { get; set; } = "";
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // ---------------- 写 / 清 ----------------

    /// <summary>
    /// 在改动 vault.json **之前**调用：把当前（旧）密钥状态落盘。
    /// 写不进去不阻断主流程（只是失去断电保护），但会记一条 WARN。
    /// </summary>
    internal static void WritePending(string stage)
    {
        try
        {
            var state = new PendingState
            {
                Stage = stage,
                OldVaultText = File.Exists(DataVault.VaultFile)
                    ? File.ReadAllText(DataVault.VaultFile, Encoding.UTF8)
                    : null,
                OldCacheBase64 = File.Exists(DataVault.CachePath)
                    ? Convert.ToBase64String(File.ReadAllBytes(DataVault.CachePath))
                    : null,
                CreatedUtc = DateTime.UtcNow.ToString("o"),
            };

            string tmp = PendingPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(state, JsonOpts), new UTF8Encoding(false));
            File.Move(tmp, PendingPath, true);
        }
        catch (Exception ex)
        {
            CoreLog.Warn("[加密] 写 vault.pending 失败（本次换密钥没有断电保护）：" + ex.Message);
        }
    }

    /// <summary>操作正常结束后清理现场。删不掉也只是下次启动多做一次无害的判断。</summary>
    public static void ClearPending()
    {
        try
        {
            if (File.Exists(PendingPath)) File.Delete(PendingPath);
            string tmp = PendingPath + ".tmp";
            if (File.Exists(tmp)) File.Delete(tmp);
        }
        catch (Exception ex)
        {
            CoreLog.Warn("[加密] 清理 vault.pending 失败：" + ex.Message);
        }
    }

    /// <summary>是否有未完成的换密钥操作（界面/诊断用）。</summary>
    public static bool HasPending
    {
        get
        {
            try { return File.Exists(PendingPath); }
            catch { return false; }
        }
    }

    // ---------------- 启动时恢复 ----------------

    /// <summary>
    /// 启动时调用（必须在读取设置之前）。幂等：没有 pending 时只是一次 File.Exists。
    /// </summary>
    public static Outcome Recover()
    {
        try
        {
            if (!File.Exists(PendingPath)) return Outcome.None;

            PendingState? state;
            try
            {
                state = JsonSerializer.Deserialize<PendingState>(
                    File.ReadAllText(PendingPath, Encoding.UTF8), JsonOpts);
            }
            catch (Exception ex)
            {
                /* pending 本身体坏了：不能据此回滚（那可能把好状态改坏），
                 * 也不能留着——留着每次启动都会走这条路。改名存档。 */
                CoreLog.Error("[加密] vault.pending 无法解析，已另存为 .broken，不再据此回滚", ex);
                TryMoveAside();
                return Outcome.Failed;
            }

            if (state == null)
            {
                TryMoveAside();
                return Outcome.Failed;
            }

            /* 这份 settings.json 现在还依赖旧密钥吗？
             * 外层是本机密钥（CLP2），先剥掉；剩下的才是 vault 信封。 */
            string settingsRaw = File.Exists(AppPaths.SettingsFile)
                ? File.ReadAllText(AppPaths.SettingsFile, Encoding.UTF8)
                : "";

            string? inner = settingsRaw;
            if (LocalCrypto.IsEncrypted(inner))
            {
                inner = LocalCrypto.TryDecrypt(inner);
                if (inner == null)
                {
                    /* 本机密钥都解不开：什么都判断不了。保留现场，让上层进入只读保护。 */
                    CoreLog.Error("[加密] 本机密钥无法解密设置，暂不处理未完成的密钥切换（现场已保留）");
                    return Outcome.Failed;
                }
            }

            bool needsVaultKey = !string.IsNullOrWhiteSpace(inner) && DataVault.LooksEncrypted(inner);

            if (!needsVaultKey)
            {
                /* 设置已是明文（关闭加密写完）或文件不存在：
                 * 新状态已经达成，只要把现场清掉。 */
                ClearPending();
                CoreLog.Info($"[加密] 发现未完成的「{StageName(state.Stage)}」：设置已是明文，按已完成收尾");
                return Outcome.Completed;
            }

            byte[]? cached = DataVault.ReadCachedKeyForRecovery();
            if (cached != null && DataVault.CanDecryptEnvelope(cached, inner!))
            {
                ClearPending();
                CoreLog.Info($"[加密] 发现未完成的「{StageName(state.Stage)}」：新密钥已生效，收尾");
                return Outcome.Committed;
            }

            /* 新密钥打不开设置 → 当时的写入没完成 → 回滚到旧密钥。 */
            RestoreOldState(state);
            ClearPending();
            CoreLog.Warn($"[加密] 发现未完成的「{StageName(state.Stage)}」，新密钥打不开设置 —— " +
                         "已回滚到操作前的密钥，配置没有丢");
            return Outcome.RolledBack;
        }
        catch (Exception ex)
        {
            CoreLog.Error("[加密] 恢复未完成的密钥切换失败（现场保留，下次启动再试）", ex);
            return Outcome.Failed;
        }
    }

    private static string StageName(string stage) => stage switch
    {
        "enable" => "启用加密 / 改密码",
        "disable" => "关闭加密",
        _ => string.IsNullOrWhiteSpace(stage) ? "未知操作" : stage,
    };

    private static void RestoreOldState(PendingState state)
    {
        string vaultTmp = DataVault.VaultFile + ".tmp";
        if (string.IsNullOrEmpty(state.OldVaultText))
        {
            if (File.Exists(DataVault.VaultFile)) File.Delete(DataVault.VaultFile);
        }
        else
        {
            File.WriteAllText(vaultTmp, state.OldVaultText!, new UTF8Encoding(false));
            File.Move(vaultTmp, DataVault.VaultFile, true);
        }

        if (string.IsNullOrEmpty(state.OldCacheBase64))
        {
            if (File.Exists(DataVault.CachePath)) File.Delete(DataVault.CachePath);
        }
        else
        {
            File.WriteAllBytes(DataVault.CachePath, Convert.FromBase64String(state.OldCacheBase64!));
        }

        /* 内存里的密钥可能是"新"的：清掉，让上层重新用缓存/密码解锁。 */
        DataVault.ForgetKeyForTest();
    }

    private static void TryMoveAside()
    {
        try
        {
            string broken = PendingPath + ".broken-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            File.Move(PendingPath, broken, true);
            CoreLog.Warn("[加密] 已把损坏的 vault.pending 另存为：" + broken);
        }
        catch (Exception ex)
        {
            CoreLog.Warn("[加密] 另存损坏的 vault.pending 失败：" + ex.Message);
        }
    }
}
