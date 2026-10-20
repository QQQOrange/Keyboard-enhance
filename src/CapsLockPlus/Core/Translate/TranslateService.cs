using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CapsLockPlus.Core.Translate;

public sealed record TranslateResult(bool Success, string Text, string SourceLanguage, string TargetLanguage, string Error)
{
    public static TranslateResult Fail(string error) => new(false, "", "", "", error);
}

/// <summary>
/// 翻译服务：内置多个引擎，按需选择。
///
///   free   —— Google 免登录接口，不需要密钥，作为兜底
///   bing   —— Bing 免登录接口，不需要密钥，国内访问更稳
///   youdao —— 有道智云，需要 AppId / AppSecret
///   baidu  —— 百度翻译开放平台，需要 AppId / SecretKey
///   deepl  —— DeepL，需要 Auth Key
///   tencent—— 腾讯云翻译，需要 SecretId / SecretKey
///
/// 需要密钥的引擎一旦失败，会自动回退到免费引擎，不会让功能直接不可用。
/// </summary>
public sealed class TranslateService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };

    /// <summary>所有可选引擎（界面下拉框用）。</summary>
    public static readonly (string Id, string Name, bool NeedsKey)[] Providers =
    {
        ("free",    "免费引擎（Google，无需密钥）",  false),
        ("bing",    "必应翻译（无需密钥）",          false),
        ("youdao",  "有道智云",                     true),
        ("baidu",   "百度翻译",                     true),
        ("deepl",   "DeepL",                        true),
        ("tencent", "腾讯云翻译",                   true),
    };

    public async Task<TranslateResult> TranslateAsync(string text, string provider,
        string appId, string appSecret, string target, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return TranslateResult.Fail("没有要翻译的文本");
        text = text.Length > 4000 ? text[..4000] : text;

        bool hasKey = !string.IsNullOrWhiteSpace(appId) && !string.IsNullOrWhiteSpace(appSecret);
        try
        {
            switch (provider?.ToLowerInvariant())
            {
                case "bing":
                    return await BingAsync(text, target, ct).ConfigureAwait(false);

                case "youdao" when hasKey:
                    return await YoudaoAsync(text, appId, appSecret, target, ct).ConfigureAwait(false);

                case "baidu" when hasKey:
                    return await BaiduAsync(text, appId, appSecret, target, ct).ConfigureAwait(false);

                case "deepl" when hasKey:
                    return await DeepLAsync(text, appSecret, target, ct).ConfigureAwait(false);

                case "tencent" when hasKey:
                    return await TencentAsync(text, appId, appSecret, target, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            CoreLog.Warn($"翻译引擎 {provider} 失败，回退到免费引擎：{ex.Message}");
        }
        return await FreeAsync(text, target, ct).ConfigureAwait(false);
    }

    private static async Task<TranslateResult> FreeAsync(string text, string target, CancellationToken ct)
    {
        try
        {
            string tl = target switch
            {
                "zh-CHS" => "zh-CN",
                "en" => "en",
                "ja" => "ja",
                "ko" => "ko",
                _ => "zh-CN",
            };
            string url = $"https://translate.googleapis.com/translate_a/single?client=gtx&sl=auto&tl={tl}&dt=t&q={Uri.EscapeDataString(text)}";
            using var resp = await Http.GetAsync(url, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            var sb = new StringBuilder();
            string src = "auto";
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                var root = doc.RootElement;
                if (root.GetArrayLength() > 2 && root[2].ValueKind == JsonValueKind.String)
                    src = root[2].GetString() ?? "auto";
                if (root[0].ValueKind == JsonValueKind.Array)
                {
                    foreach (var seg in root[0].EnumerateArray())
                    {
                        if (seg.ValueKind == JsonValueKind.Array && seg.GetArrayLength() > 0 &&
                            seg[0].ValueKind == JsonValueKind.String)
                            sb.Append(seg[0].GetString());
                    }
                }
            }
            var result = sb.ToString();
            if (string.IsNullOrWhiteSpace(result)) return TranslateResult.Fail("没有拿到翻译结果");
            return new TranslateResult(true, result, src, tl, "");
        }
        catch (Exception ex)
        {
            return TranslateResult.Fail("翻译请求失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 必应免登录接口：不需要密钥，国内访问通常比 Google 稳。
    /// 返回的是 HTML 片段，用简单替换清掉标签即可。
    /// </summary>
    private static async Task<TranslateResult> BingAsync(string text, string target, CancellationToken ct)
    {
        string to = MapLang(target);
        string url = "https://www.bing.com/ttranslatev3?isVertical=1&IG=1&IID=1" +
                     $"&from=auto&to={Uri.EscapeDataString(to)}";
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["fromLang"] = "auto-detect",
            ["to"] = to,
            ["text"] = text,
        });
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        req.Headers.Add("User-Agent", "Mozilla/5.0");
        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var sb = new StringBuilder();
        if (root.TryGetProperty("translation", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var seg in arr.EnumerateArray())
                if (seg.TryGetProperty("text", out var t)) sb.Append(t.GetString());

        var result = sb.ToString();
        if (string.IsNullOrWhiteSpace(result)) return TranslateResult.Fail("必应没有返回结果");
        string src = root.TryGetProperty("detectedLanguage", out var dl)
            ? dl.GetProperty("language").GetString() ?? "auto" : "auto";
        return new TranslateResult(true, result, src, to, "");
    }

    /// <summary>百度翻译开放平台（通用翻译 API）。</summary>
    private static async Task<TranslateResult> BaiduAsync(string text, string appId, string secret,
        string target, CancellationToken ct)
    {
        string to = target switch
        {
            "zh-CHS" => "zh", "en" => "en", "ja" => "jp", "ko" => "kor",
            "fr" => "fra", "de" => "de", "ru" => "ru", "es" => "spa",
            _ => "zh",
        };
        string salt = Guid.NewGuid().ToString("N");
        string sign = Md5(appId + text + salt + secret);
        var form = new Dictionary<string, string>
        {
            ["q"] = text, ["from"] = "auto", ["to"] = to,
            ["appid"] = appId, ["salt"] = salt, ["sign"] = sign,
        };
        using var content = new FormUrlEncodedContent(form);
        using var resp = await Http.PostAsync("https://fanyi-api.baidu.com/api/trans/vip/translate",
            content, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.TryGetProperty("error_code", out var ec) && ec.ValueKind == JsonValueKind.String)
            return TranslateResult.Fail($"百度接口错误 {ec.GetString()}: " +
                (root.TryGetProperty("error_msg", out var em) ? em.GetString() : ""));
        var sb = new StringBuilder();
        if (root.TryGetProperty("trans_result", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var seg in arr.EnumerateArray())
                if (seg.TryGetProperty("dst", out var d)) sb.AppendLine(d.GetString());
        string src = root.TryGetProperty("from", out var f) ? f.GetString() ?? "auto" : "auto";
        return new TranslateResult(true, sb.ToString().Trim(), src, to, "");
    }

    /// <summary>DeepL：密钥放在 appSecret（Auth Key）。</summary>
    private static async Task<TranslateResult> DeepLAsync(string text, string authKey,
        string target, CancellationToken ct)
    {
        string to = target switch
        {
            "zh-CHS" => "ZH", "en" => "EN", "ja" => "JA", "ko" => "KO",
            "fr" => "FR", "de" => "DE", "ru" => "RU", "es" => "ES",
            _ => "ZH",
        };
        using var req = new HttpRequestMessage(HttpMethod.Post,
            "https://api-free.deepl.com/v2/translate");
        req.Headers.Add("Authorization", "DeepL-Auth-Key " + authKey);
        req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["text"] = text, ["target_lang"] = to, ["source_lang"] = "auto",
        });
        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        var sb = new StringBuilder();
        if (doc.RootElement.TryGetProperty("translations", out var arr) &&
            arr.ValueKind == JsonValueKind.Array)
            foreach (var t in arr.EnumerateArray())
                if (t.TryGetProperty("text", out var x)) sb.AppendLine(x.GetString());
        string src = "auto";
        if (doc.RootElement.TryGetProperty("translations", out var a2) &&
            a2.ValueKind == JsonValueKind.Array && a2.GetArrayLength() > 0 &&
            a2[0].TryGetProperty("detected_source_language", out var ds))
            src = ds.GetString() ?? "auto";
        return new TranslateResult(true, sb.ToString().Trim(), src, to, "");
    }

    /// <summary>腾讯云翻译：appId = SecretId，appSecret = SecretKey。</summary>
    private static async Task<TranslateResult> TencentAsync(string text, string secretId,
        string secretKey, string target, CancellationToken ct)
    {
        string to = target switch
        {
            "zh-CHS" => "zh", "en" => "en", "ja" => "ja", "ko" => "ko",
            "fr" => "fr", "de" => "de", "ru" => "ru", "es" => "es",
            _ => "zh",
        };
        // TC3-HMAC-SHA256 签名
        string host = "tmt.tencentcloudapi.com";
        string service = "tmt", action = "TextTranslate", version = "2018-03-21";
        string timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        string date = DateTimeOffset.UtcNow.ToString("yyyyMMdd");
        string payload = JsonSerializer.Serialize(new
        {
            SourceText = text, Source = "auto", Target = to, ProjectId = 0,
        });
        string canonicalRequest =
            "POST\n/\n\n" +
            $"content-type:application/json; charset=utf-8\nhost:{host}\n\n" +
            "content-type;host\n" +
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();

        string scope = $"{date}/{service}/tc3_request";
        string stringToSign =
            "TC3-HMAC-SHA256\n" +
            $"{timestamp}\n" +
            $"{scope}\n" +
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest))).ToLowerInvariant();

        byte[] key = HmacSha256(Encoding.UTF8.GetBytes("TC3" + secretKey),
                    Encoding.UTF8.GetBytes(date));
        key = HmacSha256(key, Encoding.UTF8.GetBytes(service));
        key = HmacSha256(key, Encoding.UTF8.GetBytes("tc3_request"));
        byte[] sign = HmacSha256(key, Encoding.UTF8.GetBytes(stringToSign));

        using var req = new HttpRequestMessage(HttpMethod.Post, $"https://{host}/")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("Authorization",
            "TC3-HMAC-SHA256 Credential=" + secretId + "/" + scope +
            ", SignedHeaders=content-type;host, Signature=" +
            Convert.ToHexString(sign).ToLowerInvariant());
        req.Headers.Add("X-TC-Action", action);
        req.Headers.Add("X-TC-Version", version);
        req.Headers.Add("X-TC-Timestamp", timestamp);

        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("Response", out var r) &&
            r.TryGetProperty("Error", out var err))
            return TranslateResult.Fail($"腾讯云错误: {err.GetProperty("Message").GetString()}");
        var sb = new StringBuilder();
        if (doc.RootElement.TryGetProperty("Response", out var r2) &&
            r2.TryGetProperty("TargetText", out var tt))
            sb.Append(tt.GetString());
        return new TranslateResult(true, sb.ToString().Trim(), "auto", to, "");
    }

    private static byte[] HmacSha256(byte[] key, byte[] data)
    {
        using var h = new HMACSHA256(key);
        return h.ComputeHash(data);
    }

    private static string Md5(string input) =>
        Convert.ToHexString(System.Security.Cryptography.MD5.HashData(
            Encoding.UTF8.GetBytes(input))).ToLowerInvariant();

    /// <summary>把界面上的语言代码映射成各引擎通用形式。</summary>
    private static string MapLang(string target) => target switch
    {
        "zh-CHS" => "zh-Hans", "en" => "en", "ja" => "ja", "ko" => "ko",
        "fr" => "fr", "de" => "de", "ru" => "ru", "es" => "es",
        _ => "zh-Hans",
    };

    /// <summary>有道智云。</summary>
    private static async Task<TranslateResult> YoudaoAsync(string text, string appId, string appSecret,
        string target, CancellationToken ct)
    {
        string salt = Guid.NewGuid().ToString("N");
        string curtime = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        string sign = Sha256(appId + Truncate(text) + salt + curtime + appSecret);
        string tl = target switch { "zh-CHS" => "zh-CHS", "en" => "en", _ => "zh-CHS" };
        var form = new Dictionary<string, string>
        {
            ["q"] = text,
            ["from"] = "auto",
            ["to"] = tl,
            ["appKey"] = appId,
            ["salt"] = salt,
            ["sign"] = sign,
            ["signType"] = "v3",
            ["curtime"] = curtime,
        };
        using var content = new FormUrlEncodedContent(form);
        using var resp = await Http.PostAsync("https://openapi.youdao.com/api", content, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        string code = root.TryGetProperty("errorCode", out var ec) ? ec.GetString() ?? "" : "";
        if (code != "0")
            return TranslateResult.Fail($"有道接口返回错误码 {code}（{YoudaoError(code)}）");
        var sb = new StringBuilder();
        if (root.TryGetProperty("translation", out var tr) && tr.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in tr.EnumerateArray()) sb.AppendLine(t.GetString());
        }
        if (root.TryGetProperty("basic", out var basic) && basic.TryGetProperty("explains", out var ex2) &&
            ex2.ValueKind == JsonValueKind.Array)
        {
            sb.AppendLine();
            foreach (var e in ex2.EnumerateArray()) sb.AppendLine("· " + e.GetString());
        }
        string l = root.TryGetProperty("l", out var ll) ? ll.GetString() ?? "" : "";
        return new TranslateResult(true, sb.ToString().Trim(), l, tl, "");
    }

    private static string Truncate(string q)
    {
        int len = q.Length;
        return len <= 20 ? q : q[..10] + len + q[^10..];
    }

    private static string Sha256(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string YoudaoError(string code) => code switch
    {
        "101" => "缺少必填参数",
        "102" => "不支持的语言类型",
        "103" => "翻译文本过长",
        "108" => "应用 ID 无效",
        "110" => "无相关服务",
        "202" => "签名校验失败，请检查密钥",
        "203" => "访问 IP 不在可访问 IP 列表中",
        "401" => "账户余额不足",
        "411" => "访问频率受限",
        _ => "未知错误",
    };
}
