using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using CapsLockPlus.Core;

namespace CapsLockPlus.UI;

/// <summary>
/// 拖拽重排时的"插入位置指示线"。
///
/// 为什么需要：自定义顺序下松手前完全看不出会插到哪 —— 用户只能靠"松手试试，
/// 不对再拖一次"。加一条 2px 的强调色横线，落点立刻可预期。
///
/// 用 Adorner 而不是往列表里塞一个 Border：装饰层不会参与布局，
/// 不会把行挤动，也不会被 ListBox 的虚拟化回收。
/// </summary>
public sealed class InsertLineAdorner : Adorner
{
    private const double Thickness = 2;

    private readonly Pen _pen;
    private double _y;

    private InsertLineAdorner(UIElement adorned) : base(adorned)
    {
        IsHitTestVisible = false;

        var brush = new SolidColorBrush(Colors.Transparent);
        try
        {
            if (Application.Current?.TryFindResource("Brush.Accent") is SolidColorBrush accent)
                brush = accent;
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "取强调色");
        }

        /* 用冻结的副本：主题切换时旧线不会被牵连，也不需要重建。 */
        var frozen = brush.Clone();
        frozen.Freeze();
        _pen = new Pen(frozen, Thickness);
        _pen.Freeze();
    }

    /// <summary>在指定元素上显示一条插入线（先隐藏，等 MoveTo）。</summary>
    public static InsertLineAdorner? Show(UIElement target)
    {
        try
        {
            var layer = AdornerLayer.GetAdornerLayer(target);
            if (layer == null) return null;

            var adorner = new InsertLineAdorner(target);
            layer.Add(adorner);
            return adorner;
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "显示插入线");
            return null;
        }
    }

    public void MoveTo(double y)
    {
        _y = y;
        InvalidateVisual();
    }

    public void Remove()
    {
        try { (Parent as AdornerLayer)?.Remove(this); } catch { }
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        double width = AdornedElement.RenderSize.Width;
        if (width <= 0) return;

        double top = Math.Max(0, Math.Min(_y, AdornedElement.RenderSize.Height - Thickness));

        drawingContext.DrawLine(_pen, new Point(0, top + Thickness / 2),
                                       new Point(width, top + Thickness / 2));
    }
}
