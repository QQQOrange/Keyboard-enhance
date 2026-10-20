using System.Text.Json;
using System.Text.Json.Nodes;
using CapsLockPlus.Core;
using CapsLockPlus.Core.Settings;

namespace CapsLockPlus.Core.Sync;

/// <summary>
/// 一组可勾选的导出项。
///
/// <see cref="Keys"/> 是 <see cref="AppSettings"/> 上的属性名（PascalCase），
/// 导入侧按同一张表裁剪，两边不会各写一套"这个键怎么读"的规则。
/// </summary>
public sealed record ExportGroup(
    string Id,
    string Name,
    string Desc,
    string[] Keys,
    bool Private,
    bool DefaultChecked);

/// <summary>
/// 明文配置的按项导出 / 导入。
///
/// 与「备份」的分工（照搬 QuickSearch 的结论）：
///   * **备份**：整份配置 + 加密，恢复时全量覆盖，走 WebDAV 同步；
///   * **导出**：挑几项 + 不加密，可直接发给别人导入，是合并式的。
///
/// 把两者塞进同一个格式会立刻出乱子：同一个键在备份里是"整份替换"，
/// 在导出里是"只换这一项"。格式不分，语义就没法分。
/// 所以导出文件带 `kind: "clp-config"`，导入时靠它区分，
/// 并且**明确拒绝**加密备份（不是"试一下能不能解"，是"这不是明文导出格式"）。
/// </summary>
public static class ExportImport
{
    public const string ExportKind = "clp-config";

    /// <summary>导出文件的默认扩展名。用独立后缀，一眼看出是能给人的明文。</summary>
    public const string Extension = ".clpconf";

    /// <summary>导入文件大小上限（超过直接拒绝，避免把内存吃光）。</summary>
    public const long MaxBytes = 32L * 1024 * 1024;

    /// <summary>
    /// 可导出的组。
    ///
    /// ⚠ 一张表同时服务三处：勾选界面、导出时的键裁剪、导入时的合法性校验。
    /// **加一组只改这里**，界面和后端自动跟上。
    ///
    /// private = true 的组涉及凭据 / 隐私，默认**不勾**，界面上标红。
    /// </summary>
    public static readonly ExportGroup[] Groups =
    {
        new("keys", "按键映射", "所有层的按键绑定（含自定义组合键与宏绑定）",
            new[] { nameof(AppSettings.Keys), nameof(AppSettings.Layers) },
            Private: false, DefaultChecked: true),

        new("triggers", "触发键与层", "触发键配置、10 个层的名称与启用状态",
            new[] { nameof(AppSettings.Triggers) },
            Private: false, DefaultChecked: true),

        new("macros", "宏", "录制好的宏与步骤（不含按键绑定）",
            new[] { nameof(AppSettings.Macros) },
            Private: false, DefaultChecked: true),

        new("search", "搜索引擎", "Qbar 的搜索引擎表与图标标记",
            new[] { nameof(AppSettings.Search) },
            Private: false, DefaultChecked: true),

        new("runWeb", "启动项与网址", "Qbar 的程序启动项、常用网址、热字串",
            new[] { nameof(AppSettings.Run), nameof(AppSettings.Web), nameof(AppSettings.HotStrings) },
            Private: false, DefaultChecked: true),

        new("bindings", "窗口绑定", "10 个窗口槽位绑定的程序",
            new[] { nameof(AppSettings.Bindings) },
            Private: false, DefaultChecked: true),

        new("appearance", "外观与行为", "主题、Qbar 面板样式、鼠标速度、按键行为等",
            new[] { nameof(AppSettings.Qbar), nameof(AppSettings.Global) },
            Private: false, DefaultChecked: true),

        new("apiKeys", "翻译 API 密钥", "有道 / 百度 / DeepL / 腾讯云的 AppId 与密钥。给了别人 = 把你的额度给了别人",
            new[] { nameof(AppSettings.Translate) },
            Private: true, DefaultChecked: false),

        new("syncSettings", "同步与备份设置", "WebDAV 地址、用户名、密码、备份密码。泄露后别人能读写你的网盘备份",
            new[] { nameof(AppSettings.Sync) },
            Private: true, DefaultChecked: false),
    };

    public static ExportGroup? FindGroup(string id) =>
        Groups.FirstOrDefault(g => g.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>给界面用的组清单。</summary>
    public static IEnumerable<ExportGroup> Items() => Groups;

    // ---------------- 导出 ----------------

    private static readonly JsonSerializerOptions WriteOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// 按勾选的组生成导出内容。返回 (文件文本, 写进去的设置项个数)。
    /// </summary>
    public static (string Text, int KeyCount) Build(AppSettings settings,
        IEnumerable<string> selectedGroups)
    {
        var ids = selectedGroups.ToList();
        var allow = AllowKeys(ids);

        // 整份配置序列化成 JSON，再挑出允许的键 —— 这样加字段不用改两处
        var all = JsonSerializer.SerializeToNode(settings, SettingsStore.JsonOptions)!.AsObject();
        var data = new JsonObject();
        foreach (string key in allow)
        {
            if (all.TryGetPropertyValue(key, out var v) && v != null)
                data[key] = v.DeepClone();
        }

        var file = new JsonObject
        {
            ["kind"] = ExportKind,
            ["items"] = new JsonArray(ids.Select(i => (JsonNode)i!).ToArray()),
            // 导出时刻（Unix 秒）。导入界面显示"这份配置什么时候导出的"，方便判断新旧。
            ["at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["app"] = "CapsLock+",
            ["version"] = typeof(ExportImport).Assembly.GetName().Version?.ToString(3) ?? "",
            ["data"] = data,
        };

        return (file.ToJsonString(WriteOpts), data.Count);
    }

    /// <summary>把勾选的组写成一份明文文件。返回写进去的设置项个数。</summary>
    public static int WritePlain(string path, AppSettings settings,
        IEnumerable<string> selectedGroups)
    {
        var (text, count) = Build(settings, selectedGroups);
        File.WriteAllText(path, text, System.Text.Encoding.UTF8);
        CoreLog.Info($"已导出明文配置：{path}（{count} 个设置项）");
        return count;
    }

    // ---------------- 导入 ----------------

    /// <summary>
    /// 读一份明文导出。
    ///
    /// 三道闸，全部是"宁可拒绝也别冲掉用户配置"：
    ///   1. **加密备份直接拒**（导入只支持明文，加密的请走备份页）；
    ///   2. `kind` 必须是 clp-config —— 整份备份没有这个字段，照样拒
    ///      （全量恢复是备份页的事，明文导入不该悄悄变成全量覆盖）；
    ///   3. `items` 必须是字符串数组，`data` 必须是非空对象。
    /// </summary>
    public static (List<string> Groups, JsonObject Data) Parse(string text)
    {
        if (CryptoBox.LooksEncrypted(text))
            throw new InvalidOperationException(
                "这是加密备份，明文导入用不了 —— 请到「同步与备份」页恢复，或让导出方导出明文");

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(text);
        }
        catch (Exception e)
        {
            throw new InvalidOperationException($"文件内容不是有效的 JSON：{e.Message}");
        }
        if (node is not JsonObject root)
            throw new InvalidOperationException("文件内容不是一个 JSON 对象");

        string? kind = root["kind"]?.GetValue<string>();
        if (kind == null)
            throw new InvalidOperationException(
                "这份文件不是明文导出的配置（缺少 kind 字段）—— 若它是备份文件，请去「同步与备份」页");
        if (!kind.Equals(ExportKind, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"不支持的文件类型：{kind}");

        if (root["items"] is not JsonArray arr)
            throw new InvalidOperationException("导出文件的 items 字段不对（应当是字符串数组）");

        var ids = arr.Select(x => x?.GetValue<string>() ?? "")
                     .Where(s => s.Length > 0)
                     .ToList();

        if (root["data"] is not JsonObject data || data.Count == 0)
            throw new InvalidOperationException("这份导出里没有任何数据（导出时大概什么都没勾）");

        // 只保留认得的组
        var known = ids.Where(i => FindGroup(i) != null).ToList();
        return (known, data);
    }

    /// <summary>从文件读一份明文导出。</summary>
    public static (List<string> Groups, JsonObject Data) ReadPlain(string path)
    {
        var info = new FileInfo(path);
        if (info.Length > MaxBytes)
            throw new InvalidOperationException("文件超过 32MB，确认选对了吗");
        return Parse(File.ReadAllText(path, System.Text.Encoding.UTF8));
    }

    /// <summary>
    /// 把导入的数据合并进当前设置。
    ///
    /// ⚠ 必须**按勾选的组裁剪**，不能把整份 data 原样灌进去。
    /// 否则一份 `{"kind":"clp-config","items":["appearance"],
    /// "data":{"Sync":{...攻击者的网盘地址...}}}` 就能在用户只勾了"外观"的
    /// 情况下，把同步目标换成别人的服务器，下次启动就把含翻译密钥和备份密码的
    /// 整份配置推上去 —— 而用户看到的只是一次正常的明文导入。
    ///
    /// 返回实际应用了哪些组。
    /// </summary>
    public static List<string> Apply(AppSettings settings, List<string> groups, JsonObject data)
    {
        var allow = AllowKeys(groups);
        var filtered = new JsonObject();
        foreach (string key in allow)
        {
            if (data.TryGetPropertyValue(key, out var v) && v != null)
                filtered[key] = v.DeepClone();
        }

        // 走与读设置文件同一套反序列化：clamp、默认值补齐全部复用，两边行为一致
        var merged = JsonSerializer.Deserialize<AppSettings>(
            filtered.ToJsonString(), SettingsStore.JsonOptions);
        if (merged == null) throw new InvalidOperationException("导入的数据解析失败");

        foreach (string key in allow)
        {
            switch (key)
            {
                case nameof(AppSettings.Keys):
                    settings.Keys = merged.Keys; break;
                case nameof(AppSettings.Layers):
                    settings.Layers = merged.Layers; break;
                case nameof(AppSettings.Triggers):
                    settings.Triggers = merged.Triggers; break;
                case nameof(AppSettings.Macros):
                    settings.Macros = merged.Macros; break;
                case nameof(AppSettings.Search):
                    settings.Search = merged.Search; break;
                case nameof(AppSettings.Run):
                    settings.Run = merged.Run; break;
                case nameof(AppSettings.Web):
                    settings.Web = merged.Web; break;
                case nameof(AppSettings.HotStrings):
                    settings.HotStrings = merged.HotStrings; break;
                case nameof(AppSettings.Bindings):
                    settings.Bindings = merged.Bindings; break;
                case nameof(AppSettings.Qbar):
                    settings.Qbar = merged.Qbar; break;
                case nameof(AppSettings.Global):
                    settings.Global = merged.Global; break;
                case nameof(AppSettings.Translate):
                    settings.Translate = merged.Translate; break;
                case nameof(AppSettings.Sync):
                    settings.Sync = merged.Sync; break;
            }
        }

        // 导入的数据可能缺项，归一化一遍保证结构完整
        SettingsStore.Normalize(settings);
        CoreLog.Info($"已导入明文配置：组={string.Join(",", groups)}，键={filtered.Count}");
        return groups;
    }

    /// <summary>把组 ID 展开成允许的设置项键名（去重）。</summary>
    internal static List<string> AllowKeys(IEnumerable<string> groups)
    {
        var set = new List<string>();
        foreach (string id in groups)
        {
            var g = FindGroup(id);
            if (g == null) continue;
            foreach (string k in g.Keys)
            {
                if (!set.Contains(k, StringComparer.OrdinalIgnoreCase)) set.Add(k);
            }
        }
        return set;
    }
}