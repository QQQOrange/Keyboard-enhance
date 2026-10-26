using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
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
    string Sha256,
    string AssetName = "",
    bool MatchedSystem = true)
{
    public string SizeText => Size <= 0 ? "" : $"{Size / 1024.0 / 1024.0:0.0} MB";
}

public sealed record UpdateCheckResult(bool Success, UpdateInfo? Update, string Message, bool IsNewer)
{
    public static UpdateCheckResult Fail(string message) => new(false, null, message, false);
}

/// <summary>
/// 一次令牌验证的结果。
/// </summary>
/// <param name="Ok">令牌能不能用。</param>
/// <param name="Login">令牌所属账号（fine-grained 只读仓库时可能为空）。</param>
/// <param name="Message">给用户看的一句话，成功能看到配额。</param>
/// <param name="Limit">每小时配额（0 = 没读到）。</param>
/// <param name="Remaining">剩余次数（0 = 没读到或真用完了）。</param>
public sealed record TokenCheckResult(bool Ok, string Login, string Message, int Limit = 0, int Remaining = 0);

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

    /// <summary>
    /// 「什么是个人访问令牌」的帮助文档（GitHub 官方中文页）。
    ///
    /// 地址集中在这一个地方：以后 GitHub 换文档路径，改这里即可，
    /// 不用满项目找那串 URL（界面上的问号按钮读的就是它）。
    /// </summary>
    public static string TokenHelpUrl =>
        "https://docs.github.com/zh/authentication/keeping-your-account-and-data-secure/managing-your-personal-access-tokens";

    /// <summary>创建令牌的入口页（问号旁边那个「去创建」）。</summary>
    public static string TokenCreateUrl => "https://github.com/settings/tokens";

    private static readonly HttpClient Http = CreateClient();

    /// <summary>
    /// 下载专用客户端：**超时给足 5 分钟**。
    ///
    /// 查询接口 20 秒够（几百 KB 的 JSON），但安装包有 3MB ——
    /// 用 20 秒那个客户端下载，在 1 Mbps 的网或慢代理下**必然超时**，
    /// 现象就是"点下载，转几秒然后报下载失败"。HttpClient.Timeout 是
    /// 覆盖整个响应体读取的，不是只算首字节。
    /// </summary>
    private static readonly HttpClient DownloadHttp = CreateDownloadClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("CapsLockPlus-Updater/1.0");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    private static HttpClient CreateDownloadClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("CapsLockPlus-Updater/1.0");
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

    // ---------------- 系统识别与安装包挑选 ----------------

    /// <summary>
    /// 当前系统是不是 Win10 之前那一代（Win7 / Win8 / Win8.1）。
    ///
    /// 判据是**主版本号 &lt; 10**，不是"等于 6.1"：
    /// Win7 = 6.1、Win8 = 6.2、Win8.1 = 6.3 都属于"装不上 .NET 10"的一代，
    /// 只能拿 .NET 7 自包含包（发布资产名里叫 `Win7`，覆盖的是这一整代）。
    /// 反过来，Win10 2004 之后主版本号可能报成 10.0.x —— 只要 ≥ 10 就走
    /// Win10/11 那条线。
    /// </summary>
    public static bool IsLegacyWindows
    {
        get
        {
            try { return Environment.OSVersion.Version.Major < 10; }
            catch { return false; }   /* 预期内：探测型，读不到就按新系统处理 */
        }
    }

    /// <summary>
    /// 当前进程的架构族。只有 x86 / x64 两种发布形态：
    /// ARM64 上跑的是 x64 模拟进程（<see cref="System.Runtime.InteropServices.Architecture.X64"/>），
    /// 所以非 x86 一律归到 x64。
    /// </summary>
    public static string ProcessArch =>
        System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
            == System.Runtime.InteropServices.Architecture.X86 ? "x86" : "x64";

    /// <summary>挑包的结果。<see cref="Exact"/> 为 false 表示没有完全匹配的包。</summary>
    public sealed record AssetPick(string Name, bool FamilyMatched, bool ArchMatched)
    {
        public static readonly AssetPick None = new("", false, false);
        public bool Any => Name.Length > 0;
        public bool Exact => FamilyMatched && ArchMatched;
    }

    /// <summary>资产名里读出来的特征（发布命名约定见 BUILD.md）。</summary>
    private readonly record struct AssetTraits(bool X86, bool Win7Family, bool SelfContained)
    {
        /// <summary>
        /// 没标注的按发布约定理解：不带架构后缀 = x64，不带系统后缀 = Win10/11。
        /// 主资产 `KeyboardEnhance.exe` 就属于这种"裸名"。
        /// </summary>
        public static AssetTraits Parse(string name)
        {
            string n = name.ToLowerInvariant();
            bool win7 = n.Contains("win7");
            bool x86 = n.Contains("x86") || n.Contains("win32") || n.Contains("-32");
            bool self = n.Contains("selfcontained") || n.Contains("self-contained");
            return new AssetTraits(x86, win7, self);
        }
    }

    /// <summary>
    /// 从 release 的资产名里挑出**适合本机**的那一个。
    ///
    /// 为什么必须挑：GitHub 返回的 assets 是**按名字排的**，而
    /// `KeyboardEnhance-4.5.27-x64-Win7-SelfContained.exe` 排在
    /// `KeyboardEnhance.exe` 前面（`-4` &lt; `.`）。以前的实现只取
    /// "第一个以 .exe 结尾的资产"，于是在 Win11 上也会被推 67 MB 的
    /// Win7 自包含包 —— 能装、能跑，但白白多下 60 MB。
    ///
    /// 打分制（同分靠名字长度，主包名最短）：
    /// <list type="bullet">
    /// <item>系统家族匹配 +1000 / 不匹配 −500；</item>
    /// <item>架构匹配 +200 / 不匹配 −100；</item>
    /// <item>体积偏好 +50：Win10+ 选「依赖运行时」的瘦包，Win7 代选自包含包；</item>
    /// <item>再减去名字长度，让裸名主包优先。</item>
    /// </list>
    /// </summary>
    public static AssetPick PickAsset(IReadOnlyList<string> names, bool legacyWindows, string arch)
    {
        AssetPick best = AssetPick.None;
        int bestScore = int.MinValue;
        bool wantX86 = string.Equals(arch, "x86", StringComparison.OrdinalIgnoreCase);

        foreach (string name in names)
        {
            if (string.IsNullOrEmpty(name)) continue;
            if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;

            var t = AssetTraits.Parse(name);
            bool familyOk = t.Win7Family == legacyWindows;
            bool archOk = t.X86 == wantX86;

            int score = familyOk ? 1000 : -500;
            score += archOk ? 200 : -100;
            // legacyWindows 与 SelfContained 同真同假才是想要的组合：
            // Win7 代（true）要自包含（true）；Win10+（false）要瘦包（false）。
            score += legacyWindows == t.SelfContained ? 50 : 0;
            score -= name.Length;

            if (score > bestScore)
            {
                bestScore = score;
                best = new AssetPick(name, familyOk, archOk);
            }
        }
        return best;
    }

    /// <summary>
    /// 把用户粘进来的令牌洗一遍。
    ///
    /// 从网页、聊天、curl 示例里复制常常会带上 `Bearer `、`token `、
    /// `Authorization:` 这些前缀和换行 —— 原样塞进请求头会被 GitHub 判 401，
    /// 而用户看到的只是"令牌无效"，根本想不到是自己多粘了几个字。
    ///
    /// 返回空串表示"没填"，调用方据此走匿名路径（60 次/小时）。
    /// </summary>
    public static string NormalizeToken(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        string s = raw.Trim();

        // 前缀可能叠着来（"Authorization: Bearer xxx"），剥到剥不动为止
        bool stripped = true;
        while (stripped)
        {
            stripped = false;
            foreach (string prefix in TokenPrefixes)
            {
                if (!s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                s = s[prefix.Length..].TrimStart(' ', ':', '\t');
                stripped = true;
                break;
            }
        }

        // 令牌本身不含空格；空格/换行之后的内容不属于它
        int cut = s.IndexOfAny(new[] { ' ', '\r', '\n', '\t' });
        if (cut > 0) s = s[..cut];

        return s.Trim();
    }

    /// <summary>用户是否填了令牌（决定"能不能自动下载"这件事，见 UpdatePromptWindow）。</summary>
    public static bool HasToken(string? raw) => NormalizeToken(raw).Length > 0;

    /// <summary>
    /// 粗判是不是 GitHub 的令牌。
    ///
    /// 只用来给提示（"看着不像，你核对一下"），**不作为保存前的拦截**：
    /// GitHub 将来可能启用新前缀，卡死格式等于把用户挡在门外。
    /// </summary>
    public static bool LooksLikeGitHubToken(string? raw)
    {
        string t = NormalizeToken(raw);
        if (t.Length == 0) return false;
        return t.StartsWith("ghp_", StringComparison.Ordinal)
            || t.StartsWith("gho_", StringComparison.Ordinal)
            || t.StartsWith("ghu_", StringComparison.Ordinal)
            || t.StartsWith("ghs_", StringComparison.Ordinal)
            || t.StartsWith("ghr_", StringComparison.Ordinal)
            || t.StartsWith("github_pat_", StringComparison.Ordinal);
    }

    /// <summary>
    /// 令牌的显示形式：**固定 8 个圆点**，不露任何真实字符。
    ///
    /// 以前是"头 6 尾 4"（`ghp_a…9f3d`），用户明确说这同样算泄露 ——
    /// 截图、录屏、远程协助都会把它带出去。现在只回答一个问题："这里有没有东西"。
    /// 连长度都不透露（20 位是老式 PAT，93 位是 fine-grained）。
    /// </summary>
    public static string MaskToken(string? raw) =>
        NormalizeToken(raw).Length == 0 ? "" : "••••••••";

    /// <summary>一次配额查询的结果（本小时已用 / 总共）。</summary>
    public sealed record RateLimitInfo(bool Ok, int Limit, int Used, int Remaining, string Message);

    /// <summary>
    /// 查当前 GitHub 配额。
    ///
    /// 用 <c>/rate_limit</c>：这个端点**不消耗**配额，匿名也能查 ——
    /// 匿名时看到的就是 60/小时，正好让用户直观知道"没填令牌的代价"。
    /// </summary>
    public async Task<RateLimitInfo> FetchRateLimitAsync(string? token, CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/rate_limit");
            string pat = NormalizeToken(token);
            if (pat.Length > 0)
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", pat);
            req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return new RateLimitInfo(false, 0, 0, 0, $"HTTP {(int)resp.StatusCode}");

            string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("resources", out var res)
                || !res.TryGetProperty("core", out var core))
                return new RateLimitInfo(false, 0, 0, 0, "返回里没有 core 配额");

            int limit = core.TryGetProperty("limit", out var l) ? l.GetInt32() : 0;
            int used = core.TryGetProperty("used", out var u) ? u.GetInt32() : 0;
            int remain = core.TryGetProperty("remaining", out var r) ? r.GetInt32() : 0;
            return new RateLimitInfo(true, limit, used, remain, "");
        }
        catch (TaskCanceledException)
        {
            return new RateLimitInfo(false, 0, 0, 0, "超时");
        }
        catch (Exception ex)
        {
            CoreLog.Warn("查询 GitHub 配额失败：" + ex.Message);
            return new RateLimitInfo(false, 0, 0, 0, ex.Message);
        }
    }

    private static readonly string[] TokenPrefixes = { "Bearer", "token", "Authorization" };

    /// <summary>检查是否有新版本。</summary>
    /// <param name="token">
    /// GitHub 个人访问令牌（可选）。带上它把接口配额从 60 次/小时（按 IP）
    /// 提到 5000 次/小时（按账号），见 <see cref="NormalizeToken"/>。
    /// </param>
    public async Task<UpdateCheckResult> CheckAsync(string? token = null, CancellationToken ct = default)
    {
        string pat = NormalizeToken(token);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);

            /* 令牌走**每次请求的报文头**，不放进静态 HttpClient 的默认头：
             * ① 用户在界面里改令牌后要立刻生效，改默认头容易漏掉旧的；
             * ② 请求头里带着令牌的报文不该被任何 HttpClient 级别的缓存/复用搞混。 */
            if (pat.Length > 0)
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", pat);
                req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            }

            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);

            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                return UpdateCheckResult.Fail("仓库还没有发布任何版本");

            if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                return UpdateCheckResult.Fail(
                    "GitHub 令牌无效或已过期 —— 到「更新」里重新填一个，或者清空它改回匿名访问");

            if (resp.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                bool quotaGone = resp.Headers.TryGetValues("X-RateLimit-Remaining", out var remain) &&
                                 remain.FirstOrDefault() == "0";

                // 填了令牌还 403：多半是令牌本身没权限（fine-grained 没勾 Public repositories）
                if (pat.Length > 0)
                    return UpdateCheckResult.Fail(quotaGone
                        ? "这个令牌的接口配额也用完了（5000 次/小时），一小时后再试"
                        : "令牌没有读取公开仓库的权限 —— fine-grained 令牌请勾上 Public repositories (read)");

                // 没填令牌：直接告诉用户怎么彻底解决，而不是只让他"稍后再试"
                return UpdateCheckResult.Fail(quotaGone
                    ? "GitHub 接口访问频率已达上限（未填令牌时每小时 60 次）。"
                      + "到「更新」里填一个个人访问令牌，可提到 5000 次/小时"
                    : "GitHub 拒绝了这次请求（HTTP 403）—— 可以试着填一个个人访问令牌");
            }

            if (!resp.IsSuccessStatusCode)
                return UpdateCheckResult.Fail(Loc.F("无法访问 GitHub（HTTP {0}）", (int)resp.StatusCode));

            var release = await resp.Content.ReadFromJsonAsync<GitHubRelease>(cancellationToken: ct)
                .ConfigureAwait(false);
            if (release == null) return UpdateCheckResult.Fail("返回内容无法解析");

            string tag = (release.TagName ?? "").TrimStart('v', 'V');
            if (!Version.TryParse(tag, out var remote))
                return UpdateCheckResult.Fail(Loc.F("无法识别版本号：{0}", release.TagName));
            var current = Version.Parse(CurrentVersion);
            bool newer = remote > current;

            /* 挑包：【必须】按本机系统 + 架构挑，不能取"第一个 exe"。
             * GitHub 的 assets 按名字排序返回，Win7 自包含包会排在主包前面
             * （`KeyboardEnhance-4.5.27-x64-Win7-…` < `KeyboardEnhance.exe`），
             * 于是 Win11 上也会被推 67 MB 的包。详见 PickAsset 的注释。 */
            var names = (release.Assets ?? new List<GitHubAsset>())
                .Select(a => a.Name ?? "")
                .Where(n => n.Length > 0)
                .ToList();
            var pick = PickAsset(names, IsLegacyWindows, ProcessArch);
            var asset = pick.Any
                ? release.Assets?.FirstOrDefault(a =>
                    string.Equals(a.Name, pick.Name, StringComparison.Ordinal))
                : null;

            if (asset == null)
                return new UpdateCheckResult(true, null,
                    newer ? Loc.F("发现新版本 {0}，但该版本没有附带 exe 安装包", tag) : "已是最新版本", newer);

            if (!pick.Exact)
            {
                CoreLog.Warn($"更新包没有完全匹配的形态（系统 Win7 代={IsLegacyWindows}、架构 {ProcessArch}），" +
                             $"退回 {pick.Name}；程序内不提供自动下载");
            }

            var info = new UpdateInfo(
                tag,
                asset.BrowserDownloadUrl ?? "",
                release.Body ?? "",
                release.HtmlUrl ?? ReleasesUrl,
                asset.Size,
                NormalizeDigest(asset.Digest),
                pick.Name,
                pick.Exact);

            return new UpdateCheckResult(true, info,
                newer ? Loc.F("发现新版本 {0}", tag) : "已是最新版本", newer);
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

    /// <summary>
    /// 下载到临时文件，返回文件路径。
    ///
    /// 第四项 <c>Canceled</c> 专门标出"用户主动取消"：它不该走到失败分支去
    /// 显示一句红色的"下载失败：操作已取消"—— 用户自己按的取消，不是错误。
    /// 临时文件在这条路上一样会删干净。
    /// </summary>
    public async Task<(bool Ok, string Path, string Error, bool Canceled)> DownloadAsync(
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
            return (false, "", "无法创建下载目录：" + ex.Message, false);
        }
        try
        {
            /* 两次重试：GitHub 的下载会 302 到 objects.githubusercontent.com，
             * 国内网络下偶发一次连接重置 —— 一次失败就报错，用户会觉得"更新坏了"。
             * 重试前把临时文件清掉，避免追加写。 */
            long read = 0;
            long total = info.Size;
            Exception? last = null;

            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (attempt > 0)
                {
                    TryDelete(temp);
                    await Task.Delay(TimeSpan.FromSeconds(1.5 * attempt), ct).ConfigureAwait(false);
                    progress?.Report(0);
                }

                try
                {
                    using var resp = await DownloadHttp
                        .GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct)
                        .ConfigureAwait(false);
                    resp.EnsureSuccessStatusCode();

                    total = resp.Content.Headers.ContentLength ?? info.Size;
                    await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    await using var dst = File.Create(temp);

                    var buffer = new byte[81920];
                    read = 0;
                    int n;
                    while ((n = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                    {
                        await dst.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                        read += n;
                        if (total > 0) progress?.Report((double)read / total);
                    }
                    await dst.FlushAsync(ct).ConfigureAwait(false);
                    dst.Close();

                    last = null;
                    break;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    last = ex;
                    CoreLog.Warn($"下载第 {attempt + 1} 次失败：{ex.Message}");
                }
            }

            if (last != null)
            {
                TryDelete(temp);
                return (false, "", "下载失败：" + last.Message, false);
            }

            if (info.Size > 0 && read != info.Size)
            {
                TryDelete(temp);
                return (false, "", Loc.F("下载不完整（{0}/{1} 字节）", read, info.Size), false);
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
                        Loc.F("校验失败，文件可能已损坏（期望 {0}…，", expected[..Math.Min(12, expected.Length)]) +
                        Loc.F("实际 {0}…）", actual[..12]), false);
                }
            }
            return (true, temp, "", false);
        }
        /* 取消必须排在通用 catch 前面，否则会被当成普通失败吞掉：
         * 用户点了「取消」，界面却显示"下载失败：操作已取消"—— 既难看，
         * 也会让后面的分支错误地进入"重试/打开下载页"的流程。 */
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 刚写完的 exe 常被 Defender 之类即时扫描占住句柄，一次删不掉；
            // 重试几次，仍不行就留给 CleanupLeftovers 的定期清理。
            await DeleteWithRetryAsync(temp).ConfigureAwait(false);
            return (false, "", "", true);
        }
        catch (Exception ex)
        {
            TryDelete(temp);
            return (false, "", "下载失败：" + ex.Message, false);
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
            string staged = current + ".new";

            /* 顺序很重要：**先落盘到最终位置旁边 → 再改名换岗**。
             * 复制是整个流程里最容易失败的一步（磁盘满、杀毒锁文件），
             * 它必须在不影响现状的前提下进行 —— 直接往 current 上复制，
             * 一旦失败，程序名已经被改掉，用户手上什么都没有。 */
            File.Copy(downloadedPath, staged, true);

            /* 去掉下载文件自带的"来自互联网"标记（Zone.Identifier 流）。
             * File.Copy 会把这个流一起带过来，不清掉的话第一次程序内更新
             * 会被 SmartScreen 拦一道（"Windows 保护了你的电脑"），
             * 用户手一滑点"不运行"，这台机器上就再也没有 KeyboardEnhance 了。 */
            try { Core.Interop.NativeMethods.DeleteFile(staged + ":Zone.Identifier"); }
            catch { }   /* 预期内：清理动作，失败无副作用 */

            // 1) 当前 exe 改名（不会失败于"正在使用"）
            File.Move(current, old, true);
            // 2) 新文件就位（同目录同卷，一次原子改名）
            File.Move(staged, current, true);

            // 3) 启动新版本（只有一个 exe，就是它自己）。
            //    带 --elevated，避免再次弹 UAC
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
                // 换岗没走完留下的 .new（见 ApplyAndRestart 的暂存逻辑）
                string stagedNew = current + ".new";
                try { if (File.Exists(stagedNew)) File.Delete(stagedNew); } catch { }
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
                    catch { }   /* 预期内：探测/重试型，失败即视为无结果 */
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

    /// <summary>
    /// 删下载残留，失败重试。
    ///
    /// 取消下载时文件刚写完，杀毒软件（尤其是 Defender 的实时扫描）往往还捏着
    /// 句柄 —— 直接 <c>File.Delete</c> 会抛 IOException。实测取消后 <c>updates/</c>
    /// 里会留下半个安装包，所以这里等一小会儿再删。
    /// </summary>
    private static async Task DeleteWithRetryAsync(string path)
    {
        for (int i = 0; i < 4; i++)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
                return;
            }
            catch (IOException) { }                    /* 预期内：句柄未释放，重试 */
            catch (UnauthorizedAccessException) { }    /* 同上 */
            try { await Task.Delay(250).ConfigureAwait(false); } catch { return; }
        }
        CoreLog.Warn("下载残留删不掉，留给下次启动清理：" + path);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }   /* 预期内：清理动作，失败无副作用 */
    }

    /// <summary>
    /// 验证一个令牌是否真的能用。
    ///
    /// 为什么不用 `/rate_limit`：那个端点**匿名也能调**，随便填一串乱码它照样返回
    /// 200 和一份"配额" —— 用它验证等于什么都没验。这里打 `/user`：
    /// 必须带有效凭据才会 200，同时顺手把配额头（X-RateLimit-*）读回来，
    /// 用户能直接看到"这个令牌是 5000/小时还是 60/小时"。
    /// </summary>
    public async Task<TokenCheckResult> VerifyTokenAsync(string? token, CancellationToken ct = default)
    {
        string pat = NormalizeToken(token);
        if (pat.Length == 0) return new TokenCheckResult(false, "", "先填一个令牌再验证");

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", pat);
            req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);

            int limit = ReadIntHeader(resp, "X-RateLimit-Limit");
            int remain = ReadIntHeader(resp, "X-RateLimit-Remaining");

            if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                return new TokenCheckResult(false, "", "令牌无效、已过期或已被撤销（HTTP 401）");

            if (resp.StatusCode == System.Net.HttpStatusCode.Forbidden)
                return new TokenCheckResult(false, "",
                    remain == 0
                        ? "令牌本身有效，但这个 IP 的配额刚好用完了，一小时后再试"
                        : "令牌被拒绝（HTTP 403）—— fine-grained 令牌需要至少 Public repositories 的读权限");

            if (!resp.IsSuccessStatusCode)
                return new TokenCheckResult(false, "", Loc.F("GitHub 返回 HTTP {0}", (int)resp.StatusCode));

            var user = await resp.Content.ReadFromJsonAsync<GitHubUser>(cancellationToken: ct)
                .ConfigureAwait(false);

            string who = string.IsNullOrWhiteSpace(user?.Login) ? "（仓库只读，账号名不可见）" : "@" + user!.Login;
            return new TokenCheckResult(true, user?.Login ?? "",
                Loc.F("令牌有效：{0}　·　配额 {1}/小时，当前剩余 {2}", who, limit, remain), limit, remain);
        }
        catch (TaskCanceledException)
        {
            return new TokenCheckResult(false, "", "验证超时，检查一下网络或代理");
        }
        catch (Exception ex)
        {
            CoreLog.Warn("验证 GitHub 令牌失败：" + ex.Message);
            return new TokenCheckResult(false, "", "验证失败：" + ex.Message);
        }
    }

    private static int ReadIntHeader(HttpResponseMessage resp, string name) =>
        resp.Headers.TryGetValues(name, out var v) && int.TryParse(v.FirstOrDefault(), out int n) ? n : 0;

    // ---------------- GitHub API 模型 ----------------

    private sealed class GitHubUser
    {
        [JsonPropertyName("login")] public string? Login { get; set; }
    }

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
