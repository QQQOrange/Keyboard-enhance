using System.Security.Cryptography;
using System.Text.Json;

namespace CapsLockPlus.Core.Settings;

/// <summary>
/// 启动密码：决定"谁能打开这个软件"。
///
/// ⚠ 与备份密码（<see cref="SyncOptions.BackupPassword"/>）**完全不是一回事**，
/// 用户点名强调过：那一个给备份文件和本地数据加密用；这一个只卡启动。
/// 两者分开存放、分开校验、互不影响。
///
/// 存放方式：数据目录下的 <c>startup.json</c>（盐 + PBKDF2 派生值 + 迭代次数）。
/// 为什么不拿 settings.json 当唯一真相：settings.json 可能被备份密码加密，
/// 解锁失败时整个文件读不出来（只读保护返回默认值）—— 那时要是只认
/// settings.json，密码门会被悄悄跳过。独立文件保证门永远可校验。
///
/// 不存明文、不存可逆形式：磁盘上只有 PBKDF2-HMAC-SHA256 的派生值，
/// 校验方式是"拿输入的密码按同样的盐和迭代次数再派生一次，定长时间比对"。
///
/// 忘记密码的退路：删掉 startup.json 即解除保护（其他设置不受影响）。
/// 这是有意的设计 —— 把用户永久关在门外比"可破解"更糟。
/// </summary>
public static class StartupPassword
{
    /// <summary>密码最短长度。太短的密码挡不住人，只添麻烦。</summary>
    public const int MinLength = 4;

    /// <summary>密码最长长度（PBKDF2 对长度没有硬限制，这里防误粘贴）。</summary>
    public const int MaxLength = 64;

    /// <summary>
    /// PBKDF2 迭代次数，与备份加密（<see cref="Sync.CryptoBox"/>）同量级
    /// （OWASP 对 PBKDF2-HMAC-SHA256 的推荐值）。每次校验约 0.3~0.6 秒，
    /// 启动一次验一回，完全可接受。
    /// </summary>
    public const int Iterations = 600_000;

    private const int SaltLen = 16;
    private const int HashLen = 32;

    private const string FileName = "startup.json";

    /// <summary>
    /// 自检用：把密码文件指到别处，避免碰真实数据目录
    /// （否则跑一次 --selftest 就在用户机器上留下一把锁）。
    /// </summary>
    internal static string? DirOverride { get; set; }

    /// <summary>密码文件的完整路径。</summary>
    public static string FilePath =>
        System.IO.Path.Combine(DirOverride ?? AppPaths.DataDir, FileName);

    /* 这些命令行模式不弹密码门：自检 / 离屏渲染 / 一次性诊断都跑完即退，
     * 弹窗只会把它们卡死。正常启动（含 --tray、--updated）一律要过门。 */
    private static readonly string[] SkipFlags =
    {
        "--selftest", "--render", "--render-diag", "--osd", "--diag",
        "--hwtest", "--webdav-test", "--svg2png", "--reveal",
    };

    /// <summary>这次启动该不该跳过密码门（测试 / 诊断模式跳过）。</summary>
    public static bool ShouldSkip(string[] args) =>
        args.Any(a => SkipFlags.Contains(a, StringComparer.OrdinalIgnoreCase));

    private sealed record Payload(string Salt, string Hash, int Iterations);

    /// <summary>现在有没有设启动密码（文件存在且内容完整）。</summary>
    public static bool IsSet()
    {
        try { return Read() != null; }
        catch { return false; }
    }

    /// <summary>
    /// 校验密码。没设密码时一律返回 false（调用方应先问 <see cref="IsSet"/>）。
    /// 空输入直接不过 —— 密码不允许为空。
    /// </summary>
    public static bool Verify(string? password)
    {
        if (string.IsNullOrEmpty(password)) return false;

        var p = Read();
        if (p == null) return false;

        byte[] salt, hash;
        try
        {
            salt = Convert.FromBase64String(p.Salt);
            hash = Convert.FromBase64String(p.Hash);
        }
        catch { return false; }
        if (salt.Length == 0 || hash.Length == 0) return false;
        if (p.Iterations < 10_000 || p.Iterations > 10_000_000) return false;

        byte[] derived = Rfc2898DeriveBytes.Pbkdf2(
            password, salt, p.Iterations, HashAlgorithmName.SHA256, HashLen);
        try
        {
            // 定长比较：防时序侧信道（虽然本地软件意义有限，顺手做对）
            return CryptographicOperations.FixedTimeEquals(derived, hash);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(derived);
        }
    }

    /// <summary>
    /// 校验新密码合不合规；返回 null = 可以用，否则是人话错误（界面直接显示）。
    /// 不做 Trim：密码里的空格可能是故意的，只有"全是空格"才拒。
    /// </summary>
    public static string? ValidateNew(string? password)
    {
        if (string.IsNullOrEmpty(password)) return "请先输入密码";
        if (string.IsNullOrWhiteSpace(password)) return "密码不能只有空格";
        if (password.Length < MinLength) return $"密码至少 {MinLength} 位";
        if (password.Length > MaxLength) return $"密码最长 {MaxLength} 位";
        return null;
    }

    /// <summary>
    /// 设置（或修改）启动密码：随机新盐 → 派生 → 写文件。
    /// 返回 false 时 <paramref name="error"/> 是原因（密码不合规或写盘失败）。
    /// </summary>
    public static bool TrySet(string? password, out string error)
    {
        error = ValidateNew(password) ?? "";
        if (error.Length > 0) return false;

        byte[] salt = RandomNumberGenerator.GetBytes(SaltLen);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(
            password!, salt, Iterations, HashAlgorithmName.SHA256, HashLen);
        try
        {
            Write(new Payload(
                Convert.ToBase64String(salt),
                Convert.ToBase64String(hash),
                Iterations));
            return true;
        }
        catch (Exception ex)
        {
            error = "保存密码失败：" + ex.Message;
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(hash);
        }
    }

    /// <summary>
    /// 关掉启动密码（删文件）。设置里的镜像由 <see cref="MirrorTo"/> 一并清。
    /// </summary>
    public static void Clear()
    {
        try
        {
            string path = FilePath;
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            CoreLog.Warn("删除启动密码文件失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 把文件状态同步进设置对象（界面显示 + 跟着设置一起持久化）。
    ///
    /// 文件没了就清空镜像 —— "删 startup.json 即解除保护"必须可预期：
    /// 否则用户删了文件，设置里还留着开启态，下次改点什么又给写回去。
    /// </summary>
    public static void MirrorTo(SecurityOptions sec)
    {
        var p = Read();
        if (p == null)
        {
            sec.StartupPasswordEnabled = false;
            sec.StartupPasswordSalt = "";
            sec.StartupPasswordHash = "";
            sec.StartupPasswordIterations = 0;
            return;
        }

        sec.StartupPasswordEnabled = true;
        sec.StartupPasswordSalt = p.Salt;
        sec.StartupPasswordHash = p.Hash;
        sec.StartupPasswordIterations = p.Iterations;
    }

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        // 大小写不敏感：文件是手写的也能读（自检/迁移脚本各写一份时省得对大小写）
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// 读密码文件。任何异常（不存在 / 损坏 / 参数被手改坏）都当"没有密码"——
    /// 启动密码是"锦上添花的一把锁"，绝不能因为它把用户永久关在门外。
    /// 反过来说：删掉文件即可解除保护，这是文档里写明的退路。
    /// </summary>
    private static Payload? Read()
    {
        try
        {
            string path = FilePath;
            if (!File.Exists(path)) return null;

            string raw = File.ReadAllText(path);
            var p = JsonSerializer.Deserialize<Payload>(raw, ReadOptions);
            if (p == null) return null;
            if (string.IsNullOrEmpty(p.Salt) || string.IsNullOrEmpty(p.Hash)) return null;
            if (p.Iterations <= 0) return null;
            return p;
        }
        catch (Exception ex)
        {
            CoreLog.Warn("读取启动密码文件失败，按未设置处理：" + ex.Message);
            return null;
        }
    }

    private static void Write(Payload p)
    {
        string path = FilePath;
        string? dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var options = new JsonSerializerOptions { WriteIndented = true };
        // 先写临时文件再换名：写到一半断电也不会留下半个 JSON 把门搞丢
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(p, options));
        File.Move(tmp, path, overwrite: true);
    }
}
