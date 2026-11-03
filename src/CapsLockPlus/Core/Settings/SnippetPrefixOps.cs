using CapsLockPlus.Core.Settings;

namespace CapsLockPlus.Core.Settings;

/// <summary>
/// 前缀符号的整批迁移。
///
/// **要解决的问题**：别名（片段）把前缀符号存在每条 <see cref="Snippet.Trigger"/> 里
/// （例如 <c>;mail</c>）。用户在「别名替换」里把前缀从 <c>;</c> 改成 <c>/</c> 时，
/// 以前只改了设置里的那一个字符，已经存在的条目还是 <c>;mail</c> ——
/// 列表按新前缀去剥，剥不掉，于是**每条别名前面都多出一个旧前缀**。
///
/// 这里在改前缀的瞬间把已有触发词一次性搬过去，保证设置和条目始终一致。
/// </summary>
public static class SnippetPrefixOps
{
    /// <summary>
    /// 把所有片段的触发词从 <paramref name="oldPrefix"/> 换成 <paramref name="newPrefix"/>。
    /// 返回实际改动的条数。
    /// </summary>
    public static int Migrate(AppSettings settings, string? oldPrefix, string? newPrefix)
    {
        oldPrefix ??= "";
        newPrefix ??= "";

        if (oldPrefix == newPrefix) return 0;

        int changed = 0;
        foreach (var snippet in settings.Global.Snippets)
        {
            string trigger = snippet.Trigger ?? "";
            if (trigger.Length == 0) continue;

            string next;

            if (oldPrefix.Length > 0 && trigger.StartsWith(oldPrefix, StringComparison.Ordinal))
            {
                /* 正常情况：把旧前缀换成新的。 */
                next = newPrefix + trigger[oldPrefix.Length..];
            }
            else if (newPrefix.Length > 0 && trigger.StartsWith(newPrefix, StringComparison.Ordinal))
            {
                /* 已经是新前缀了（用户改回原样、或这条是刚建的）→ 不动。 */
                next = trigger;
            }
            else
            {
                /* 既不是旧前缀也不是新前缀：多半是手写进来的裸词。
                 * 统一补上新前缀，否则列表里它会一直显示成"没前缀"的怪样子。 */
                next = newPrefix + trigger;
            }

            if (next != trigger)
            {
                snippet.Trigger = next;
                changed++;
            }
        }

        return changed;
    }

}
