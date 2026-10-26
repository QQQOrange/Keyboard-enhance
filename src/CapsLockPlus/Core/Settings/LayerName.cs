namespace CapsLockPlus.Core.Settings;

/// <summary>
/// 层名字的**显示出口**。
///
/// 为什么不能直接 `Loc.T(layer.Name)`：层的名字是存在配置里的**数据**，不是界面文案。
/// 用户改过的名字必须原样显示（哪怕他正好把它改成"基础层"这三个字）。
/// 所以只在"名字还是程序生成的默认值"时才翻译 ——
/// 默认值是 1 层「基础层」、n 层「第 n 层」（见 SettingsStore.EnsureDefaults）。
///
/// 判断方式是按 Id 重建一遍默认名再比对，比"认中文关键词"稳：
/// 用户把 3 层改名成"基础层"时，它是自定义名字，不该被翻成 Base。
/// </summary>
public static class LayerName
{
    /// <summary>默认名（已按当前语言翻好），给"还没有名字"的层兜底。</summary>
    public static string Default(int id) => id <= 1
        ? Localization.Loc.T("基础层")
        : Localization.Loc.F("第 {0} 层", id);

    /// <summary>界面上该显示什么名字：默认名跟着语言走，自定义名原样返回。</summary>
    public static string Display(int id, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return Default(id);

        // 程序生成的默认名（"基础层" / "第 3 层"）→ 翻译
        string zh = id <= 1 ? "基础层" : $"第 {id} 层";
        return string.Equals(name, zh, System.StringComparison.Ordinal) ? Default(id) : name;
    }
}
