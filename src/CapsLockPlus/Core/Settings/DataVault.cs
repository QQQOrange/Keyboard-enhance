using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CapsLockPlus.Core.Settings;

/// <summary>
/// 本地数据的静态加密（"备份密码"一设，磁盘上就不再有明文用户数据）。
///
/// 为什么需要它：设置文件里躺着搜索记录、常用邮箱地址、宏脚本、
/// 窗口绑定路径这些隐私内容，之前是明文 JSON，任何人拿到这个文件
/// （共享电脑、备份盘、云同步盘）都能直接读。
///
/// 方案（兼顾安全与可用）：
///   · 数据用 AES-256-GCM 加密，每条记录带随机 nonce，篡改会被认证标签抓出来；
///   · 密钥由**备份密码**经 PBKDF2-HMAC-SHA256（60 万次）派生 —— 密码真的参与了加密；
///   · 派生结果用 Windows DPAPI 缓存（<c>vault.cache</c>，绑定当前用户），
///     所以日常启动**不需要每次输密码**；
///   · 缓存丢了（换机器 / 换用户 / 手动删了）才需要输一次密码。
///
/// 这样文件拷到别的电脑上既解不开（DPAPI + 密码），也不会因为
/// 忘记"启动要输密码"而天天被拦一次。
/// </summary>
public static class DataVault
{
    private const int SaltLen = 16;
    private const int NonceLen = 12;
    private const int TagLen = 16;
    private const int KeyLen = 32;
    private const int Iterations = 600_000;
    private const string Magic = "clpVault";

    /// <summary>DPAPI 附加熵：同一用户下别的程序也解不开我们的缓存。</summary>
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("CapsLockPlus.DataVault.v1");

    private static readonly object Gate = new();
    private static byte[]? _key;

    public static string VaultFile => Path.Combine(AppPaths.DataDir, "vault.json");
    private static string CacheFile => Path.Combine(AppPaths.DataDir, "vault.cache");

    /// <summary>是否已启用加密（存在 vault.json）。</summary>
    public static bool IsEnabled => File.Exists(VaultFile);

    /// <summary>当前会话是否已拿到密钥。</summary>
    public static bool IsUnlocked { get { lock (Gate) return _key != null; } }

    /// <summary>这段文本是不是加密后的信封。</summary>
    public static bool LooksEncrypted(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        // 加密后是紧凑的 JSON，明文设置的第一个非空白字符是 '{'，
        // 快速判断能省掉绝大多数 JSON 解析
        if (!text.TrimStart().StartsWith('{')) return false;
        try
        {
            var node = JsonNode.Parse(text);
            return node?[Magic]?.GetValue<int>() == 1;
        }
        catch { return false; }
    }

    // ---------------- 密钥生命周期 ----------------

    /// <summary>
    /// 启用加密：用密码派生密钥，写 vault.json 与 DPAPI 缓存。
    /// 调用方负责在此之前把设置以加密形式重新保存一次。
    /// </summary>
    public static void Enable(string password)
    {
        if (string.IsNullOrWhiteSpace(password))
            throw new ArgumentException("密码不能为空", nameof(password));

        lock (Gate)
        {
            Directory.CreateDirectory(AppPaths.DataDir);

            byte[] salt = RandomNumberGenerator.GetBytes(SaltLen);
            byte[] key = Derive(password, salt, Iterations);

            var meta = new JsonObject
            {
                ["v"] = 1,
                ["kdf"] = "pbkdf2-sha256",
                ["iter"] = Iterations,
                ["salt"] = Convert.ToBase64String(salt),
                ["check"] = Convert.ToBase64String(CheckOf(key)),
            };
            File.WriteAllText(VaultFile, meta.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                Encoding.UTF8);

            _key = key;
            WriteCache(key);
        }
    }

    /// <summary>
    /// 关闭加密：删掉 vault.json 与缓存，清空内存密钥。
    /// 调用方负责把设置改存成明文。
    /// </summary>
    public static void Disable()
    {
        lock (Gate)
        {
            _key = null;
            TryDelete(VaultFile);
            TryDelete(CacheFile);
        }
    }

    /// <summary>用密码解锁。密码不对返回 false（不会覆盖已有密钥）。</summary>
    public static bool UnlockWithPassword(string password)
    {
        lock (Gate)
        {
            if (!TryReadMeta(out var salt, out var iter, out var check)) return false;

            byte[] key = Derive(password, salt, iter);
            if (!CryptographicOperations.FixedTimeEquals(CheckOf(key), check)) return false;

            _key = key;
            WriteCache(key);
            return true;
        }
    }

    /// <summary>
    /// 删掉设置文件旁边可能残留的**明文**副本（`.bak` / `.broken` / `.tmp`）。
    ///
    /// 为什么必须做：`.bak` 是"上次保存前的副本"。启用加密之前写下的那份就是明文，
    /// 留在同一个目录里等于把加密白做了 —— 而且它长得和正牌设置一模一样，
    /// 用户拷走整个文件夹时根本不会注意到。
    ///
    /// 只删确认是明文 JSON 的那些；已经是密文的副本保留（那是有效的回退点）。
    /// 返回删了几个。
    /// </summary>
    public static int PurgePlaintextCopies(string settingsFile)
    {
        int removed = 0;
        foreach (string suffix in new[] { ".bak", ".broken", ".tmp" })
        {
            try
            {
                string path = settingsFile + suffix;
                if (!File.Exists(path)) continue;

                string text = File.ReadAllText(path);
                if (LooksEncrypted(text)) continue;          // 密文副本留着
                if (!text.TrimStart().StartsWith('{')) continue;   // 不是 JSON，别乱删

                File.Delete(path);
                removed++;
                CoreLog.Info($"[加密] 已删除明文副本 {Path.GetFileName(path)}");
            }
            catch (Exception ex)
            {
                CoreLog.Warn("[加密] 清理明文副本失败：" + ex.Message);
            }
        }
        return removed;
    }

    /// <summary>
    /// 尝试用 DPAPI 缓存解锁（日常启动走这条）。
    /// 缓存不存在 / 换了用户 / 换机器 → 返回 false，由上层提示输密码。
    /// </summary>
    public static bool TryUnlockCached()
    {
        lock (Gate)
        {
            if (!IsEnabled) return false;
            if (_key != null) return true;

            try
            {
                if (!File.Exists(CacheFile)) return false;
                byte[] wrapped = File.ReadAllBytes(CacheFile);
                byte[] key = ProtectedData.Unprotect(wrapped, Entropy, DataProtectionScope.CurrentUser);
                if (key.Length != KeyLen) return false;
                _key = key;
                return true;
            }
            catch (Exception ex)
            {
                CoreLog.Warn("[加密] 缓存解锁失败（需要输密码）：" + ex.Message);
                return false;
            }
        }
    }

    /// <summary>
    /// 换密码：用新密码重新派生密钥并覆盖 vault.json。
    /// 必须在**旧密钥仍可用于读写**的时候调用 —— 调用方先换、再立刻 Save，
    /// Save 会用新密钥加密。
    /// </summary>
    public static void ChangePassword(string newPassword) => Enable(newPassword);

    // ---------------- 加解密 ----------------

    /// <summary>加密一段文本；未启用加密时原样返回。</summary>
    public static string Encrypt(string plain)
    {
        lock (Gate)
        {
            if (_key == null) return plain;

            byte[] nonce = RandomNumberGenerator.GetBytes(NonceLen);
            byte[] plainBytes = Encoding.UTF8.GetBytes(plain);
            byte[] cipher = new byte[plainBytes.Length];
            byte[] tag = new byte[TagLen];

            using var aes = new AesGcm(_key.AsSpan());
            aes.Encrypt(nonce, plainBytes, cipher, tag);

            var env = new JsonObject
            {
                [Magic] = 1,
                ["alg"] = "aes-256-gcm",
                ["nonce"] = Convert.ToBase64String(nonce),
                ["tag"] = Convert.ToBase64String(tag),
                ["data"] = Convert.ToBase64String(cipher),
            };
            return env.ToJsonString();
        }
    }

    /// <summary>
    /// 解密。未启用加密 / 不是信封格式时原样返回。
    /// 密钥不对或内容被篡改时返回 null（调用方必须当作"读不到"处理，
    /// 绝不能拿 null 当空配置去覆盖原文件）。
    /// </summary>
    public static string? Decrypt(string text)
    {
        lock (Gate)
        {
            if (!LooksEncrypted(text)) return text;
            if (_key == null) return null;

            try
            {
                var node = JsonNode.Parse(text)!;
                byte[] nonce = Convert.FromBase64String(node["nonce"]!.GetValue<string>());
                byte[] tag = Convert.FromBase64String(node["tag"]!.GetValue<string>());
                byte[] data = Convert.FromBase64String(node["data"]!.GetValue<string>());

                byte[] plain = new byte[data.Length];
                using var aes = new AesGcm(_key.AsSpan());
                aes.Decrypt(nonce, data, tag, plain);
                return Encoding.UTF8.GetString(plain);
            }
            catch (Exception ex)
            {
                CoreLog.Error("解密本地数据失败", ex);
                return null;
            }
        }
    }

    /// <summary>
    /// 备份密码是否真的能把数据解回来（用于界面提示"密码已生效"）。
    /// 只校验密钥能否派生并匹配校验值，不动数据。
    /// </summary>
    public static bool VerifyPassword(string password)
    {
        lock (Gate)
        {
            if (!IsEnabled) return true;
            if (!TryReadMeta(out var salt, out var iter, out var check)) return false;
            return CryptographicOperations.FixedTimeEquals(CheckOf(Derive(password, salt, iter)), check);
        }
    }

    // ---------------- 内部 ----------------

    private static byte[] Derive(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations,
            HashAlgorithmName.SHA256, KeyLen);

    /// <summary>密钥校验值（只存前 8 字节，够用且不泄露密钥）。</summary>
    private static byte[] CheckOf(byte[] key)
    {
        byte[] mac = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes("clp-vault-check"));
        return mac[..8];
    }

    private static bool TryReadMeta(out byte[] salt, out int iterations, out byte[] check)
    {
        salt = Array.Empty<byte>();
        iterations = Iterations;
        check = Array.Empty<byte>();
        try
        {
            if (!File.Exists(VaultFile)) return false;
            var node = JsonNode.Parse(File.ReadAllText(VaultFile, Encoding.UTF8));
            if (node == null) return false;
            salt = Convert.FromBase64String(node["salt"]!.GetValue<string>());
            iterations = node["iter"]?.GetValue<int>() ?? Iterations;
            check = Convert.FromBase64String(node["check"]!.GetValue<string>());
            return salt.Length > 0 && check.Length > 0;
        }
        catch (Exception ex)
        {
            CoreLog.Warn("[加密] vault.json 读取失败：" + ex.Message);
            return false;
        }
    }

    private static void WriteCache(byte[] key)
    {
        try
        {
            byte[] wrapped = ProtectedData.Protect(key, Entropy, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(CacheFile, wrapped);
        }
        catch (Exception ex)
        {
            // 缓存写不了不影响本次会话，只是下次启动要输密码
            CoreLog.Warn("[加密] 密钥缓存写入失败：" + ex.Message);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { CoreLog.Warn($"[加密] 删除 {Path.GetFileName(path)} 失败：" + ex.Message); }
    }

    /// <summary>仅供自检：强制清空内存密钥（模拟"重启"）。</summary>
    internal static void ForgetKeyForTest()
    {
        lock (Gate) _key = null;
    }

    /// <summary>
    /// 仅供自检：直接注入内存密钥，不碰磁盘上的 vault.json。
    /// 用于验证加解密算法本身，又不会污染用户真实数据。
    /// </summary>
    internal static void SetKeyForTest(byte[] key)
    {
        lock (Gate) _key = key;
    }

    /// <summary>仅供自检：当前是否持有密钥。</summary>
    internal static bool HasKeyForTest => _key != null;
}
