using System.Text.Json.Serialization;
using CapsLockPlus.Core.Input;

namespace CapsLockPlus.Core.Settings;

/// <summary>一个按键绑定：组合键 ID -> 动作 + 可选参数。</summary>
public sealed class KeyBinding
{
    public string Combo { get; set; } = "";
    public string Action { get; set; } = "doNothing";
    public string Arg { get; set; } = "";

    /// <summary>
    /// 备注（可选）。用于「直达链接」的三类目标（网址 / 文件夹 / 文件）：
    /// 起个好记的名字，键帽上显示的就是"公司后台"而不是一长串 URL 或路径。
    ///
    /// 默认值是 <c>null</c> 而不是 <c>""</c>：配合 WhenWritingNull，
    /// 没写备注时这个字段根本不出现在设置文件里 —— 配置文件与旧版完全一致。
    /// 读取时统一用 <c>?.Trim()</c> 处理，不会因为 null 出问题。
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Note { get; set; }

    [JsonIgnore]
    public bool IsBound => !string.IsNullOrEmpty(Action) && Action != "doNothing";
}

/// <summary>
/// 一条「基础改键」：不按住任何前缀键，把 <see cref="From"/> 换成 <see cref="To"/>。
///
/// 与「层内绑定」的区别：
///   · 层内绑定要按住触发键才生效，一条键可以有多套含义；
///   · 基础改键**始终生效**，语义就是"这个物理键现在等于那个键"。
/// 两者冲突时层内绑定优先（层是"临时接管键盘"）。
/// </summary>
public sealed class KeyRemap
{
    /// <summary>源键名（见 KeyName，如 capslock / esc / lctrl）。</summary>
    public string From { get; set; } = "";

    /// <summary>目标键名。为空表示不生效。</summary>
    public string To { get; set; } = "";

    /// <summary>
    /// 只在这只键盘上生效（VID/PID 或完整设备路径）。留空 = 所有键盘。
    ///
    /// 判定顺序：先看有没有这只键盘的规则，没有再退回"所有键盘"的规则。
    /// 拿不到设备标识时按"所有键盘"处理（fail open）——
    /// 宁可多改一只键盘，也不要在关联失败时让键位规则时灵时不灵。
    /// </summary>
    public string Device { get; set; } = "";

    public string Comment { get; set; } = "";
}

/// <summary>
/// 「按应用自动启用层」的一条规则：前台是这个进程时，把它绑定的层当成**叠加层**。
///
/// 与触发键的层不同：叠加层**不吞未绑定的键** —— 只有真正绑过的组合才执行动作，
/// 其余按键原样放行。否则在 IDE 里打字会被吞掉，这个功能就没法用了。
/// </summary>
public sealed class AppLayerRule
{
    /// <summary>进程名（如 Code.exe）。存文件名即可，匹配时大小写不敏感。</summary>
    public string Process { get; set; } = "";

    /// <summary>要叠加的层 id。</summary>
    public int LayerId { get; set; }
}

/// <summary>
/// 一条「双击动作」：某个键在 <see cref="HotkeyEngine.DoubleTapWindowMs"/> 内被按两次时，
/// 执行指定动作（第二次按下会被吞掉，不会打出第二个字符）。
///
/// 与触发键的 DoubleTapAction 的区别：触发键必须"按住才进层"，会改变这个键的基本行为；
/// 这里对**任何键**都适用，且单击完全保持原样。
/// </summary>
public sealed class KeyGesture
{
    /// <summary>键名（见 KeyName，如 capslock / esc / f1）。</summary>
    public string Key { get; set; } = "";

    /// <summary>双击时执行的动作 id（见 ActionCatalog）。</summary>
    public string DoubleTapAction { get; set; } = "";

    public string Comment { get; set; } = "";
}

/// <summary>
/// 一条「文本片段」：输入触发词后自动展开成一段文字。
///
/// 触发规则（保守，防误触）：
///   · 触发词必须在**词边界**上（前一个字符不是字母/数字）；
///   · 默认要求触发词以非字母数字开头（如 `;mail`），这类前缀几乎不会
///     出现在正常打字里。
/// </summary>
public sealed class Snippet
{
    /// <summary>触发词，如 `;mail`。</summary>
    public string Trigger { get; set; } = "";

    /// <summary>展开后的内容（支持换行）。</summary>
    public string Text { get; set; } = "";

    /// <summary>
    /// 是否启用。关掉后只是不展开，配置还留着（片段列表经常有一堆临时用的）。
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 只在哪些程序里展开（进程名，逗号分隔，如 `code.exe, notepad.exe`）。
    ///
    /// 留空 = 所有程序都展开（旧行为，默认值）。
    /// 存在的意义：片段误触是不可逆的输入副作用 —— 在聊天窗口里打 `;log`
    /// 也会展开成一大段代码，是这类功能最容易被卸载的原因。
    /// </summary>
    public string Apps { get; set; } = "";

    /// <summary>
    /// 这条别名被展开过多少次。
    ///
    /// 用途：找出"设了但从来没用过"的条目 —— 别名攒多了以后，
    /// 光看列表分不清哪些还在用、哪些是当年试了一下就放着的。
    /// </summary>
    public int UseCount { get; set; }

    /// <summary>最近一次展开的时间（从未用过就是 null）。</summary>
    public DateTime? LastUsed { get; set; }

    /// <summary>
    /// 自定义排序位置（越小越靠前）。
    ///
    /// 只有把列表按「自定义顺序」排时才用它；默认全部为 0，
    /// 此时退回按触发词排 —— 老配置不需要迁移。
    /// </summary>
    public int Order { get; set; }

    public string Comment { get; set; } = "";

    /// <summary>分组（空 = 未分组）。</summary>
    public string Group { get; set; } = "";
}


/// <summary>
/// 文本片段的全局设置。
/// </summary>
public sealed class HotstringOptions
{
    /// <summary>
    /// 触发前缀符号（**只有一个**）。空字符串 = 不需要前缀，直接打触发词。
    ///
    /// 以前是"好几个符号都认"，实际用起来很别扭：打到句末的逗号、问号
    /// 都可能把后面的词凑成一个片段。现在只有一个，用户自己定。
    /// </summary>
    public string Prefix { get; set; } = "/";

    /// <summary>用全角符号（把 <see cref="Prefix"/> 按全角输出，如 "/" → "／"）。</summary>
    public bool FullWidth { get; set; }

    /// <summary>打完触发词再按空格就替换（推荐）；关掉则只认触发键（Caps+Tab）。</summary>
    public bool ExpandOnSpace { get; set; } = true;

    /// <summary>替换掉那个空格（true）还是保留它（false）。</summary>
    public bool SwallowSpace { get; set; } = true;

    /// <summary>列表里优先显示备注（没备注才显示触发词）。</summary>
    public bool ShowNoteFirst { get; set; } = true;

    /// <summary>
    /// 列表里是否显示展开后的内容。
    ///
    /// 关掉后只显示备注 —— 别名可能是整段正文/密钥/口令，
    /// 摊在列表里一览无余并不合适（旁边有人、或者正在录屏时）。
    /// </summary>
    public bool ShowTextInList { get; set; } = true;

    /// <summary>实际生效的前缀字符（考虑全角设置）。</summary>
    public string EffectivePrefix => FullWidth ? ToFullWidthOrSelf(Prefix) : (Prefix ?? "");

    /// <summary>把半角符号换成全角（只处理常见 ASCII 符号）。</summary>
    public static string ToFullWidthOrSelf(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (char c in s) sb.Append(ToFullWidthOrSelf(c));
        return sb.ToString();
    }

    public static char ToFullWidthOrSelf(char c) => c switch
    {
        '/' => '／',
        '\\' => '＼',
        ',' => '，',
        '.' => '．',
        ';' => '；',
        ':' => '：',
        '=' => '＝',
        '-' => '－',
        '?' => '？',
        '!' => '！',
        '+' => '＋',
        '#' => '＃',
        '@' => '＠',
        _ => c,
    };
}

public sealed class QEntry
{
    /// <summary>
    /// 这条记录是否匹配过滤词。
    ///
    /// 按**关键字 / 备注 / 内容**三个字段做大小写不敏感的包含匹配 ——
    /// 用户记不住关键字时可以用中文备注找（"剪贴板""截图"），
    /// 也可以用网址片段找。空过滤词 = 全部匹配。
    ///
    /// 抽成静态方法是为了让自检能直接验，而不必去驱动界面控件。
    /// </summary>
    public static bool Matches(QEntry entry, string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return true;
        string f = filter.Trim();
        return (entry.Key?.Contains(f, StringComparison.OrdinalIgnoreCase) ?? false)
               || (entry.Comment?.Contains(f, StringComparison.OrdinalIgnoreCase) ?? false)
               || (entry.Value?.Contains(f, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    /// <summary>按过滤词筛一组记录。</summary>
    public static List<QEntry> Filter(IEnumerable<QEntry> source, string? filter) =>
        source.Where(e => Matches(e, filter)).ToList();

    public string Key { get; set; } = "";
    public string Value { get; set; } = "";

    /// <summary>
    /// 分组（文本片段用）。空 = 未分组。
    ///
    /// 片段多了以后列表会长得没法看，同一个主题（"地址""账号""常用语"）放一组，
    /// 列表里按组显示、组内按触发词排序。
    /// </summary>
    public string Group { get; set; } = "";

    /// <summary>是否启用（文本片段用；关掉就只是留在列表里不参与替换）。</summary>
    public bool Enabled { get; set; } = true;
    public string Comment { get; set; } = "";

    /// <summary>
    /// 自定义图标（png / ico / exe 路径）。为空时：
    /// 程序条目会自动取该 exe 的图标，搜索/网址条目则显示首字母色块。
    /// </summary>
    public string IconPath { get; set; } = "";

    /// <summary>
    /// 内置图标的资源名（对应 Assets/engine-icons 里的一张 PNG）。
    ///
    /// 与 <see cref="IconPath"/> 的区别：IconPath 指向磁盘上的用户自定义文件，
    /// 而 EngineIcon 指向**随程序打包**的内置图标 —— 内置引擎表用的都是后者，
    /// 这样即使用户清掉数据目录，图标也不会丢，也不依赖任何网络下载。
    /// </summary>
    public string EngineIcon { get; set; } = "";

    /// <summary>Run 条目：是否以管理员身份启动。</summary>
    public bool RunAsAdmin { get; set; }

    /// <summary>Run 条目：启动参数。</summary>
    public string Arguments { get; set; } = "";

    /// <summary>标签上显示的名字：优先用备注，其次用关键字。</summary>
    /// <summary>
    /// 界面上显示的备注。
    ///
    /// 「默认搜索」这条是**程序生成的数据**（默认引擎表的第一条），
    /// 它不像用户自己填的备注 —— 该跟着界面语言走。否则英文界面下
    /// 下拉里会突兀地出现中文"默认搜索"，连图标兜底字母都变成"默"。
    /// 其余情况（用户备注 / 没备注时用关键字）原样返回。
    /// </summary>
    [JsonIgnore]
    public string DisplayName =>
        string.Equals(Comment?.Trim(), "默认搜索", System.StringComparison.Ordinal)
            ? Localization.Loc.T("默认搜索")
            : string.IsNullOrWhiteSpace(Comment) ? Key : Comment.Trim();

    public QEntry Clone() => new()
    {
        Key = Key, Value = Value, Comment = Comment, IconPath = IconPath,
        EngineIcon = EngineIcon,
        RunAsAdmin = RunAsAdmin, Arguments = Arguments,
    };
}

public sealed class GlobalOptions
{
    /// <summary>开机自启（写入 HKCU Run）。</summary>
    public bool StartWithWindows { get; set; }

    /// <summary>
    /// 是否已经问过"检测到修饰键改键，要保留吗"。
    ///
    /// 只问一次：用户选了保留就别再烦他；以后新加同类规则时由设置页把这里清回 false。
    /// </summary>
    public bool RemapReviewDone { get; set; }

    /// <summary>热键方案：capslox（默认）或 capslock_plus（3.0 之前的经典布局）。</summary>
    public string HotkeyScheme { get; set; } = "capslox";

    /// <summary>独立剪贴板功能（Caps+C/X/V 使用 CapsLock+ 专属剪贴板）。</summary>
    public bool AllowClipboard { get; set; } = true;

    /// <summary>Caps+LAlt 时鼠标速度 1~20。</summary>
    public int MouseSpeed { get; set; } = 3;

    /// <summary>短按 CapsLock 的行为。</summary>
    public string TapCapsAction { get; set; } = "toggleCapsLock";

    /// <summary>动作执行后在屏幕右下角显示轻提示。</summary>
    public bool ShowToast { get; set; } = true;

    /// <summary>
    /// 「输入文本」用哪种方式打字：auto（默认）/ keys / unicode。
    ///
    /// 默认 auto = 纯 ASCII 走**真实按键**，其它走 Unicode 注入。
    /// 密码框会过滤掉"没有扫描码"的 Unicode 合成输入，所以默认改成了真实按键；
    /// 极少数程序反过来只认 Unicode，那种情况下在设置里改成 unicode 即可。
    /// </summary>
    public string TextInputMode { get; set; } = "auto";

    /// <summary>
    /// 音量键一次调多少（百分比）。
    ///
    /// 音量本身走系统音量键（这样才有系统横条），而系统步进是固定的
    /// （通常 2%），所以这里按"发几次键"来凑：设 5% 就按系统步进发 2~3 次，
    /// 并用累计误差补偿，长期平均正好等于设定值。
    /// </summary>
    public int VolumeStep { get; set; } = 5;

    /// <summary>亮度键一次调多少（百分比）。亮度是我们自己调的，可以精确到 1%。</summary>
    public int BrightnessStep { get; set; } = 10;

    /// <summary>
    /// 宏录制的固定延迟（毫秒）。0 = 按真实按键间隔（默认）。
    ///
    /// 存在的理由：录"点一串按钮"这类操作时，人手按的真实间隔忽快忽慢，
    /// 回放出来节奏很脏；固定成 100~300ms 反而稳定，也更好改。
    /// </summary>
    public int MacroFixedDelayMs { get; set; }

    /// <summary>
    /// 是否以管理员身份启动。
    ///
    /// **默认 false**，这是刻意的。
    ///
    /// 管理员权限会让程序无法向普通权限的窗口注入输入（UIPI 限制），
    /// 于是"选中文字 → 直接搜索"这类功能在浏览器、编辑器里**全部失效**。
    /// 而绝大多数功能（键盘钩子、窗口绑定、宏、Qbar）并不需要管理员权限。
    ///
    /// 只有确实要操作提权窗口时才需要打开它。
    /// </summary>
    public bool RunAsAdmin { get; set; } = false;

    /// <summary>
    /// 自动更新完成后是否弹出「已更新到 x.x.x」提示。
    /// 开机自启的用户每次更新都被弹一次很烦，所以独立成一个开关（默认开）。
    /// </summary>
    public bool ShowUpdateToast { get; set; } = true;

    /// <summary>
    /// 每次组合键触发时在屏幕上显示动作名称。
    /// 默认关闭；开启后能一眼看出“按键有没有被识别”，排查问题时非常有用。
    /// </summary>
    public bool ShowActionFeedback { get; set; }

    /// <summary>关闭托盘图标（不推荐）。</summary>
    public bool ShowTrayIcon { get; set; } = true;


    /// <summary>
    /// 文本片段：输入触发词自动展开。默认空，不影响任何现有输入。
    /// </summary>
    public List<Snippet> Snippets { get; set; } = new();

    /// <summary>
    /// 别名替换的分组名（用户手动建的）。
    ///
    /// 以前分组是从片段身上"推"出来的 —— 空分组根本存不下来，
    /// 于是用户没法"先建个组再往里放"。现在单独存一份，
    /// 与片段里实际用到的组名取并集显示。
    /// </summary>
    public List<string> SnippetGroups { get; set; } = new();

    /// <summary>
    /// 双击动作：任意键连按两次触发一个动作。默认空，不影响任何现有行为。
    /// </summary>
    public List<KeyGesture> KeyGestures { get; set; } = new();

    /// <summary>
    /// 按应用自动启用层：前台窗口的进程命中规则时，把对应层当作叠加层。
    /// 默认空，不影响任何现有行为。
    /// </summary>
    public List<AppLayerRule> AppLayers { get; set; } = new();

    /// <summary>
    /// 基础改键表：不按住触发键也生效的全局重映射。
    ///
    /// 典型用途：Caps→Esc、Ctrl↔Alt、把笔记本上闲置的键变成 Home/End。
    /// 只在**基础层**生效；层内绑定优先，键盘钩子也不会因此改变触发键判定。
    /// </summary>
    public List<KeyRemap> BaseRemaps { get; set; } = new();

    /// <summary>主题：system / light / dark。</summary>
    public string Theme { get; set; } = "system";

    /// <summary>
    /// 「按键映射」页的键盘大小档位（small / medium / large / xlarge）。
    ///
    /// 这个值同时决定两件事，别只当它是"显示比例"看：
    ///   · 每次打开按键映射页，键盘直接按这档渲染；
    ///   · 这个页面的**最小窗口尺寸** = 装下这档键盘所需的尺寸，
    ///     所以窗口不会被缩到把键帽压糊。
    /// </summary>
    public string KeyboardSize { get; set; } = KeyboardSizePreset.DefaultId;

    /// <summary>界面语言：zh-CN / en-US。</summary>
    public string Language { get; set; } = "zh-CN";

    /// <summary>首次运行引导是否已完成。</summary>
    public bool OnboardingDone { get; set; }

    /// <summary>启动时是否打开主窗口。</summary>
    public bool OpenWindowOnStartup { get; set; }

    /// <summary>主窗口关闭时最小化到托盘而不是退出。</summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>按住 Caps 组合键时，动作是否可重复触发（打字流场景更顺手）。</summary>
    public bool RepeatWhenHeld { get; set; }

    /// <summary>双击 CapsLock 时触发动作（默认：打开 Qbar）。</summary>
    public bool DoubleTapCaps { get; set; }

    public string DoubleTapCapsAction { get; set; } = "qbar";

    /// <summary>鼠标移动动作的基础步长。</summary>
    public int MouseMoveStep { get; set; } = 10;

    /// <summary>自动更新：勾选后每次启动都会静默检查，有新版本再提示。</summary>
    public bool AutoUpdate { get; set; }

    /// <summary>上次检查更新的时间（避免启动时频繁请求）。</summary>
    public DateTime LastUpdateCheck { get; set; } = DateTime.MinValue;

    /// <summary>用户点过“跳过这个版本”的版本号。</summary>
    public string SkippedVersion { get; set; } = "";

    /// <summary>
    /// GitHub 个人访问令牌（PAT），用来把更新检查的接口配额从 60 次/小时
    /// 提到 5000 次/小时。
    ///
    /// 为什么需要它：匿名请求 GitHub API 是按 **IP** 限流的 —— 60 次/小时。
    /// 公司/校园网、家里多台机器、或开了代理的人，经常在一天里就把这点配额用光，
    /// 于是"检查更新"永远报"访问频率已达上限"。带上令牌就按**账号**限流，
    /// 5000 次/小时，正常使用根本碰不到。
    ///
    /// 只读公开仓库**不需要勾选任何 scope**（classic token 留空即可，
    /// fine-grained 只需要 Public repositories 的 read）。令牌只存在本机设置里：
    /// **明文导出会把它摘掉**（见 ExportImport.NeverExport），加密备份才会带上。
    /// </summary>
    public string GitHubToken { get; set; } = "";
}

/// <summary>
/// WebDAV 同步与备份设置。
///
/// 地址、用户名、密码都只存在本机设置文件里，不会被导出到明文配置
/// （除非用户在导出时显式勾选"同步与备份设置"这个隐私组）。
/// </summary>
public sealed class SyncOptions
{
    /// <summary>WebDAV 服务器地址（目录形式）。</summary>
    public string WebDavUrl { get; set; } = "";
    public string WebDavUser { get; set; } = "";
    public string WebDavPass { get; set; } = "";

    /// <summary>启动软件时自动同步一次。</summary>
    public bool SyncOnStart { get; set; }

    /// <summary>关闭软件时自动同步一次。</summary>
    public bool SyncOnExit { get; set; }

    /// <summary>
    /// 备份密码。
    ///
    /// 它有两个作用，别当成一回事：
    ///   * 给**备份文件**加密（本地 + 云端都算）；
    ///   * 给**本地用户数据**加密（settings.json 整份变密文）。
    /// 所以设了它之后，磁盘上不会再有明文用户数据；
    /// 备份也一律是密文，没有"没密码就明文"的退路。
    /// </summary>
    public string BackupPassword { get; set; } = "";

    /// <summary>本地 / 云端各保留最近几份备份。默认 10。</summary>
    public int KeepLocalBackups { get; set; } = 10;

    /// <summary>自动清掉多少天之前的备份（0 = 不按天数清）。默认 7。</summary>
    public int CleanupDays { get; set; } = 7;

    /// <summary>按间隔自动备份（本地必写，配了网盘就一起上传）。</summary>
    public bool AutoBackup { get; set; }

    /// <summary>自动备份间隔（分钟）。</summary>
    public int AutoBackupMinutes { get; set; } = 60;

    /// <summary>上次同步时间。</summary>
    public DateTime LastSyncAt { get; set; } = DateTime.MinValue;

    /// <summary>上次同步结果的一句话说明。</summary>
    public string LastSyncInfo { get; set; } = "";
}

/// <summary>
/// 启动密码（有人叫它"应用密码"）。
///
/// ⚠ 与 <see cref="SyncOptions.BackupPassword"/>（备份密码）**完全不是一回事**，
/// 用户点名强调过：备份密码管"备份文件和本地数据加密"，设在「同步与备份」页；
/// 启动密码只管"谁能打开这个软件"，设在「设置」页。两套字段、两套校验、
/// 互不影响 —— 关掉启动密码，备份加密照旧；改备份密码，启动密码也不动。
///
/// 这里只存**镜像**（盐 + PBKDF2 派生值 + 迭代次数），随 settings.json 走；
/// 启动时的校验真相在数据目录的 startup.json（见 <see cref="StartupPassword"/>）——
/// 因为 settings.json 可能被备份密码加密，解锁失败时读不出来，
/// 那时要是只认 settings.json，密码门会被悄悄跳过。
/// </summary>
public sealed class SecurityOptions
{
    /// <summary>是否开着启动密码（与 startup.json 的存在状态保持一致）。</summary>
    public bool StartupPasswordEnabled { get; set; }

    /// <summary>PBKDF2 盐（base64）。空 = 没设过。</summary>
    public string StartupPasswordSalt { get; set; } = "";

    /// <summary>PBKDF2 派生值（base64）。不存明文，也不可逆推密码。</summary>
    public string StartupPasswordHash { get; set; } = "";

    /// <summary>设密码时的 PBKDF2 迭代次数（调参后老密码照样能验）。</summary>
    public int StartupPasswordIterations { get; set; }

    /// <summary>设置里有没有一份可用的密码记录（不等于门开着 —— 门看 startup.json）。</summary>
    [JsonIgnore]
    public bool HasPassword => !string.IsNullOrEmpty(StartupPasswordSalt)
                            && !string.IsNullOrEmpty(StartupPasswordHash)
                            && StartupPasswordIterations > 0;
}

/// <summary>
/// 触发键（Trigger）配置。
///
/// 参照键盘驱动的做法：用户按下一个"触发键"，
/// 短按（未超阈值）保留原键盘功能，长按超过阈值后
/// 临时切换到该触发键绑定的层，层内按键执行自定义动作。
///
/// 默认只有一个触发键 = CapsLock，绑第 1 层，
/// 这样老用户的行为完全不变。
/// </summary>
public sealed class TriggerKey
{
    /// <summary>触发的物理键名（KeyName.NameOf 形式），例如 "capslock"。</summary>
    public string Key { get; set; } = "";

    /// <summary>该触发键绑定到哪一层（1-based；对应 <see cref="LayerOptions.Id"/>）。</summary>
    public int LayerId { get; set; } = 1;

    /// <summary>
    /// 长按阈值（毫秒）。按住超过这个时间才进入层模式；
    /// 在此之前松开则视为"短按"，走 <see cref="TapAction"/>。
    /// </summary>
    public int HoldMs { get; set; } = 200;

    /// <summary>短按时要执行的动作（默认切换大小写；可设为 Esc / 无操作等）。</summary>
    public string TapAction { get; set; } = "toggleCapsLock";

    /// <summary>
    /// 按住触发键期间又按了别的键时，**立即进入层**而不等长按阈值。
    ///
    /// 开（默认）：Caps 按住后马上按 Q 就能触发组合键，手感与旧版一致。
    /// 关：必须按满 <see cref="HoldMs"/> 才进层，组合键要"等一下"。
    /// </summary>
    public bool HoldOnOtherKey { get; set; } = true;

    /// <summary>双击触发键时执行的动作（空 = 关闭双击功能）。</summary>
    public string DoubleTapAction { get; set; } = "";

    /// <summary>是否启用这个触发键。</summary>
    public bool Enabled { get; set; } = true;

    public string Display => string.IsNullOrEmpty(Key) ? "(未设置)" : Key;

    // ---------------- 供列表直接绑定的只读展示属性 ----------------
    //
    // 触发键列表那一行要显示「键名 / 绑定到哪一层 / 阈值 / 启停」。
    // 这些属性直接给界面绑，省得界面自己拼字符串
    // （之前 XAML 绑了 KeyLabel/Summary/Detail 但类上没有，整行是空的）。

    /// <summary>键帽上显示的名字。</summary>
    public string KeyLabel => string.IsNullOrEmpty(Key)
        ? "?"
        : Input.KeyName.Display(Key);

    /// <summary>主标题：这一项绑到第几层。</summary>
    public string Summary => LayerId <= 0 ? Loc.T("未绑定层") : Loc.F("第 {0} 层", LayerId);

    /// <summary>副标题：阈值 + 短按动作，一眼看清这个触发键怎么用。</summary>
    public string Detail
    {
        get
        {
            /* 短按动作的名字从**动作表**里取，不再写死 switch。
             *
             * 之前只映射了 toggleCapsLock / esc / qbar / doNothing 四个，
             * 用户把短按改成「保留按键原功能」（keepKey）后，
             * 列表上显示的是动作 ID 甚至"无"，看起来像设置没生效。 */
            string tap;
            if (TapAction == "doNothing")
                tap = Loc.T("无");
            else if (string.IsNullOrEmpty(TapAction))
                // 空值在引擎里的语义就是"补发原键"，等价于 keepKey
                tap = Loc.T("保留原功能");
            else
                tap = Actions.ActionCatalog.TitleOf(TapAction) ?? TapAction;

            string hold = HoldOnOtherKey ? Loc.T("一按别的键就进层") : Loc.F("长按 {0}ms 进层", HoldMs);
            return Loc.F("{0}　·　短按：{1}", hold, tap);
        }
    }

    /// <summary>列表里的位置（供"启停"复选框回写用）。</summary>
    public int Index { get; set; }
}

/// <summary>
/// 键盘层（Layer）。
///
/// 一个层就是一套独立的"触发键 + 按键 → 动作/宏"映射表。
/// 层与层之间互不干扰；同一层内再支持 Alt / Win 修饰键，
/// 组合键 ID 形如 <c>caps_q</c> / <c>caps_lalt_q</c>。
/// </summary>
public sealed class LayerOptions
{
    /// <summary>层 ID，从 1 开始，对应触发键的 <see cref="TriggerKey.LayerId"/>。</summary>
    public int Id { get; set; } = 1;

    /// <summary>层名称，界面上显示用，例如「基础层」「Layer 2」。</summary>
    public string Name { get; set; } = "";

    /// <summary>是否启用。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>该层的按键绑定列表。</summary>
    public List<KeyBinding> Keys { get; set; } = new();

    /// <summary>
    /// 该层的**层内修饰键 / 子层**定义。
    ///
    /// 每个层最多 10 个子层：进入本层后再按住某个键，
    /// 就能用到另一套独立绑定。
    ///
    /// 子层键可以是**任意物理键**（Ctrl、Shift、Tab、CapsLock…），
    /// 也可以给每个子层起**自定义名字**（键帽和图例上显示这个名字）。
    ///
    /// 为空时回退到默认 4 个（Ctrl / Shift / Alt / Win），
    /// 保证老配置行为不变。
    /// </summary>
    public List<SubLayerDef> SubLayers { get; set; } = new();

    /// <summary>
    /// 旧版字段：只存修饰键位标记。
    ///
    /// 保留用于向后兼容 —— 老配置文件里是 <c>[1, 2, 4, 8]</c> 这样的整数数组。
    /// 读进来后由 <see cref="Normalize"/> 转成 <see cref="SubLayers"/>。
    /// 写入时若 <see cref="SubLayers"/> 为空则继续用这个字段，避免版本回退丢配置。
    /// </summary>
    public List<int> SubLayerModifiers { get; set; } = new();

    /// <summary>每个层允许的最大子层数。</summary>
    public const int MaxSubLayers = 10;

    /// <summary>默认的子层定义（与旧版行为一致：Ctrl / Shift / Alt / Win）。</summary>
    public static List<SubLayerDef> DefaultSubLayers() => new()
    {
        new SubLayerDef { Key = "ctrl", Name = "Ctrl" },
        new SubLayerDef { Key = "shift", Name = "Shift" },
        new SubLayerDef { Key = "alt", Name = "Alt" },
        new SubLayerDef { Key = "win", Name = "Win" },
    };

    /// <summary>默认的层内修饰键位标记（旧接口，保留兼容）。</summary>
    public static List<int> DefaultSubLayerModifiers() => new()
    {
        (int)KeyModifiers.Ctrl,
        (int)KeyModifiers.Shift,
        (int)KeyModifiers.Alt,
        (int)KeyModifiers.Win,
    };

    /// <summary>
    /// 实际生效的子层列表。
    ///
    /// 优先级：SubLayers（新） > SubLayerModifiers（旧） > 默认四个。
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<SubLayerDef> EffectiveSubLayers
    {
        get
        {
            if (SubLayers.Count > 0) return SubLayers.Take(MaxSubLayers).ToList();

            // 老配置：把位标记还原成子层定义
            if (SubLayerModifiers.Count > 0)
            {
                return SubLayerModifiers
                    .Take(MaxSubLayers)
                    .Select(m => SubLayerDef.FromModifierBits(m))
                    .ToList();
            }

            return DefaultSubLayers();
        }
    }
}

/// <summary>
/// 一个子层（层内修饰键）的定义。
///
/// <see cref="Key"/> 是任意键名（ctrl / shift / tab / capslock / f13…），
/// <see cref="Name"/> 是显示名（留空则自动用键名）。
/// </summary>
public sealed class SubLayerDef
{
    /// <summary>子层触发键的键名（与 KeyName 的命名一致）。</summary>
    public string Key { get; set; } = "";

    /// <summary>显示名。留空时自动取键名。</summary>
    public string? Name { get; set; }

    /// <summary>该子层的按键绑定（combo 里带修饰前缀）。</summary>
    public List<KeyBinding> Keys { get; set; } = new();

    /// <summary>界面上显示的文字。</summary>
    [JsonIgnore]
    public string Display =>
        !string.IsNullOrWhiteSpace(Name) ? Name!.Trim() : KeyName.Display(Key);

    /// <summary>把位标记转成子层定义（用于迁移旧配置）。</summary>
    public static SubLayerDef FromModifierBits(int bits)
    {
        var mods = (KeyModifiers)bits;
        var parts = new List<string>();
        if (mods.HasFlag(KeyModifiers.Ctrl)) parts.Add("ctrl");
        if (mods.HasFlag(KeyModifiers.Shift)) parts.Add("shift");
        if (mods.HasFlag(KeyModifiers.Alt)) parts.Add("alt");
        if (mods.HasFlag(KeyModifiers.Win)) parts.Add("win");

        string key = parts.Count switch
        {
            0 => "",
            1 => parts[0],
            _ => string.Join("_", parts),      // 复合位标记暂时存成 ctrl_shift
        };

        return new SubLayerDef { Key = key };
    }
}

/// <summary>
/// 宏动作（Macro）序列中的一步。
///
/// 参照键盘驱动的宏方案，一个宏就是一串"带时间的输入事件"：
/// 按下键 / 抬起键 / 等待固定毫秒 / 等待随机毫秒区间。
/// 文本输入则拆成逐字符的"按下+抬起"序列，
/// 这样才能在游戏和不响应粘贴的程序里生效。
/// </summary>
public sealed class MacroStep
{
    /// <summary>
    /// 步骤类型：keyDown / keyUp / delay / delayRandom / text / setVar，
    /// 以及流程控制 if / else / endIf / loop / endLoop / waitFor / retry / endRetry，
    /// 和执行内置动作的 action。
    ///
    /// 流程控制是**成对标记**而不是嵌套结构：宏仍然是一条平铺的列表，
    /// 靠 <c>Core.Macro.MacroFlow</c> 在播放前算出配对关系。
    /// 这样老版本存的宏（没有这些标记）可以原样播放，JSON 也不会变成树。
    /// </summary>
    public string Kind { get; set; } = "delay";

    /// <summary>Kind 为 keyDown/keyUp 时的键名（KeyName 形式，如 "a"、"ctrl"）。</summary>
    public string Key { get; set; } = "";

    /// <summary>
    /// Kind 为 text 时要输入的文本（整体一步，播放时逐字符发送并自动加微小延迟）。
    /// </summary>
    public string Text { get; set; } = "";

    /// <summary>Kind 为 delay 时的固定等待毫秒。</summary>
    public int DelayMs { get; set; }

    /// <summary>Kind 为 delayRandom 时的随机区间下界。</summary>
    public int MinMs { get; set; }

    /// <summary>Kind 为 delayRandom 时的随机区间上界。</summary>
    public int MaxMs { get; set; }

    /// <summary>
    /// Kind 为 if 时的条件类型：app（前台进程）/ title（窗口标题）/
    /// clipboard（剪贴板包含）/ clipboardEmpty（剪贴板为空）。
    /// </summary>
    public string Cond { get; set; } = "app";

    /// <summary>条件的比较值（app / title / clipboard 用；clipboardEmpty 忽略）。</summary>
    public string CondValue { get; set; } = "";

    /// <summary>Kind 为 if 时是否取反。</summary>
    public bool Not { get; set; }

    /// <summary>Kind 为 loop 时的循环次数；0 或负数 = 一直循环（由取消或步数上限终止）。</summary>
    public int Count { get; set; } = 3;

    /// <summary>
    /// Kind 为 waitFor 时的最长等待毫秒。
    /// 超过就判定这一步失败 —— 失败会交给外层「重试」块，没有重试块就整段中止。
    /// </summary>
    public int TimeoutMs { get; set; } = 5000;

    /// <summary>Kind 为 action 时要执行的动作 id（见 ActionCatalog）。</summary>
    public string ActionId { get; set; } = "";

    /// <summary>Kind 为 action 时传给动作的参数（多数动作不需要）。</summary>
    public string ActionArg { get; set; } = "";

    /// <summary>
    /// 变量名。
    ///
    /// 两种用法：
    ///   · Kind 为 setVar 时 —— 要写入的变量名；
    ///   · Kind 为 loop 时 —— 把"当前是第几轮"写进这个变量（留空则用 i）。
    ///
    /// 循环计数走这条而不是另开一个步骤类型：用户想要的本来就是
    /// "循环 10 次，每次用当前序号"，多一步只会多一处忘配。
    /// </summary>
    public string VarName { get; set; } = "";

    /// <summary>
    /// Kind 为 setVar 时要写入的值。可以含 `{变量}` 占位符（写入前会展开）。
    /// </summary>
    public string VarValue { get; set; } = "";

    public MacroStep Clone() => new()
    {
        Kind = Kind, Key = Key, Text = Text,
        DelayMs = DelayMs, MinMs = MinMs, MaxMs = MaxMs,
        Cond = Cond, CondValue = CondValue, Not = Not, Count = Count,
        TimeoutMs = TimeoutMs, ActionId = ActionId, ActionArg = ActionArg,
        VarName = VarName, VarValue = VarValue,
    };
}

/// <summary>
/// 一个可绑定的宏。
///
/// 存在设置文件里，可以在界面上录制、编辑、绑定到任意层的任意按键。
/// </summary>
public sealed class Macro
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "新宏";
    public List<MacroStep> Steps { get; set; } = new();

    /// <summary>循环播放次数（1 = 播放一次；0 = 一直循环直到用户中断）。</summary>
    public int Repeat { get; set; } = 1;

    /// <summary>备注。</summary>
    public string Comment { get; set; } = "";

    public Macro Clone() => new()
    {
        Id = Id, Name = Name, Comment = Comment, Repeat = Repeat,
        Steps = Steps.Select(s => s.Clone()).ToList(),
    };

    /// <summary>
    /// 估算总时长（用于界面显示）。
    ///
    /// 循环体按次数放大：不算这一项的话，"循环 50 次 + 每次等 200ms"会显示成几百毫秒，
    /// 与真实感受差两个数量级，用户会以为循环没生效。
    /// 无限循环按 1 次算 —— 显示成"无限"没有意义，也没法参与加法。
    /// </summary>
    public int EstimatedMs
    {
        get
        {
            int total = 0;
            var stack = new Stack<(int Outer, int Count)>();

            foreach (var s in Steps)
            {
                switch (s.Kind)
                {
                    case "loop":
                    {
                        int count = s.Count <= 0 ? 1 : Math.Min(s.Count, 1000);
                        stack.Push((total, count));
                        total = 0;
                        break;
                    }
                    case "endLoop":
                        if (stack.Count > 0)
                        {
                            var (outer, count) = stack.Pop();
                            total = outer + total * count;
                        }
                        break;

                    /* 流程标记本身不耗时，别当成一次按键。
                     * waitFor 的时长取决于条件什么时候成立，无法预估，按 0 算。 */
                    case "if":
                    case "else":
                    case "endIf":
                    case "retry":
                    case "endRetry":
                    case "waitFor":
                        break;

                    case "delay": total += s.DelayMs; break;
                    case "delayRandom": total += (s.MinMs + s.MaxMs) / 2; break;
                    case "text": total += s.Text.Length * 12; break;
                    default: total += 8; break;   // 一次按键的按下+抬起
                }
            }

            return total;
        }
    }

    /// <summary>
    /// 列表里显示的名字：空名字给个占位。
    ///
    /// 真实用户配置里就存在 Name 为空的宏（早期版本"新建"后没填名字），
    /// 直接绑 Name 会在列表里出现一行空白，动作下拉里变成「播放宏：」。
    /// </summary>
    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? "（未命名宏）" : Name;

    /// <summary>列表副标题：步数与循环次数。
    ///
    /// 界面里宏列表绑定了 Summary / DurationText，但这两个属性曾是**不存在的** ——
    /// 结果整个宏列表只有名字和图标，副标题与时长永远空白。
    /// </summary>
    public string Summary
    {
        get
        {
            string repeat = Repeat <= 0 ? Loc.T("循环播放") : Repeat == 1 ? Loc.T("播放一次") : Loc.F("播放 {0} 次", Repeat);
            return Loc.F("{0} 步　·　{1}", Steps.Count, repeat);
        }
    }

    /// <summary>列表右侧的时长文字（估算）。</summary>
    public string DurationText
    {
        get
        {
            int ms = EstimatedMs;
            return ms < 1000 ? $"{ms}ms" : $"{ms / 1000.0:0.#}s";
        }
    }
}

public sealed class QbarOptions
{
    public string PanelColor { get; set; } = "auto";   // auto = 跟随主题
    public string PanelOpacity { get; set; } = "0.96";
    public double TextFontSize { get; set; } = 20;
    public int ListCount { get; set; } = 6;
    public double LineHeight { get; set; } = 38;
    public string AccentColor { get; set; } = "#4C8DFF";
    public double Width { get; set; } = 760;
    public bool ShowIcons { get; set; } = true;
}

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;

    /// <summary>
    /// 内置搜索引擎表的版本号。
    ///
    /// 每次发布新增内置引擎时把这个数字加一，启动时会自动把**缺失的**内置引擎
    /// 补进用户的设置里（已存在或用户自己改过的条目不覆盖）。
    /// 否则老用户永远看不到新引擎 —— 因为设置文件已经存在，不会再走默认值分支。
    /// </summary>
    public int BuiltinEnginesVersion { get; set; }

    public GlobalOptions Global { get; set; } = new();
    public QbarOptions Qbar { get; set; } = new();
    public SyncOptions Sync { get; set; } = new();

    /// <summary>启动密码（与备份密码独立的另一套，见 <see cref="SecurityOptions"/>）。</summary>
    public SecurityOptions Security { get; set; } = new();
    public List<KeyBinding> Keys { get; set; } = new();
    public List<QEntry> Search { get; set; } = new();
    public List<QEntry> Run { get; set; } = new();
    public List<QEntry> Web { get; set; } = new();
    public List<QEntry> HotStrings { get; set; } = new();

    /// <summary>文本片段的全局设置（前缀符号、全/半角、空格触发）。</summary>
    public HotstringOptions Hotstring { get; set; } = new();

    /// <summary>触发键列表（可多个；每个绑定一层）。</summary>
    public List<TriggerKey> Triggers { get; set; } = new();

    /// <summary>层列表（默认 10 层）。</summary>
    public List<LayerOptions> Layers { get; set; } = new();

    /// <summary>宏列表。</summary>
    public List<Macro> Macros { get; set; } = new();

    public KeyBinding? FindBinding(string combo) =>
        Keys.FirstOrDefault(k => string.Equals(k.Combo, combo, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 拿某一层的按键绑定（找不到层时返回 null）。
    /// 老设置文件没有层概念，此时 <see cref="Keys"/> 就是事实上的第 1 层。
    /// </summary>
    public List<KeyBinding> LayerKeys(int layerId)
    {
        var layer = Layers.FirstOrDefault(l => l.Id == layerId);
        if (layer == null)
        {
            return layerId == 1 ? Keys : new List<KeyBinding>();
        }
        return layer.Keys;
    }

    public AppSettings Clone()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(this, SettingsStore.JsonOptions);
        return System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json, SettingsStore.JsonOptions)!;
    }
}
