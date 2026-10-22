using System.Text.Json;

namespace CapsLockPlus.Core.Actions;

/// <summary>
/// 「自定义按键」的参数：把哪个键发给哪个应用，以及启动它时的两个选项。
///
/// 为什么塞进 <see cref="Settings.KeyBinding.Arg"/> 而不是给 KeyBinding 加字段：
/// 动作执行链路（HotkeyEngine → ActionCatalog.Invoke(action, arg)）只传一个字符串，
/// 加字段就得改引擎签名、改所有调用点、还要处理设置文件的兼容。
/// 序列化成一个字符串，链路一行不用动，老配置也完全不受影响
/// （其它动作的 Arg 仍然是普通文本，只有这个动作的 Arg 是 JSON）。
/// </summary>
public sealed class CustomKeySpec
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        // 读的时候容忍大小写差异，手改配置也不至于整条失效
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>要发送给应用的按键（KeyName 形式，如 "f13" / "ctrl_shift_k"）。</summary>
    public string Key { get; set; } = "";

    /// <summary>绑定的应用（exe 完整路径；也接受只填 exe 名字）。</summary>
    public string AppPath { get; set; } = "";

    /// <summary>拉起应用时是否以管理员身份运行（会弹一次 UAC）。</summary>
    public bool RunAsAdmin { get; set; }

    /// <summary>
    /// 触发时要不要把目标应用的**主窗口调到前台**。
    ///
    /// 默认 <c>false</c>，与 QuickSearch 的 <c>app_bring_front</c> 一致：
    /// 应用在后台就**不动它的窗口**，按键直接交给系统路由
    /// （目标应用若是全局热键，自己在后台就能收到）。
    /// 打开则先激活它的窗口再发键 —— 适合"这个键只在它被激活时才有意义"的场景。
    /// </summary>
    public bool BringFront { get; set; }

    /// <summary>
    /// 启动应用之后、发按键之前**额外再等多少毫秒**。
    ///
    /// 为什么需要：窗口建出来 ≠ 能收键盘 —— 程序还在读配置、建 UI、加载数据。
    /// QuickSearch 里这一拍默认 900ms，并且做成了每个引擎可调：
    /// 大型软件（IDE、设计软件）常常不够，小工具则嫌慢。
    /// </summary>
    public int WaitAfterLaunchMs { get; set; } = DefaultWaitMs;

    /// <summary>默认额外等待（毫秒），与 QuickSearch 的默认值保持一致。</summary>
    public const int DefaultWaitMs = 900;

    /// <summary>两个必填项都填了才算配置完整。</summary>
    public bool IsReady => !string.IsNullOrWhiteSpace(Key) && !string.IsNullOrWhiteSpace(AppPath);

    /// <summary>应用的进程名（不带 .exe），用于"有没有在运行"。</summary>
    public string ProcessName
    {
        get
        {
            if (string.IsNullOrWhiteSpace(AppPath)) return "";
            string name = Path.GetFileName(AppPath.Trim());
            return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? name[..^4]
                : name;
        }
    }

    public string ToArg() => JsonSerializer.Serialize(this, Json);

    /// <summary>从 Arg 解析；空或坏数据都返回一个空 spec，不抛异常。</summary>
    public static CustomKeySpec FromArg(string? arg)
    {
        if (string.IsNullOrWhiteSpace(arg)) return new CustomKeySpec();
        string s = arg.Trim();

        // 兼容"用户手填了一个纯键名"的情况：当成只设了 Key
        if (!s.StartsWith('{')) return new CustomKeySpec { Key = s };

        try
        {
            return JsonSerializer.Deserialize<CustomKeySpec>(s, Json) ?? new CustomKeySpec();
        }
        catch (JsonException)
        {
            return new CustomKeySpec();
        }
    }
}
