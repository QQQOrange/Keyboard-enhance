using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;

namespace CapsLockPlus.Core.Sync;

/// <summary>WebDAV 连接参数。</summary>
public sealed class WebDavOptions
{
    /// <summary>服务器地址（目录形式）。</summary>
    public string Url { get; set; } = "";
    public string User { get; set; } = "";
    public string Pass { get; set; } = "";

    /// <summary>单次请求超时（秒）。退出时同步会调小，免得网盘不通导致关不掉软件。</summary>
    public int TimeoutSeconds { get; set; } = 20;

    public bool Ready => !string.IsNullOrWhiteSpace(Url);
}

/// <summary>云端上的一个备份文件。</summary>
public sealed record RemoteFile(string Name, DateTimeOffset Modified, long Size);

/// <summary>
/// 带 HTTP 状态码的 WebDAV 错误。
///
/// 为什么要单独一个类型：上层需要区分"目录还不存在（404/409，可以先建再试）"
/// 和"认证失败 / 没权限（建也没用）"。靠解析异常文本来判断太脆。
/// </summary>
public sealed class WebDavStatusException : InvalidOperationException
{
    public HttpStatusCode Status { get; }

    public WebDavStatusException(HttpStatusCode status, string message) : base(message)
        => Status = status;

    /// <summary>是不是"目录还不存在"这类可以靠 MKCOL 解决的错误。</summary>
    public bool IsMissingDirectory =>
        Status is HttpStatusCode.NotFound or HttpStatusCode.Conflict or HttpStatusCode.Gone;
}

/// <summary>
/// WebDAV 客户端。
///
/// 只做最普通的三件事：PUT 一个文件上去、GET 下来、PROPFIND 列目录。
/// 用 .NET 自带的 HttpClient，不引任何第三方库。
///
/// 地址处理参照 QuickSearch：用户常常只填 `https://dav.jianguoyun.com/dav/`
/// 这样的根地址，软件要自动补上自己的子目录；末尾没斜杠也要补，
/// 否则会拼出 `...dav/capslock-backup.json` 这种缺分隔符的地址。
/// </summary>
public sealed class WebDavClient
{
    /// <summary>
    /// 软件在网盘下用的子目录名。
    ///
    /// 用**可执行文件名**（KeyboardEnhance）而不是显示名：
    ///   * 显示名是「按键增强 / Keyboard Enhance」，带空格和中文，
    ///     当目录名既不好打也不好排在网盘里；
    ///   * 和 exe、备份扩展名 `.keyboardenhance` 保持一致，一眼能对上是同一个东西；
    ///   * 大小写敏感的服务（部分自建 WebDAV）也不会因为大小写变体建出两个目录。
    ///
    /// 早先用的是改名前的老名字 CapsLockPlus，老用户网盘里可能还留着那个目录，
    /// 见 <see cref="LegacySubdir"/> 的处理。
    /// </summary>
    public const string DefaultSubdir = "KeyboardEnhance";

    /// <summary>
    /// 改名前的子目录名。
    ///
    /// 只用于两件事：把老配置里"根地址 + 旧子目录"还原成纯根地址；
    /// 以及在探测时提醒用户"旧目录里还有备份"。
    /// 新上传一律走 <see cref="DefaultSubdir"/>。
    /// </summary>
    public const string LegacySubdir = "CapsLockPlus";

    /// <summary>稳定文件名：永远指向最新一份备份，方便下载时不必列目录。</summary>
    public const string LatestFile = "keyboardenhance-latest.keyboardenhance";

    /// <summary>老版本的稳定文件名。只用于读，不再写。</summary>
    public const string LegacyLatestFile = "capslock-backup.json";

    /// <summary>已知网盘根地址，用来判断用户是不是只填了半截。</summary>
    private static readonly string[] KnownRoots =
    {
        "https://dav.jianguoyun.com/dav/",
        "http://dav.jianguoyun.com/dav/",
    };

    private readonly WebDavOptions _opt;

    /// <summary>
    /// 把用户填的地址整理成「可存进配置」的形态 —— 不含软件自己的子目录。
    ///
    /// 干两件事：补 https://、末尾补 /。
    /// 另外做一件兼容老配置的事：早期版本可能把子目录一起写进配置，
    /// 这里把它剥掉还原成纯根地址（真正使用时再补回来）。
    /// </summary>
    public static string NormalizeInput(string raw)
    {
        string s = (raw ?? "").Trim();
        if (s.Length == 0) return "";

        // 只有没写协议时才补；用户写了 http:// 就尊重他
        if (!s.Contains("://", StringComparison.Ordinal)) s = "https://" + s;
        if (!s.EndsWith('/')) s += "/";

        // 还原「已知根地址 + 默认子目录」（新老名字都要认，否则老配置会多套一层）
        foreach (string sub in new[] { DefaultSubdir, LegacySubdir })
        {
            string suffix = sub + "/";
            if (s.Length <= suffix.Length) continue;
            if (!s.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;

            string prefix = s[..^suffix.Length];
            if (KnownRoots.Any(r => r.Equals(prefix, StringComparison.OrdinalIgnoreCase)))
                return prefix;
        }
        return s;
    }

    /// <summary>
    /// 整理成「一定是目录、且带软件子目录」的形态 —— 真正发请求时用它。
    ///
    /// 只填了已知网盘根地址时补上 KeyboardEnhance/ 子目录；
    /// 用户自己填了任意层级路径（如 /dav/我的备份/）则原样保留，不再多套一层。
    /// </summary>
    public static string NormalizeBase(string raw)
    {
        string s = NormalizeInput(raw);
        if (s.Length == 0) return s;

        if (KnownRoots.Any(r => r.Equals(s, StringComparison.OrdinalIgnoreCase)))
            return s + DefaultSubdir + "/";

        return s;
    }

    /// <summary>校验地址是否可用，返回错误说明（null = 没问题）。</summary>
    public static string? Validate(string raw)
    {
        string s = (raw ?? "").Trim();
        if (s.Length == 0) return null;      // 没填 = 没启用，不算错
        if (!Uri.TryCreate(NormalizeBase(s), UriKind.Absolute, out var uri))
            return "地址格式不对，应该像 https://dav.jianguoyun.com/dav/ 这样";
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return "只支持 http / https 地址";
        return null;
    }

    private Uri TargetOf(string fileName)
    {
        string baseUrl = NormalizeBase(_opt.Url);
        // 文件名里的空格等字符要转义，否则地址非法
        return new Uri(baseUrl + Uri.EscapeDataString(fileName));
    }

    /// <summary>共享的 HttpClient。整个客户端生命周期只建一次。</summary>
    private readonly HttpClient _client;

    /// <summary>按需建立的短超时客户端（退出同步用，避免拖慢关机）。</summary>
    private HttpClient? _quickClient;

    /// <summary>同步所有操作共用的锁（懒建 quick 客户端时用）。</summary>
    private readonly object _clientGate = new();

    public WebDavClient(WebDavOptions opt)
    {
        _opt = opt;
        _client = MakeClient(null);
    }

    /// <summary>
    /// 建一个配置好认证与 UA 的 HttpClient。
    ///
    /// ⚠ 绝不能每个请求 new 一个：
    ///   * <c>HttpClient.Dispose</c> 只释放托管对象，底层连接进入 TIME_WAIT
    ///     状态最长 4 分钟 —— 反复"new + Dispose"会耗尽临时端口
    ///     （SocketException: Only one usage of each socket address），
    ///     这是 .NET 里最著名的反模式之一；
    ///   * 这里改成**整个客户端生命周期共享一个实例**
    ///     （连接复用本来也是 HTTP/1.1 keep-alive 的设计意图）。
    /// </summary>
    private HttpClient MakeClient(int? timeoutSeconds)
    {
        var handler = new HttpClientHandler
        {
            // 网盘自签证书几乎不存在，但重定向要跟随（有些服务会 302 到实际存储）
            AllowAutoRedirect = true,
            PreAuthenticate = false,
        };
        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(timeoutSeconds ?? _opt.TimeoutSeconds),
        };
        // WebDAV 用 HTTP Basic；用户名密码非空才加，否则匿名访问也会被拒
        if (!string.IsNullOrEmpty(_opt.User) || !string.IsNullOrEmpty(_opt.Pass))
        {
            string token = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{_opt.User}:{_opt.Pass}"));
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Basic", token);
        }
        client.DefaultRequestHeaders.UserAgent.ParseAdd("KeyboardEnhance/1.6");
        return client;
    }

    /// <summary>取用于本次请求的客户端（quick=true 时给短超时的那份）。</summary>
    private HttpClient Client(bool quick = false)
    {
        if (!quick) return _client;
        lock (_clientGate)
        {
            _quickClient ??= MakeClient(Math.Min(8, _opt.TimeoutSeconds));
            return _quickClient;
        }
    }

    /// <summary>
    /// 连通性探测：能列目录就算通；目录不存在就先建出来再列。
    ///
    /// 为什么要建：用户刚填完地址点「测连接」时，云端那个子目录**通常还不存在**，
    /// 旧实现直接 PROPFIND → 404 → 提示"地址或目录不存在"，
    /// 而界面上写的是"子目录会自动创建" —— 说的和做的对不上。
    /// 现在先把 MKCOL 打出去，再列，第一次点就能通过。
    /// </summary>
    public async Task<string> ProbeAsync(CancellationToken ct = default)
    {
        try
        {
            var (files, created) = await ListEnsuringDirectoryAsync(ct).ConfigureAwait(false);
            return created
                ? $"连接成功，已自动创建目录 {DefaultSubdir}/，云端有 {files.Count} 份备份"
                : $"连接成功，云端有 {files.Count} 份备份";
        }
        catch (Exception e)
        {
            throw new InvalidOperationException(Describe(e), e);
        }
    }

    /// <summary>
    /// 列目录；目录不存在就先建再列。
    /// 返回 (文件列表, 是否新建了目录)。
    /// </summary>
    public async Task<(List<RemoteFile> Files, bool Created)> ListEnsuringDirectoryAsync(
        CancellationToken ct = default)
    {
        try
        {
            return (await ListAsync(ct).ConfigureAwait(false), false);
        }
        catch (WebDavStatusException e) when (e.IsMissingDirectory)
        {
            // 目录不存在 → 建出来再列一次。这是"刚填完地址"最常见的情况。
            bool created = await EnsureDirectoryAsync(ct).ConfigureAwait(false);
            var files = await ListAsync(ct).ConfigureAwait(false);
            return (files, created);
        }
    }

    /// <summary>上传一个文件。目录不存在时先尝试创建。</summary>
    public async Task UploadAsync(string fileName, string content,
        CancellationToken ct = default)
    {
        try
        {
            await EnsureDirectoryAsync(ct).ConfigureAwait(false);
            var uri = TargetOf(fileName);
            using var body = new StringContent(content, Encoding.UTF8, "application/json");
            using var resp = await _client.PutAsync(uri, body, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException(DescribeStatus(resp.StatusCode, "上传"));
            CoreLog.Info($"WebDAV：已上传 {fileName}（{content.Length} 字节）");
        }
        catch (Exception e) when (e is not InvalidOperationException)
        {
            throw new InvalidOperationException(Describe(e), e);
        }
    }

    /// <summary>下载一个文件；不存在返回 null。</summary>
    public async Task<string?> DownloadAsync(string fileName,
        CancellationToken ct = default)
    {
        try
        {
            var uri = TargetOf(fileName);
            using var resp = await _client.GetAsync(uri, ct).ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.NotFound) return null;
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException(DescribeStatus(resp.StatusCode, "下载"));
            string text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            CoreLog.Info($"WebDAV：已下载 {fileName}（{text.Length} 字符）");
            return text;
        }
        catch (Exception e) when (e is not InvalidOperationException)
        {
            throw new InvalidOperationException(Describe(e), e);
        }
    }

    /// <summary>删掉云端的一个文件（清理旧备份用）。不存在视为成功。</summary>
    public async Task DeleteAsync(string fileName, CancellationToken ct = default)
    {
        try
        {
            var uri = TargetOf(fileName);
            using var resp = await _client.DeleteAsync(uri, ct).ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.NotFound) return;
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException(DescribeStatus(resp.StatusCode, "删除"));
            CoreLog.Info($"WebDAV：已删除 {fileName}");
        }
        catch (Exception e) when (e is not InvalidOperationException)
        {
            throw new InvalidOperationException(Describe(e), e);
        }
    }

    /// <summary>取某个文件的修改时间；不存在返回 null。</summary>
    public async Task<DateTimeOffset?> GetModifiedAsync(string fileName,
        CancellationToken ct = default)
    {
        try
        {
            // 先试 HEAD（最省流量）；有些网盘不支持 HEAD，回落到 PROPFIND
            var uri = TargetOf(fileName);
            using (var head = new HttpRequestMessage(HttpMethod.Head, uri))
            using (var resp = await _client.SendAsync(head, ct).ConfigureAwait(false))
            {
                if (resp.StatusCode == HttpStatusCode.NotFound) return null;
                if (resp.IsSuccessStatusCode && resp.Content.Headers.LastModified != null)
                    return resp.Content.Headers.LastModified;
            }

            var files = await ListAsync(ct).ConfigureAwait(false);
            var hit = files.FirstOrDefault(f =>
                f.Name.Equals(fileName, StringComparison.OrdinalIgnoreCase));
            return hit?.Modified;
        }
        catch (Exception e)
        {
            CoreLog.Warn($"WebDAV：取修改时间失败（{Describe(e)}）");
            return null;
        }
    }

    /// <summary>列出目录里的备份文件（按时间倒序）。</summary>
    public Task<List<RemoteFile>> ListAsync(CancellationToken ct = default) =>
        ListAsync(_client, ct);

    /// <summary>列出目录的实现（给定客户端）。</summary>
    private async Task<List<RemoteFile>> ListAsync(HttpClient client,
        CancellationToken ct)
    {
        string baseUrl = NormalizeBase(_opt.Url);
        var req = new HttpRequestMessage(new HttpMethod("PROPFIND"), new Uri(baseUrl));
        req.Headers.Add("Depth", "1");
        req.Content = new StringContent(
            "<?xml version=\"1.0\"?><D:propfind xmlns:D=\"DAV:\">" +
            "<D:prop><D:getlastmodified/><D:getcontentlength/><D:resourcetype/></D:prop>" +
            "</D:propfind>", Encoding.UTF8, "application/xml");

        using var resp = await client.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new WebDavStatusException(resp.StatusCode,
                DescribeStatus(resp.StatusCode, "列出目录"));

        string xml = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return ParsePropfind(xml);
    }

    /// <summary>
    /// 解析 PROPFIND 的 XML。
    /// 命名空间前缀各家不一样（D: / d: / lp1:），所以按**本地名**匹配而不是前缀。
    /// </summary>
    public static List<RemoteFile> ParsePropfind(string xml)
    {
        var list = new List<RemoteFile>();
        try
        {
            var doc = XDocument.Parse(xml);
            foreach (var resp in doc.Descendants()
                         .Where(e => e.Name.LocalName == "response"))
            {
                string href = resp.Elements()
                    .FirstOrDefault(e => e.Name.LocalName == "href")?.Value ?? "";
                var prop = resp.Descendants()
                    .FirstOrDefault(e => e.Name.LocalName == "prop");
                if (prop == null) continue;

                string modRaw = prop.Elements()
                    .FirstOrDefault(e => e.Name.LocalName == "getlastmodified")?.Value ?? "";
                string lenRaw = prop.Elements()
                    .FirstOrDefault(e => e.Name.LocalName == "getcontentlength")?.Value ?? "";

                // 目录项（有 resourcetype/collection 子元素）跳过
                var rt = prop.Elements().FirstOrDefault(e => e.Name.LocalName == "resourcetype");
                if (rt != null && rt.Elements().Any(e => e.Name.LocalName == "collection"))
                    continue;

                string name = Uri.UnescapeDataString(
                    href.TrimEnd('/').Split('/').LastOrDefault() ?? "");
                if (name.Length == 0) continue;

                DateTimeOffset modified = DateTimeOffset.MinValue;
                ParseHttpDate(modRaw, out modified);
                long.TryParse(lenRaw, out long size);

                list.Add(new RemoteFile(name, modified, size));
            }
        }
        catch (Exception e)
        {
            CoreLog.Warn("WebDAV：解析目录列表失败 —— " + e.Message);
        }
        return list.OrderByDescending(f => f.Modified).ToList();
    }

    /// <summary>
    /// 解析 HTTP 日期（RFC 1123 形式，如 `Tue, 20 Oct 2026 05:00:00 GMT`）。
    ///
    /// 两个坑：
    ///   1. **必须用 InvariantCulture** —— 中文区域下认不出 "Tue" / "Oct"
    ///      这样的英文缩写，会静默返回 false，于是"哪份更新"的比较永远偏向
    ///      云端，同步方向就反了；
    ///   2. **星期几不能参与匹配** —— 有些服务器（或代理）发的日期星期是错的，
    ///      严格匹配 ddd 会整条失败。所以先按完整格式试，失败就把 "Xxx, "
    ///      前缀摘掉再试，这样星期写错也不影响。
    /// </summary>
    public static bool ParseHttpDate(string raw, out DateTimeOffset result)
    {
        result = DateTimeOffset.MinValue;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        string s = raw.Trim();
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var styles = System.Globalization.DateTimeStyles.AssumeUniversal
                     | System.Globalization.DateTimeStyles.AdjustToUniversal;

        // ① 标准 RFC 1123
        if (DateTimeOffset.TryParseExact(s, inv.DateTimeFormat.RFC1123Pattern,
                inv, styles, out result))
            return true;

        // ② 摘掉星期前缀再试 —— 星期写错也能解出来
        int comma = s.IndexOf(',');
        if (comma > 0 && comma < s.Length - 1)
        {
            string noDay = s[(comma + 1)..].Trim();
            string[] withoutDay =
            {
                "dd MMM yyyy HH:mm:ss 'GMT'",
                "d MMM yyyy HH:mm:ss 'GMT'",
                "dd MMM yyyy HH:mm:ss",
            };
            if (DateTimeOffset.TryParseExact(noDay, withoutDay, inv, styles, out result))
                return true;
        }

        // ③ 兜底：非标准但可解析的形式
        return DateTimeOffset.TryParse(s, inv, styles, out result);
    }

    /// <summary>
    /// 确保子目录存在（不存在就 MKCOL）。
    /// 返回 true 表示这次真的新建了（201）。
    /// </summary>
    private async Task<bool> EnsureDirectoryAsync(CancellationToken ct)
    {
        string baseUrl = NormalizeBase(_opt.Url);
        try
        {
            var req = new HttpRequestMessage(new HttpMethod("MKCOL"), new Uri(baseUrl));
            using var resp = await _client.SendAsync(req, ct).ConfigureAwait(false);
            // 201 = 建好了；405 = 已存在；301/409 = 父目录问题，交给后面的请求去报错
            if (resp.StatusCode == HttpStatusCode.Created)
            {
                CoreLog.Info($"WebDAV：已创建目录 {baseUrl}");
                return true;
            }
            if (resp.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.Conflict)
                return false;
        }
        catch (Exception ex)
        {
            // MKCOL 失败不致命：很多网盘根目录已存在，直接 PUT 也能成功
            CoreLog.Warn("WebDAV：MKCOL 失败（忽略）：" + ex.Message);
        }
        return false;
    }

    /// <summary>把异常翻译成人能看懂的话。</summary>
    public static string Describe(Exception e) => e switch
    {
        TaskCanceledException or OperationCanceledException =>
            "连接超时 —— 检查网络，或地址是否写错",
        HttpRequestException h when h.StatusCode == HttpStatusCode.Unauthorized =>
            "认证失败 —— 用户名或密码不对（坚果云要用「应用密码」，不是登录密码）",
        HttpRequestException h when h.StatusCode == HttpStatusCode.Forbidden =>
            "服务器拒绝访问 —— 检查账号权限或目录是否存在",
        HttpRequestException h when h.StatusCode == HttpStatusCode.NotFound =>
            "地址不存在 —— 检查 WebDAV 地址是否写对",
        HttpRequestException h =>
            $"网络请求失败：{h.Message}",
        _ => e.Message,
    };

    private static string DescribeStatus(HttpStatusCode code, string action) => code switch
    {
        HttpStatusCode.Unauthorized => $"{action}失败：认证不通过（用户名或密码不对）",
        HttpStatusCode.Forbidden => $"{action}失败：没有权限",
        HttpStatusCode.NotFound => $"{action}失败：地址或目录不存在",
        HttpStatusCode.InsufficientStorage => $"{action}失败：网盘空间不足",
        _ => $"{action}失败：服务器返回 {(int)code} {code}",
    };
}