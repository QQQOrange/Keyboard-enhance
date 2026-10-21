using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace CapsLockPlus.Core.Actions;

/// <summary>
/// 抓取网页标题，用来给「直达网页」预填备注。
///
/// 只在用户填完网址时跑一次，超时很短（3 秒）—— 用户正在界面里等，
/// 不能因为对方站点慢就卡住 UI。抓不到就静默返回 null，让用户自己写备注。
///
/// 不引入 HtmlAgilityPack 之类的依赖：只要 &lt;title&gt;，用正则足够，
/// 而且这里对"抓不准"的容忍度很高（用户可以改）。
/// </summary>
public static class WebTitleFetcher
{
    /// <summary>仅供自检：拦截真实网络请求。</summary>
    public static Func<string, string?>? InterceptForTest { get; set; }

    private static readonly HttpClient Client = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        };
        // 共享实例：HttpClient 每次 new 会耗尽 socket
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) };
    }

    /// <summary>
    /// 取网页标题。失败（网络错误、超时、非 HTML、没有 title）返回 null。
    /// </summary>
    /// <param name="url">任意写法的网址，内部会先规范化</param>
    public static string? Fetch(string? url)
    {
        string? target = UrlUtil.Normalize(url);
        if (target == null) return null;

        if (InterceptForTest != null) return InterceptForTest(target);

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, target);
            // 有些站点（知乎/淘宝）对陌生 UA 直接 403，伪装成浏览器
            req.Headers.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
                + "(KHTML, like Gecko) Chrome/120.0 Safari/537.36");
            req.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml");

            using var resp = Client.Send(req, HttpCompletionOption.ResponseContentRead);
            if (!resp.IsSuccessStatusCode) return null;

            // 只读前 64KB：title 一定在头部，没必要拖整个页面
            using var stream = resp.Content.ReadAsStream();
            var buf = new byte[64 * 1024];
            int n = 0, read;
            while (n < buf.Length && (read = stream.Read(buf, n, buf.Length - n)) > 0) n += read;
            if (n == 0) return null;

            string html = DecodeHtml(buf, n, resp.Content.Headers.ContentType?.CharSet);
            return ExtractTitle(html);
        }
        catch
        {
            // 网络问题不该影响用户绑按键，静默失败
            return null;
        }
    }

    /// <summary>按响应头里的 charset 解码；没有就试 UTF-8，失败退 GB18030。</summary>
    private static string DecodeHtml(byte[] buf, int len, string? charset)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(charset))
                return System.Text.Encoding.GetEncoding(charset.Trim('"', '\''))
                    .GetString(buf, 0, len);
        }
        catch { }

        try { return new System.Text.UTF8Encoding(false, true).GetString(buf, 0, len); }
        catch
        {
            // 中文站点常见 GBK
            try
            {
                System.Text.Encoding.RegisterProvider(
                    System.Text.CodePagesEncodingProvider.Instance);
                return System.Text.Encoding.GetEncoding("gb18030").GetString(buf, 0, len);
            }
            catch { return System.Text.Encoding.UTF8.GetString(buf, 0, len); }
        }
    }

    private static readonly Regex TitleRegex = new(
        @"<title[^>]*>(.*?)</title>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>从 HTML 里抠出 title 并做基本清洗。</summary>
    public static string? ExtractTitle(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;

        var m = TitleRegex.Match(html);
        if (!m.Success) return null;

        string t = m.Groups[1].Value;

        // 常见实体
        t = t.Replace("&amp;", "&").Replace("&lt;", "<").Replace("&gt;", ">")
             .Replace("&quot;", "\"").Replace("&#39;", "'").Replace("&apos;", "'")
             .Replace("&nbsp;", " ");

        // 折叠空白（HTML 里的换行缩进不该带进备注）
        t = Regex.Replace(t, @"\s+", " ").Trim();

        if (t.Length == 0) return null;
        return t.Length <= 60 ? t : t[..60];
    }
}
