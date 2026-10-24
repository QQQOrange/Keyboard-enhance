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
    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Comment) ? Key : Comment.Trim();

    public QEntry Clone() => new()
    {
        Key = Key, Value = Value, Comment = Comment, IconPath = IconPath,
        EngineIcon = EngineIcon,
        RunAsAdmin = RunAsAdmin, Arguments = Arguments,
    };
}

public enum WinBindMode
{
    /// <summary>只绑定一个具体窗口。</summary>
    Single = 0,
    /// <summary>绑定多个窗口，反复按键循环切换。</summary>
    Multi = 1,
    /// <summary>绑定某个程序的全部窗口（自动跟随新窗口）。</summary>
    Application = 2,
}

public sealed class WinBinding
{
    public int Slot { get; set; } = 1;
    public WinBindMode Mode { get; set; } = WinBindMode.Single;
    public string ExePath { get; set; } = "";
    public string ClassName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public List<long> WindowIds { get; set; } = new();

    [JsonIgnore] public bool IsEmpty => string.IsNullOrEmpty(ExePath) && WindowIds.Count == 0;

    public WinBinding Clone() => new()
    {
        Slot = Slot, Mode = Mode, ExePath = ExePath, ClassName = ClassName,
        DisplayName = DisplayName, WindowIds = new List<long>(WindowIds),
    };
}

public sealed class GlobalOptions
{
    /// <summary>开机自启（写入 HKCU Run）。</summary>
    public bool StartWithWindows { get; set; }

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

    /// <summary>Caps+Tab 计算器/热字串开关。</summary>
    public bool EnableTabScript { get; set; } = true;

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
    public string Summary => LayerId <= 0 ? "未绑定层" : $"第 {LayerId} 层";

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
                tap = "无";
            else if (string.IsNullOrEmpty(TapAction))
                // 空值在引擎里的语义就是"补发原键"，等价于 keepKey
                tap = "保留原功能";
            else
                tap = Actions.ActionCatalog.TitleOf(TapAction) ?? TapAction;

            string hold = HoldOnOtherKey ? "一按别的键就进层" : $"长按 {HoldMs}ms 进层";
            return $"{hold}　·　短按：{tap}";
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
    /// <summary>步骤类型：keyDown / keyUp / delay / delayRandom / text。</summary>
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

    public MacroStep Clone() => new()
    {
        Kind = Kind, Key = Key, Text = Text,
        DelayMs = DelayMs, MinMs = MinMs, MaxMs = MaxMs,
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

    /// <summary>估算总时长（用于界面显示）。</summary>
    public int EstimatedMs
    {
        get
        {
            int total = 0;
            foreach (var s in Steps)
            {
                switch (s.Kind)
                {
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
            string repeat = Repeat <= 0 ? "循环播放" : Repeat == 1 ? "播放一次" : $"播放 {Repeat} 次";
            return $"{Steps.Count} 步　·　{repeat}";
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
    public string BorderColor { get; set; } = "#2B6CF6";
    public double BorderRadius { get; set; } = 14;
    public string PanelColor { get; set; } = "auto";   // auto = 跟随主题
    public string PanelOpacity { get; set; } = "0.96";
    public string TextColor { get; set; } = "#F2F5FA";
    public string TextFont { get; set; } = "Microsoft YaHei UI";
    public double TextFontSize { get; set; } = 20;
    public string ListFont { get; set; } = "Microsoft YaHei UI";
    public double ListFontSize { get; set; } = 13.5;
    public string ListBackgroundColor { get; set; } = "#1C1F27";
    public string ListTextColor { get; set; } = "#C9D1E0";
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
    public List<KeyBinding> Keys { get; set; } = new();
    public List<QEntry> Search { get; set; } = new();
    public List<QEntry> Run { get; set; } = new();
    public List<QEntry> Web { get; set; } = new();
    public List<QEntry> HotStrings { get; set; } = new();
    public List<WinBinding> Bindings { get; set; } = new();

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
