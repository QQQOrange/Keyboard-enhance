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
    bool DefaultChecked)
{
    /* 表里存的是中文原文（身份用 Id，不靠名字），显示时翻一次 ——
     * 界面上这张表有十来行名字 + 说明，逐个调用点包 Loc.T 只会把那份清单弄脏。 */
    public string DisplayName => Localization.Loc.T(Name);

    public string DisplayDesc => Localization.Loc.T(Desc);
}

/// <summary>
/// 配置的按项导出 / 导入。
///
/// 与「备份」的分工（照搬 QuickSearch 的结论）：
///   * **备份**：整份配置 + 加密，恢复时全量覆盖，走 WebDAV 同步；
///   * **导出**：挑几项 + 合并式导入，用来在机器之间搬配置。
///
/// 导出有两种模式：
///   * **加密导出（默认）**：用备份密码加密，**勾了哪组就导出那组的全部字段**，
///     包括网盘密码、备份密码 —— 文件本身是密文，泄露风险由加密承担；
///   * **明文导出**：为了"直接发给别人"这种场景，剔除凭据类字段。
///
/// 导入侧靠内容自动识别（<see cref="CryptoBox.LooksEncrypted"/>），
/// 加密的和明文的都能读，用户不用记"这份是哪种"。
/// </summary>
public static class ExportImport
{
    public const string ExportKind = "clp-config";

    /// <summary>导出文件的默认扩展名。加密与明文共用，导入时按内容判断。</summary>
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

        /* Keys 必须把**组名承诺的东西**全列上：这一组叫"触发键与层"，
         * 以前只写了 Triggers，导出的文件里没有 Layers —— 用户勾了却拿不到层。 */
        new("triggers", "触发键与层", "触发键配置、10 个层的名称与启用状态",
            new[] { nameof(AppSettings.Triggers), nameof(AppSettings.Layers) },
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

        new("syncSettings", "同步与备份设置", "WebDAV 地址、用户名、备份保留策略；加密导出时含密码",
            new[] { nameof(AppSettings.Sync) },
            Private: true, DefaultChecked: false),
    };

    /// <summary>
    /// **明文导出**时永远不写的字段（`属性.子属性`）。
    ///
    /// 只在明文模式下生效：明文文件的设计前提是"可以直接发给别人"，
    /// 那么凭据就不能靠"用户别勾"来保证安全 —— 总有人会点「全选」。
    ///
    /// 加密导出**不走这张表**：文件是 AES-256-GCM 密文，
    /// 勾了「同步与备份设置」就该把网盘密码一起带走，
    /// 否则换台机器恢复完还得重新填一遍，等于没导出。
    /// </summary>
    public static readonly string[] NeverExport =
    {
        "Sync.WebDavPass",
        "Sync.BackupPassword",

        /* 启动密码整套都不出门：它是"本机上的一把锁"——
         *   · 哈希跟着配置走的话，换台机器导入完会向用户要一个
         *     他从没在本机设过的密码，直接把自己锁在门外；
         *   · 而启动密码的真相（startup.json）本来就不随备份走，
         *     设置里这份只是镜像，两边必须一致。
         * 想在新机器上用启动密码，到那台机器的设置里再设一次。 */
        "Security.StartupPasswordEnabled",
        "Security.StartupPasswordSalt",
        "Security.StartupPasswordHash",
        "Security.StartupPasswordIterations",

        /* GitHub 令牌**不再**在这张表里：用户明确要求"备份和导出都必须带令牌"，
         * 否则换台机器导入后还得重新去 GitHub 建一个。
         *
         * 代价是明文导出会把它写进文件 —— 这是有意接受的，因为：
         *   · 令牌只读公开仓库的更新信息，泄露的后果是"别人用你的配额"；
         *   · 而备份密码泄露 = 别人能解开你所有云备份，所以那两个仍然摘掉；
         *   · 界面上会明确标出"当前是明文导出，文件里含令牌"，
         *     想安全就勾上加密（默认就是勾着的）。 */
    };

    /// <summary>把不该出门的字段从**明文**导出数据里摘掉。</summary>
    internal static void Sanitize(JsonObject data)
    {
        foreach (string path in NeverExport)
        {
            int dot = path.IndexOf('.');
            if (dot <= 0) continue;
            string root = path[..dot], child = path[(dot + 1)..];
            if (data[root] is JsonObject obj) obj.Remove(child);
        }
    }

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
    /// <param name="includeSecrets">
    /// true = 勾了哪组就导出那组的**全部**字段（加密导出用）；
    /// false = 剔除 <see cref="NeverExport"/> 里的凭据（明文导出用）。
    /// </param>
    public static (string Text, int KeyCount) Build(AppSettings settings,
        IEnumerable<string> selectedGroups, bool includeSecrets = false)
    {
        var ids = selectedGroups.ToList();
        var allow = AllowKeys(ids);

        // 整份配置序列化成 JSON，再挑出允许的键 —— 这样加字段不用改两处
        var all = JsonSerializer.SerializeToNode(settings, SettingsStore.JsonOptions)!.AsObject();
        var data = new JsonObject();
        foreach (string key in allow)
        {
            if (all.TryGetPropertyValue(key, out var v) && v != null)
                data[key] = JsonNodeTool.DeepClone(v);
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

        // 只有明文导出才脱敏：加密导出的安全性由加密本身保证，
        // 摘掉密码会让"换台机器恢复"变成"还得重新填一遍"。
        if (!includeSecrets) Sanitize(data);

        return (file.ToJsonString(WriteOpts), data.Count);
    }

    /// <summary>
    /// 这些组里有没有"隐私组"（含凭据）。
    ///
    /// 有就不能明文导出 —— 界面据此锁住加密开关，<see cref="WritePlain"/> 再兜一层。
    /// 返回它们的显示名，方便直接拼进提示里。
    /// </summary>
    public static List<string> PrivateNames(IEnumerable<string> groups)
    {
        var names = new List<string>();
        foreach (string id in groups)
        {
            var g = FindGroup(id);
            if (g is { Private: true } && !names.Contains(g.Name)) names.Add(g.Name);
        }
        return names;
    }

    /// <summary>
    /// 把勾选的组写成一份**明文**文件。
    ///
    /// 勾了隐私组直接拒绝：那些组里有网盘密码、备份密码这类凭据，
    /// 明文文件一旦被转发就等于把凭据送出去。界面上加密开关会被锁死，
    /// 这里再兜一层是为了防止别的调用路径（导入侧、命令行）绕过去。
    /// </summary>
    public static int WritePlain(string path, AppSettings settings,
        IEnumerable<string> selectedGroups)
    {
        var selected = selectedGroups.ToList();
        var priv = PrivateNames(selected);
        if (priv.Count > 0)
            throw new InvalidOperationException(
                Loc.F("勾选了隐私内容（{0}），只能用加密导出。", Loc.Join(priv)));

        var (text, count) = Build(settings, selected);
        File.WriteAllText(path, text, System.Text.Encoding.UTF8);
        CoreLog.Info($"已导出明文配置：{path}（{count} 个设置项）");
        return count;
    }

    /// <summary>
    /// 把勾选的组写成一份**加密**文件：勾了哪组就是那组的全部字段。
    /// 返回写进去的设置项个数。
    /// </summary>
    public static int WriteEncrypted(string path, AppSettings settings,
        IEnumerable<string> selectedGroups, string password)
    {
        if (string.IsNullOrWhiteSpace(password))
            throw new InvalidOperationException(
                "加密导出需要密码 —— 先在「备份密码」里设一个，或改成明文导出");

        var (text, count) = Build(settings, selectedGroups, includeSecrets: true);
        File.WriteAllText(path, CryptoBox.Encrypt(text, password), System.Text.Encoding.UTF8);
        CoreLog.Info($"已导出加密配置：{path}（{count} 个设置项）");
        return count;
    }

    // ---------------- 导入 ----------------

    /// <summary>
    /// 读一份配置文件（导出文件或备份文件，加密或明文都行）。
    ///
    /// 三道闸，全部是"宁可拒绝也别冲掉用户配置"：
    ///   1. **加密内容先解密**：口令不对就抛 <see cref="BackupPasswordException"/>，
    ///      让界面去问密码 —— 不能把密文当坏 JSON 报个看不懂的错；
    ///   2. 有 `kind` 就必须是 clp-config；
    ///   3. `items` 必须是字符串数组，`data` 必须是非空对象。
    ///
    /// **备份文件也接受**：它解密出来就是一份完整的 AppSettings，只是没有
    /// kind/items 这层包装。用户经常分不清 `.keyboardenhance`（备份）和
    /// `.clpconf`（导出）—— "另一台电脑导入不了"多半就是拿错了文件。
    /// 与其报一句"这不是导出文件"再让他去别的页面，不如按整份导入处理
    /// （<paramref name="wasBackup"/> 会告诉界面，好把确认文案说清楚）。
    /// </summary>
    /// <param name="password">解密口令；null = 用当前设置里的备份密码（由调用方决定）。</param>
    /// <returns>Groups = 认得的组；Data = 要应用的数据；WasBackup = 来自备份文件。</returns>
    public static (List<string> Groups, JsonObject Data, bool WasBackup) Parse(
        string text, string? password = null)
    {
        if (CryptoBox.LooksEncrypted(text))
        {
            try
            {
                text = CryptoBox.Decrypt(text, password ?? "");
            }
            catch (Exception e)
            {
                // 统一成"密码问题"，界面据此弹密码框重试
                throw new BackupPasswordException(e.Message, e);
            }
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(text);
        }
        catch (Exception e)
        {
            throw new InvalidOperationException(Loc.F("文件内容不是有效的 JSON：{0}", e.Message));
        }
        if (node is not JsonObject root)
            throw new InvalidOperationException("文件内容不是一个 JSON 对象");

        string? kind = root["kind"]?.GetValue<string>();

        /* 没有 kind：可能是整份备份。看它像不像一份配置 —— 有 Layers / Keys / Sync
         * 任一顶层字段就认，然后按"全部组"整份导入。 */
        if (kind == null)
        {
            bool looksLikeBackup =
                root.ContainsKey(nameof(AppSettings.Layers))
                || root.ContainsKey(nameof(AppSettings.Keys))
                || root.ContainsKey(nameof(AppSettings.Sync));

            if (!looksLikeBackup)
                throw new InvalidOperationException(
                    "这个文件既不是导出配置、也不是备份（里面没有可识别的配置字段）。"
                    + "请确认选的是 KeyboardEnhance 导出的 .clpconf 或备份的 .keyboardenhance 文件。");

            var allIds = Groups.Select(g => g.Id).ToList();
            return (allIds, root, true);
        }

        if (!kind.Equals(ExportKind, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(Loc.F("不支持的文件类型：{0}", kind));

        if (root["items"] is not JsonArray arr)
            throw new InvalidOperationException("导出文件的 items 字段不对（应当是字符串数组）");

        var ids = arr.Select(x => x?.GetValue<string>() ?? "")
                     .Where(s => s.Length > 0)
                     .ToList();

        if (root["data"] is not JsonObject data || data.Count == 0)
            throw new InvalidOperationException("这份导出里没有任何数据（导出时大概什么都没勾）");

        // 只保留认得的组
        var known = ids.Where(i => FindGroup(i) != null).ToList();
        return (known, data, false);
    }

    /// <summary>从文件读一份导出（加密或明文；备份文件也认）。</summary>
    public static (List<string> Groups, JsonObject Data, bool WasBackup) ReadPlain(
        string path, string? password = null)
    {
        var info = new FileInfo(path);
        if (info.Length > MaxBytes)
            throw new InvalidOperationException("文件超过 32MB，确认选对了吗");
        return Parse(File.ReadAllText(path, System.Text.Encoding.UTF8), password);
    }

    /// <summary>
    /// 把导入的数据合并进当前设置。
    ///
    /// ⚠ 必须**按勾选的组裁剪**，不能把整份 data 原样灌进去。
    /// 否则一份 `{"kind":"clp-config","items":["appearance"],
    /// "data":{"Sync":{...攻击者的网盘地址...}}}` 就能在用户只勾了"外观"的
    /// 情况下，把同步目标换成别人的服务器，下次启动就把含网盘密码和备份密码的
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
                filtered[key] = JsonNodeTool.DeepClone(v);
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
