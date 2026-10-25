using System;
using System.Collections.Generic;
using System.ComponentModel;

namespace CapsLockPlus.Core.Localization;

/// <summary>
/// 一条文案的"活令牌"：持有原文，转发成当前语言的译文，并在切语言时发通知。
///
/// 为什么需要它：XAML 里的 `{loc:T '保存'}` 必须返回一个**能自己变**的值，
/// 否则切完语言要重启界面。WPF 里能变的最小单位是"绑定 + INotifyPropertyChanged"，
/// 而绑定需要一个属性路径 —— 用索引器 `[原文]` 的话，原文里的 `]` `,` `\`
/// 会把路径解析搞坏。这里给每条文案分配一个令牌对象、绑定它的常量属性名
/// <see cref="Text"/>，路径永远是普通标识符，多奇怪的原文都不受影响。
/// </summary>
public sealed class LocToken : INotifyPropertyChanged
{
    internal LocToken(string source) => Source = source;

    /// <summary>原文（语言包里的键）。</summary>
    public string Source { get; }

    /// <summary>当前语言的译文。切语言后由 <see cref="LocTokens.InvalidateAll"/> 触发刷新。</summary>
    public string Text => Loc.T(Source);

    public event PropertyChangedEventHandler? PropertyChanged;

    internal void Invalidate()
    {
        try { PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text))); }
        catch { /* 某个绑定失效了不该影响其它文案 */ }
    }

    public override string ToString() => Text;
}

/// <summary>全部令牌的缓存：同一条文案全局只建一个，切语言时统一刷新。</summary>
public static class LocTokens
{
    private static readonly Dictionary<string, LocToken> Cache = new(StringComparer.Ordinal);
    private static readonly object Gate = new();

    /// <summary>取（或新建）某条文案的令牌。</summary>
    public static LocToken For(string text)
    {
        if (string.IsNullOrEmpty(text)) text = "";
        lock (Gate)
        {
            if (Cache.TryGetValue(text, out LocToken? t)) return t;
            var created = new LocToken(text);
            Cache[text] = created;
            return created;
        }
    }

    /// <summary>切语言后把所有令牌刷一遍（令牌数量 = 界面用到的文案条数，很小）。</summary>
    internal static void InvalidateAll()
    {
        LocToken[] all;
        lock (Gate) all = new List<LocToken>(Cache.Values).ToArray();
        foreach (var t in all) t.Invalidate();
    }

    /// <summary>仅供自检：缓存里有几条令牌。</summary>
    internal static int CountForTest { get { lock (Gate) return Cache.Count; } }
}
