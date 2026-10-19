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
    public const string Repo = "capslock-plus-QQQOrange-";
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
                return UpdateCheckResult.Fail("仓库还没有发布任何版本（Releases 为空）");
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
                asset.Digest ?? "");

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

    /// <summary>下载到临时文件，返回文件路径。</summary>
    public async Task<(bool Ok, string Path, string Error)> DownloadAsync(
        UpdateInfo info, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        string temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            $"CapsLockPlus-{info.Version}-{Guid.NewGuid():N}.exe");
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

            if (!string.IsNullOrWhiteSpace(info.Sha256))
            {
                string actual = await Sha256Async(temp, ct).ConfigureAwait(false);
                if (!actual.Equals(info.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(temp);
                    return (false, "", "校验失败，文件可能已损坏");
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

    /// <summary>启动时清理上次更新留下的 .old-*.exe。</summary>
    public static void CleanupLeftovers()
    {
        try
        {
            string? current = Environment.ProcessPath;
            if (string.IsNullOrEmpty(current)) return;
            string dir = System.IO.Path.GetDirectoryName(current)!;
            foreach (var f in Directory.GetFiles(dir, "*.old-*.exe"))
            {
                try { File.Delete(f); } catch { /* 可能仍被占用，下次再删 */ }
            }
            // 临时下载残留
            foreach (var f in Directory.GetFiles(System.IO.Path.GetTempPath(), "CapsLockPlus-*.exe"))
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
            CoreLog.Warn("清理更新残留失败：" + ex.Message);
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
