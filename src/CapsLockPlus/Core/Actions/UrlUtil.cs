namespace CapsLockPlus.Core.Actions;

/// <summary>
/// 网址的规范化与展示。
///
/// 「直达网页」让用户直接填网址，得容忍各种写法：
///   github.com                → https://github.com
///   www.baidu.com/s?wd=x      → https://www.baidu.com/s?wd=x
///   http://intranet/wiki      → 原样（内部 http 站点很常见）
///
/// 这些规则集中在这里，界面（键帽标签）和动作执行都用同一套，
/// 免得"键帽上显示一个样、点开又是另一个样"。
/// </summary>
public static class UrlUtil
{
    /// <summary>
    /// 把用户输入补成可用的绝对 URL；实在不是网址就返回 null。
    ///
    /// 不抛异常：键帽渲染路径上任何一次抛异常都会让整个键盘画不出来。
    /// </summary>
    public static string? Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        string s = input.Trim();

        // 已经带协议
        if (s.Contains("://", StringComparison.Ordinal))
            return Uri.TryCreate(s, UriKind.Absolute, out var u0) ? u0.ToString() : null;

        // 形如 "mailto:xxx" / "file:///xxx" 这类也放过
        if (s.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            return s;

        // 本地文件路径
        if (s.StartsWith(@"\\", StringComparison.Ordinal) || (s.Length > 2 && s[1] == ':' && s[2] == '\\'))
        {
            try { return new Uri(s).ToString(); } catch { return null; }
        }

        // 其余一律当作域名补 https
        return Uri.TryCreate("https://" + s, UriKind.Absolute, out var u1)
               && !string.IsNullOrEmpty(u1.Host)
            ? u1.ToString()
            : null;
    }

    /// <summary>
    /// 取出用于展示的短标签（域名，去掉 www.）。
    /// 取不到就返回"网页"，绝不返回空串 —— 键帽上没有文字会很难看。
    /// </summary>
    public static string HostOf(string? input)
    {
        var norm = Normalize(input);
        if (norm == null) return "网页";

        if (Uri.TryCreate(norm, UriKind.Absolute, out var u) && !string.IsNullOrEmpty(u.Host))
        {
            string h = u.Host;
            return h.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? h[4..] : h;
        }
        return "网页";
    }

    /// <summary>键帽上显示的标签：有备注用备注，否则用域名。</summary>
    public static string LabelOf(string? note, string? url) =>
        !string.IsNullOrWhiteSpace(note) ? note.Trim() : HostOf(url);
}
