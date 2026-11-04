using System.Text.Json;
using System.Text.Json.Serialization;

namespace CapsLockPlus.Core.Settings;

/// <summary>
/// 别名的导入 / 导出。
///
/// 为什么单独做：别名是用户攒出来的资产（邮箱、地址、工号……），
/// 换机器、重装系统、发给同事，都应该能带走。整份配置的导入导出太重
/// （会连按键方案、层、外观一起带走），这里只处理别名本身。
///
/// 设计取舍：
///   · 导出用 JSON（可读、可手改、可进版本库），字段名和内部模型一致；
///   · 导入默认**合并**：同触发词覆盖内容，新触发词追加，不删除本地已有条目；
///   · 不做前缀处理 —— 触发词里带不带前缀都按原样存，由调用方在导入后统一。
/// </summary>
public static class SnippetPortable
{
    /// <summary>导出文件里的一条别名（比内部模型少一些字段，避免泄漏无关状态）。</summary>
    public sealed class Entry
    {
        public string Trigger { get; set; } = "";
        public string Text { get; set; } = "";
        public string Comment { get; set; } = "";
        public string Group { get; set; } = "";
        public string Apps { get; set; } = "";
        public bool Enabled { get; set; } = true;
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>把别名序列化成 JSON 文本。</summary>
    public static string Export(IEnumerable<Snippet> snippets)
    {
        var list = new List<Entry>();
        foreach (var s in snippets)
        {
            list.Add(new Entry
            {
                Trigger = s.Trigger ?? "",
                Text = s.Text ?? "",
                Comment = s.Comment ?? "",
                Group = s.Group ?? "",
                Apps = s.Apps ?? "",
                Enabled = s.Enabled,
            });
        }

        return JsonSerializer.Serialize(list, Json);
    }

    /// <summary>
    /// 解析导入文件。返回 null 表示不是合法的别名文件（调用方据此提示用户）。
    /// </summary>
    public static List<Entry>? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            var list = JsonSerializer.Deserialize<List<Entry>>(json!, Json);
            if (list == null) return null;

            /* 触发词为空的条目没有意义（导入别人的手改文件时很常见）。 */
            list.RemoveAll(x => string.IsNullOrWhiteSpace(x.Trigger));
            return list;
        }
        catch
        {
            /* 预期内：用户选错了文件。返回 null，让界面给出提示而不是抛异常。 */
            return null;
        }
    }

    /// <summary>导入结果统计。</summary>
    public readonly record struct ImportResult(int Added, int Updated, int Ignored);

    /// <summary>
    /// 把解析出来的条目合并进 <paramref name="target"/>。
    ///
    /// 合并规则：触发词（忽略大小写、忽略两侧空白）相同 → 覆盖内容并保留本地分组为空时的原分组；
    /// 否则追加。导入不会删除任何本地条目。
    /// </summary>
    public static ImportResult Import(IEnumerable<Entry> entries, List<Snippet> target)
    {
        int added = 0, updated = 0, ignored = 0;

        foreach (var e in entries)
        {
            string trigger = (e.Trigger ?? "").Trim();
            if (trigger.Length == 0) { ignored++; continue; }

            var existing = target.FirstOrDefault(x =>
                string.Equals((x.Trigger ?? "").Trim(), trigger, StringComparison.CurrentCultureIgnoreCase));

            if (existing == null)
            {
                target.Add(new Snippet
                {
                    Trigger = trigger,
                    Text = e.Text ?? "",
                    Comment = e.Comment ?? "",
                    Group = e.Group ?? "",
                    Apps = e.Apps ?? "",
                    Enabled = e.Enabled,
                });
                added++;
                continue;
            }

            existing.Text = e.Text ?? existing.Text;
            existing.Comment = e.Comment ?? existing.Comment;
            existing.Apps = e.Apps ?? existing.Apps;
            existing.Enabled = e.Enabled;

            /* 分组以"导入文件里有值"为准；文件里没写就保留用户原来的分组。 */
            if (!string.IsNullOrWhiteSpace(e.Group)) existing.Group = e.Group.Trim();

            updated++;
        }

        return new ImportResult(added, updated, ignored);
    }
}
