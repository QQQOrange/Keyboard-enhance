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
/// 翻译服务：内置 Windows 不需要任何密钥的 Google 免登录接口作为兜底，
/// 同时支持有道智云（需要 AppId / AppSecret）。
/// </summary>
public sealed class TranslateService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };

    public async Task<TranslateResult> TranslateAsync(string text, string provider,
        string appId, string appSecret, string target, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return TranslateResult.Fail("没有要翻译的文本");
        text = text.Length > 4000 ? text[..4000] : text;
        if (string.Equals(provider, "youdao", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(appId) && !string.IsNullOrWhiteSpace(appSecret))
        {
            try
            {
                return await YoudaoAsync(text, appId, appSecret, target, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                CoreLog.Warn("有道翻译失败，回退到内置引擎：" + ex.Message);
            }
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
