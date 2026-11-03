using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Effects;
using CapsLockPlus.Core;

namespace CapsLockPlus.UI;

/// <summary>
/// 拖拽时跟着鼠标的小"药丸"卡片（显示被拖的别名）。
///
/// 为什么不用默认拖拽光标：默认只有一个"移动"箭头，用户看不出自己在拖什么，
/// 尤其列表里同时有好几条别名时。这里画一张跟手的卡片，落到分组上时配合高亮，
/// 整个动作就有"拿起来 → 移到目标 → 放下"的连续感。
/// </summary>
public sealed class DragChipAdorner : Adorner
{
    private readonly VisualCollection _children;
    private readonly Border _chip;
    private readonly TranslateTransform _offset = new();

    private DragChipAdorner(UIElement adorned, string text) : base(adorned)
    {
        _children = new VisualCollection(this);

        var label = new TextBlock
        {
            Text = text,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextPrimary");

        _chip = new Border
        {
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(11, 6, 11, 6),
            Child = label,
            RenderTransform = _offset,
            Opacity = 0.96,
            Effect = new DropShadowEffect
            {
                BlurRadius = 14,
                ShadowDepth = 2,
                Opacity = 0.35,
                Color = Colors.Black,
            },
        };
        _chip.SetResourceReference(Border.BackgroundProperty, "Brush.SurfaceAlt");
        _chip.SetResourceReference(Border.BorderBrushProperty, "Brush.Accent");
        _chip.BorderThickness = new Thickness(1);

        _children.Add(_chip);
    }

    /// <summary>在指定元素的装饰层上显示一个拖拽卡片。</summary>
    public static DragChipAdorner? Show(UIElement target, string text, Point at)
    {
        try
        {
            var layer = AdornerLayer.GetAdornerLayer(target);
            if (layer == null) return null;

            var adorner = new DragChipAdorner(target, text);
            layer.Add(adorner);
            adorner.MoveTo(at);
            return adorner;
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "显示拖拽卡片");
            return null;
        }
    }

    /// <summary>
    /// 取光标的"元素内坐标"。
    ///
    /// ⚠ 不能用 <c>Mouse.GetPosition</c>：DoDragDrop 期间跑的是 OLE 自己的消息循环，
    /// WPF 的鼠标位置不会更新（实测拖到 +X 方向却返回 -568,-196），
    /// 卡片就会飞到奇怪的地方 / 卡住不动。GetCursorPos 是系统级真值，任何循环里都准。
    /// </summary>
    public static Point CursorIn(UIElement element)
    {
        try
        {
            if (GetCursorPos(out POINT p))
                return element.PointFromScreen(new Point(p.X, p.Y));
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "取光标位置");
        }

        return new Point(0, 0);
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    public void MoveTo(Point point)
    {
        _offset.X = point.X;
        _offset.Y = point.Y;
    }

    public void Remove()
    {
        try { (Parent as AdornerLayer)?.Remove(this); } catch { }
    }

    protected override int VisualChildrenCount => 1;

    protected override Visual GetVisualChild(int index) => _chip;

    protected override Size MeasureOverride(Size constraint)
    {
        _chip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return _chip.DesiredSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _chip.Arrange(new Rect(_chip.DesiredSize));
        return finalSize;
    }
}
