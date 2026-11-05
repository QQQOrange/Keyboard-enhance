using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace CapsLockPlus.Core.Security;

/// <summary>
/// 本程序所有"用户数据"文件的读写入口：落到磁盘上一律是密文。
///
/// 两类文件：
///   · 整体文件（usage.json / bindings.txt / crash-context …）—— 整个文件就是一条密文；
///   · 追加日志（app.log / hookhost.log）—— 每批追加一条密文记录，
///     读的时候按行解密再拼起来。
///
/// 读的时候会自动识别旧明文（没有 CLP2: 前缀），所以升级不会丢数据；
/// 调用 <see cref="EncryptInPlace"/> 即可把旧文件转成密文。
/// </summary>
public static class SecureFile
{
    public static bool IsEncryptedFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            using var reader = new StreamReader(path, Encoding.UTF8, true);
            char[] head = new char[LocalCrypto.Prefix.Length];
            int n = reader.Read(head, 0, head.Length);
            return n == head.Length && new string(head) == LocalCrypto.Prefix;
        }
        catch
        {
            return false;
        }
    }

    public static string ReadAllText(string path)
    {
        try
        {
            if (!File.Exists(path)) return "";
            string raw = File.ReadAllText(path, Encoding.UTF8);
            if (LocalCrypto.IsEncrypted(raw)) return LocalCrypto.TryDecrypt(raw) ?? "";
            return raw;
        }
        catch (Exception ex)
        {
            CoreLog.Warn($"读取 {Path.GetFileName(path)} 失败：{ex.Message}");
            return "";
        }
    }

    public static void WriteAllText(string path, string text)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        string tmp = path + ".tmp";
        File.WriteAllText(tmp, LocalCrypto.Encrypt(text), new UTF8Encoding(false));
        try
        {
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { File.Copy(tmp, path, overwrite: true); File.Delete(tmp); } catch { }
        }
    }

    /// <summary>追加一段文本（内部按记录加密；读的时候用 <see cref="ReadAllText"/> 拼回来）。</summary>
    public static void AppendBlock(string path, string text)
    {
        if (text.Length == 0) return;

        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        string record = LocalCrypto.Encrypt(text) + Environment.NewLine;
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(record);
    }

    public static void AppendLine(string path, string line) => AppendBlock(path, line + Environment.NewLine);

    /// <summary>读取追加式日志：逐行解密后再拼起来。</summary>
    public static string ReadLog(string path)
    {
        try
        {
            if (!File.Exists(path)) return "";

            var sb = new StringBuilder();
            foreach (string line in File.ReadLines(path, Encoding.UTF8))
            {
                if (line.Length == 0) continue;

                if (LocalCrypto.IsEncrypted(line)) sb.Append(LocalCrypto.TryDecrypt(line) ?? "");
                else sb.AppendLine(line);      // 旧明文日志
            }

            return sb.ToString();
        }
        catch (Exception ex)
        {
            CoreLog.Warn($"读取日志 {Path.GetFileName(path)} 失败：{ex.Message}");
            return "";
        }
    }

    /// <summary>把单个文件就地转成密文；已经是密文/不存在则跳过。返回是否做了转换。</summary>
    public static bool EncryptInPlace(string path)
    {
        try
        {
            if (!File.Exists(path) || IsEncryptedFile(path)) return false;

            string raw = File.ReadAllText(path, Encoding.UTF8);
            if (LocalCrypto.IsEncrypted(raw)) return false;

            WriteAllText(path, raw);
            return true;
        }
        catch (Exception ex)
        {
            CoreLog.Warn($"加密 {Path.GetFileName(path)} 失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>把追加式日志整体转成密文（保持行不变）。</summary>
    public static bool EncryptLogInPlace(string path)
    {
        try
        {
            if (!File.Exists(path) || IsEncryptedFile(path)) return false;

            string text = File.ReadAllText(path, Encoding.UTF8);
            if (LocalCrypto.IsEncrypted(text)) return false;

            File.Delete(path);
            AppendBlock(path, text);
            return true;
        }
        catch (Exception ex)
        {
            CoreLog.Warn($"加密日志 {Path.GetFileName(path)} 失败：{ex.Message}");
            return false;
        }
    }
}
