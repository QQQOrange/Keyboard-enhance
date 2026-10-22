using System.Text.Json;
using System.Text.Json.Serialization;
using CapsLockPlus.Core.Input;
using CapsLockPlus.Core.Settings;

namespace CapsLockPlus.Core.Settings;

/// <summary>设置的读写（JSON，带注释友好的格式、原子写入与自动备份）。</summary>
public static class SettingsStore
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
    };

    private static readonly object Gate = new();

    /* ---- 保存节流 ----------------------------------------------------------
     *
     * 现状：全仓 71 处调用 SaveSettings()，其中参数框的 TextChanged 是**逐字符**触发。
     * Save() 里是"序列化 + AES + 写 tmp + 复制 bak + move"全套同步 IO，
     * 打一个三位数就是三次，拖滑杆每 tick 一次 —— 输入卡顿与无谓磁盘写入都从这来。
     *
     * 现在的做法：
     *   1. RequestSave() 只记一个"待保存"，400ms 内有新改动就顺延（防抖）；
     *   2. 到时在**调用线程（界面线程）**上序列化成字符串 —— 对象图只在这条线程被改，
     *      这样后台线程永远不会去枚举正在变动的集合（之前的一个偶发崩溃源）；
     *   3. 加密与落盘交给一条后台写线程，按"后写覆盖先写"合并。
     *
     * 退出/恢复等关键路径仍然可以调 Save() 同步落盘。
     */
    private const int DebounceMs = 400;
    private static AppSettings? _pendingSave;
    private static System.Threading.Timer? _debounce;
    private static string? _queuedJson;
    private static Thread? _writerThread;
    private static readonly AutoResetEvent WriteSignal = new(false);

    /// <summary>
    /// 把回调切到界面线程（由 AppHost 注入）。
    ///
    /// 序列化必须在"改对象图的那条线程"上做，否则后台线程会去枚举
    /// 用户正在增删的 List，抛"集合已修改"。Core 层不引用 WPF，
    /// 所以由上层注入一个 marshal 函数。
    /// </summary>
    public static Action<Action>? Marshal { get; set; }

    /// <summary>
    /// 设置文件最后写入时间（本地时间）。
    /// 启动时同步要拿它跟云端备份的修改时间比"谁新用谁"。
    /// 文件不存在时返回最小时间，这样云端那份会被判定为更新。
    /// </summary>
    public static DateTime LastWriteTimeUtc
    {
        get
        {
            try
            {
                var fi = new FileInfo(AppPaths.SettingsFile);
                return fi.Exists ? fi.LastWriteTimeUtc : DateTime.MinValue;
            }
            catch
            {
                return DateTime.MinValue;
            }
        }
    }

    /// <summary>
    /// 把另一份设置的内容整体拷进目标对象（保持引用不变）。
    ///
    /// 用于"从备份恢复"：界面各处都持有同一个 AppSettings 引用，
    /// 直接替换引用会让它们指向旧对象，所以必须原地修改。
    /// </summary>
    public static void CopyInto(AppSettings target, AppSettings source)
    {
        var src = JsonSerializer.Serialize(source, JsonOptions);
        var copy = JsonSerializer.Deserialize<AppSettings>(src, JsonOptions);
        if (copy == null) return;

        target.Global = copy.Global;
        target.Qbar = copy.Qbar;
        target.Sync = copy.Sync;
        target.Keys = copy.Keys;
        target.Layers = copy.Layers;
        target.Triggers = copy.Triggers;
        target.Macros = copy.Macros;
        target.Search = copy.Search;
        target.Run = copy.Run;
        target.Web = copy.Web;
        target.HotStrings = copy.HotStrings;
        target.Bindings = copy.Bindings;
        target.BuiltinEnginesVersion = copy.BuiltinEnginesVersion;
    }

    /// <summary>所有绑定列表（各层的 Keys + 老的顶层 Keys）。</summary>
    private static IEnumerable<List<KeyBinding>> AllBindingLists(AppSettings s)
    {
        yield return s.Keys;
        foreach (var layer in s.Layers) yield return layer.Keys;
    }

    public static AppSettings Load()
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.DataDir);
                if (!File.Exists(AppPaths.SettingsFile))
                {
                    var fresh = DefaultSettings.Create();
                    Save(fresh);
                    return fresh;
                }

                string raw = File.ReadAllText(AppPaths.SettingsFile);

                /* 加密过的设置：先解锁再解密。
                 *
                 * 解不开时**绝不能**返回默认值然后让它覆盖原文件 ——
                 * 那等于把用户所有配置一次性抹掉（而且不可逆）。
                 * 这里进入"只读保护"：返默认值给界面先用着，
                 * 但 Save 全部跳过，等上层拿到密码解锁后再正常读写。 */
                if (DataVault.LooksEncrypted(raw))
                {
                    if (!DataVault.IsUnlocked) DataVault.TryUnlockCached();
                    string? plain = DataVault.Decrypt(raw);
                    if (plain == null)
                    {
                        _dataLocked = true;
                        CoreLog.Error("设置已加密但当前无法解密（密钥缓存失效）—— 进入只读保护，不会覆盖原文件");
                        return DefaultSettings.Create();
                    }
                    raw = plain;
                }

                var s = JsonSerializer.Deserialize<AppSettings>(raw, JsonOptions) ?? DefaultSettings.Create();
                Normalize(s);
                bool migrated = MigrateBuiltinEngines(s);

                /* 已经设了备份密码、但这份文件还是明文的旧数据 → 立刻以加密形式重存，
                 * 让"设了密码就不再留明文"当场生效，而不是等下一次改设置。 */
                bool needsEncrypt = DataVault.IsEnabled && DataVault.IsUnlocked
                                    && !DataVault.LooksEncrypted(File.ReadAllText(AppPaths.SettingsFile));

                if (migrated || needsEncrypt) Save(s);

                /* 顺手清掉明文副本（.bak / .broken / .tmp）。
                 *
                 * 每次都清而不是只在"用户改密码"时清：数据目录可能刚被迁移过，
                 * 老目录里的明文 .bak 会跟着搬进来；程序也可能在崩溃时留下明文 .tmp。
                 * 只要加密是开着的，这些文件就不该存在于磁盘上。 */
                if (DataVault.IsEnabled && DataVault.IsUnlocked)
                    DataVault.PurgePlaintextCopies(AppPaths.SettingsFile);

                return s;
            }
            catch (Exception ex)
            {
                CoreLog.Error("读取设置失败，已回退到默认设置", ex);
                try
                {
                    if (File.Exists(AppPaths.SettingsFile))
                        File.Copy(AppPaths.SettingsFile, AppPaths.SettingsFile + ".broken", true);
                }
                catch { }
                var s = DefaultSettings.Create();
                return s;
            }
        }
    }

    /// <summary>
    /// 数据因为"加密但解不开"而处于只读保护状态。
    ///
    /// 界面必须据此拦住"保存"，否则会用空配置覆盖掉用户那份还等着解锁的数据。
    /// </summary>
    public static bool IsDataLocked { get { lock (Gate) return _dataLocked; } }

    private static bool _dataLocked;

    /// <summary>
    /// 用密码解锁并重新读一遍设置（上层弹完密码框后调用）。
    /// 返回 false = 密码不对。
    /// </summary>
    public static bool UnlockAndReload(string password, out AppSettings settings)
    {
        lock (Gate)
        {
            settings = DefaultSettings.Create();
            if (!DataVault.UnlockWithPassword(password)) return false;

            _dataLocked = false;
            settings = Load();
            return true;
        }
    }

    /// <summary>请求保存（防抖 400ms）。界面上的高频改动都走这里。</summary>
    public static void RequestSave(AppSettings settings)
    {
        lock (Gate)
        {
            _pendingSave = settings;
            _debounce ??= new System.Threading.Timer(_ =>
            {
                Action<Action>? marshal = Marshal;
                if (marshal != null) marshal(SaveDeferredNow);
                else SaveDeferredNow();
            }, null, Timeout.Infinite, Timeout.Infinite);

            _debounce.Change(DebounceMs, Timeout.Infinite);
        }
    }

    /// <summary>把当前待保存内容序列化并排队（必须在界面线程调用）。</summary>
    private static void SaveDeferredNow()
    {
        AppSettings? pending;
        lock (Gate)
        {
            pending = _pendingSave;
            _pendingSave = null;
        }
        if (pending != null) SaveDeferred(pending);
    }

    /// <summary>
    /// 保存但**不等落盘**：序列化（调用线程）→ 交给后台写线程。
    ///
    /// 界面上的高频保存走这条：加密 + 三次文件操作都在后台，
    /// 界面线程只花一次序列化的时间。
    /// </summary>
    public static void SaveDeferred(AppSettings settings)
    {
        string? json;
        lock (Gate) json = SerializeLocked(settings);
        if (json == null) return;

        lock (Gate) _queuedJson = json;
        EnsureWriter();
        WriteSignal.Set();
    }

    /// <summary>
    /// 立刻把待保存的内容落盘（退出、切页面、用户明确点"保存"时用）。
    /// 同步执行，保证函数返回时文件已经写好。
    /// </summary>
    public static void FlushPending()
    {
        AppSettings? pending;
        lock (Gate)
        {
            pending = _pendingSave;
            _pendingSave = null;
            _debounce?.Change(Timeout.Infinite, Timeout.Infinite);
        }

        if (pending != null) Save(pending);
        else DrainWrites();
    }

    /// <summary>把已排队的写入全部落盘（同步，退出前用）。</summary>
    public static void DrainWrites()
    {
        lock (Gate) DrainWritesLocked();
    }

    /// <summary>
    /// 后台写线程：只负责"加密 + 落盘"。
    ///
    /// 队列里永远只留最新一份（<see cref="_queuedJson"/>）：
    /// 用户连打十个字符，磁盘上只会留下最后一次的结果，中间态没有意义。
    /// </summary>
    private static void EnsureWriter()
    {
        if (_writerThread != null) return;

        _writerThread = new Thread(() =>
        {
            while (true)
            {
                WriteSignal.WaitOne(2000);
                DrainWrites();
            }
        })
        {
            IsBackground = true,
            Name = "CapsLockPlus.SettingsWriter",
            Priority = ThreadPriority.BelowNormal,
        };
        _writerThread.Start();
    }

    /// <summary>
    /// 同步保存：函数返回时文件已经写好。
    /// 退出、恢复备份、切换加密这类"必须立刻落盘"的场景用它；
    /// 界面上的普通改动请用 <see cref="RequestSave"/>。
    /// </summary>
    public static void Save(AppSettings settings)
    {
        string? json;
        lock (Gate) json = SerializeLocked(settings);
        if (json == null) return;

        lock (Gate)
        {
            _queuedJson = json;      // 覆盖尚未落盘的旧内容，保证顺序
            DrainWritesLocked();
        }
    }

    /// <summary>
    /// 序列化（调用线程）。返回 null 表示这次不该写盘（只读保护 / 未解锁）。
    /// 只碰内存，不做 IO —— 所以可以放心在界面线程调用。
    /// </summary>
    private static string? SerializeLocked(AppSettings settings)
    {
        // 只读保护期间一律不落盘 —— 这份内存里的对象是默认值，写下去就是灾难
        if (_dataLocked)
        {
            CoreLog.Warn("设置处于只读保护状态，本次保存已跳过");
            return null;
        }

        if (DataVault.IsEnabled && !DataVault.IsUnlocked)
        {
            CoreLog.Warn("加密已启用但尚未解锁，本次保存已跳过");
            return null;
        }

        try
        {
            return JsonSerializer.Serialize(settings, JsonOptions);
        }
        catch (Exception ex)
        {
            CoreLog.Error("序列化设置失败", ex);
            return null;
        }
    }

    /// <summary>加密 + 原子写入（只在写线程或同步保存时执行）。</summary>
    private static void DrainWritesLocked()
    {
        while (_queuedJson != null)
        {
            string json = _queuedJson;
            _queuedJson = null;
            WriteJsonLocked(json);
        }
    }

    private static void WriteJsonLocked(string json)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);

            // 启用了加密就写密文；没解锁时宁可保留旧文件也不写半成品
            if (DataVault.IsEnabled)
            {
                if (!DataVault.IsUnlocked)
                {
                    CoreLog.Warn("加密已启用但尚未解锁，本次保存已跳过");
                    return;
                }
                json = DataVault.Encrypt(json);
            }

            var tmp = AppPaths.SettingsFile + ".tmp";
            File.WriteAllText(tmp, json);
            if (File.Exists(AppPaths.SettingsFile))
                File.Copy(AppPaths.SettingsFile, AppPaths.SettingsFile + ".bak", true);
            File.Move(tmp, AppPaths.SettingsFile, true);
        }
        catch (Exception ex)
        {
            CoreLog.Error("保存设置失败", ex);
        }
    }

    /// <summary>供自检调用的迁移入口（与启动时走同一条逻辑）。</summary>
    internal static bool MigrateBuiltinEnginesForTest(AppSettings s) => MigrateBuiltinEngines(s);

    /// <summary>
    /// 把新增的内置搜索引擎补进用户已有设置。
    ///
    /// 为什么需要它：设置文件一旦存在，启动时走的就是"读取"分支，
    /// 不会再套用默认值。于是每次扩充引擎表，老用户都看不到新引擎。
    ///
    /// 策略（保守，不破坏用户自定义）：
    ///   * 用户已有的关键字：原样保留（哪怕改过链接或备注）；
    ///   * 用户没有的关键字：把内置引擎补上；
    ///   * 只补不删，用户自己新增的引擎不受影响。
    /// </summary>
    private static bool MigrateBuiltinEngines(AppSettings s)
    {
        if (s.BuiltinEnginesVersion >= DefaultSettings.BuiltinEnginesVersion) return false;

        // 旧版用过、现在已改名的关键字：直接删掉，免得用户看到 g 和 go 两个 Google。
        // 只删"还保持原始内容"的条目；如果用户改过链接，说明他自己在用，保留。
        var renamedAway = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["g"] = "go",    // Google
            ["b"] = "bi",    // Bing
            ["wk"] = "wi",   // Wikipedia
            ["m"] = "md",    // MDN
        };
        foreach (var (oldKey, newKey) in renamedAway)
        {
            var old = s.Search.FirstOrDefault(e => e.Key.Equals(oldKey, StringComparison.OrdinalIgnoreCase));
            if (old == null) continue;
            var target = DefaultSettings.Create().Search
                .FirstOrDefault(e => e.Key.Equals(newKey, StringComparison.OrdinalIgnoreCase));
            if (target != null && old.Value == target.Value)
            {
                s.Search.Remove(old);
                CoreLog.Info($"搜索引擎 {oldKey} 已重命名为 {newKey}，移除旧条目");
            }
        }

        // 先清理"同一个站点、关键字却不同"的旧条目。
        //
        // 背景：早先版本亲手造过一批引擎（tr 翻译 / ai ChatGPT / yh Yahoo Japan …），
        // 后来改用 QuickSearch 的表，同一站点关键字变了（ai → gpt），
        // 链接写法也常有出入（尾部多一个参数就判不出相同）。
        // 若不清理，用户列表里会出现两个 ChatGPT，看着很乱。
        //
        // 判据用"站点域名"而不是完整链接 —— 域名相同即视为同一站点。
        // 两条限制保证不误删：只删图标还是本程序生成的旧条目；用户改过图标的不动。
        var builtinDomains = new HashSet<string>(
            DefaultSettings.Create().Search
                .Where(e => !e.Key.Equals("default", StringComparison.OrdinalIgnoreCase))
                .Select(e => DomainOf(e.Value))
                .Where(d => d.Length > 0),
            StringComparer.OrdinalIgnoreCase);

        var builtinKeys = new HashSet<string>(
            DefaultSettings.Create().Search.Select(e => e.Key),
            StringComparer.OrdinalIgnoreCase);

        int removedLegacy = s.Search.RemoveAll(e =>
            !e.Key.Equals("default", StringComparison.OrdinalIgnoreCase) &&
            !builtinKeys.Contains(e.Key) &&
            IsOurGeneratedIcon(e.IconPath) &&
            builtinDomains.Contains(DomainOf(e.Value)));

        var builtin = DefaultSettings.Create().Search
            .Where(e => !e.Key.Equals("default", StringComparison.OrdinalIgnoreCase))
            .ToList();

        int added = 0, upgraded = 0;
        foreach (var engine in builtin)
        {
            var existing = s.Search.FirstOrDefault(
                e => e.Key.Equals(engine.Key, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                s.Search.Add(new QEntry
                {
                    Key = engine.Key,
                    Value = engine.Value,
                    Comment = engine.Comment,
                    IconPath = engine.IconPath,
                    EngineIcon = engine.EngineIcon,
                });
                added++;
                continue;
            }

            // 已有条目：把旧的图标路径换成内置图标。
            //
            // 为什么要换：早先版本把图标渲染成磁盘上的 .svg（%LocalAppData% 下），
            // 那些是程序自己画的简化图标；现在统一改用 QuickSearch 的 PNG 内置图标。
            // 只动图标，不碰用户改过的链接和备注。
            if (string.IsNullOrEmpty(existing.EngineIcon) && !string.IsNullOrEmpty(engine.EngineIcon))
            {
                existing.EngineIcon = engine.EngineIcon;
                // 旧的 IconPath 指向程序自己生成的 svg，清掉它才会优先用内置图标
                if (IsOurGeneratedIcon(existing.IconPath)) existing.IconPath = "";
                upgraded++;
            }
        }

        s.BuiltinEnginesVersion = DefaultSettings.BuiltinEnginesVersion;
        if (added > 0 || upgraded > 0 || removedLegacy > 0)
            CoreLog.Info($"搜索引擎表更新：新增 {added} 个，图标升级 {upgraded} 个，" +
                         $"清理旧条目 {removedLegacy} 个（引擎表版本 → {s.BuiltinEnginesVersion}）");
        return true;
    }

    /// <summary>
    /// 取链接的站点域名（小写，去掉 www. 前缀），用于判断两个引擎是否同一站点。
    /// 解析不出来时返回空串，调用方据此跳过判定（宁可不删，也不误删）。
    /// </summary>
    private static string DomainOf(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";
        try
        {
            var s = url.Trim();
            int scheme = s.IndexOf("://", StringComparison.Ordinal);
            if (scheme >= 0) s = s[(scheme + 3)..];
            int slash = s.IndexOfAny(new[] { '/', '?', '#' });
            if (slash >= 0) s = s[..slash];
            int colon = s.IndexOf(':');
            if (colon >= 0) s = s[..colon];
            s = s.ToLowerInvariant();
            if (s.StartsWith("www.", StringComparison.Ordinal)) s = s[4..];
            return s;
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// 判断某个图标路径是否是"本程序早先生成到数据目录里的图标"。
    /// 这类路径可以直接替换成内置图标；用户自己指定的路径则不动。
    ///
    /// 判定放宽到"路径位于名为 icons 的目录下、且是 .svg"：
    /// 早先版本把图标渲染成 &lt;数据目录&gt;/icons/*.svg，是本程序自己生成的产物，
    /// 不存在用户手写这种路径的情况。放宽后即使用户改过数据目录位置也能识别。
    /// </summary>
    private static bool IsOurGeneratedIcon(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            if (!path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)) return false;

            var full = Path.GetFullPath(path);
            var dir = Path.GetDirectoryName(full);
            if (string.IsNullOrEmpty(dir)) return false;

            var ours = Path.Combine(Path.GetFullPath(AppPaths.DataDir), "icons");
            if (string.Equals(dir.TrimEnd('\\'), ours.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                return true;

            // 兼容数据目录被改过的情况：父目录名是 icons，且再上一层含 CapsLockPlus
            if (string.Equals(Path.GetFileName(dir), "icons", StringComparison.OrdinalIgnoreCase))
            {
                var parent = Path.GetFileName(Path.GetDirectoryName(dir) ?? "");
                return parent.Contains("CapsLockPlus", StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>补齐缺失字段、修正非法值。</summary>
    public static void Normalize(AppSettings s)
    {
        s.Global ??= new GlobalOptions();
        s.Qbar ??= new QbarOptions();
        s.Sync ??= new SyncOptions();
        s.Keys ??= new List<KeyBinding>();
        s.Search ??= new List<QEntry>();
        s.Run ??= new List<QEntry>();
        s.Web ??= new List<QEntry>();
        s.HotStrings ??= new List<QEntry>();
        s.Bindings ??= new List<WinBinding>();
        s.Triggers ??= new List<TriggerKey>();
        s.Layers ??= new List<LayerOptions>();
        s.Macros ??= new List<Macro>();
        s.Global.MouseSpeed = Math.Clamp(s.Global.MouseSpeed, 1, 20);
        // 步进允许 1~50：太小没感觉，太大一下到底
        s.Global.VolumeStep = Math.Clamp(s.Global.VolumeStep, 1, 50);
        s.Global.BrightnessStep = Math.Clamp(s.Global.BrightnessStep, 1, 50);
        s.Qbar.ListCount = Math.Clamp(s.Qbar.ListCount, 3, 12);

        /* 已删除的动作：老配置里可能还绑着，统一改成"什么都不做"。
         *
         * 不这么做的话，按下去会提示"未知动作：translate" —— 用户不知道
         * 是软件坏了还是功能被删了。翻译功能整块移除，这里把它清干净。 */
        foreach (var list in AllBindingLists(s))
        {
            foreach (var b in list)
            {
                if (string.Equals(b.Action, "translate", StringComparison.OrdinalIgnoreCase))
                {
                    b.Action = "doNothing";
                    b.Arg = "";
                }
            }
        }

        /* 备份保留策略：老配置里没有这两个字段，反序列化后是 0。
         *
         * 0 在业务上是"不限制"，跟默认值 10 完全相反 ——
         * 不在这里补默认值，老用户一升级就会变成"永远不清"，备份越堆越多；
         * 而 0 份保留又会把刚写的备份自己删掉。两头都不能要，必须夹住。 */
        if (s.Sync.KeepLocalBackups <= 0) s.Sync.KeepLocalBackups = 10;
        s.Sync.KeepLocalBackups = Math.Clamp(s.Sync.KeepLocalBackups, 1, 200);
        if (s.Sync.CleanupDays < 0) s.Sync.CleanupDays = 0;
        s.Sync.CleanupDays = Math.Clamp(s.Sync.CleanupDays, 0, 3650);

        if (s.Sync.AutoBackupMinutes <= 0) s.Sync.AutoBackupMinutes = 60;
        s.Sync.AutoBackupMinutes = Math.Clamp(s.Sync.AutoBackupMinutes, 5, 1440);

        /* Qbar 面板底色：老配置里是写死的深色值（#1C1F27），
         * 导致浅色主题下"白字白底"看不见。
         *
         * 这里把**旧版的默认深色值**迁移成 auto（跟随主题）——
         * 只迁移这一个已知的旧默认值，用户自己选的颜色不动。 */
        if (string.Equals(s.Qbar.PanelColor, "#1C1F27", StringComparison.OrdinalIgnoreCase))
            s.Qbar.PanelColor = "auto";
        /* 布局方案固定为新版（Capslox），不再允许切换。
         *
         * 旧的「经典 CapsLock+ 3.0」方案只是历史兼容产物：两套布局的差别
         * 仅在于默认绑定表，而用户真正需要的是"改哪个键做什么"，
         * 不需要先理解自己属于哪一代方案。留着切换只会制造困惑。
         *
         * 这里强制归一化：老配置即使写着 capslock_plus，读进来也会变成
         * capslox。字段本身保留，避免旧配置文件解析失败。 */
        s.Global.HotkeyScheme = "capslox";

        NormalizeLayers(s);

        // 去掉重复的绑定项，保留最后一条
        var dup = s.Keys.GroupBy(k => k.Combo, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).ToList();
        foreach (var g in dup)
        {
            var last = g.Last();
            s.Keys.RemoveAll(k => string.Equals(k.Combo, last.Combo, StringComparison.OrdinalIgnoreCase));
            s.Keys.Add(last);
        }
    }

    /// <summary>
    /// 保证"层 / 触发键"这套新结构始终处于可用状态：
    ///   * 老设置文件没有 Triggers / Layers，这里补齐默认值；
    ///   * 老设置里的 Keys 视为第 1 层的绑定，行为完全不变；
    ///   * 默认 10 层，每层独立；只创建缺失的层，不覆盖用户改过的。
    /// </summary>
    private static void NormalizeLayers(AppSettings s)
    {
        const int DefaultLayerCount = 10;

        // 层
        if (s.Layers.Count == 0)
        {
            for (int i = 1; i <= DefaultLayerCount; i++)
            {
                s.Layers.Add(new LayerOptions
                {
                    Id = i,
                    Name = i == 1 ? "基础层" : $"第 {i} 层",
                    Enabled = true,
                    // 老用户的 Keys 就是第 1 层
                    Keys = i == 1 ? s.Keys : new List<KeyBinding>(),
                });
            }
        }
        else
        {
            // 补齐到 10 层（用户可能手动删过）
            for (int i = 1; i <= DefaultLayerCount; i++)
            {
                if (!s.Layers.Any(l => l.Id == i))
                    s.Layers.Add(new LayerOptions { Id = i, Name = $"第 {i} 层" });
            }
            s.Layers = s.Layers.OrderBy(l => l.Id).ToList();

            // 第 1 层若为空而 Keys 有内容，把 Keys 当作第 1 层
            var first = s.Layers.First(l => l.Id == 1);
            if (first.Keys.Count == 0 && s.Keys.Count > 0) first.Keys = s.Keys;
        }

        /* ⚠ `Settings.Keys` 必须**始终**是第 1 层那一份列表（同一个对象）。
         *
         * 引擎查第 1 层时先看 Layers[0].Keys，而界面/旧代码走 Settings.Keys；
         * 两者一旦不是同一个 List，就会出现"界面上改了但按键没生效"
         * 或者"第 5 层新建的绑定跑到第 1 层去"这类诡异问题。
         * 这里统一成同一份引用，从根上消除这种可能。 */
        {
            var first = s.Layers.First(l => l.Id == 1);
            if (first.Keys.Count == 0 && s.Keys.Count > 0) first.Keys = s.Keys;
            s.Keys = first.Keys;
        }

        // 触发键：老用户默认 CapsLock → 第 1 层，短按行为沿用全局设置
        if (s.Triggers.Count == 0)
        {
            s.Triggers.Add(new TriggerKey
            {
                Key = "capslock",
                LayerId = 1,
                HoldMs = 200,
                TapAction = s.Global.TapCapsAction,
                DoubleTapAction = s.Global.DoubleTapCaps ? s.Global.DoubleTapCapsAction : "",
                Enabled = true,
            });
        }
        else
        {
            foreach (var t in s.Triggers)
            {
                if (t.LayerId < 1 || t.LayerId > s.Layers.Count) t.LayerId = 1;
                t.HoldMs = Math.Clamp(t.HoldMs, 50, 2000);
            }
        }
    }
}

/// <summary>默认设置：完整复刻上游 CapsLock+ 的 capslox 布局。</summary>
public static class DefaultSettings
{
    /// <summary>
    /// 内置搜索引擎表的版本号。新增内置引擎时 +1，老用户启动时会自动补齐缺失项。
    ///   1 = 最初的 10 个引擎
    ///   2 = 扩充到 29 个（国内用拼音首字母、国外用英文前两字母，全部带图标）
    ///   3 = 再补 18 个（Yahoo / DuckDuckGo / WolframAlpha / Google 图片·地图 /
    ///       Netflix / Gmail / Drive / YT Music / Gist / 搜狗 / Ecosia / Brave /
    ///       Spotify / 词典 / ChatGPT），共 47 个
    ///   4 = 改用 QuickSearch（ui/data/engines.json）的完整引擎表，共 304 个，
    ///       图标全部换为其内置 PNG
    ///   5 = 旧条目改用内置图标（替换掉磁盘上的 svg 图标路径）
    /// </summary>
    public const int BuiltinEnginesVersion = 5;

    public static AppSettings Create()
    {
        var s = new AppSettings { BuiltinEnginesVersion = BuiltinEnginesVersion };
        s.Keys = CapsloxScheme();
        s.Search = new List<QEntry>
        {
            new() { Key = "default", Value = "https://www.bing.com/search?q={q}", Comment = "默认搜索" },

            // ---- 引擎表由 QuickSearch 的 ui/data/engines.json 转换而来 ----
            // 生成脚本：tools/qs_final.py ；图标已内置为 Assets/engine-icons/*.png。
            // 关键字规则：国内引擎取拼音首字母，国外引擎取英文名前两个字母。
            new() { Key = "bd", Value = "https://www.baidu.com/s?wd={q}&ie=utf-8", Comment = "百度", EngineIcon = "bai_du" },
            new() { Key = "zh", Value = "https://www.zhihu.com/search?q={q}", Comment = "知乎", EngineIcon = "zhi_hu" },
            new() { Key = "bili", Value = "https://search.bilibili.com/all?keyword={q}", Comment = "哔哩哔哩", EngineIcon = "bi_li_bi_li" },
            new() { Key = "tb", Value = "https://s.taobao.com/search?q={q}", Comment = "淘宝", EngineIcon = "tao_bao" },
            new() { Key = "wiki", Value = "https://zh.wikipedia.org/wiki/{q}", Comment = "维基百科", EngineIcon = "wei_ji_bai_ke" },
            new() { Key = "deepseek", Value = "https://chat.deepseek.com/?q={q}", Comment = "DeepSeek", EngineIcon = "deepseek" },
            new() { Key = "gpt", Value = "https://chatgpt.com/?q={q}", Comment = "ChatGPT", EngineIcon = "chatgpt" },
            new() { Key = "wngp", Value = "https://115.com/?mode=search&search_value={q}", Comment = "115 网盘", EngineIcon = "115_wang_pan" },
            new() { Key = "e166", Value = "https://s.1688.com/selloffer/offer_search.htm?keywords={q}", Comment = "1688", EngineIcon = "1688" },
            new() { Key = "diny", Value = "https://www.1905.com/search/?q={q}", Comment = "1905 电影网", EngineIcon = "1905_dian_ying_wang" },
            new() { Key = "so", Value = "https://www.so.com/s?ie=utf-8&q={q}", Comment = "360", EngineIcon = "360" },
            new() { Key = "dm", Value = "https://so.3dmgame.com/?keyword={q}", Comment = "3DM", EngineIcon = "3dm" },
            new() { Key = "px", Value = "https://500px.com/search?q={q}", Comment = "500px", EngineIcon = "500px" },
            new() { Key = "ao", Value = "https://archiveofourown.org/works/search?work_search%5Bquery%5D={q}", Comment = "AO3", EngineIcon = "ao3" },
            new() { Key = "ap", Value = "https://apkpure.com/search?q={q}", Comment = "APKPure", EngineIcon = "apkpure" },
            new() { Key = "ac", Value = "https://www.acfun.cn/search/?type=complex&keyword={q}", Comment = "AcFun", EngineIcon = "acfun" },
            new() { Key = "al", Value = "https://www.aliexpress.com/wholesale?SearchText={q}", Comment = "AliExpress", EngineIcon = "aliexpress" },
            new() { Key = "al2", Value = "https://alternativeto.net/browse/search/?q={q}", Comment = "AlternativeTo", EngineIcon = "alternativeto" },
            new() { Key = "am", Value = "https://www.amazon.com/s?k={q}", Comment = "Amazon", EngineIcon = "ya_ma_xun" },
            new() { Key = "apache", Value = "https://issues.apache.org/jira/secure/QuickSearch.jspa?searchString={q}", Comment = "Apache Issues", EngineIcon = "apache_issues" },
            new() { Key = "arch", Value = "https://wiki.archlinux.org/index.php?search={q}", Comment = "Arch Wiki", EngineIcon = "arch_wiki" },
            new() { Key = "ar", Value = "https://www.artstation.com/search?q={q}", Comment = "ArtStation", EngineIcon = "artstation" },
            new() { Key = "bb2", Value = "https://www.bbc.co.uk/search?q={q}", Comment = "BBC", EngineIcon = "bbc" },
            new() { Key = "ba", Value = "https://bandcamp.com/search?q={q}", Comment = "Bandcamp", EngineIcon = "bandcamp" },
            new() { Key = "be", Value = "https://www.behance.net/search/projects?search={q}", Comment = "Behance", EngineIcon = "behance" },
            new() { Key = "bi", Value = "https://cn.bing.com/search?q={q}", Comment = "Bing", EngineIcon = "bi_ying" },
            new() { Key = "bitbucket", Value = "https://bitbucket.org/repo/all?name={q}", Comment = "Bitbucket", EngineIcon = "bitbucket" },
            new() { Key = "bl", Value = "https://bsky.app/search?q={q}", Comment = "Bluesky", EngineIcon = "bluesky" },
            new() { Key = "br", Value = "https://search.brave.com/search?q={q}", Comment = "Brave 搜索", EngineIcon = "brave_sou_suo" },
            new() { Key = "cn", Value = "https://edition.cnn.com/search?q={q}", Comment = "CNN", EngineIcon = "cnn" },
            new() { Key = "csdn", Value = "https://so.csdn.net/so/search?q={q}", Comment = "CSDN", EngineIcon = "csdn" },
            new() { Key = "ch", Value = "https://chrome.google.com/webstore/search/{q}", Comment = "Chrome商店", EngineIcon = "chrome_shang_dian" },
            new() { Key = "claude", Value = "https://claude.ai/new?q={q}", Comment = "Claude", EngineIcon = "claude" },
            new() { Key = "codeberg", Value = "https://codeberg.org/explore/repos?q={q}", Comment = "Codeberg", EngineIcon = "codeberg" },
            new() { Key = "co2", Value = "https://www.codecademy.com/search?query={q}", Comment = "Codecademy", EngineIcon = "codecademy" },
            new() { Key = "conda", Value = "https://anaconda.org/search?q={q}", Comment = "Conda", EngineIcon = "conda" },
            new() { Key = "copilot", Value = "https://copilot.microsoft.com/?q={q}", Comment = "Copilot", EngineIcon = "copilot" },
            new() { Key = "co", Value = "https://www.coursera.org/search?query={q}", Comment = "Coursera", EngineIcon = "coursera" },
            new() { Key = "cr", Value = "https://www.crunchyroll.com/search?q={q}", Comment = "Crunchyroll", EngineIcon = "crunchyroll" },
            new() { Key = "db", Value = "https://dblp.org/search?q={q}", Comment = "DBLP", EngineIcon = "dblp" },
            new() { Key = "dev", Value = "https://dev.to/search?q={q}", Comment = "DEV", EngineIcon = "dev" },
            new() { Key = "da", Value = "https://www.dailymotion.com/search/{q}", Comment = "Dailymotion", EngineIcon = "dailymotion" },
            new() { Key = "de", Value = "https://www.deepl.com/translator#zh/en/{q}", Comment = "DeepL", EngineIcon = "deepl" },
            new() { Key = "de2", Value = "https://www.deviantart.com/search?q={q}", Comment = "DeviantArt", EngineIcon = "deviantart" },
            new() { Key = "docker", Value = "https://hub.docker.com/search?q={q}", Comment = "Docker Hub", EngineIcon = "docker_hub" },
            new() { Key = "dr", Value = "https://dribbble.com/search/{q}", Comment = "Dribbble", EngineIcon = "dribbble" },
            new() { Key = "dr2", Value = "https://www.dropbox.com/search/personal?query={q}", Comment = "Dropbox", EngineIcon = "dropbox" },
            new() { Key = "dd", Value = "https://duckduckgo.com/?q={q}", Comment = "DuckDuckGo", EngineIcon = "duckduckgo" },
            new() { Key = "eb2", Value = "https://www.ebsco.com/search?q={q}", Comment = "EBSCO", EngineIcon = "ebsco" },
            new() { Key = "ec", Value = "https://www.ecosia.org/search?q={q}", Comment = "Ecosia", EngineIcon = "ecosia" },
            new() { Key = "ed", Value = "https://microsoftedge.microsoft.com/addons/search/{q}", Comment = "Edge商店", EngineIcon = "edge_shang_dian" },
            new() { Key = "ep", Value = "https://store.epicgames.com/zh-CN/search?q={q}", Comment = "Epic 商店", EngineIcon = "epic_shang_dian" },
            new() { Key = "et", Value = "https://www.etsy.com/search?q={q}", Comment = "Etsy", EngineIcon = "etsy" },
            new() { Key = "fd", Value = "https://search.f-droid.org/?q={q}", Comment = "F-Droid", EngineIcon = "f_droid" },
            new() { Key = "fa", Value = "https://www.facebook.com/search/results.php?q={q}", Comment = "Facebook", EngineIcon = "facebook" },
            new() { Key = "fr2", Value = "https://www.thefreedictionary.com/{q}", Comment = "Free Dictionary", EngineIcon = "free_dictionary" },
            new() { Key = "fr", Value = "https://www.freepik.com/search?query={q}", Comment = "Freepik", EngineIcon = "freepik" },
            new() { Key = "go2", Value = "https://www.gog.com/games?query={q}", Comment = "GOG", EngineIcon = "gog" },
            new() { Key = "gemini", Value = "https://gemini.google.com/app?q={q}", Comment = "Gemini", EngineIcon = "gemini" },
            new() { Key = "ge", Value = "https://genius.com/search?q={q}", Comment = "Genius", EngineIcon = "genius" },
            new() { Key = "gi", Value = "https://giphy.com/search/{q}", Comment = "Giphy", EngineIcon = "giphy" },
            new() { Key = "gh", Value = "https://github.com/search?q={q}", Comment = "GitHub", EngineIcon = "github" },
            new() { Key = "gl", Value = "https://gitlab.com/search?search={q}", Comment = "GitLab", EngineIcon = "gitlab" },
            new() { Key = "gitee", Value = "https://search.gitee.com/?q={q}", Comment = "Gitee", EngineIcon = "gitee" },
            new() { Key = "gopkg", Value = "https://pkg.go.dev/search?q={q}", Comment = "Go Packages", EngineIcon = "go_packages" },
            new() { Key = "go4", Value = "https://www.goodreads.com/search?q={q}", Comment = "Goodreads", EngineIcon = "goodreads" },
            new() { Key = "go", Value = "https://www.google.com/search?q={q}&ie=utf-8&oe=utf-8", Comment = "Google", EngineIcon = "google" },
            new() { Key = "go3", Value = "https://drive.google.com/drive/u/0/search?q={q}", Comment = "Google Drive", EngineIcon = "google_drive" },
            new() { Key = "gr", Value = "https://greasyfork.org/scripts?q={q}&utf8=✓", Comment = "GreasyFork", EngineIcon = "greasyfork" },
            new() { Key = "grok", Value = "https://grok.com/?q={q}", Comment = "Grok", EngineIcon = "grok" },
            new() { Key = "hn", Value = "https://hn.algolia.com/?q={q}", Comment = "Hacker News", EngineIcon = "hacker_news" },
            new() { Key = "hf", Value = "https://huggingface.co/models?search={q}", Comment = "Hugging Face", EngineIcon = "hugging_face" },
            new() { Key = "hu", Value = "https://www.humblebundle.com/store/search?search={q}", Comment = "Humble Bundle", EngineIcon = "humble_bundle" },
            new() { Key = "ie", Value = "https://ieeexplore.ieee.org/search/searchresult.jsp?queryText={q}", Comment = "IEEE Xplore", EngineIcon = "ieee_xplore" },
            new() { Key = "ie2", Value = "https://standards.ieee.org/search/?q={q}", Comment = "IEEE 标准", EngineIcon = "ieee_biao_zhun" },
            new() { Key = "ig2", Value = "https://www.igdb.com/search?type=1&q={q}", Comment = "IGDB", EngineIcon = "igdb" },
            new() { Key = "ig", Value = "https://www.ign.com/search?q={q}", Comment = "IGN", EngineIcon = "ign" },
            new() { Key = "ip", Value = "https://www.ip138.com/iplookup.asp?ip={q}", Comment = "IP138", EngineIcon = "ip138" },
            new() { Key = "it2", Value = "https://www.ithome.com/search/{q}.html", Comment = "IT之家", EngineIcon = "it_zhi_jia" },
            new() { Key = "ic2", Value = "https://icon-sets.iconify.design/?query={q}", Comment = "Iconify", EngineIcon = "iconify" },
            new() { Key = "infoq", Value = "https://www.infoq.cn/search?q={q}", Comment = "InfoQ", EngineIcon = "infoq" },
            new() { Key = "in", Value = "https://archive.org/search?query={q}", Comment = "Internet Archive", EngineIcon = "internet_archive" },
            new() { Key = "js", Value = "http://www.jstor.org/action/doAdvancedSearch?q0={q}", Comment = "JSTOR", EngineIcon = "jstor" },
            new() { Key = "kh", Value = "https://www.khanacademy.org/search?page_search_query={q}", Comment = "Khan Academy", EngineIcon = "khan_academy" },
            new() { Key = "kimi", Value = "https://www.kimi.com/?q={q}", Comment = "Kimi", EngineIcon = "kimi" },
            new() { Key = "la", Value = "https://www.last.fm/search?q={q}", Comment = "Last.fm", EngineIcon = "last_fm" },
            new() { Key = "leetcode", Value = "https://leetcode.cn/search/?q={q}", Comment = "LeetCode", EngineIcon = "leetcode" },
            new() { Key = "li", Value = "https://www.linguee.com/english-chinese/search?query={q}", Comment = "Linguee", EngineIcon = "linguee" },
            new() { Key = "li2", Value = "https://www.linkedin.com/search/results/all/?keywords={q}", Comment = "LinkedIn", EngineIcon = "linkedin" },
            new() { Key = "lobsters", Value = "https://lobste.rs/search?q={q}", Comment = "Lobsters", EngineIcon = "lobsters" },
            new() { Key = "lo", Value = "https://lottiefiles.com/search?q={q}", Comment = "Lottiefiles", EngineIcon = "lottiefiles" },
            new() { Key = "md", Value = "https://developer.mozilla.org/zh-CN/search?q={q}", Comment = "MDN", EngineIcon = "mdn" },
            new() { Key = "me2", Value = "https://mega.nz/search?q={q}", Comment = "MEGA", EngineIcon = "mega" },
            new() { Key = "mi2", Value = "https://ocw.mit.edu/search/?q={q}", Comment = "MIT OpenCourseWare", EngineIcon = "mit_opencourseware" },
            new() { Key = "maven", Value = "https://mvnrepository.com/search?q={q}", Comment = "Maven", EngineIcon = "maven" },
            new() { Key = "me", Value = "https://www.metacritic.com/search/{q}/", Comment = "Metacritic", EngineIcon = "metacritic" },
            new() { Key = "mi", Value = "https://apps.microsoft.com/search?query={q}", Comment = "Microsoft Store", EngineIcon = "microsoft_store" },
            new() { Key = "mistral", Value = "https://chat.mistral.ai/chat?q={q}", Comment = "Mistral", EngineIcon = "mistral" },
            new() { Key = "mj", Value = "https://www.mojeek.com/search?q={q}", Comment = "Mojeek", EngineIcon = "mojeek" },
            new() { Key = "ne", Value = "https://www.nexusmods.com/search/?gsearch={q}", Comment = "Nexus Mods", EngineIcon = "nexus_mods" },
            new() { Key = "nu", Value = "https://www.nuget.org/packages?q={q}", Comment = "NuGet", EngineIcon = "nuget" },
            new() { Key = "on", Value = "https://onedrive.live.com/?q={q}", Comment = "OneDrive", EngineIcon = "onedrive" },
            new() { Key = "op2", Value = "https://opencritic.com/search?criteria={q}", Comment = "OpenCritic", EngineIcon = "opencritic" },
            new() { Key = "op", Value = "https://www.openstreetmap.org/search?query={q}", Comment = "OpenStreetMap", EngineIcon = "openstreetmap" },
            new() { Key = "packagist", Value = "https://packagist.org/search/?q={q}", Comment = "Packagist", EngineIcon = "packagist" },
            new() { Key = "pa", Value = "https://papago.naver.com/?sk=auto&tk=zh-CN&st={q}", Comment = "Papago", EngineIcon = "papago" },
            new() { Key = "pa2", Value = "https://paperswithcode.com/search?q={q}", Comment = "Papers with Code", EngineIcon = "papers_with_code" },
            new() { Key = "perplexity", Value = "https://www.perplexity.ai/search?q={q}", Comment = "Perplexity AI", EngineIcon = "perplexity_ai" },
            new() { Key = "pe", Value = "https://www.pexels.com/search/{q}/", Comment = "Pexels", EngineIcon = "pexels" },
            new() { Key = "pi3", Value = "https://www.pinterest.com/search/pins/?q={q}", Comment = "Pinterest", EngineIcon = "pinterest" },
            new() { Key = "pi2", Value = "https://pixabay.com/zh/images/search/{q}/", Comment = "Pixabay", EngineIcon = "pixabay" },
            new() { Key = "poe", Value = "https://poe.com/search?q={q}", Comment = "Poe", EngineIcon = "poe" },
            new() { Key = "pr2", Value = "https://www.producthunt.com/search?q={q}", Comment = "Product Hunt", EngineIcon = "product_hunt" },
            new() { Key = "pr", Value = "https://www.gutenberg.org/ebooks/search/?query={q}", Comment = "Project Gutenberg", EngineIcon = "project_gutenberg" },
            new() { Key = "pu", Value = "https://pubmed.ncbi.nlm.nih.gov/?term={q}", Comment = "PubMed", EngineIcon = "pubmed" },
            new() { Key = "pypi", Value = "https://pypi.org/search/?q={q}", Comment = "PyPI", EngineIcon = "pypi" },
            new() { Key = "qq", Value = "https://y.qq.com/portal/search.html#page=1&searchid=1&remoteplace=txt.yqq.top&t=song&w={q}", Comment = "QQ音乐", EngineIcon = "qq_yin_yue" },
            new() { Key = "quora", Value = "https://www.quora.com/search?q={q}", Comment = "Quora", EngineIcon = "quora" },
            new() { Key = "qw", Value = "https://www.qwant.com/?q={q}", Comment = "Qwant", EngineIcon = "qwant" },
            new() { Key = "qwen", Value = "https://chat.qwen.ai/?q={q}", Comment = "Qwen Chat", EngineIcon = "qwen_chat" },
            new() { Key = "re2", Value = "https://www.reddit.com/search/?q={q}", Comment = "Reddit", EngineIcon = "reddit" },
            new() { Key = "re", Value = "https://www.researchgate.net/search?q={q}", Comment = "ResearchGate", EngineIcon = "researchgate" },
            new() { Key = "gems", Value = "https://rubygems.org/search?query={q}", Comment = "RubyGems", EngineIcon = "rubygems" },
            new() { Key = "sc", Value = "https://www.sciencedirect.com/search?qs={q}", Comment = "ScienceDirect", EngineIcon = "sciencedirect" },
            new() { Key = "se2", Value = "https://www.semanticscholar.org/search?q={q}", Comment = "Semantic Scholar", EngineIcon = "semantic_scholar" },
            new() { Key = "sh", Value = "https://shopee.sg/search?keyword={q}", Comment = "Shopee", EngineIcon = "shopee" },
            new() { Key = "si2", Value = "https://www.similarweb.com/website/{q}/", Comment = "SimilarWeb", EngineIcon = "similarweb" },
            new() { Key = "so4", Value = "https://www.softpedia.com/search.php?q={q}", Comment = "Softpedia", EngineIcon = "softpedia" },
            new() { Key = "so3", Value = "https://soundcloud.com/search?q={q}", Comment = "SoundCloud", EngineIcon = "soundcloud" },
            new() { Key = "sourcegraph", Value = "https://sourcegraph.com/search?q={q}", Comment = "Sourcegraph", EngineIcon = "sourcegraph" },
            new() { Key = "sp", Value = "https://open.spotify.com/search/{q}", Comment = "Spotify", EngineIcon = "spotify" },
            new() { Key = "se", Value = "https://stackexchange.com/search?q={q}", Comment = "Stack Exchange", EngineIcon = "stack_exchange" },
            new() { Key = "so2", Value = "https://stackoverflow.com/search?q={q}", Comment = "StackOverflow", EngineIcon = "stackoverflow_gu_ge" },
            new() { Key = "st", Value = "https://www.startpage.com/sp/search?query={q}", Comment = "Startpage", EngineIcon = "startpage" },
            new() { Key = "st2", Value = "https://store.steampowered.com/search/?term={q}", Comment = "Steam", EngineIcon = "steam" },
            new() { Key = "ta", Value = "https://www.taptap.cn/search/{q}", Comment = "TapTap", EngineIcon = "taptap" },
            new() { Key = "te", Value = "https://www.temu.com/search_result.html?search_key={q}", Comment = "Temu", EngineIcon = "temu" },
            new() { Key = "th", Value = "https://www.threads.net/search?q={q}", Comment = "Threads", EngineIcon = "threads" },
            new() { Key = "ti", Value = "https://www.tiktok.com/search?q={q}", Comment = "TikTok", EngineIcon = "tiktok" },
            new() { Key = "tu", Value = "https://www.tumblr.com/search/{q}", Comment = "Tumblr", EngineIcon = "tumblr" },
            new() { Key = "tw", Value = "https://www.twitch.tv/search?term={q}", Comment = "Twitch", EngineIcon = "twitch" },
            new() { Key = "tw2", Value = "https://twitter.com/search?q={q}", Comment = "Twitter", EngineIcon = "twitter" },
            new() { Key = "ud", Value = "https://www.udemy.com/courses/search/?q={q}", Comment = "Udemy", EngineIcon = "udemy" },
            new() { Key = "un", Value = "https://unsplash.com/s/photos/{q}", Comment = "Unsplash", EngineIcon = "unsplash" },
            new() { Key = "vi", Value = "https://vimeo.com/search?q={q}", Comment = "Vimeo", EngineIcon = "vimeo" },
            new() { Key = "vi2", Value = "https://www.virustotal.com/gui/search/{q}", Comment = "VirusTotal", EngineIcon = "virustotal" },
            new() { Key = "vo", Value = "https://www.vocabulary.com/dictionary/{q}", Comment = "Vocabulary.com", EngineIcon = "vocabulary_com" },
            new() { Key = "wo", Value = "http://apps.webofknowledge.com/UA_GeneralSearch.do?fieldCount=3&action=search&product=UA&search_mode=GeneralSearch&max_field_count=25&max_field_notice=Notice%3A+You+cannot+add+another+field.&input_invalid_notice=Search+Error%3A+Please+enter+a+search+term.&input_invalid_notice_limits=+%3Cbr%2F%3ENote%3A+Fields+displayed+in+scrolling+boxes+must+be+combined+with+at+least+one+other+search+field.&sa_img_alt=Select+terms+from+the+index&value(input1)={q}&value%28select1%29=TI&value%28hidInput1%29=initVoid&value%28hidShowIcon1%29=0&value%28bool_1_2%29=AND&value%28input2%29=&value%28select2%29=AU&value%28hidInput2%29=initAuthor&value%28hidShowIcon2%29=1&value%28bool_2_3%29=AND&value%28input3%29=&value%28select3%29=SO&value%28hidInput3%29=initSource&value%28hidShowIcon3%29=1&limitStatus=collapsed&expand_alt=Expand+these+settings&expand_title=Expand+these+settings&collapse_alt=Collapse+these+settings&collapse_title=Collapse+these+settings&SinceLastVisit_UTC=&SinceLastVisit_DATE=×panStatus=display%3A+block&timeSpanCollapsedListStatus=display%3A+none&period=Range+Selection&range=ALL&ssStatus=display%3Anone&ss_lemmatization=On&ss_query_language=&rsStatus=display%3Anone&rs_rec_per_page=10&rs_sort_by=PY.D%3BLD.D%3BVL.D%3BSO.A%3BPG.A%3BAU.A&rs_refinePanel=visibility%3Ashow", Comment = "WOS", EngineIcon = "wos" },
            new() { Key = "wa", Value = "https://www.walmart.com/search?q={q}", Comment = "Walmart", EngineIcon = "walmart" },
            new() { Key = "wa2", Value = "https://web.archive.org/web/*/{q}", Comment = "Wayback Machine", EngineIcon = "wayback_machine" },
            new() { Key = "wikihow", Value = "https://zh.wikihow.com/wikiHowTo?search={q}", Comment = "WikiHow", EngineIcon = "wikihow" },
            new() { Key = "wi", Value = "https://en.wikipedia.org/w/index.php?search={q}", Comment = "Wikipedia（英文）", EngineIcon = "wei_ji_bai_ke" },
            new() { Key = "wo2", Value = "https://www.wolframalpha.com/input?i={q}", Comment = "Wolfram Alpha", EngineIcon = "wolfram_alpha" },
            new() { Key = "ya", Value = "https://search.yahoo.com/search?p={q}", Comment = "Yahoo", EngineIcon = "yahoo" },
            new() { Key = "yx", Value = "https://yandex.com/search/?text={q}", Comment = "Yandex", EngineIcon = "yandex" },
            new() { Key = "ya2", Value = "https://yandex.com/images/search?text={q}", Comment = "Yandex 图片", EngineIcon = "yandex_tu_pian" },
            new() { Key = "yo", Value = "https://www.youtube.com/results?search_query={q}", Comment = "YouTube", EngineIcon = "youtube" },
            new() { Key = "yom", Value = "https://music.youtube.com/search?q={q}", Comment = "YouTube Music", EngineIcon = "youtube_music" },
            new() { Key = "ar2", Value = "https://arxiv.org/search/?query={q}", Comment = "arXiv", EngineIcon = "arxiv" },
            new() { Key = "crates", Value = "https://crates.io/search?q={q}", Comment = "crates.io", EngineIcon = "crates_io" },
            new() { Key = "eb", Value = "https://www.ebay.com/sch/i.html?_nkw={q}", Comment = "eBay", EngineIcon = "ebay" },
            new() { Key = "ed2", Value = "https://www.edx.org/search?q={q}", Comment = "edX", EngineIcon = "edx" },
            new() { Key = "fl", Value = "https://www.flickr.com/search/?q={q}", Comment = "flickr", EngineIcon = "flickr" },
            new() { Key = "fr3", Value = "https://www.freecodecamp.org/news/search?query={q}", Comment = "freeCodeCamp", EngineIcon = "freecodecamp" },
            new() { Key = "ic", Value = "https://www.iconfont.cn/search/index?searchType=icon&q={q}", Comment = "iconfont", EngineIcon = "iconfont" },
            new() { Key = "it", Value = "https://itch.io/search?q={q}", Comment = "itch.io", EngineIcon = "itch_io" },
            new() { Key = "ni", Value = "https://www.nicovideo.jp/search/{q}", Comment = "niconico", EngineIcon = "niconico" },
            new() { Key = "np", Value = "https://www.npmjs.com/search?q={q}", Comment = "npm", EngineIcon = "npm" },
            new() { Key = "pi", Value = "https://www.pixiv.net/tags/{q}", Comment = "pixiv", EngineIcon = "pixiv" },
            new() { Key = "wh", Value = "https://who.is/whois/{q}", Comment = "who.is", EngineIcon = "who_is" },
            new() { Key = "dngx", Value = "https://dxy.com/search?keyword={q}", Comment = "丁香医生", EngineIcon = "ding_xiang_yi_sheng" },
            new() { Key = "wnfn", Value = "http://www.wanfangdata.com.cn/search/searchList.do?searchType=all&searchWord={q}", Comment = "万方", EngineIcon = "wan_fang" },
            new() { Key = "xich", Value = "https://www.xiachufang.com/search/?keyword={q}", Comment = "下厨房", EngineIcon = "xia_chu_fang" },
            new() { Key = "dngf", Value = "https://so.eastmoney.com/web/s?keyword={q}", Comment = "东方财富", EngineIcon = "dong_fang_cai_fu" },
            new() { Key = "mo", Value = "https://www.icourse163.org/search.htm?search={q}", Comment = "中国大学MOOC", EngineIcon = "zhong_guo_da_xue_mooc" },
            new() { Key = "zhng", Value = "https://sou.chinanews.com.cn/search.do?q={q}", Comment = "中国新闻网", EngineIcon = "zhong_guo_xin_wen_wang" },
            new() { Key = "ltin", Value = "https://search.rakuten.co.jp/search/mall/{q}/", Comment = "乐天市场", EngineIcon = "le_tian_shi_chang" },
            new() { Key = "ymxn", Value = "https://www.amazon.cn/s?k={q}", Comment = "亚马逊", EngineIcon = "ya_ma_xun" },
            new() { Key = "jd", Value = "https://search.jd.com/Search?keyword={q}&enc=utf-8", Comment = "京东", EngineIcon = "jing_dong" },
            new() { Key = "shnm", Value = "https://search.smzdm.com/?c=home&s={q}", Comment = "什么值得买", EngineIcon = "shen_me_zhi_de_mai" },
            new() { Key = "jnrt", Value = "https://www.toutiao.com/search/?keyword={q}", Comment = "今日头条", EngineIcon = "jin_ri_tou_tiao" },
            new() { Key = "qzhz", Value = "https://www.qcc.com/web/search?key={q}", Comment = "企查查", EngineIcon = "qi_zha_zha" },
            new() { Key = "yuk", Value = "https://so.youku.com/search_video/q_{q}", Comment = "优酷", EngineIcon = "you_ku" },
            new() { Key = "fngh", Value = "https://search.ifeng.com/sofeng/search.action?q={q}", Comment = "凤凰网", EngineIcon = "feng_huang_wang" },
            new() { Key = "jinq", Value = "https://dictionary.cambridge.org/dictionary/english-chinese-simplified/{q}", Comment = "剑桥词典", EngineIcon = "jian_qiao_ci_dian" },
            new() { Key = "cnblogs", Value = "https://zzk.cnblogs.com/s?w={q}", Comment = "博客园", EngineIcon = "bo_ke_yuan" },
            new() { Key = "jk", Value = "https://web.okjike.com/search?q={q}", Comment = "即刻", EngineIcon = "ji_ke" },
            new() { Key = "gshw", Value = "https://so.gushiwen.cn/search.aspx?value={q}", Comment = "古诗文网", EngineIcon = "gu_shi_wen_wang" },
            new() { Key = "mgyn", Value = "https://music.migu.cn/v3/search?keyword={q}", Comment = "咪咕音乐", EngineIcon = "mi_gu_yin_yue" },
            new() { Key = "wipn", Value = "https://category.vip.com/suggest.php?keyword={q}", Comment = "唯品会", EngineIcon = "wei_pin_hui" },
            new() { Key = "dzhn", Value = "https://www.dianping.com/search/keyword/1/0_{q}", Comment = "大众点评", EngineIcon = "da_zhong_dian_ping" },
            new() { Key = "tinq", Value = "https://www.tianqi.com/so/?keyword={q}", Comment = "天气网", EngineIcon = "tian_qi_wang" },
            new() { Key = "tinm", Value = "https://list.tmall.com/search_product.htm?q={q}", Comment = "天猫", EngineIcon = "tian_mao" },
            new() { Key = "tiny", Value = "https://www.tianyancha.com/search?key={q}", Comment = "天眼查", EngineIcon = "tian_yan_cha" },
            new() { Key = "tipn", Value = "https://so.pcauto.com.cn/cs/s?q={q}", Comment = "太平洋汽车", EngineIcon = "tai_ping_yang_qi_che" },
            new() { Key = "yngs", Value = "https://search.cctv.com/search.php?qtext={q}", Comment = "央视网", EngineIcon = "yang_shi_wang" },
            new() { Key = "tt", Value = "https://so.toutiao.com/search?keyword={q}", Comment = "头条搜索", EngineIcon = "tou_tiao_sou_suo" },
            new() { Key = "kk", Value = "https://quark.sm.cn/s?q={q}", Comment = "夸克搜索", EngineIcon = "kua_ke_sou_suo" },
            new() { Key = "kukw", Value = "https://pan.quark.cn/search?q={q}", Comment = "夸克网盘", EngineIcon = "kua_ke_wang_pan" },
            new() { Key = "hodf", Value = "https://www.haodf.com/search/?kw={q}", Comment = "好大夫在线", EngineIcon = "hao_da_fu_zai_xian" },
            new() { Key = "hokn", Value = "https://haokan.baidu.com/web/search/page?query={q}", Comment = "好看视频", EngineIcon = "hao_kan_shi_pin" },
            new() { Key = "kngf", Value = "https://search.kongfz.com/product_result/?key={q}", Comment = "孔夫子旧书网", EngineIcon = "kong_fu_zi_jiu_shu_wang" },
            new() { Key = "yji", Value = "https://www.ikea.cn/cn/zh/search/products/?q={q}", Comment = "宜家", EngineIcon = "yi_jia" },
            new() { Key = "xiom", Value = "https://app.mi.com/searchAll?keywords={q}", Comment = "小米应用商店", EngineIcon = "xiao_mi_ying_yong_shang_dian" },
            new() { Key = "xhs", Value = "https://www.xiaohongshu.com/search_result?keyword={q}", Comment = "小红书", EngineIcon = "xiao_hong_shu" },
            new() { Key = "sspai", Value = "https://sspai.com/search/post/{q}", Comment = "少数派", EngineIcon = "shao_shu_pai" },
            new() { Key = "oschina", Value = "https://www.oschina.net/search?q={q}", Comment = "开源中国", EngineIcon = "kai_yuan_zhong_guo" },
            new() { Key = "dngd", Value = "https://search.dangdang.com/?key={q}", Comment = "当当", EngineIcon = "dang_dang" },
            new() { Key = "wx", Value = "https://weixin.sogou.com/weixin?ie=utf8&type=2&query={q}", Comment = "微信(搜狗)", EngineIcon = "wei_xin_sou_gou" },
            new() { Key = "wixn", Value = "https://weread.qq.com/web/search/books?keyword={q}", Comment = "微信读书", EngineIcon = "wei_xin_du_shu" },
            new() { Key = "wb", Value = "https://s.weibo.com/weibo/{q}", Comment = "微博", EngineIcon = "wei_bo" },
            new() { Key = "byng3", Value = "https://www.bing.com/images/search?q={q}", Comment = "必应图片", EngineIcon = "bi_ying" },
            new() { Key = "byng2", Value = "https://www.bing.com/maps?q={q}", Comment = "必应地图", EngineIcon = "bi_ying" },
            new() { Key = "byng", Value = "https://cn.bing.com/dict/search?q={q}", Comment = "必应翻译", EngineIcon = "bi_ying" },
            new() { Key = "kuis", Value = "https://www.kuaishou.com/search/video?searchKey={q}", Comment = "快手", EngineIcon = "kuai_shou" },
            new() { Key = "kuid", Value = "https://www.kuaidi100.com/?nu={q}", Comment = "快递100", EngineIcon = "kuai_di_100" },
            new() { Key = "mkwn", Value = "https://www.imooc.com/search/?words={q}", Comment = "慕课网", EngineIcon = "mu_ke_wang" },
            new() { Key = "dngc", Value = "https://www.dongchedi.com/search?keyword={q}", Comment = "懂车帝", EngineIcon = "dong_che_di" },
            new() { Key = "dy", Value = "https://www.douyin.com/search/{q}", Comment = "抖音", EngineIcon = "dou_yin" },
            new() { Key = "pdd", Value = "https://mobile.yangkeduo.com/search_result.html?search_key={q}", Comment = "拼多多", EngineIcon = "pin_duo_duo" },
            new() { Key = "juejin", Value = "https://juejin.cn/search?query={q}", Comment = "掘金", EngineIcon = "jue_jin" },
            new() { Key = "suhs", Value = "https://so.tv.sohu.com/mts?wd={q}", Comment = "搜狐视频", EngineIcon = "sou_hu_shi_pin" },
            new() { Key = "sogou", Value = "https://www.sogou.com/web?query={q}", Comment = "搜狗", EngineIcon = "sou_gou" },
            new() { Key = "sugu2", Value = "https://pic.sogou.com/pics?query={q}", Comment = "搜狗图片", EngineIcon = "sou_gou" },
            new() { Key = "sugu", Value = "https://fanyi.sogou.com/text?keyword={q}", Comment = "搜狗翻译", EngineIcon = "sou_gou_fan_yi" },
            new() { Key = "wy", Value = "https://yiyan.baidu.com/?q={q}", Comment = "文心一言", EngineIcon = "wen_xin_yi_yan" },
            new() { Key = "xnln", Value = "https://search.sina.com.cn/?q={q}", Comment = "新浪新闻", EngineIcon = "xin_lang_xin_wen" },
            new() { Key = "ych", Value = "https://so.yiche.com/?q={q}", Comment = "易车", EngineIcon = "yi_che" },
            new() { Key = "jnji", Value = "https://search.jjwxc.net/search.php?kw={q}", Comment = "晋江文学城", EngineIcon = "jin_jiang_wen_xue_cheng" },
            new() { Key = "zp", Value = "https://chatglm.cn/?q={q}", Comment = "智谱清言", EngineIcon = "zhi_pu_qing_yan" },
            new() { Key = "yudo", Value = "https://youdao.com/w/{q}", Comment = "有道词典", EngineIcon = "you_dao_ci_dian" },
            new() { Key = "guokr", Value = "https://www.guokr.com/search/?q={q}", Comment = "果壳", EngineIcon = "guo_ke" },
            new() { Key = "klns", Value = "https://www.collinsdictionary.com/dictionary/english/{q}", Comment = "柯林斯词典", EngineIcon = "ke_lin_si_ci_dian" },
            new() { Key = "hndi", Value = "https://www.zdic.net/hans/{q}", Comment = "汉典", EngineIcon = "han_dian" },
            new() { Key = "qchz", Value = "https://sou.autohome.com.cn/zonghe?q={q}", Comment = "汽车之家", EngineIcon = "qi_che_zhi_jia" },
            new() { Key = "hailuo", Value = "https://hailuoai.com/?q={q}", Comment = "海螺AI", EngineIcon = "hai_luo_ai" },
            new() { Key = "hic", Value = "https://dict.cn/{q}", Comment = "海词", EngineIcon = "hai_ci" },
            new() { Key = "yumn", Value = "https://so.gamersky.com/?s={q}", Comment = "游民星空", EngineIcon = "you_min_xing_kong" },
            new() { Key = "pngp", Value = "https://www.thepaper.cn/searchResult?searchWord={q}", Comment = "澎湃新闻", EngineIcon = "peng_pai_xin_wen" },
            new() { Key = "iqch", Value = "https://aiqicha.baidu.com/s?q={q}", Comment = "爱企查", EngineIcon = "ai_qi_cha" },
            new() { Key = "ikqc", Value = "https://so.xcar.com.cn/?q={q}", Comment = "爱卡汽车", EngineIcon = "ai_ka_qi_che" },
            new() { Key = "iqy", Value = "https://so.iqiyi.com/so/q_{q}", Comment = "爱奇艺", EngineIcon = "ai_qi_yi" },
            new() { Key = "nijn", Value = "https://www.oxfordlearnersdictionaries.com/search/english/?q={q}", Comment = "牛津词典", EngineIcon = "niu_jin_ci_dian" },
            new() { Key = "bidt", Value = "https://image.baidu.com/search/index?tn=baiduimage&ie=utf-8&word={q}", Comment = "百度图片", EngineIcon = "bai_du_zhi_dao" },
            new() { Key = "bidd", Value = "https://map.baidu.com/search?querytype=s&wd={q}", Comment = "百度地图", EngineIcon = "bai_du_di_tu" },
            new() { Key = "bidx", Value = "http://xueshu.baidu.com/s?wd={q}", Comment = "百度学术", EngineIcon = "bai_du_xue_shu" },
            new() { Key = "bwk", Value = "https://wenku.baidu.com/search?word={q}", Comment = "百度文库", EngineIcon = "bai_du_wen_ku" },
            new() { Key = "bidh", Value = "https://hanyu.baidu.com/s?wd={q}", Comment = "百度汉语", EngineIcon = "bai_du_han_yu" },
            new() { Key = "bb", Value = "https://baike.baidu.com/search/word?pic=1&sug=1&word={q}", Comment = "百度百科", EngineIcon = "bai_du_bai_ke" },
            new() { Key = "bzd", Value = "https://zhidao.baidu.com/search?word={q}", Comment = "百度知道", EngineIcon = "bai_du_zhi_dao" },
            new() { Key = "bjy", Value = "https://jingyan.baidu.com/search?word={q}", Comment = "百度经验", EngineIcon = "bai_du_jing_yan" },
            new() { Key = "bidw", Value = "https://pan.baidu.com/disk/home?#/search?key={q}", Comment = "百度网盘", EngineIcon = "bai_du_wang_pan" },
            new() { Key = "bf", Value = "https://fanyi.baidu.com/#auto/zh/{q}", Comment = "百度翻译", EngineIcon = "bai_du_fan_yi" },
            new() { Key = "zd", Value = "https://zhida.zhihu.com/?q={q}", Comment = "知乎直答", EngineIcon = "zhi_hu_zhi_da" },
            new() { Key = "zhwn", Value = "http://epub.cnki.net/kns/brief/default_result.aspx?txt_1_value1={q}&dbPrefix=SCDB&db_opt=CJFQ%2CCJFN%2CCDFD%2CCMFD%2CCPFD%2CIPFD%2CCCND%2CCCJD%2CHBRD&singleDB=SCDB&action=scdbsearch", Comment = "知网", EngineIcon = "zhi_wang" },
            new() { Key = "mt", Value = "https://metaso.cn/?q={q}", Comment = "秘塔AI搜索", EngineIcon = "mi_ta_ai_sou_suo" },
            new() { Key = "zhnk", Value = "https://www.zcool.com.cn/search/content?&word={q}", Comment = "站酷", EngineIcon = "zhan_ku" },
            new() { Key = "zhnz", Value = "https://tool.chinaz.com/?q={q}", Comment = "站长之家", EngineIcon = "zhan_zhang_zhi_jia" },
            new() { Key = "dyci", Value = "https://www.yicai.com/search?keys={q}", Comment = "第一财经", EngineIcon = "di_yi_cai_jing" },
            new() { Key = "jianshu", Value = "https://www.jianshu.com/search?q={q}", Comment = "简书", EngineIcon = "jian_shu" },
            new() { Key = "zngh", Value = "https://search.zongheng.com/s?keyword={q}", Comment = "纵横中文网", EngineIcon = "zong_heng_zhong_wen_wang" },
            new() { Key = "wks", Value = "https://zh.wikisource.org/w/index.php?search={q}", Comment = "维基文库", EngineIcon = "wei_ji_wen_ku" },
            new() { Key = "wngy2", Value = "https://you.163.com/search?keyword={q}", Comment = "网易严选", EngineIcon = "wang_yi_yan_xuan" },
            new() { Key = "wngy4", Value = "https://study.163.com/search.htm?keyword={q}", Comment = "网易云课堂", EngineIcon = "wang_yi_yun_ke_tang" },
            new() { Key = "wngy", Value = "https://music.163.com/#/search/m/?s={q}", Comment = "网易云音乐", EngineIcon = "wang_yi_yun_yin_yue" },
            new() { Key = "wngy3", Value = "https://www.163.com/search?keyword={q}", Comment = "网易新闻", EngineIcon = "wang_yi_xin_wen" },
            new() { Key = "maimai", Value = "https://maimai.cn/web/search_center?type=gossip&query={q}&highlight=true", Comment = "脉脉", EngineIcon = "mo_mo" },
            new() { Key = "txcloud", Value = "https://cloud.tencent.com/developer/search/article-{q}", Comment = "腾讯云社区", EngineIcon = "teng_xun_yun_she_qu" },
            new() { Key = "yb", Value = "https://yuanbao.tencent.com/?q={q}", Comment = "腾讯元宝", EngineIcon = "teng_xun_yuan_bao" },
            new() { Key = "tngx", Value = "https://map.qq.com/?what={q}", Comment = "腾讯地图", EngineIcon = "teng_xun_di_tu" },
            new() { Key = "tx", Value = "https://v.qq.com/x/search/?q={q}", Comment = "腾讯视频", EngineIcon = "teng_xun_shi_pin" },
            new() { Key = "tngx2", Value = "https://pc.qq.com/search.html#!keyword={q}", Comment = "腾讯软件中心", EngineIcon = "teng_xun_ruan_jian_zhong_xin" },
            new() { Key = "tv", Value = "https://so.mgtv.com/so?k={q}", Comment = "芒果TV", EngineIcon = "mang_guo_tv" },
            new() { Key = "hubn", Value = "https://huaban.com/search/?q={q}", Comment = "花瓣", EngineIcon = "hua_ban" },
            new() { Key = "snng", Value = "https://search.suning.com/{q}/", Comment = "苏宁易购", EngineIcon = "su_ning_yi_gou" },
            new() { Key = "runoob", Value = "https://www.runoob.com/?s={q}", Comment = "菜鸟教程", EngineIcon = "cai_niao_jiao_cheng" },
            new() { Key = "meng", Value = "https://zh.moegirl.org.cn/index.php?search={q}", Comment = "萌娘百科", EngineIcon = "meng_niang_bai_ke" },
            new() { Key = "hxi", Value = "https://www.huxiu.com/search.html?s={q}", Comment = "虎嗅", EngineIcon = "hu_xiu" },
            new() { Key = "hp", Value = "https://bbs.hupu.com/search?q={q}", Comment = "虎扑", EngineIcon = "hu_pu" },
            new() { Key = "xgus", Value = "https://www.ixigua.com/search/{q}", Comment = "西瓜视频", EngineIcon = "xi_gua_shi_pin" },
            new() { Key = "ggdt", Value = "https://www.google.com/maps/search/{q}", Comment = "谷歌地图", EngineIcon = "gu_ge_di_tu" },
            new() { Key = "ggdq", Value = "https://earth.google.com/web/search/{q}", Comment = "谷歌地球", EngineIcon = "gu_ge_di_qiu" },
            new() { Key = "ggxu", Value = "https://scholar.google.com/scholar?hl=zh-CN&q={q}", Comment = "谷歌学术", EngineIcon = "gu_ge_xue_shu" },
            new() { Key = "ggxn", Value = "https://news.google.com/search?q={q}&hl=zh-CN&gl=CN&ceid=CN:zh-Hans", Comment = "谷歌新闻", EngineIcon = "gu_ge_xin_wen" },
            new() { Key = "ggfn", Value = "https://translate.google.com/?q={q}", Comment = "谷歌翻译", EngineIcon = "gu_ge_fan_yi" },
            new() { Key = "doubao", Value = "https://www.doubao.com/chat/?q={q}", Comment = "豆包", EngineIcon = "dou_bao" },
            new() { Key = "db2", Value = "https://www.douban.com/search?q={q}", Comment = "豆瓣", EngineIcon = "dou_ban" },
            new() { Key = "dubn", Value = "https://search.douban.com/movie/subject_search?search_text={q}", Comment = "豆瓣电影", EngineIcon = "dou_ban" },
            new() { Key = "dushu", Value = "https://search.douban.com/book/subject_search?search_text={q}", Comment = "豆瓣读书", EngineIcon = "dou_ban" },
            new() { Key = "tiba", Value = "https://tieba.baidu.com/f?kw={q}&ie=utf-8", Comment = "贴吧", EngineIcon = "tie_ba" },
            new() { Key = "qdin", Value = "https://www.qidian.com/so/{q}.html", Comment = "起点中文网", EngineIcon = "qi_dian_zhong_wen_wang" },
            new() { Key = "ltus", Value = "https://www.reuters.com/site-search/?query={q}", Comment = "路透社", EngineIcon = "lu_tou_she" },
            new() { Key = "tongyi", Value = "https://www.tongyi.com/qianwen/?q={q}", Comment = "通义", EngineIcon = "tong_yi" },
            new() { Key = "kn", Value = "https://www.coolapk.com/algo?keyword={q}", Comment = "酷安", EngineIcon = "ku_an" },
            new() { Key = "kwyn", Value = "http://www.kuwo.cn/search/list?type=all&key={q}", Comment = "酷我音乐", EngineIcon = "ku_wo_yin_yue" },
            new() { Key = "si", Value = "http://search.5sing.kugou.com/?keyword={q}", Comment = "酷狗5sing", EngineIcon = "ku_gou_5sing" },
            new() { Key = "jnsh", Value = "https://www.iciba.com/word?w={q}", Comment = "金山词霸", EngineIcon = "jin_shan_ci_ba" },
            new() { Key = "xiny", Value = "https://www.goofish.com/search?q={q}", Comment = "闲鱼", EngineIcon = "xian_yu" },
            new() { Key = "aliyun", Value = "https://developer.aliyun.com/search?q={q}", Comment = "阿里云开发者", EngineIcon = "a_li_yun_kai_fa_zhe" },
            new() { Key = "lynp", Value = "https://www.alipan.com/search?q={q}", Comment = "阿里云盘", EngineIcon = "a_li_yun_pan" },
            new() { Key = "xuqi", Value = "https://xueqiu.com/k?q={q}", Comment = "雪球", EngineIcon = "xue_qiu" },
            new() { Key = "wish", Value = "https://www.merriam-webster.com/dictionary/{q}", Comment = "韦氏词典", EngineIcon = "wei_shi_ci_dian" },
            new() { Key = "godd", Value = "https://www.amap.com/search?query={q}", Comment = "高德地图", EngineIcon = "gao_de_di_tu" },
        };
        s.Run = new List<QEntry>
        {
            new() { Key = "notepad", Value = "notepad.exe", Comment = "记事本" },
            new() { Key = "calc", Value = "calc.exe", Comment = "计算器" },
            new() { Key = "mspaint", Value = "mspaint.exe", Comment = "画图" },
            new() { Key = "explorer", Value = "explorer.exe", Comment = "文件资源管理器" },
            new() { Key = "cmd", Value = "cmd.exe", Comment = "命令提示符" },
            new() { Key = "taskmgr", Value = "taskmgr.exe", Comment = "任务管理器" },
        };
        s.Web = new List<QEntry>
        {
            new() { Key = "gh", Value = "https://github.com", Comment = "GitHub" },
            new() { Key = "bili", Value = "https://www.bilibili.com", Comment = "B 站" },
        };
        s.HotStrings = new List<QEntry>
        {
            new() { Key = "clp", Value = "CapsLock+", Comment = "展开成 CapsLock+" },
            new() { Key = "@@", Value = "你的邮箱@example.com", Comment = "邮箱" },
        };
        s.Bindings = Enumerable.Range(1, 10).Select(i => new WinBinding { Slot = i }).ToList();

        // 键盘层：默认 10 层，第 1 层就是上面的 Keys；触发键默认只有 CapsLock。
        for (int i = 1; i <= 10; i++)
        {
            s.Layers.Add(new LayerOptions
            {
                Id = i,
                Name = i == 1 ? "基础层" : $"第 {i} 层",
                Enabled = true,
                Keys = i == 1 ? s.Keys : new List<KeyBinding>(),
            });
        }
        s.Triggers.Add(new TriggerKey
        {
            Key = "capslock",
            LayerId = 1,
            HoldMs = 200,
            TapAction = s.Global.TapCapsAction,
            DoubleTapAction = s.Global.DoubleTapCaps ? s.Global.DoubleTapCapsAction : "",
            Enabled = true,
        });

        return s;
    }

    /// <summary>Capslox 布局（CapsLock+ 3.0 之后的默认布局）。</summary>
    public static List<KeyBinding> CapsloxScheme() => new()
    {
        B("press_caps", "toggleCapsLock"),

        B("caps_a", "moveWordLeft"), B("caps_b", "moveDown", "10"), B("caps_c", "copy1"),
        B("caps_d", "moveDown"), B("caps_e", "moveUp"), B("caps_f", "moveRight"),
        B("caps_g", "moveWordRight"), B("caps_h", "selectWordLeft"), B("caps_i", "selectUp"),
        B("caps_j", "selectLeft"), B("caps_k", "selectDown"), B("caps_l", "selectRight"),
        B("caps_m", "doNothing"), B("caps_n", "selectDown", "10"), B("caps_o", "selectEnd"),
        B("caps_p", "home"), B("caps_q", "qbar"), B("caps_r", "delete"),
        B("caps_s", "moveLeft"), B("caps_t", "moveUp", "10"), B("caps_u", "selectHome"),
        B("caps_v", "paste1"), B("caps_w", "backspace"), B("caps_x", "cut1"),
        B("caps_y", "selectUp", "10"), B("caps_z", "doNothing"),

        B("caps_backquote", "doNothing"),

        /* 数字键 1~0 的默认绑定。
         *
         * 旧版这里是 winbindActivate1~10（固定 10 个窗口槽位），槽位机制已经
         * 移除，改由「窗口绑定」动作承担。但**默认不能直接填 winBind** ——
         * 它需要一个程序路径参数，空参数的 winBind 按下去只会弹提示。
         * 所以默认留空（doNothing），由用户自己去按键映射页绑程序。 */
        B("caps_1", "doNothing"), B("caps_2", "doNothing"), B("caps_3", "doNothing"),
        B("caps_4", "doNothing"), B("caps_5", "doNothing"), B("caps_6", "doNothing"),
        B("caps_7", "doNothing"), B("caps_8", "doNothing"), B("caps_9", "doNothing"),
        B("caps_0", "doNothing"),
        B("caps_minus", "winSnapLeft"), B("caps_equal", "winSnapRight"),
        B("caps_backspace", "deleteLine"), B("caps_tab", "tabScript"),
        B("caps_leftSquareBracket", "deleteToLineBeginning"), B("caps_rightSquareBracket", "doNothing"),
        B("caps_backslash", "doNothing"), B("caps_semicolon", "end"), B("caps_quote", "doNothing"),
        B("caps_enter", "enterWherever"), B("caps_comma", "selectCurrentWord"),
        B("caps_dot", "selectWordRight"), B("caps_slash", "deleteToLineEnd"),
        B("caps_space", "enter"), B("caps_ralt", "doNothing"),

        B("caps_f1", "openDocs"), B("caps_f2", "mathBoard"), B("caps_f3", "doNothing"),
        B("caps_f4", "winTransparent"), B("caps_f5", "reload"), B("caps_f6", "winPin"),
        B("caps_f7", "doNothing"), B("caps_f8", "clipboardHistory"), B("caps_f9", "doNothing"),
        B("caps_f10", "doNothing"), B("caps_f11", "doNothing"), B("caps_f12", "toggleClipboard"),

        // Caps + LAlt
        B("caps_lalt_a", "moveWordLeft", "3"), B("caps_lalt_b", "moveDown", "30"), B("caps_lalt_c", "copy2"),
        B("caps_lalt_d", "moveDown", "3"), B("caps_lalt_e", "moveUp", "3"), B("caps_lalt_f", "moveRight", "5"),
        B("caps_lalt_g", "moveWordRight", "3"), B("caps_lalt_h", "selectWordLeft", "3"),
        B("caps_lalt_i", "selectUp", "3"), B("caps_lalt_j", "selectLeft", "5"), B("caps_lalt_k", "selectDown", "3"),
        B("caps_lalt_l", "selectRight", "5"), B("caps_lalt_m", "doNothing"), B("caps_lalt_n", "selectDown", "30"),
        B("caps_lalt_o", "selectToPageEnd"), B("caps_lalt_p", "moveToPageBeginning"), B("caps_lalt_q", "doNothing"),
        B("caps_lalt_r", "forwardDeleteWord"), B("caps_lalt_s", "moveLeft", "5"), B("caps_lalt_t", "moveUp", "30"),
        B("caps_lalt_u", "selectToPageBeginning"), B("caps_lalt_v", "paste2"), B("caps_lalt_w", "deleteWord"),
        B("caps_lalt_x", "cut2"), B("caps_lalt_y", "selectUp", "30"), B("caps_lalt_z", "doNothing"),

        B("caps_lalt_backquote", "doNothing"),
        // 旧版是 winbindBind1~10（把当前窗口绑到槽位）；槽位机制已移除，
        // 绑定改由「窗口绑定」动作在自己的参数里存程序路径。
        B("caps_lalt_1", "doNothing"), B("caps_lalt_2", "doNothing"), B("caps_lalt_3", "doNothing"),
        B("caps_lalt_4", "doNothing"), B("caps_lalt_5", "doNothing"), B("caps_lalt_6", "doNothing"),
        B("caps_lalt_7", "doNothing"), B("caps_lalt_8", "doNothing"), B("caps_lalt_9", "doNothing"),
        B("caps_lalt_0", "doNothing"),
        B("caps_lalt_minus", "doNothing"), B("caps_lalt_equal", "doNothing"),
        B("caps_lalt_backspace", "deleteAll"), B("caps_lalt_tab", "doNothing"),
        B("caps_lalt_leftSquareBracket", "deleteToPageBeginning"), B("caps_lalt_rightSquareBracket", "doNothing"),
        B("caps_lalt_backslash", "doNothing"), B("caps_lalt_semicolon", "moveToPageEnd"),
        B("caps_lalt_quote", "doNothing"), B("caps_lalt_enter", "doNothing"),
        B("caps_lalt_comma", "selectCurrentLine"), B("caps_lalt_dot", "selectWordRight", "3"),
        B("caps_lalt_slash", "deleteToPageEnd"), B("caps_lalt_space", "doNothing"),
        B("caps_lalt_ralt", "doNothing"),
        B("caps_lalt_f1", "doNothing"), B("caps_lalt_f2", "doNothing"), B("caps_lalt_f3", "doNothing"),
        B("caps_lalt_f4", "doNothing"), B("caps_lalt_f5", "doNothing"), B("caps_lalt_f6", "doNothing"),
        B("caps_lalt_f7", "doNothing"), B("caps_lalt_f8", "doNothing"), B("caps_lalt_f9", "doNothing"),
        B("caps_lalt_f10", "doNothing"), B("caps_lalt_f11", "doNothing"), B("caps_lalt_f12", "doNothing"),

        B("caps_lalt_wheelUp", "mouseSpeedUp"), B("caps_lalt_wheelDown", "mouseSpeedDown"),

        // Caps + Win（旧版是窗口槽位 1~10，槽位机制已移除）
        B("caps_win_1", "doNothing"), B("caps_win_2", "doNothing"), B("caps_win_3", "doNothing"),
        B("caps_win_4", "doNothing"), B("caps_win_5", "doNothing"), B("caps_win_6", "doNothing"),
        B("caps_win_7", "doNothing"), B("caps_win_8", "doNothing"), B("caps_win_9", "doNothing"),
        B("caps_win_0", "doNothing"),
    };

    /// <summary>经典布局（CapsLock+ 3.0 之前）。</summary>
    public static List<KeyBinding> ClassicScheme()
    {
        var list = CapsloxScheme();
        void Set(string combo, string action, string arg = "")
        {
            var b = list.FirstOrDefault(x => x.Combo == combo);
            if (b == null) list.Add(B(combo, action, arg));
            else { b.Action = action; b.Arg = arg; }
        }
        Set("caps_b", "moveDown", "5");
        Set("caps_m", "selectDown", "5");
        Set("caps_n", "selectWordRight");
        Set("caps_t", "doNothing");
        Set("caps_y", "moveUp", "5");
        Set("caps_z", "undoRedo");
        Set("caps_backquote", "winbindActivate9");
        Set("caps_9", "doNothing");
        Set("caps_0", "selectUp", "5");
        Set("caps_minus", "pageUp");
        Set("caps_equal", "pageDown");
        Set("caps_leftSquareBracket", "doNothing");
        Set("caps_rightSquareBracket", "doNothing");
        Set("caps_comma", "doNothing");
        Set("caps_dot", "doNothing");
        Set("caps_slash", "doNothing");
        Set("caps_f3", "mediaNext");
        Set("caps_lalt_a", "winLeft");
        Set("caps_lalt_d", "winDown");
        Set("caps_lalt_e", "winUp");
        Set("caps_lalt_f", "winRight");
        Set("caps_lalt_q", "winCenter");
        Set("caps_lalt_r", "winNextApp");
        Set("caps_lalt_w", "winPrevApp");
        Set("caps_lalt_z", "winSendToBottom");
        Set("caps_lalt_minus", "moveToPageBeginning");
        Set("caps_lalt_equal", "moveToPageEnd");
        Set("caps_lalt_backspace", "backspace");
        return list;
    }

    private static KeyBinding B(string combo, string action, string arg = "") =>
        new() { Combo = combo, Action = action, Arg = arg };
}
