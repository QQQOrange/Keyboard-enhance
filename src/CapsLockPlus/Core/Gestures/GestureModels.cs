using System;
using System.Collections.Generic;
using System.Linq;

namespace CapsLockPlus.Core.Gestures;

/// <summary>
/// 一条鼠标手势。
///
/// 三种触发方式共用一张表，靠 <see cref="Kind"/> 区分：
/// <list type="bullet">
/// <item><c>stroke</c>：按住 <see cref="Button"/> 画一笔，形状匹配 <see cref="Path"/>；</item>
/// <item><c>corner</c>：把指针甩到屏幕角上单击 <see cref="Button"/>；</item>
/// <item><c>wheel</c>：按住 <see cref="Button"/> 时滚轮朝 <see cref="Wheel"/> 方向拨。</item>
/// </list>
/// 三条都用同一套动作执行管线，所以"手势 → 宏/按键/启动程序"的能力天然一致。
/// </summary>
public sealed class MouseGesture
{
    /// <summary>稳定 ID：界面用它定位，导入时用它去重。</summary>
    public string Id { get; set; } = "";

    /// <summary>显示名。空的就按动作标题显示。</summary>
    public string Name { get; set; } = "";

    /// <summary>起始键：left / right / middle / x1 / x2。</summary>
    public string Button { get; set; } = "right";

    /// <summary>
    /// 组合键：按住 <see cref="Button"/> 的同时再点这一颗。
    /// 空 = 普通手势。WGestures 里「按住右键再点左键」这类特殊手势就靠它，
    /// 导入时第二颗鼠标键落在这里，不丢。
    /// </summary>
    public string Chord { get; set; } = "";

    /// <summary>触发类型：stroke / corner / wheel。</summary>
    public string Kind { get; set; } = "stroke";

    /// <summary>
    /// 笔画模板：x0,y0,x1,y1… 的扁平数组，已归一化到 0~1000。
    ///
    /// 归一化发生在录制/导入时，识别时只做同样的归一化再比距离 ——
    /// 这样模板与"用户这次画多大"无关，拇指画一小段和手臂甩一大段算同一笔。
    /// </summary>
    public List<int> Path { get; set; } = new();

    /// <summary>
    /// 方向串：<c>↑→↓←</c> 的组合，识别时按它做精确匹配。
    ///
    /// 为什么不再只靠 <see cref="Path"/> 的形状：手势的本意是"往哪个方向拐"，
    /// 不是"描出同样一条曲线"。方向串既好匹配（拐弯对了就中），又好显示
    /// （列表里直接写 ↑→），编辑时用户一眼就知道自己配了什么。
    /// 老数据没有这个字段，读的时候用 <see cref="EnsurePattern"/> 从 Path 反推。
    /// </summary>
    public string Pattern { get; set; } = "";

    /// <summary>滚轮方向：-1 上、+1 下、-2 左、+2 右（仅 Kind=wheel）。</summary>
    public int Wheel { get; set; } = -1;

    /// <summary>角落：0 任意角、1 左上、2 右上、3 左下、4 右下（仅 Kind=corner）。</summary>
    public int Corner { get; set; }

    /// <summary>动作 ID（见 ActionCatalog）。</summary>
    public string Action { get; set; } = "";

    /// <summary>动作参数（启动程序路径、文本、宏 ID…）。</summary>
    public string Arg { get; set; } = "";

    /// <summary>限定应用（可执行文件全路径）。空 = 全局。</summary>
    public string AppPath { get; set; } = "";

    public bool Enabled { get; set; } = true;

    /// <summary>来源标记：从 WGestures 导入的会带上，方便用户知道哪些是搬过来的。</summary>
    public bool FromWGestures { get; set; }

    /// <summary>导入时无法自动翻译的说明（例如 Lua 脚本），显示在列表里提醒用户。</summary>
    public string Note { get; set; } = "";

    public int PathPointCount => Path.Count / 2;

    public bool HasPath => Path.Count >= 4;

    /// <summary>通配手势：任何方向都能触发（FlowMouse 的 <c>*</c>）。</summary>
    public bool IsWildcard => Pattern == GesturePattern.Wildcard;

    /// <summary>
    /// 保证 <see cref="Pattern"/> 有值：老数据 / 导入时漏算的，从归一化路径反推一次。
    /// 只补齐空值，绝不覆盖用户已有的方向串。
    /// </summary>
    public void EnsurePattern()
    {
        if (!string.IsNullOrEmpty(Pattern)) return;
        if (Kind != "stroke" || !HasPath) return;
        Pattern = GesturePattern.FromNormalized(Points());
    }

    /// <summary>把扁平数组还原成点列（识别时用）。</summary>
    public List<GestureMath.Pt> Points()
    {
        var list = new List<GestureMath.Pt>(Path.Count / 2);
        for (int i = 0; i + 1 < Path.Count; i += 2)
            list.Add(new GestureMath.Pt(Path[i], Path[i + 1]));
        return list;
    }

    public static string NewId() => Guid.NewGuid().ToString("N")[..8];

    /// <summary>显示名：没填名字就用动作标题，再不行用触发方式的通用叫法。</summary>
    public string Title(string? actionTitle = null)
    {
        if (!string.IsNullOrWhiteSpace(Name)) return Name;
        if (!string.IsNullOrWhiteSpace(actionTitle)) return actionTitle!;
        return Kind switch
        {
            "corner" => "角落触发",
            "wheel" => "滚轮触发",
            _ => "手势"
        };
    }

    /// <summary>复制一份（界面里"取消"要能还原，不能改到列表里的活对象）。</summary>
    public MouseGesture Clone() => new()
    {
        Id = Id,
        Name = Name,
        Button = Button,
        Chord = Chord,
        Kind = Kind,
        Path = new List<int>(Path),
        Pattern = Pattern,
        Wheel = Wheel,
        Corner = Corner,
        Action = Action,
        Arg = Arg,
        AppPath = AppPath,
        Enabled = Enabled,
        FromWGestures = FromWGestures,
        Note = Note
    };

    /// <summary>把所有字段用 <paramref name="other"/> 覆盖（保持对象引用不变，列表绑定不会跳）。</summary>
    public void CopyFrom(MouseGesture other)
    {
        Name = other.Name;
        Button = other.Button;
        Chord = other.Chord;
        Kind = other.Kind;
        Path = new List<int>(other.Path);
        Pattern = other.Pattern;
        Wheel = other.Wheel;
        Corner = other.Corner;
        Action = other.Action;
        Arg = other.Arg;
        AppPath = other.AppPath;
        Enabled = other.Enabled;
        FromWGestures = other.FromWGestures;
        Note = other.Note;
    }
}

/// <summary>手势功能的全局选项。</summary>
public sealed class GestureOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>拖动时在屏幕上画轨迹。</summary>
    public bool ShowTrail { get; set; } = true;

    /// <summary>
    /// 没画出形状的单击，是否也去匹配「单击这个键」的手势。
    /// 默认关：右键按下又松开就该弹右键菜单，软件不能把普通单击吃掉。
    /// </summary>
    public bool ClickGestures { get; set; }

    /// <summary>轨迹颜色（#RRGGBB）。</summary>
    public string TrailColor { get; set; } = "#3B82F6";

    public double TrailWidth { get; set; } = 3.0;

    /// <summary>起点到当前点超过这么多像素才算"画了一笔"，否则当单击处理。</summary>
    public int MinMove { get; set; } = 30;

    /// <summary>起手距离：走出这么多像素才开始记方向（FlowMouse 默认 20）。</summary>
    public int ActivationDistance { get; set; } = 20;

    /// <summary>
    /// 转折系数：换方向要越过 起手距离 + 上一段长度 × 这个系数。
    /// 越大越"懒得拐弯"，适合手抖；越小越容易切出短段落。
    /// </summary>
    public double TurnTolerance { get; set; } = 0.10;

    /// <summary>最大转折距离：再长的一笔，换方向也不该超过这么多像素。</summary>
    public int MaxTurnDistance { get; set; } = 120;

    /// <summary>形状匹配阈值：平均偏差 / 归一化尺寸，越小越严格。</summary>
    public double MatchThreshold { get; set; } = 0.26;

    /// <summary>角落感应区边长（像素）。</summary>
    public int CornerSize { get; set; } = 24;

    /// <summary>
    /// 上一次从 WGestures 自动导入的配置路径。
    /// 空 = 还没导过；非空 = 只导一次，用户删光手势也不会被重新塞回来。
    /// </summary>
    public string AutoImportSource { get; set; } = "";

    /// <summary>
    /// 手势页左侧「作用目标」里手动加过、但还没配手势的应用。
    /// 只有这些路径也列出来，用户才能先建目标再画手势。
    /// </summary>
    public List<string> Targets { get; set; } = new();

    /// <summary>画完后多少毫秒内仍显示识别出的手势名。</summary>
    public int LabelLingerMs { get; set; } = 450;

    public static GestureOptions Default => new();
}

/// <summary>按钮名 ↔ 虚拟键码。设置里存名字而不是数字，配置读起来才像人话。</summary>
public static class GestureButtons
{
    public const string Left = "left";
    public const string Right = "right";
    public const string Middle = "middle";
    public const string X1 = "x1";
    public const string X2 = "x2";

    public static readonly string[] All = { Right, Middle, X2, X1, Left };

    public static int ToVk(string? name) => (name ?? "").Trim().ToLowerInvariant() switch
    {
        "left" or "mouseleft" => 0x01,
        "right" or "mouseright" => 0x02,
        "middle" or "mousemiddle" => 0x04,
        "x1" or "mousex1" => 0x05,
        "x2" or "mousex2" => 0x06,
        _ => 0
    };

    public static string FromVk(int vk) => vk switch
    {
        0x01 => Left,
        0x02 => Right,
        0x04 => Middle,
        0x05 => X1,
        0x06 => X2,
        _ => ""
    };

    public static string Display(string? name) => (name ?? "").Trim().ToLowerInvariant() switch
    {
        "left" => "鼠标左键",
        "right" => "鼠标右键",
        "middle" => "鼠标中键",
        "x1" => "侧键 1",
        "x2" => "侧键 2",
        _ => "鼠标右键"
    };
}
