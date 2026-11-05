using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace CapsLockPlus.Core.Security;

/// <summary>
/// 本地数据文件的加密（AES-256-GCM）。
///
/// 密钥是随机生成的一次性 32 字节，用 Windows DPAPI（当前用户）包起来
/// 存在 <c>keys/local.key</c>：
///   · 换一个 Windows 账户读不走；
///   · 把整个数据目录拷到别的机器也解不开；
///   · 本程序不用输密码就能读（DPAPI 自动解）。
///
/// 密文格式：<c>CLP2:</c> + Base64(nonce[12] || cipher || tag[16])。
/// 旧版本留下的明文文件仍能按原样读出，随后由 <see cref="SecureFile"/> 就地加密。
/// </summary>
public static class LocalCrypto
{
    public const string Prefix = "CLP2:";
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const string KeyFolder = "keys";
    private const string KeyFileName = "local.key";

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("CLP.LocalKey.v1");
    private static readonly object Gate = new();
    private static byte[]? _key;

    public static bool IsEncrypted(string? text)
        => text != null && text.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>密钥文件是否存在（第一次运行会创建）。</summary>
    public static bool HasKey => File.Exists(KeyPath);

    private static string KeyPath => Path.Combine(AppPaths.DataDir, KeyFolder, KeyFileName);

    private static byte[] Key
    {
        get
        {
            if (_key != null) return _key;
            lock (Gate)
            {
                if (_key != null) return _key;

                string path = KeyPath;
                if (File.Exists(path))
                {
                    try
                    {
                        _key = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
                        if (_key.Length == 32) return _key;
                    }
                    catch (Exception ex)
                    {
                        CoreLog.Warn("本地密钥读取失败，将重新生成：" + ex.Message);
                    }
                }

                _key = RandomNumberGenerator.GetBytes(32);
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllBytes(path, ProtectedData.Protect(_key, Entropy, DataProtectionScope.CurrentUser));
                }
                catch (Exception ex)
                {
                    CoreLog.Warn("本地密钥保存失败（重启后旧文件将无法解密）：" + ex.Message);
                }

                return _key;
            }
        }
    }

    public static string Encrypt(string plain)
    {
        if (plain.Length == 0) plain = "";
        byte[] src = Encoding.UTF8.GetBytes(plain);
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);
        byte[] cipher = new byte[src.Length];
        byte[] tag = new byte[TagSize];

        using (var aes = AesGcmFactory.Create(Key))
            aes.Encrypt(nonce, src, cipher, tag);

        var all = new byte[NonceSize + cipher.Length + TagSize];
        Buffer.BlockCopy(nonce, 0, all, 0, NonceSize);
        Buffer.BlockCopy(cipher, 0, all, NonceSize, cipher.Length);
        Buffer.BlockCopy(tag, 0, all, NonceSize + cipher.Length, TagSize);

        return Prefix + Convert.ToBase64String(all);
    }

    /// <summary>解密；失败返回 null（调用方决定是丢弃还是当明文处理）。</summary>
    public static string? TryDecrypt(string text)
    {
        if (!IsEncrypted(text)) return null;

        try
        {
            byte[] all = Convert.FromBase64String(text.Substring(Prefix.Length));
            if (all.Length < NonceSize + TagSize) return null;

            int cipherLen = all.Length - NonceSize - TagSize;
            byte[] nonce = new byte[NonceSize];
            byte[] cipher = new byte[cipherLen];
            byte[] tag = new byte[TagSize];
            Buffer.BlockCopy(all, 0, nonce, 0, NonceSize);
            Buffer.BlockCopy(all, NonceSize, cipher, 0, cipherLen);
            Buffer.BlockCopy(all, NonceSize + cipherLen, tag, 0, TagSize);

            byte[] plain = new byte[cipherLen];
            using (var aes = AesGcmFactory.Create(Key))
                aes.Decrypt(nonce, cipher, tag, plain);

            return Encoding.UTF8.GetString(plain);
        }
        catch
        {
            return null;
        }
    }

    public static string Decrypt(string text) => TryDecrypt(text) ?? "";
}
