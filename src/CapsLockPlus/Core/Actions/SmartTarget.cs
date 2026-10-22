using System.IO;
using System.Text.RegularExpressions;

namespace CapsLockPlus.Core.Actions;

/// <summary>「直达链接」识别出来的目标类型。</summary>
public enum SmartKind
{
    /// <summary>既不像网址，也不像路径（多行文字、普通句子）。</summary>
    None,
    /// <summary>网址：交给默认浏览器。</summary>
    Url,
    /// <summary>存在的文件夹：交给资源管理器。</summary>
    Folder,
    /// <summary>存在的文件：交给系统默认程序。</summary>
    File,
    /// <summary>看起来是文件夹，但磁盘上不存在。</summary>
    MissingFolder,
    /// <summary>看起来是文件，但磁盘上不存在。</summary>
    MissingFile,
}

/// <summary>一次识别的结果。</summary>
/// <param name="Kind">识别出的类型。</param>
/// <param name="Target">整理后可以直接交给 shell 的目标（网址已补协议，路径已去引号）。</param>
/// <param name="Raw">用户选中的原文，提示信息里原样回显用。</param>
public sealed record SmartTarget(SmartKind Kind, string Target, string Raw)
{
    /// <summary>类别名，用于提示与日志（"网址" / "文件夹" / "文件"）。</summary>
    public string KindName => Kind switch
    {
        SmartKind.Url => "网址",
        SmartKind.Folder or SmartKind.MissingFolder => "文件夹",
        SmartKind.File or SmartKind.MissingFile => "文件",
        _ => "未知",
    };

    /// <summary>是否为"识别成功且目标存在"的类型。</summary>
    public bool IsOpenable => Kind is SmartKind.Url or SmartKind.Folder or SmartKind.File;
}

/// <summary>
/// 「直达链接」的智能识别：把用户选中的一段文字判断成网址 / 文件夹 / 文件。
///
/// ## 为什么要有这一层
///
/// 上一版是"让用户预先填好网址或文件夹，按键时照单打开"。实际用起来，
/// 最常见的情形恰好相反：**眼前已经有一个链接或路径了**，选中它，
/// 想让它直接打开 —— 再让人先去设置页填一遍，等于把负担转回给用户。
///
/// 所以这里做纯识别：输入一串文字，输出一个明确的类型 + 整理好的目标。
/// 真正的"打开"交给 <see cref="ActionContext"/>（它才知道怎么调 shell）。
///
/// ## 判断顺序（顺序本身就是规则，别随意调换）
///
///   ① `file:///…` —— 先转本地路径，否则会被当成普通网址（`file` 也是协议名）
///   ② 带协议的网址（`https://`、`ftp://`、`mailto:`…）—— 最不会有歧义
///   ③ 路径特征（盘符 `D:\`、UNC `\\server`、`%ENV%`、`~`、`shell:`）
///      必须先于"裸域名"，否则 `D:\a.com\x` 会被当成网址
///   ④ 相对路径能命中磁盘上的真实文件/目录 → 按文件/目录处理
///   ⑤ 裸域名（github.com、www.baidu.com/s?wd=x）
///   ⑥ 都不是 → <see cref="SmartKind.None"/>
///
/// 第 ④ 步之所以要排掉一批"看着像域名、其实是扩展名"的后缀
/// （`.md`、`.py`、`.txt`…），是因为 `readme.md`、`build.ps1`
/// 这类选中内容太常见了，把它们丢进浏览器只会让人莫名其妙。
/// </summary>
public static class SmartTargetDetector
{
    // ---- 正则：都用 Compiled，识别发生在按键回调里，越快越好 ----

    /// <summary>带协议的网址：`https://`、`ftp://`、`a+b://`…</summary>
    private static readonly Regex SchemeRe =
        new(@"^[a-zA-Z][a-zA-Z0-9+.\-]*://", RegexOptions.Compiled);

    /// <summary>`mailto:` / `tel:` 这类没有 `//` 的协议。</summary>
    private static readonly Regex SpecialSchemeRe =
        new(@"^(mailto|tel|magnet|callto|ms-settings|ms-screenclip):", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>盘符路径：`D:\` 或 `D:/`。</summary>
    private static readonly Regex DriveRe =
        new(@"^[a-zA-Z]:[\\/]", RegexOptions.Compiled);

    /// <summary>`%USERPROFILE%` 这类环境变量开头。</summary>
    private static readonly Regex EnvRe =
        new(@"^%[^%]+%", RegexOptions.Compiled);

    /// <summary>
    /// 裸域名：`github.com`、`www.baidu.com/s?wd=x`、`localhost:8080`（不带点的不算）。
    /// TLD 允许 2~24 个字母（`.museum`、`.software` 这类长后缀也认）。
    /// </summary>
    private static readonly Regex BareDomainRe = new(
        @"^(?=.{1,253}$)(?:[a-zA-Z0-9](?:[a-zA-Z0-9\-]{0,61}[a-zA-Z0-9])?\.)+[a-zA-Z]{2,24}(?::\d{1,5})?(?:[/?#]\S*)?$",
        RegexOptions.Compiled);

    /// <summary>
    /// 长得像域名、实际几乎总是文件名的后缀。
    /// 命中它们就不往"网址"上判，免得 `readme.md` 被浏览器打开。
    /// </summary>
    private static readonly string[] FileLikeTlds =
    {
        "txt", "md", "rst", "pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "csv",
        "zip", "rar", "7z", "tar", "gz", "bz2", "xz", "iso", "img", "bin", "dmg",
        "png", "jpg", "jpeg", "gif", "bmp", "webp", "svg", "ico", "tif", "tiff", "psd",
        "mp3", "mp4", "avi", "mkv", "mov", "wmv", "flv", "wav", "flac", "aac", "m4a", "webm",
        "exe", "msi", "dll", "sys", "bat", "cmd", "ps1", "vbs", "jar", "apk", "msix",
        "sh", "py", "js", "mjs", "cjs", "ts", "jsx", "tsx", "java", "kt", "cs", "vb",
        "c", "h", "cpp", "hpp", "cc", "go", "rs", "rb", "php", "lua", "pl", "swift", "m",
        "html", "htm", "css", "scss", "less", "vue", "svelte",
        "json", "xml", "yaml", "yml", "toml", "ini", "cfg", "conf", "env", "properties",
        "log", "db", "sql", "sqlite", "bak", "tmp", "dat", "lock", "list", "map",
    };

    /// <summary>网址末尾常见的标点（选中一句话时经常被一起选中）。</summary>
    private const string UrlTrailingPunct = ".,;:!?'\"。，；：！？、》」』”’";

    /// <summary>路径末尾常见的中文标点（`.`,`:` 不剥，文件名里可能真有）。</summary>
    private const string PathTrailingPunct = "。，；！？、》」』”’";

    /// <summary>超过这个长度就不当链接处理了（真实路径/网址远小于此）。</summary>
    private const int MaxLength = 2048;

    /// <summary>识别一段文字（真实文件系统）。</summary>
    public static SmartTarget Detect(string? raw) =>
        Detect(raw, File.Exists, Directory.Exists);

    /// <summary>
    /// 识别一段文字，文件/目录是否存在由调用方给出。
    ///
    /// 抽成可注入是为了自检：测试机上不可能真有 `D:\Work\项目\a.docx`，
    /// 但"存在 → 文件"这条规则必须能验。
    /// </summary>
    public static SmartTarget Detect(string? raw,
        Func<string, bool> fileExists, Func<string, bool> dirExists)
    {
        string text = Clean(raw);
        if (text.Length == 0) return new SmartTarget(SmartKind.None, "", raw ?? "");

        /* ① file:/// 必须在"带协议网址"之前处理。
         *
         * `file` 本身是个合法协议名，先走 SchemeRe 就会被当成网址丢给浏览器 ——
         * 浏览器虽然也能打开 file:/// 地址，但我们要的是"用默认程序打开这个文件"，
         * 而且路径要经过 ExpandPath 归一（正斜杠、环境变量、尾部分隔符）。 */
        if (text.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            string local = FileUriToPath(text);
            if (local.Length > 0)
                return ResolvePath(local, raw ?? "", fileExists, dirExists);
        }

        // ② 带协议的网址
        if (SchemeRe.IsMatch(text) || SpecialSchemeRe.IsMatch(text))
        {
            /* 选中一句话时，末尾的句号/逗号经常被一起选进来
             * （"官网是 https://a.com。"）。那个句号不属于网址，
             * 留着会让浏览器打开一个 404 的地址。 */
            string url = text.TrimEnd(UrlTrailingPunct.ToCharArray());
            return new SmartTarget(SmartKind.Url, url.Length > 0 ? url : text, raw ?? "");
        }

        // ③ 路径特征
        if (LooksLikePath(text))
            return ResolvePath(text, raw ?? "", fileExists, dirExists);

        // ④ 相对路径恰好命中真实存在的文件/目录（少数场景：选中了当前目录下的文件名）
        if (SafeExists(fileExists, text))
            return new SmartTarget(SmartKind.File, text, raw ?? "");
        if (SafeExists(dirExists, text))
            return new SmartTarget(SmartKind.Folder, text, raw ?? "");

        // ⑤ 裸域名
        string bare = text.TrimEnd(UrlTrailingPunct.ToCharArray());
        if (BareDomainRe.IsMatch(bare) && !HasFileLikeTld(bare))
            return new SmartTarget(SmartKind.Url, bare, raw ?? "");

        return new SmartTarget(SmartKind.None, "", raw ?? "");
    }

    /// <summary>
    /// 清洗原始文本：去首尾空白、去成对包裹的引号（`"…"`、`「…」`）。
    ///
    /// 多行内容一律判为"不是链接" —— 选了一整段话时按下去，
    /// 与其猜里面哪一行像路径，不如明确告诉用户"这不是链接"。
    /// </summary>
    internal static string Clean(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        string s = raw.Trim();
        if (s.Length == 0 || s.Length > MaxLength) return "";
        if (s.Contains('\n') || s.Contains('\r')) return "";

        s = TrimQuotes(s);

        return s.Trim();
    }

    private static string TrimQuotes(string s)
    {
        if (s.Length < 2) return s;

        (char Open, char Close)[] pairs =
        {
            ('"', '"'), ('\'', '\''), ('“', '”'), ('‘', '’'),
            ('「', '」'), ('『', '』'), ('《', '》'), ('<', '>'),
        };

        foreach (var (open, close) in pairs)
        {
            if (s[0] == open && s[^1] == close) return s[1..^1].Trim();
        }
        return s;
    }

    /// <summary>是否带路径特征（盘符 / UNC / 环境变量 / ~ / shell: / 反斜杠路径）。</summary>
    private static bool LooksLikePath(string text)
    {
        if (DriveRe.IsMatch(text)) return true;
        if (text.StartsWith(@"\\", StringComparison.Ordinal)) return true;
        if (text.StartsWith("//", StringComparison.Ordinal)) return true;
        if (EnvRe.IsMatch(text)) return true;
        if (text == "~" || text.StartsWith(@"~\", StringComparison.Ordinal) ||
            text.StartsWith("~/", StringComparison.Ordinal)) return true;
        if (text.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }

    /// <summary>
    /// 把字面路径变成明确的类型。
    /// 存在性优先于"看后缀猜"：`D:\Work.Bak` 是个目录，不能因为有点就当成文件。
    /// </summary>
    private static SmartTarget ResolvePath(string pathText, string raw,
        Func<string, bool> fileExists, Func<string, bool> dirExists)
    {
        bool trailingSeparator =
            pathText.EndsWith('\\') || pathText.EndsWith('/');

        string p = ActionContext.ExpandPath(pathText.TrimEnd(PathTrailingPunct.ToCharArray()));
        if (p.Length == 0) return new SmartTarget(SmartKind.None, "", raw);

        // shell:Downloads 这类虚拟路径没法用 Directory.Exists 判断，直接当文件夹
        if (p.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
            return new SmartTarget(SmartKind.Folder, p, raw);

        if (SafeExists(dirExists, p)) return new SmartTarget(SmartKind.Folder, p, raw);
        if (SafeExists(fileExists, p)) return new SmartTarget(SmartKind.File, p, raw);

        if (trailingSeparator) return new SmartTarget(SmartKind.MissingFolder, p, raw);

        string ext = SafeExtension(p);
        if (ext.Length is >= 2 and <= 13) return new SmartTarget(SmartKind.MissingFile, p, raw);
        return new SmartTarget(SmartKind.MissingFolder, p, raw);
    }

    /// <summary>`file:///C:/a/b.txt` → `C:/a/b.txt`。</summary>
    private static string FileUriToPath(string uri)
    {
        try
        {
            string s = uri["file://".Length..];
            s = Uri.UnescapeDataString(s);
            // file:///C:/… 前面会留一个斜杠
            if (s.StartsWith('/') && s.Length > 2 && s[2] == ':') s = s[1..];
            s = s.TrimStart('/');
            return s;
        }
        catch { return ""; }
    }

    private static bool HasFileLikeTld(string domainish)
    {
        string s = domainish;

        // 去掉端口 / 路径 / 查询串，只留 host
        int cut = s.IndexOfAny(new[] { '/', '?', '#', ':' });
        if (cut >= 0) s = s[..cut];

        int dot = s.LastIndexOf('.');
        if (dot < 0 || dot == s.Length - 1) return false;

        string tld = s[(dot + 1)..].ToLowerInvariant();
        foreach (string f in FileLikeTlds)
            if (tld == f) return true;
        return false;
    }

    private static bool SafeExists(Func<string, bool> probe, string path)
    {
        try { return probe(path); } catch { return false; }
    }

    private static string SafeExtension(string path)
    {
        try { return Path.GetExtension(path); } catch { return ""; }
    }
}
