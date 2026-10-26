using System;
using System.Windows.Data;
using System.Windows.Markup;
using CapsLockPlus.Core.Localization;

namespace CapsLockPlus.UI.Markup;

/// <summary>
/// XAML 里的本地化标记扩展：`Text="{loc:T '保存'}"`。
///
/// 返回的不是字符串，而是**绑定**到该文案令牌的 OneWay Binding —— 切语言时
/// 令牌发 PropertyChanged，界面立刻重画，不需要重建窗口、更不需要重启。
///
/// 用法（两种写法等价）：
///   Text="{loc:T '保存'}"          位置参数
///   Text="{loc:T Text=保存}"       命名参数（原文含逗号时用单引号裹住）
///
/// ⚠ 原文里的单引号要写成 `&apos;`，大括号开头的原文要写成 `{}` 转义；
/// 这两条都由 tools/i18n.py 在生成时处理，手写时留意即可。
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TExtension : MarkupExtension
{
    public TExtension()
    {
    }

    public TExtension(string text) => Text = text;

    /// <summary>原文（= 语言包里的键）。</summary>
    [ConstructorArgument("text")]
    public string Text { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        if (string.IsNullOrEmpty(Text)) return "";

        var binding = new Binding(nameof(LocToken.Text))
        {
            Source = LocTokens.For(Text),
            Mode = BindingMode.OneWay,
        };
        return binding.ProvideValue(serviceProvider);
    }
}
