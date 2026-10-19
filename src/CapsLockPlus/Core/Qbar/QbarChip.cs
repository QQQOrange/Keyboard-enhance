using System.Windows.Media;
using CapsLockPlus.Core.Icons;
using CapsLockPlus.Core.Settings;

namespace CapsLockPlus.Core.Qbar;

public enum ChipKind
{
    /// <summary>搜索：bd 关键词 → 百度搜索。</summary>
    Search,
    /// <summary>启动程序：calc → 计算器。</summary>
    Run,
    /// <summary>打开网址：gh → GitHub。</summary>
    Web,
}

/// <summary>
/// Qbar 输入框里的“标签”（Chip）。
/// 用户输入 "bd" 再按空格，bd 就从纯文本变成一枚标签，输入框里只剩下要搜索的内容。
/// 有图标就显示图标，没有图标就显示名字。
/// </summary>
public sealed class QbarChip
{
    public ChipKind Kind { get; init; }
    public string Key { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Value { get; init; } = "";
    public string IconPath { get; init; } = "";
    public bool RunAsAdmin { get; init; }
    public string Arguments { get; init; } = "";

    /// <summary>解析出来的图标；为 null 时界面显示文字标签。</summary>
    public ImageSource? Icon { get; init; }

    public bool HasIcon => Icon != null;

    /// <summary>没有图标时用的品牌色与首字。</summary>
    public Color Accent { get; init; } = Colors.SteelBlue;
    public string Initial { get; init; } = "";

    /// <summary>标签上的文字（没有图标时显示）。</summary>
    public string Label => string.IsNullOrWhiteSpace(DisplayName) ? Key : DisplayName;

    public static QbarChip FromEntry(ChipKind kind, QEntry entry) => new()
    {
        Kind = kind,
        Key = entry.Key,
        DisplayName = entry.DisplayName,
        Value = entry.Value,
        IconPath = entry.IconPath,
        RunAsAdmin = entry.RunAsAdmin,
        Arguments = entry.Arguments,
        Icon = IconCache.ForEntry(entry),
        Accent = IconCache.ColorFor(entry.DisplayName),
        Initial = IconCache.Initial(entry.DisplayName),
    };

    /// <summary>把标签拼回“关键字 + 内容”的形式，用于执行。</summary>
    public string Compose(string tail) => string.IsNullOrEmpty(tail) ? Key : $"{Key} {tail}";
}
