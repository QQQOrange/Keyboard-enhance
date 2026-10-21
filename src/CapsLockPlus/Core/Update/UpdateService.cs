using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CapsLockPlus.Core.Update;

public sealed record UpdateInfo(
    string Version,
    string DownloadUrl,
    string ReleaseNotes,
    string PageUrl,
    long Size,
    string Sha256)
{
    public string SizeText => Size <= 0 ? "" : $"{Size / 1024.0 / 1024.0:0.0} MB";
}

public sealed record UpdateCheckResult(bool Success, UpdateInfo? Update, string Message, bool IsNewer)
{
    public static UpdateCheckResult Fail(string message) => new(false, null, message, false);
}

/// <summary>
/// 更新服务。
///
/// 检查：读取 GitHub Releases 的 latest 发布（公开仓库无需 token），
///       从 release 的 assets 里挑出 .exe，比较版本号。
/// 下载：流式下载到临时目录，校验 SHA-256（release 里给了才校验）。
/// 替换：当前 exe 改名 → 新 exe 放到原位 → 重启。
///       用“改名而不是删除”是为了避免文件被占用导致失败；
///       真正删除旧文件交给下一次启动（见 CleanupLeftovers）。
///
/// 如果仓库还没有发布 release，会退回到“检查 tags 里的版本号”并提示没有可用安装包。
/// </summary>
public sealed class UpdateService
{
    public const string Owner = "QQQOrange";
    public const string Repo = "Keyboard-enhance";
    public static string GitHubUrl => $"https://github.com/{Owner}/{Repo}";
    public static string ReleasesUrl => $"{GitHubUrl}/releases";
    public static string LatestReleaseApi => $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("CapsLockPlus-Updater/1.0");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    /// <summary>当前版本号（三段式，例如 1.0.0）。</summary>
    public static string CurrentVersion
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            return v == null ? "1.0.0" : $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}";
        }
    }

    /// <summary>检查是否有新版本。</summary>
    public async Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await Http.GetAsync(LatestReleaseApi, ct).ConfigureAwait(false);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                return UpdateCheckResult.Fail("仓库还没有发布任何版本");
            if (resp.StatusCode == System.Net.HttpStatusCode.Forbidden &&
                resp.Headers.TryGetValues("X-RateLimit-Remaining", out var remain) &&
                remain.FirstOrDefault() == "0")
            {
                // 匿名访问 GitHub API 有频率限制（每 IP 每小时 60 次），
                // 同网络下多台机器同时检查就可能撞上。给出可操作的提示。
                return UpdateCheckResult.Fail("GitHub 接口访问频率已达上限，请稍后再试");
            }
            if (!resp.IsSuccessStatusCode)
                return UpdateCheckResult.Fail($"无法访问 GitHub（HTTP {(int)resp.StatusCode}）");

            var release = await resp.Content.ReadFromJsonAsync<GitHubRelease>(cancellationToken: ct)
                .ConfigureAwait(false);
            if (release == null) return UpdateCheckResult.Fail("返回内容无法解析");

            string tag = (release.TagName ?? "").TrimStart('v', 'V');
            if (!Version.TryParse(tag, out var remote))
                return UpdateCheckResult.Fail($"无法识别版本号：{release.TagName}");
            var current = Version.Parse(CurrentVersion);
            bool newer = remote > current;

            var asset = release.Assets?.FirstOrDefault(a =>
                a.Name?.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) == true);
            if (asset == null)
                return new UpdateCheckResult(true, null,
                    newer ? $"发现新版本 {tag}，但该版本没有附带 exe 安装包" : "已是最新版本", newer);

            var info = new UpdateInfo(
                tag,
                asset.BrowserDownloadUrl ?? "",
                release.Body ?? "",
                release.HtmlUrl ?? ReleasesUrl,
                asset.Size,
                NormalizeDigest(asset.Digest));

            return new UpdateCheckResult(true, info,
                newer ? $"发现新版本 {tag}" : "已是最新版本", newer);
        }
        catch (TaskCanceledException)
        {
            return UpdateCheckResult.Fail("检查更新超时，请稍后再试");
        }
        catch (Exception ex)
        {
            CoreLog.Warn("检查更新失败：" + ex.Message);
            return UpdateCheckResult.Fail("检查更新失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 下载目录。优先放在数据目录下的 updates/，这样在 %TEMP% 被策略限制的环境里
    /// 也能正常更新；确认可用后才回退到系统临时目录。
    /// </summary>
    private static string ResolveDownloadDir()
    {
        try
        {
            string dir = System.IO.Path.Combine(AppPaths.DataDir, "updates");
            Directory.CreateDirectory(dir);
            // 写一个探针文件确认真的可写
            string probe = System.IO.Path.Combine(dir, ".probe");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return dir;
        }
        catch (Exception ex)
        {
            CoreLog.Warn("数据目录不可用于下载，改用系统临时目录：" + ex.Message);
            return System.IO.Path.GetTempPath();
        }
    }

    /// <summary>下载到临时文件，返回文件路径。</summary>
    public async Task<(bool Ok, string Path, string Error)> DownloadAsync(
        UpdateInfo info, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        string temp;
        try
        {
            temp = System.IO.Path.Combine(ResolveDownloadDir(),
                $"CapsLockPlus-{info.Version}-{Guid.NewGuid():N}.exe");
        }
        catch (Exception ex)
        {
            return (false, "", "无法创建下载目录：" + ex.Message);
        }
        try
        {
            using var resp = await Http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? info.Size;
            await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var dst = File.Create(temp);

            var buffer = new byte[81920];
            long read = 0;
            int n;
            while ((n = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                read += n;
                if (total > 0) progress?.Report((double)read / total);
            }
            await dst.FlushAsync(ct).ConfigureAwait(false);
            dst.Close();

            if (info.Size > 0 && read != info.Size)
            {
                TryDelete(temp);
                return (false, "", $"下载不完整（{read}/{info.Size} 字节）");
            }

            // GitHub 的资产摘要是 "sha256:xxxx" 这种带算法前缀的格式，
            // 必须先把前缀剥掉再比较，否则每次下载都会误判为“校验失败”。
            string expected = NormalizeDigest(info.Sha256);
            if (expected.Length > 0)
            {
                string actual = await Sha256Async(temp, ct).ConfigureAwait(false);
                if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(temp);
                    return (false, "",
                        $"校验失败，文件可能已损坏（期望 {expected[..Math.Min(12, expected.Length)]}…，" +
                        $"实际 {actual[..12]}…）");
                }
            }
            return (true, temp, "");
        }
        catch (Exception ex)
        {
            TryDelete(temp);
            return (false, "", "下载失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 把发布方给的摘要规范成纯十六进制串。
    /// 兼容 "sha256:abc..."、"SHA256 abc..."、"sha256=abc..." 与纯摘要几种写法。
    ///
    /// 注意：不能只靠“过滤非十六进制字符”来做——`sha256` 这几个字符本身
    /// 全是合法的十六进制字符（a、2、5、6），过滤后会把前缀留下。
    /// 所以必须先按分隔符切分，再判断是不是算法名。
    /// </summary>
    public static string NormalizeDigest(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest)) return "";
        string d = digest.Trim();

        // 1) 用 : 或 = 分隔（"sha256:xxx" / "sha256=xxx"）
        int sep = d.IndexOfAny(new[] { ':', '=' });
        if (sep >= 0)
        {
            string head = d[..sep].Trim();
            string tailPart = d[(sep + 1)..].Trim();
            // 前半段是短标识符（算法名）、后半段是足够长的摘要时，丢弃前半段。
            // "sha256" 本身全是十六进制字符，所以不能靠 IsHexDigit 判断。
            bool headLooksLikeAlgorithm = head.Length is > 0 and <= 16 && head.All(char.IsLetterOrDigit);
            if (headLooksLikeAlgorithm && tailPart.Length >= 32)
                d = tailPart;
            else if (head.Length == 0)
                d = tailPart;
        }
        else
        {
            // 2) 空格分隔（"sha256 xxx"）：首段短且是字母数字组合、后半段是长摘要时才丢弃首段。
            //    注意 "sha256" 里含数字，不能用 char.IsLetter 判定。
            int sp = d.IndexOf(' ');
            if (sp > 0)
            {
                string head = d[..sp].Trim();
                string tailPart = d[(sp + 1)..].Trim();
                bool headLooksLikeAlgorithm = head.Length is > 0 and <= 16 && head.All(char.IsLetterOrDigit);
                if (tailPart.Length >= 32 && headLooksLikeAlgorithm)
                    d = tailPart;
            }
        }

        // 3) 去掉剩余的空格与等号，只保留十六进制字符
        var sb = new System.Text.StringBuilder(d.Length);
        foreach (char c in d)
        {
            if (c is ' ' or '=' or '\t') continue;
            if (Uri.IsHexDigit(c)) sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// 用下载好的 exe 替换当前程序并重启。
    /// 做法：当前 exe 先改名（Windows 允许给运行中的 exe 改名），
    /// 再把新文件放到原路径，然后启动新进程并退出。
    /// </summary>
    public static bool ApplyAndRestart(string downloadedPath)
    {
        try
        {
            string? current = Environment.ProcessPath;
            if (string.IsNullOrEmpty(current) || !File.Exists(current))
                return false;
            if (!File.Exists(downloadedPath)) return false;

            string dir = System.IO.Path.GetDirectoryName(current)!;
            string old = System.IO.Path.Combine(dir,
                System.IO.Path.GetFileNameWithoutExtension(current) + $".old-{DateTime.Now:HHmmss}.exe");

            // 1) 当前 exe 改名（不会失败于“正在使用”）
            File.Move(current, old, true);
            // 2) 新文件就位
            File.Copy(downloadedPath, current, true);

            // 3) 启动新版本（带 --elevated，避免再次弹 UAC）
            var psi = new ProcessStartInfo(current) { UseShellExecute = true };
            psi.ArgumentList.Add("--elevated");
            psi.ArgumentList.Add("--updated");
            Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            CoreLog.Error("替换程序失败", ex);
            return false;
        }
    }

    /// <summary>启动时清理上次更新留下的 .old-*.exe 与下载残留。</summary>
    public static void CleanupLeftovers()
    {
        string? current = Environment.ProcessPath;

        // 1) 程序目录里上一次替换留下的旧文件
        try
        {
            if (!string.IsNullOrEmpty(current))
            {
                string dir = System.IO.Path.GetDirectoryName(current)!;
                foreach (var f in Directory.GetFiles(dir, "*.old-*.exe"))
                {
                    try { File.Delete(f); } catch { /* 可能仍被占用，下次再删 */ }
                }
            }
        }
        catch (Exception ex)
        {
            CoreLog.Warn("清理旧版本残留失败：" + ex.Message);
        }

        // 2) 下载目录里超过 6 小时的残留安装包
        foreach (var dir in new[] { System.IO.Path.Combine(AppPaths.DataDir, "updates"),
                                    System.IO.Path.GetTempPath() })
        {
            try
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var f in Directory.GetFiles(dir, "CapsLockPlus-*.exe"))
                {
                    try
                    {
                        if (DateTime.Now - File.GetLastWriteTime(f) > TimeSpan.FromHours(6)) File.Delete(f);
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                CoreLog.Warn($"清理下载残留失败（{dir}）：{ex.Message}");
            }
        }
    }

    private static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        await using var fs = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    // ---------------- GitHub API 模型 ----------------

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")] public string? TagName { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("body")] public string? Body { get; set; }
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
        [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
        [JsonPropertyName("assets")] public List<GitHubAsset>? Assets { get; set; }
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("size")] public long Size { get; set; }
        [JsonPropertyName("browser_download_url")] public string? BrowserDownloadUrl { get; set; }
        [JsonPropertyName("digest")] public string? Digest { get; set; }
    }
}
