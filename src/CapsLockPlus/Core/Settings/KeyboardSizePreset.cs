namespace CapsLockPlus.Core.Settings;

/// <summary>
/// 「按键映射」页的键盘大小档位。
///
/// 为什么是"档位"而不是"跟着窗口等比缩"：
/// 等比缩放在**没有下限**时会一路缩到看不清字（用户实测 0.55 倍，
/// 8.5 DIP 的功能名只剩 4.7 DIP）；加了硬下限又变成"我替你决定字号"。
/// 现在把选择权交出来：用户挑一档，这档既是键盘的固定大小，
/// 也是这个页面的**最小尺寸** —— 窗口再小也不会把键帽压得更小，
/// 窗口更大也不会把键帽放大（那会让用户找不到上次看到的那个大小）。
///
/// 档位之间的比例是按**功能名字号**定的（键帽上的主信息是 8.5 DIP）：
///   · 0.75 → 6.4 DIP：1440 宽的屏也放得下，但中文笔画开始粘；
///   · 0.85 → 7.2 DIP：1300~1500 宽屏的折中；
///   · 1.00 → 8.5 DIP：设计尺寸，字最清楚；
///   · 1.15 → 9.8 DIP：2K 屏，远看也认得。
/// 再往下（0.7 以下）就到 6 DIP 以内了，不如不做。
/// </summary>
public sealed record KeyboardSizePreset(string Id, string Name, double Scale, string Hint)
{
    /// <summary>全部档位，按缩放从小到大。</summary>
    public static readonly IReadOnlyList<KeyboardSizePreset> All = new[]
    {
        new KeyboardSizePreset("small",  "小",   0.75, "窄屏可用，字会小一些"),
        new KeyboardSizePreset("medium", "中",   0.85, "1366~1600 宽的屏比较合适"),
        new KeyboardSizePreset("large",  "大",   1.00, "设计尺寸，字最清楚"),
        new KeyboardSizePreset("xlarge", "特大", 1.15, "2K 屏，不凑近也认得出"),
    };

    /// <summary>
    /// 默认档 = 设计尺寸。
    ///
    /// 不用最小档做默认：第一次打开就给人看一屏小字，等于替他把"看得清"
    /// 这个最重要的事让步掉了。宁可窗口宽一点。
    /// </summary>
    public const string DefaultId = "large";

    /// <summary>
    /// 把一个可能是 null / 空 / 拼错的 ID 解析成合法档位。
    ///
    /// 老配置文件里没有这个字段（读到 null）→ 落到默认档；
    /// 用户手改配置文件写了个不存在的 ID → 也落到默认档，
    /// 不能因为一个字符串就让页面算不出尺寸。
    /// </summary>
    public static KeyboardSizePreset Resolve(string? id)
    {
        foreach (var p in All)
        {
            if (string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)) return p;
        }
        return All.First(p => p.Id == DefaultId);
    }

    /// <summary>界面上的档位名（小/中/大/特大 只有一两个字，得跟着语言走）。</summary>
    public string DisplayName => Localization.Loc.T(Name);

    /// <summary>界面上的档位说明。</summary>
    public string DisplayHint => Localization.Loc.T(Hint);

    /// <summary>
    /// 档位在界面上的标签，例如「大 · 1058」。
    ///
    /// 只写宽度：高度是宽度的固定比例（键盘宽高比 3.2:1），写出来也是重复；
    /// 而且带上"× 380"时，「特大」那档在下拉框里会被截成"1217 × 38"。
    /// 完整尺寸显示在下拉框右边那行小字里（见 KeysView.SizeText）。
    /// </summary>
    public string Label(double naturalWidth) => $"{DisplayName} · {naturalWidth * Scale:0}";
}
