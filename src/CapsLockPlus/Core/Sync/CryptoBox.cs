using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CapsLockPlus.Core;

namespace CapsLockPlus.Core.Sync;

/// <summary>
/// 备份加密。
///
/// 设计参照 QuickSearch 的 crypto.rs，格式几乎一致：
///   * 密钥派生用口令 + 随机盐；
///   * 内容用 AEAD（认证加密），密文被改一个字节就解不开；
///   * **KDF 参数写进文件**，将来调参也能解开老备份。
///
/// 与 QuickSearch 的唯一区别：KDF 用 PBKDF2-HMAC-SHA256 而不是 Argon2id。
/// 原因是 .NET 没内置 Argon2，引第三方包会带来供应链风险和额外体积；
/// PBKDF2 是 FIPS 认可的标准算法，配合足够大的迭代次数同样安全。
/// 派生参数一样写进文件，所以以后换算法也能兼容老备份。
/// </summary>
public static class CryptoBox
{
    /// <summary>标记这是加密内容；恢复时靠它决定要不要解密。</summary>
    private const string Magic = "clpEnc";

    /// <summary>KDF 标识。以后若要换算法，解密的按文件里的值分支。</summary>
    private const string KdfName = "pbkdf2-sha256";

    /// <summary>
    /// PBKDF2 迭代次数（OWASP 对 PBKDF2-HMAC-SHA256 的推荐量级）。
    /// 每次加解密约 0.3~0.6 秒，对备份这种低频操作完全可接受。
    /// </summary>
    private const int DefaultIterations = 600_000;

    private const int SaltLen = 16;
    private const int NonceLen = 12;    // AES-GCM 标准 96 位
    private const int TagLen = 16;      // AES-GCM 认证标签 128 位
    private const int KeyLen = 32;      // AES-256

    private static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>内容是不是我们加密过的（恢复时用来决定要不要先解密）。</summary>
    public static bool LooksEncrypted(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        try
        {
            var node = JsonNode.Parse(text);
            return node?[Magic]?.GetValue<int>() == 1;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 加密。口令为空 = 不加密，原样返回（与 QuickSearch 行为一致）。
    /// </summary>
    public static string Encrypt(string plain, string password)
    {
        if (string.IsNullOrEmpty(password)) return plain;

        // 只输空格的口令不能静默降级成明文：用户以为加密了，实际没有。
        if (string.IsNullOrWhiteSpace(password))
            throw new InvalidOperationException(
                "备份密码不能只有空格 —— 换个真实密码，或者清空密码改用不加密备份");

        byte[] salt = RandomNumberGenerator.GetBytes(SaltLen);
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceLen);
        byte[] key = Derive(password, salt, DefaultIterations);

        try
        {
            byte[] plainBytes = Encoding.UTF8.GetBytes(plain);
            byte[] cipher = new byte[plainBytes.Length];
            byte[] tag = new byte[TagLen];

            using var aes = new AesGcm(key.AsSpan());
            // 把格式版本作为附加认证数据：改了版本号就解不开，防降级
            aes.Encrypt(nonce, plainBytes, cipher, tag, Encoding.UTF8.GetBytes($"{Magic}=1"));

            var doc = new JsonObject
            {
                [Magic] = 1,
                ["kdf"] = KdfName,
                ["iterations"] = DefaultIterations,
                ["salt"] = Convert.ToBase64String(salt),
                ["nonce"] = Convert.ToBase64String(nonce),
                ["tag"] = Convert.ToBase64String(tag),
                ["data"] = Convert.ToBase64String(cipher),
            };
            return doc.ToJsonString(Pretty);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>
    /// 解密。不是加密格式 → 原样返回（兼容明文备份）。
    /// 口令不对 / 内容被改 → 抛出说人话的异常。
    /// </summary>
    public static string Decrypt(string text, string password)
    {
        if (!LooksEncrypted(text)) return text;     // 明文备份原样放行

        if (string.IsNullOrWhiteSpace(password))
            throw new InvalidOperationException(
                "这份备份是加密的，但还没设置备份密码 —— 先在设置里填上再恢复");

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(text);
        }
        catch (Exception e)
        {
            throw new InvalidOperationException(Loc.F("备份不是有效 JSON：{0}", e.Message));
        }
        if (node?[Magic]?.GetValue<int>() != 1)
            throw new InvalidOperationException("不认识的加密格式");

        byte[] salt = Unb64(node, "salt");
        byte[] nonce = Unb64(node, "nonce");
        byte[] tag = Unb64(node, "tag");
        byte[] data = Unb64(node, "data");

        if (salt.Length != SaltLen || nonce.Length != NonceLen || tag.Length != TagLen)
            throw new InvalidOperationException("备份的加密参数不对（文件可能被改过）");

        // 按文件里记下的迭代次数派生：将来调默认值也能解开老备份
        int iterations = node["iterations"]?.GetValue<int>() ?? DefaultIterations;
        // 防呆：手改过的文件可能给出荒谬参数（几亿次 → 卡死）
        if (iterations < 10_000 || iterations > 10_000_000)
            throw new InvalidOperationException("备份里的 KDF 参数不合理（文件可能被改过）");

        string kdf = node["kdf"]?.GetValue<string>() ?? KdfName;
        if (!kdf.Equals(KdfName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(Loc.F("不支持的密钥派生算法：{0}", kdf));

        byte[] key = Derive(password, salt, iterations);
        try
        {
            byte[] plain = new byte[data.Length];
            using var aes = new AesGcm(key.AsSpan());
            aes.Decrypt(nonce, data, tag, plain, Encoding.UTF8.GetBytes($"{Magic}=1"));
            return Encoding.UTF8.GetString(plain);
        }
        catch (CryptographicException)
        {
            // AEAD 认证失败 = 口令不对，或文件被动过。两者都归到"解不开"。
            throw new InvalidOperationException(
                "解密失败 —— 备份密码不对（或这份备份不是用这个密码加密的）");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static byte[] Unb64(JsonNode node, string key)
    {
        string s = node[key]?.GetValue<string>() ?? "";
        if (string.IsNullOrEmpty(s))
            throw new InvalidOperationException("备份里的 base64 解不开（文件可能被截断或改过）");
        try
        {
            return Convert.FromBase64String(s);
        }
        catch (FormatException)
        {
            throw new InvalidOperationException("备份里的 base64 解不开（文件可能被截断或改过）");
        }
    }

    private static byte[] Derive(string password, byte[] salt, int iterations)
    {
        // 用静态 Pbkdf2：Rfc2898DeriveBytes 的构造函数已标记过时（SYSLIB0060）。
        // 静态版本没有可释放的堆状态，也更省内存。
        return Rfc2898DeriveBytes.Pbkdf2(
            password, salt, iterations, HashAlgorithmName.SHA256, KeyLen);
    }
}
