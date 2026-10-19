using System.Text.Json.Serialization;

namespace CapsLockPlus.Core.Settings;

/// <summary>一个按键绑定：组合键 ID -> 动作 + 可选参数。</summary>
public sealed class KeyBinding
{
    public string Combo { get; set; } = "";
    public string Action { get; set; } = "doNothing";
    public string Arg { get; set; } = "";

    [JsonIgnore]
    public bool IsBound => !string.IsNullOrEmpty(Action) && Action != "doNothing";
}

public sealed class QEntry
{
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
    /// 每次组合键触发时在屏幕上显示动作名称。
    /// 默认关闭；开启后能一眼看出“按键有没有被识别”，排查问题时非常有用。
    /// </summary>
    public bool ShowActionFeedback { get; set; }

    /// <summary>切换窗口时播放“从小到大”的动画。</summary>
    public bool WindowActivationAnimation { get; set; } = true;

    /// <summary>关闭托盘图标（不推荐）。</summary>
    public bool ShowTrayIcon { get; set; } = true;

    /// <summary>主题：system / light / dark。</summary>
    public string Theme { get; set; } = "system";

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
}

public sealed class QbarOptions
{
    public string BorderColor { get; set; } = "#2B6CF6";
    public double BorderRadius { get; set; } = 14;
    public string PanelColor { get; set; } = "#1C1F27";
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

public sealed class TranslateOptions
{
    /// <summary>翻译服务：youdao（有道智云）/ none。</summary>
    public string Provider { get; set; } = "youdao";
    public string AppId { get; set; } = "";
    public string AppSecret { get; set; } = "";
    public string TargetLanguage { get; set; } = "zh-CHS";
    public bool AutoDetectSource { get; set; } = true;
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
    public TranslateOptions Translate { get; set; } = new();
    public List<KeyBinding> Keys { get; set; } = new();
    public List<QEntry> Search { get; set; } = new();
    public List<QEntry> Run { get; set; } = new();
    public List<QEntry> Web { get; set; } = new();
    public List<QEntry> HotStrings { get; set; } = new();
    public List<WinBinding> Bindings { get; set; } = new();

    public KeyBinding? FindBinding(string combo) =>
        Keys.FirstOrDefault(k => string.Equals(k.Combo, combo, StringComparison.OrdinalIgnoreCase));

    public AppSettings Clone()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(this, SettingsStore.JsonOptions);
        return System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json, SettingsStore.JsonOptions)!;
    }
}
