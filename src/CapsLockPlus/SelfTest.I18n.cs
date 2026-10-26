using System.Text.RegularExpressions;
using CapsLockPlus.Core.Localization;

namespace CapsLockPlus;

/// <summary>
/// 界面语言的自检。
///
/// 这里只查"语言内核"能自证的部分 —— 语言包齐不齐、占位符对不对、
/// 切语言后译文/日期格式/令牌刷新是不是真跟着变了。
/// **界面到底有没有中文残留，自检查不出来**（那要看渲染出来的像素），
/// 那部分靠 `--render` 截图逐页核对，别指望这里。
/// </summary>
public static partial class SelfTest
{
    /// <summary>译文里不该出现的字符（汉字 + 全角标点）。`、` 除外，自检样本要用。</summary>
    private static readonly Regex CjkInValue = new(@"[\u4e00-\u9fff\u3000-\u303f\uff01-\uff60]", RegexOptions.Compiled);

    /// <summary>格式项：{0} / {1:0.#} —— 只认数字索引，`{{` 这种转义不算。</summary>
    private static readonly Regex FormatItem = new(@"\{(\d+)(?::[^{}]*)?\}", RegexOptions.Compiled);

    private static void TestLocalization()
    {
        // 这个用例会真的切语言，跑完必须切回去 ——
        // 否则后面所有断言（按键名、菜单文案）都在英文状态里跑，失败原因会变得莫名其妙。
        string original = Loc.Language;
        try
        {
            TestLocDiscoveryAndNormalize();
            TestEnPackIntegrity();
            TestLocLiveSwitch();
            TestLocTokenRefresh();
            TestLocFormatSafety();
        }
        finally
        {
            Loc.Apply(original);
        }

        Check("自检结束后语言已还原", Loc.Language == original, $"期望 {original}，实际 {Loc.Language}");
    }

    private static void TestLocDiscoveryAndNormalize()
    {
        var avail = Loc.Available;
        Check("发现内置中文语言包", avail.Any(l => l.Code == "zh-CN"));
        Check("发现内置英文语言包", avail.Any(l => l.Code == "en-US"));
        Check("中文固定排第一（默认语言）", avail.Count > 0 && avail[0].Code == Loc.DefaultLanguage);
        CheckEqual("默认语言是简体中文", Loc.DefaultLanguage, "zh-CN");

        var en = avail.FirstOrDefault(l => l.Code == "en-US");
        // 下拉框里显示的是"人话"，不能是 en-US 这种代码
        CheckEqual("英文语言包的显示名", en?.DisplayName, "English");

        // 写法归一：用户配置里可能存 zh-cn / EN / en_US 各种写法
        CheckEqual("Normalize(zh-cn)", Loc.Normalize("zh-cn"), "zh-CN");
        CheckEqual("Normalize(EN-us)", Loc.Normalize("EN-us"), "en-US");
        CheckEqual("Normalize(en) 按语种挑", Loc.Normalize("en"), "en-US");
        CheckEqual("Normalize(null) 回默认", Loc.Normalize(null), "zh-CN");
        CheckEqual("Normalize(空白) 回默认", Loc.Normalize("   "), "zh-CN");
        CheckEqual("Normalize(未知语言) 回默认", Loc.Normalize("xx-YY"), "zh-CN");
    }

    /// <summary>英文包本身的体检 —— "内置英文语言包"这条要求的硬指标。</summary>
    private static void TestEnPackIntegrity()
    {
        var pack = Loc.PackForTest("en-US");
        Check("英文包条目数 > 1700", pack.Count > 1700, $"实际 {pack.Count} 条");

        CheckEqual("英文包带有 __name 元数据",
            pack.TryGetValue("__name", out string? nm) ? nm : null, "English");

        // 关键路径抽查：这几条一错，整个英文界面就是残的
        foreach (var (key, want) in new (string, string)[]
                 {
                     ("保存", "Save"),
                     ("取消", "Cancel"),
                     ("设置", "Settings"),
                     ("、", ", "),
                 })
        {
            CheckEqual($"英文包关键条目「{key}」", pack.TryGetValue(key, out string? v) ? v : null, want);
        }

        // 空译文 = 白屏一条文案（T() 会退回原文，但界面上就是中文残留）
        var blank = pack.Where(kv => !kv.Key.StartsWith("__") && string.IsNullOrWhiteSpace(kv.Value))
                        .Select(kv => kv.Key).ToList();
        Check("英文包没有空译文", blank.Count == 0, string.Join(" / ", blank.Take(5)));

        // 译文里混进汉字 = 漏翻。这条是"英文界面无中文"最直接的守卫。
        var cjk = pack.Where(kv => !kv.Key.StartsWith("__") && kv.Value != null && CjkInValue.IsMatch(kv.Value))
                      .Select(kv => kv.Key).ToList();
        Check("英文译文里没有残留汉字", cjk.Count == 0, string.Join(" / ", cjk.Take(5)));

        // 占位符：数量与索引必须一致，否则运行时 string.Format 抛 FormatException
        var bad = new List<string>();
        foreach (var kv in pack)
        {
            if (kv.Key.StartsWith("__") || string.IsNullOrEmpty(kv.Value)) continue;
            var a = FormatItem.Matches(kv.Key).Select(m => m.Groups[1].Value).OrderBy(x => x).ToList();
            var b = FormatItem.Matches(kv.Value).Select(m => m.Groups[1].Value).OrderBy(x => x).ToList();
            if (!a.SequenceEqual(b)) bad.Add($"{kv.Key} → {kv.Value}");
        }
        Check("英文包占位符与原文一致", bad.Count == 0, string.Join(" / ", bad.Take(5)));
    }

    /// <summary>切语言后：译文、Culture、日期时间格式都要跟着走。</summary>
    private static void TestLocLiveSwitch()
    {
        var t = new DateTime(2026, 10, 25, 21, 7, 33);

        Loc.Apply("zh-CN");
        CheckEqual("中文 T(保存)", Loc.T("保存"), "保存");
        CheckEqual("中文 Culture", Loc.Culture.Name, "zh-CN");
        CheckEqual("中文日期时间", Loc.FormatDateTime(t), "2026-10-25 21:07");
        CheckEqual("中文时刻", Loc.FormatTime(t), "21:07:33");
        CheckEqual("中文列表连接符", Loc.Join(new[] { "甲", "乙", "丙" }), "甲、乙、丙");
        CheckEqual("中文下查不到就显示原文", Loc.T("这条肯定不在语言包里"), "这条肯定不在语言包里");

        Loc.Apply("en-US");
        CheckEqual("英文 T(保存)", Loc.T("保存"), "Save");
        CheckEqual("英文 Culture", Loc.Culture.Name, "en-US");
        // 12 小时制是"语言切换覆盖到日期时间"的可见证据：21:07 应变成 9:07 PM
        Check("英文时刻走 12 小时制", Loc.FormatTime(t).Contains("PM"), Loc.FormatTime(t));
        Check("英文日期不含 24 小时制", !Loc.FormatDateTime(t).Contains("21:07"), Loc.FormatDateTime(t));
        CheckEqual("英文列表连接符", Loc.Join(new[] { "A", "B", "C" }), "A, B, C");
        CheckEqual("英文下查不到就显示原文", Loc.T("这条肯定不在语言包里"), "这条肯定不在语言包里");
        Check("英文下空字符串不炸", Loc.T("") == "" && Loc.T(null!) == "");

        // 未知语言不能把界面搞成半中半英，直接回默认
        Loc.Apply("xx-YY");
        CheckEqual("未知语言回退默认", Loc.Language, "zh-CN");
        CheckEqual("未知语言下译文也跟着回默认", Loc.T("保存"), "保存");
    }

    /// <summary>XAML 令牌：同一个令牌在切语言后必须自己变（不用重建界面）。</summary>
    private static void TestLocTokenRefresh()
    {
        Loc.Apply("zh-CN");
        LocToken token = LocTokens.For("保存");
        Check("令牌缓存复用同一个实例", ReferenceEquals(token, LocTokens.For("保存")));
        int before = LocTokens.CountForTest;
        LocTokens.For("保存");
        CheckEqual("重复取令牌不增加缓存", LocTokens.CountForTest, before);
        CheckEqual("令牌初始文案", token.Text, "保存");

        int fired = 0;
        void OnChanged(object? s, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(LocToken.Text)) fired++;
        }

        token.PropertyChanged += OnChanged;
        Loc.Apply("en-US");
        token.PropertyChanged -= OnChanged;

        CheckEqual("切语言后令牌自己变成英文", token.Text, "Save");
        Check("切语言时令牌发了 PropertyChanged", fired > 0, $"收到 {fired} 次");
    }

    /// <summary>Loc.F 的容错：译文写错占位符不能把界面搞崩。</summary>
    private static void TestLocFormatSafety()
    {
        Loc.Apply("zh-CN");

        CheckEqual("F 正常格式化", Loc.F("第 {0} 步", 3), "第 3 步");
        CheckEqual("F 多参数", Loc.F("{0} / {1} 个子层", 2, 5), "2 / 5 个子层");

        // 参数比占位符少 → string.Format 抛 FormatException，F 必须吞掉并返回模板
        string got = "";
        bool threw = false;
        try { got = Loc.F("第 {0} 步 / 共 {1} 步", 3); }
        catch { threw = true; }
        Check("F 参数不足时不抛异常", !threw);
        Check("F 参数不足时原样返回", got.Contains("{1}"), got);

        CheckEqual("F 空原文", Loc.F(""), "");

        // Cat：中文不加空格、西文在标点后补一格 —— 英文界面常见 `recommended.Hotkeys` 就是缺了这条
        CheckEqual("Cat 英文句号后补空格", Loc.Cat("Done.", "Next step"), "Done. Next step");
        CheckEqual("Cat 英文逗号后补空格", Loc.Cat("a,", "b"), "a, b");
        CheckEqual("Cat 中文句号后不加空格", Loc.Cat("已完成。", "下一步"), "已完成。下一步");
        CheckEqual("Cat 中文顿号后不加空格", Loc.Cat("甲、", "乙"), "甲、乙");
        CheckEqual("Cat 跳过空段", Loc.Cat("只有一段。", null, ""), "只有一段。");
        CheckEqual("Cat 保留已有空格", Loc.Cat("Done. ", "Next"), "Done. Next");
    }
}
