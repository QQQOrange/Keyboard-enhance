using System.Text.Json;
using System.Text.Json.Serialization;

namespace CapsLockPlus.Core.Settings;

/// <summary>
/// 规则分享：把"自己调好的那套东西"导出成一段文本，发给别人或另一台机器导入。
///
/// ## 范围
///
/// 只包含**独立规则**：基础改键、文本片段、双击动作、组合键、按应用层。
/// **不包含层内绑定** —— 那些和"第几层、哪个触发键"强耦合，导过去大概率
/// 与对方的层结构冲突，属于"看起来能导、导完不能用"，不如不做。
///
/// ## 合并语义
///
/// 与场景模板一致：**只添加缺失项**。
///   · 同一源键的改键 → 跳过
///   · 同一触发词的片段 → 跳过
///   · 同一键的双击动作 → 跳过
///   · 同一键对的组合键 → 跳过
///   · 同一进程的按应用规则 → 跳过
///
/// 因此重复导入同一份分享码不会产生重复项，用户不需要先"清空"。
/// </summary>
public static class RuleShare
{
    /// <summary>当前格式版本。将来加字段时靠它做兼容。</summary>
    public const int FormatVersion = 1;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed class Dto
    {
        public int ke { get; set; } = FormatVersion;
        public List<RemapDto>? remaps { get; set; }
        public List<SnippetDto>? snippets { get; set; }
        public List<GestureDto>? gestures { get; set; }
        public List<ChordDto>? chords { get; set; }
        public List<AppLayerDto>? appLayers { get; set; }
    }

    private sealed class RemapDto { public string from { get; set; } = ""; public string to { get; set; } = ""; }
    private sealed class SnippetDto { public string trigger { get; set; } = ""; public string text { get; set; } = ""; }
    private sealed class GestureDto { public string key { get; set; } = ""; public string action { get; set; } = ""; }
    private sealed class ChordDto { public string k1 { get; set; } = ""; public string k2 { get; set; } = ""; public string action { get; set; } = ""; }
    private sealed class AppLayerDto { public string process { get; set; } = ""; public int layer { get; set; } }

    /// <summary>导出成一段 JSON 文本（空配置返回 "{}"）。</summary>
    public static string Export(AppSettings s)
    {
        var dto = new Dto
        {
            remaps = s.Global.BaseRemaps
                .Where(r => !string.IsNullOrWhiteSpace(r.From) && !string.IsNullOrWhiteSpace(r.To))
                .Select(r => new RemapDto { from = r.From, to = r.To }).ToList(),
            snippets = s.Global.Snippets
                .Where(x => !string.IsNullOrWhiteSpace(x.Trigger))
                .Select(x => new SnippetDto { trigger = x.Trigger, text = x.Text }).ToList(),
            gestures = s.Global.KeyGestures
                .Where(g => !string.IsNullOrWhiteSpace(g.Key))
                .Select(g => new GestureDto { key = g.Key, action = g.DoubleTapAction }).ToList(),
            chords = s.Global.Chords
                .Where(c => !string.IsNullOrWhiteSpace(c.Key1))
                .Select(c => new ChordDto { k1 = c.Key1, k2 = c.Key2, action = c.Action }).ToList(),
            appLayers = s.Global.AppLayers
                .Where(a => !string.IsNullOrWhiteSpace(a.Process))
                .Select(a => new AppLayerDto { process = a.Process, layer = a.LayerId }).ToList(),
        };

        // 空数组不写进 JSON，分享码更短
        if (dto.remaps?.Count == 0) dto.remaps = null;
        if (dto.snippets?.Count == 0) dto.snippets = null;
        if (dto.gestures?.Count == 0) dto.gestures = null;
        if (dto.chords?.Count == 0) dto.chords = null;
        if (dto.appLayers?.Count == 0) dto.appLayers = null;

        return JsonSerializer.Serialize(dto, JsonOpts);
    }

    /// <summary>
    /// 从分享码导入。返回（新增, 跳过）。解析失败时 error 非空且不做任何修改。
    /// </summary>
    public static (int Added, int Skipped) Import(AppSettings s, string? code, out string? error)
    {
        error = null;
        int added = 0, skipped = 0;

        if (string.IsNullOrWhiteSpace(code))
        {
            error = "分享码是空的";
            return (0, 0);
        }

        Dto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<Dto>(code.Trim(), JsonOpts);
        }
        catch (Exception ex)
        {
            error = "分享码格式不对：" + ex.Message;
            return (0, 0);
        }

        if (dto == null)
        {
            error = "分享码是空的";
            return (0, 0);
        }

        if (dto.ke > FormatVersion)
        {
            error = Loc.F("这份分享码来自更新版本（v{0}），请先升级", dto.ke);
            return (0, 0);
        }

        foreach (var r in dto.remaps ?? new())
        {
            if (string.IsNullOrWhiteSpace(r.from) || string.IsNullOrWhiteSpace(r.to)) { skipped++; continue; }
            if (s.Global.BaseRemaps.Any(x => string.Equals(x.From, r.from, StringComparison.OrdinalIgnoreCase)))
            { skipped++; continue; }

            s.Global.BaseRemaps.Add(new KeyRemap { From = r.from, To = r.to, Comment = "import" });
            added++;
        }

        foreach (var x in dto.snippets ?? new())
        {
            if (!Input.SnippetMatcher.IsValidTrigger(x.trigger)) { skipped++; continue; }
            if (s.Global.Snippets.Any(y => y.Trigger == x.trigger)) { skipped++; continue; }

            s.Global.Snippets.Add(new Snippet { Trigger = x.trigger, Text = x.text ?? "", Comment = "import" });
            added++;
        }

        foreach (var g in dto.gestures ?? new())
        {
            if (string.IsNullOrWhiteSpace(g.key) || string.IsNullOrWhiteSpace(g.action)) { skipped++; continue; }
            if (s.Global.KeyGestures.Any(y => string.Equals(y.Key, g.key, StringComparison.OrdinalIgnoreCase)))
            { skipped++; continue; }

            s.Global.KeyGestures.Add(new KeyGesture { Key = g.key, DoubleTapAction = g.action, Comment = "import" });
            added++;
        }

        foreach (var c in dto.chords ?? new())
        {
            if (string.IsNullOrWhiteSpace(c.k1) || string.IsNullOrWhiteSpace(c.k2) || string.IsNullOrWhiteSpace(c.action))
            { skipped++; continue; }

            bool exists = s.Global.Chords.Any(y =>
                (string.Equals(y.Key1, c.k1, StringComparison.OrdinalIgnoreCase) && string.Equals(y.Key2, c.k2, StringComparison.OrdinalIgnoreCase)) ||
                (string.Equals(y.Key1, c.k2, StringComparison.OrdinalIgnoreCase) && string.Equals(y.Key2, c.k1, StringComparison.OrdinalIgnoreCase)));
            if (exists) { skipped++; continue; }

            s.Global.Chords.Add(new ChordRule { Key1 = c.k1, Key2 = c.k2, Action = c.action });
            added++;
        }

        foreach (var a in dto.appLayers ?? new())
        {
            string process = Input.AppLayerRules.Normalize(a.process);
            if (process.Length == 0 || a.layer <= 0) { skipped++; continue; }
            if (s.Global.AppLayers.Any(y => Input.AppLayerRules.Normalize(y.Process) == process))
            { skipped++; continue; }

            s.Global.AppLayers.Add(new AppLayerRule { Process = process, LayerId = a.layer });
            added++;
        }

        return (added, skipped);
    }
}
