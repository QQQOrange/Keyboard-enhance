using CapsLockPlus.Core.Settings;

namespace CapsLockPlus.Core.Input;

/// <summary>
/// 文本片段的匹配规则（纯函数，便于自检）。
///
/// ## 为什么规则定得这么"小气"
///
/// 片段展开是**不可逆的输入副作用**：一旦在正常打字时误触发，
/// 用户看到的是一段话被替换成别的东西，比"没反应"糟糕得多。
/// 所以宁可少匹配，也不要错匹配：
///
///   ① 触发词必须落在**词边界**上 —— 前一个字符不能是字母/数字，
///      于是 `email` 不会在 `myemail` 里被触发；
///   ② 只认**非字母数字开头**的触发词（`;mail`、`/sig`）——
///      正常打字几乎不会出现这种前缀；
///   ③ 完全大小写敏感 —— `;Mail` 和 `;mail` 是两条不同的片段，
///      避免用户以为自己只配了一条。
/// </summary>
public static class SnippetMatcher
{
    /// <summary>缓冲区最多记多少字符（够放几个触发词，也防止无限增长）。</summary>
    public const int MaxBuffer = 64;

    /// <summary>这个字符算"词字符"吗（字母/数字/下划线）。</summary>
    public static bool IsWordChar(char c)
        => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>配置的触发词是否合法（非空、以非字母数字开头、且不纯空白）。</summary>
    public static bool IsValidTrigger(string? trigger)
    {
        if (string.IsNullOrEmpty(trigger)) return false;
        if (trigger.Any(char.IsWhiteSpace)) return false;
        return !IsWordChar(trigger[0]);
    }

    /// <summary>
    /// 在当前缓冲区里找一条可展开的片段；没有则返回 null。
    /// 命中条件是"缓冲区以触发词结尾，且触发词前面不是词字符"。
    /// </summary>
    public static Snippet? Match(IEnumerable<Snippet>? snippets, string buffer)
        => Match(snippets, buffer, null);

    /// <summary>
    /// 带前台程序的匹配。<paramref name="process"/> 为空表示"不知道前台是什么"，
    /// 此时**不做限定**（fail open）：判定不了就别拦，否则片段会在所有程序里一起失效。
    /// </summary>
    public static Snippet? Match(IEnumerable<Snippet>? snippets, string buffer, string? process)
    {
        if (snippets == null || string.IsNullOrEmpty(buffer)) return null;

        Snippet? best = null;
        foreach (var s in snippets)
        {
            if (s == null || !IsValidTrigger(s.Trigger)) continue;
            if (!AllowedIn(s, process)) continue;
            if (!buffer.EndsWith(s.Trigger, StringComparison.Ordinal)) continue;

            int start = buffer.Length - s.Trigger.Length;
            if (start > 0 && IsWordChar(buffer[start - 1])) continue;   // 不在词边界

            // 多个触发词同时命中时取最长的（更具体的优先）
            if (best == null || s.Trigger.Length > best.Trigger.Length) best = s;
        }
        return best;
    }

    /// <summary>
    /// 这条片段是否允许在指定程序里展开。
    ///
    /// 三种情况：
    ///   · 没配 Apps → 到处都行（旧行为）；
    ///   · 配了 Apps 但拿不到前台进程（受保护进程等）→ **放行**（fail open）；
    ///   · 配了 Apps → 按进程名比较，不区分大小写，允许写 `.exe` 或不写。
    /// </summary>
    public static bool AllowedIn(Snippet snippet, string? process)
    {
        string apps = (snippet.Apps ?? "").Trim();
        if (apps.Length == 0) return true;
        if (string.IsNullOrWhiteSpace(process)) return true;

        string current = Normalize(process);
        foreach (string raw in apps.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (Normalize(raw) == current) return true;
        }
        return false;
    }

    /// <summary>进程名规范化：去空白、转小写、补 .exe，便于 `code` 与 `Code.exe` 互相匹配。</summary>
    public static string Normalize(string? process)
    {
        string p = (process ?? "").Trim().ToLowerInvariant();
        if (p.Length == 0) return "";
        return p.EndsWith(".exe", StringComparison.Ordinal) ? p : p + ".exe";
    }

    /// <summary>把缓冲区截到上限，只保留末尾部分。</summary>
    public static string Trim(string buffer)
        => buffer.Length <= MaxBuffer ? buffer : buffer[^MaxBuffer..];
}
