// ⚠️ 本文件是**源码**，不是缓存 —— 极简 SVG 渲染器（只覆盖 Qbar 图标用到的元素）。
// .gitignore 里的 icons/ 曾经把这里一起忽略掉，2026-10-25 误删过一次，
// 后来从 v4.5.23 发布版反编译（ilspycmd）才找回来。因此原始注释全部丢失，
// 只剩代码本身；要改逻辑的话先对照发布版确认行为，补注释时按上面这句写。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;

namespace CapsLockPlus.Core.Icons;

public static class SvgRenderer
{
    private readonly record struct Bezier(Point C1, Point C2, Point End);

    public static Action<string>? Trace { get; set; }

    public static ImageSource? Render(string path, int size = 64)
    {

        if (!File.Exists(path))
        {
            return null;
        }
        XElement? root = XDocument.Load(path).Root;
        if (root == null)
        {
            return null;
        }
        double num = 48.0;
        double num2 = 48.0;
        string? text = (string?)root.Attribute("viewBox");
        if (!string.IsNullOrWhiteSpace(text))
        {
            string[] array = text.Split(new char[2] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (array.Length == 4 && double.TryParse(array[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var result) && double.TryParse(array[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var result2) && result > 0.0 && result2 > 0.0)
            {
                num = result;
                num2 = result2;
            }
        }
        Dictionary<string, Color> gradients = CollectGradients(root);
        DrawingGroup drawingGroup = new DrawingGroup();
        List<XElement> list = new List<XElement>();
        CollectShapes(root, list, isRoot: true);
        foreach (XElement item in list)
        {
            Geometry? geometry = BuildGeometry(item);
            if (geometry == null)
            {
                Trace?.Invoke("  跳过 " + item.Name.LocalName + ": 几何体为空");
                continue;
            }
            Brush? brush = MakeBrushInherited(item, gradients);
            Pen? pen = MakePenInherited(item);
            if (brush == null && pen == null)
            {
                Trace?.Invoke("  跳过 " + item.Name.LocalName + ": 画笔和笔刷都为空");
                continue;
            }
            Rect bounds = geometry.Bounds;
            if (!(bounds.Width <= 0.0))
            {
                bounds = geometry.Bounds;
                if (!(bounds.Height <= 0.0))
                {
                    goto IL_0210;
                }
            }
            Trace?.Invoke($"  注意 {item.Name.LocalName}: 包围盒为空 {geometry.Bounds}");
            goto IL_0210;
            IL_0210:
            GeometryDrawing value = new GeometryDrawing(brush, pen, geometry);
            drawingGroup.Children.Add(value);
        }
        if (drawingGroup.Children.Count == 0)
        {
            Trace?.Invoke("  group 为空 -> 返回 null");
            return null;
        }
        Trace?.Invoke($"  group 有 {drawingGroup.Children.Count} 个子图，总包围盒 {drawingGroup.Bounds}，viewBox {num}x{num2}");
        double num3 = (double)size / Math.Max(num, num2);
        DrawingGroup drawingGroup2 = new DrawingGroup
        {
            Transform = new ScaleTransform(num3, num3)
        };
        drawingGroup2.Children.Add(drawingGroup);
        ((Freezable)drawingGroup2).Freeze();
        RenderTargetBitmap renderTargetBitmap = new RenderTargetBitmap(size, size, 96.0, 96.0, PixelFormats.Pbgra32);
        DrawingVisual drawingVisual = new DrawingVisual();
        using (DrawingContext drawingContext = drawingVisual.RenderOpen())
        {
            drawingContext.DrawDrawing(drawingGroup2);
        }
        renderTargetBitmap.Render(drawingVisual);
        ((Freezable)renderTargetBitmap).Freeze();
        return renderTargetBitmap;
    }

    private static Geometry? BuildGeometry(XElement el)
    {

        Transform? transform = ParseTransform((string?)el.Attribute("transform"));
        Geometry? geometry = el.Name.LocalName.ToLowerInvariant() switch
        {
            "path" => AsMutable(ParsePath((string?)el.Attribute("d"))), 
            "circle" => new EllipseGeometry(new Point(Attr(el, "cx", 0.0), Attr(el, "cy", 0.0)), Attr(el, "r", 0.0), Attr(el, "r", 0.0)), 
            "ellipse" => new EllipseGeometry(new Point(Attr(el, "cx", 0.0), Attr(el, "cy", 0.0)), Attr(el, "rx", 0.0), Attr(el, "ry", 0.0)), 
            "rect" => new RectangleGeometry(new Rect(Attr(el, "x", 0.0), Attr(el, "y", 0.0), Attr(el, "width", 0.0), Attr(el, "height", 0.0)), Attr(el, "rx", 0.0), Attr(el, "ry", 0.0)), 
            "text" => BuildText(el), 
            _ => null, 
        };
        if (geometry == null)
        {
            return null;
        }
        if (transform != null)
        {
            geometry.Transform = transform;
        }
        try
        {
            ((Freezable)geometry).Freeze();
        }
        catch
        {
        }
        return geometry;
    }

    private static Geometry AsMutable(Geometry? g)
    {
        if (g == null)
        {
            return Geometry.Empty;
        }
        if (!((Freezable)g).IsFrozen)
        {
            return g;
        }
        return g.Clone() ?? Geometry.Empty;
    }

    private static Geometry? BuildText(XElement el)
    {

        string text = el.Value?.Trim() ?? "";
        if (text.Length == 0)
        {
            return null;
        }
        double num = Attr(el, "x", 0.0);
        double num2 = Attr(el, "y", 0.0);
        double emSize = Attr(el, "font-size", 14.0);
        string familyName = (((string?)el.Attribute("font-family")) ?? "Segoe UI").Split(',')[0].Trim();
        string text2 = ((string?)el.Attribute("font-weight")) ?? "400";
        FontFamily fontFamily = new FontFamily(familyName);
        FontStyle normal = FontStyles.Normal;
        bool flag = ((text2 == "700" || text2 == "bold") ? true : false);
        Typeface typeface = new Typeface(fontFamily, normal, flag ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
        FormattedText formattedText = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, emSize, Brushes.Black, 1.0);
        return formattedText.BuildGeometry(new Point(num - formattedText.Width / 2.0, num2 - formattedText.Height + formattedText.Height * 0.22));
    }

    private static double Attr(XElement el, string name, double fallback)
    {
        string? text = (string?)el.Attribute(name);
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }
        text = text.Replace("px", "").Trim();
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var result))
        {
            return fallback;
        }
        return result;
    }

    private static Transform? ParseTransform(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }
        int num = raw.IndexOf('(');
        int num2 = raw.LastIndexOf(')');
        if (num < 0 || num2 < num)
        {
            return null;
        }
        string text = raw.Substring(0, num).Trim().ToLowerInvariant();
        int num3 = num + 1;
        double[] array = (from s in raw.Substring(num3, num2 - num3).Split(new char[2] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries)
            select (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var result)) ? 0.0 : result).ToArray();
        switch (text)
        {
        case "translate":
            if (array.Length >= 2)
            {
                return new TranslateTransform(array[0], array[1]);
            }
            if (array.Length == 1)
            {
                return new TranslateTransform(array[0], 0.0);
            }
            break;
        case "scale":
            if (array.Length >= 1)
            {
                return new ScaleTransform(array[0], (array.Length > 1) ? array[1] : array[0]);
            }
            break;
        case "rotate":
            if (array.Length >= 1)
            {
                return new RotateTransform(array[0]);
            }
            break;
        }
        return null;
    }

    private static void CollectShapes(XElement parent, List<XElement> into, bool isRoot = false)
    {
        foreach (XElement item in parent.Elements())
        {
            switch (item.Name.LocalName.ToLowerInvariant())
            {
            case "path":
            case "rect":
            case "text":
            case "circle":
            case "ellipse":
                into.Add(item);
                break;
            case "g":
            case "svg":
                CollectShapes(item, into);
                break;
            }
        }
    }

    private static string? InheritedAttr(XElement el, string attr)
    {
        for (XElement? xElement = el; xElement != null; xElement = xElement.Parent)
        {
            string? text = (string?)xElement.Attribute(attr);
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }
        return null;
    }

    private static Brush? MakeBrushInherited(XElement el, IReadOnlyDictionary<string, Color>? gradients)
    {
        return MakeBrushValue(InheritedAttr(el, "fill"), InheritedAttr(el, "opacity"), gradients);
    }

    private static Pen? MakePenInherited(XElement el)
    {
        string? text = InheritedAttr(el, "stroke");
        if (string.IsNullOrWhiteSpace(text) || text.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        Color color = ParseColor(text) ?? Colors.Black;
        Pen pen = new Pen(thickness: double.TryParse(InheritedAttr(el, "stroke-width"), NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : 1.0, brush: new SolidColorBrush(color));
        if (string.Equals(InheritedAttr(el, "stroke-linecap"), "round", StringComparison.OrdinalIgnoreCase))
        {
            pen.StartLineCap = PenLineCap.Round;
            pen.EndLineCap = PenLineCap.Round;
        }
        if (string.Equals(InheritedAttr(el, "stroke-linejoin"), "round", StringComparison.OrdinalIgnoreCase))
        {
            pen.LineJoin = PenLineJoin.Round;
        }
        ((Freezable)pen).Freeze();
        return pen;
    }

    private static Brush? MakeBrush(XElement el, IReadOnlyDictionary<string, Color>? gradients = null)
    {
        return MakeBrushValue((string?)el.Attribute("fill"), (string?)el.Attribute("opacity"), gradients);
    }

    private static Brush? MakeBrushValue(string? fill, string? opacityRaw, IReadOnlyDictionary<string, Color>? gradients)
    {
        double opacity = 1.0;
        if (!string.IsNullOrWhiteSpace(opacityRaw) && double.TryParse(opacityRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out var result))
        {
            opacity = Math.Clamp(result, 0.0, 1.0);
        }
        if (string.Equals(fill, "none", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        Color color;
        if (fill != null && fill.StartsWith("url(#", StringComparison.OrdinalIgnoreCase))
        {
            string key = fill.Substring(5).TrimEnd(')');
            color = ((gradients != null && gradients.TryGetValue(key, out var value)) ? value : Colors.Black);
        }
        else
        {
            color = ParseColor(fill) ?? Colors.Black;
        }
        SolidColorBrush solidColorBrush = new SolidColorBrush(color)
        {
            Opacity = opacity
        };
        ((Freezable)solidColorBrush).Freeze();
        return solidColorBrush;
    }

    private static Dictionary<string, Color> CollectGradients(XElement root)
    {
        Dictionary<string, Color> dictionary = new Dictionary<string, Color>(StringComparer.Ordinal);
        foreach (XElement item in root.Descendants().Where((XElement e) =>
        {
            string localName = e.Name.LocalName;
            return (localName == "linearGradient" || localName == "radialGradient") ? true : false;
        }))
        {
            string? text = (string?)item.Attribute("id");
            if (!string.IsNullOrEmpty(text))
            {
                XElement? xElement = item.Descendants().FirstOrDefault((XElement e) => e.Name.LocalName == "stop");
                Color? color = ParseColor((xElement == null) ? null : (((string?)xElement.Attribute("stop-color")) ?? ((string?)xElement.Attribute("stop-color"))));
                if (color.HasValue)
                {
                    dictionary[text] = color.Value;
                }
            }
        }
        return dictionary;
    }

    private static Pen? MakePen(XElement el)
    {
        string? text = (string?)el.Attribute("stroke");
        if (string.IsNullOrWhiteSpace(text) || text.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        Color color = ParseColor(text) ?? Colors.Black;
        Pen pen = new Pen(thickness: Attr(el, "stroke-width", 1.0), brush: new SolidColorBrush(color));
        if (string.Equals((string?)el.Attribute("stroke-linecap"), "round", StringComparison.OrdinalIgnoreCase))
        {
            pen.StartLineCap = PenLineCap.Round;
            pen.EndLineCap = PenLineCap.Round;
        }
        if (string.Equals((string?)el.Attribute("stroke-linejoin"), "round", StringComparison.OrdinalIgnoreCase))
        {
            pen.LineJoin = PenLineJoin.Round;
        }
        ((Freezable)pen).Freeze();
        return pen;
    }

    private static Color? ParseColor(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }
        raw = raw.Trim();
        if (raw.StartsWith("url(", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        if (raw.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        if (raw.Equals("currentColor", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        if (raw.StartsWith('#'))
        {
            try
            {
                return (Color)ColorConverter.ConvertFromString((raw.Length == 4) ? $"#{raw[1]}{raw[1]}{raw[2]}{raw[2]}{raw[3]}{raw[3]}" : raw);
            }
            catch
            {
                return null;
            }
        }
        try
        {
            return ColorConverter.ConvertFromString(raw) as Color?;
        }
        catch
        {
            return null;
        }
    }

    private static Geometry? ParsePath(string? d)
    {

        if (string.IsNullOrWhiteSpace(d))
        {
            return null;
        }
        List<string> list = Tokenize(d);
        StreamGeometry streamGeometry = new StreamGeometry
        {
            FillRule = FillRule.Nonzero
        };
        using StreamGeometryContext streamGeometryContext = streamGeometry.Open();
        Point val = new Point(0.0, 0.0);
        Point val2 = new Point(0.0, 0.0);
        Point val3 = new Point(0.0, 0.0);
        int num = 0;
        char c = 'M';
        while (num < list.Count)
        {
            if (IsCommand(list[num]))
            {
                c = list[num++][0];
            }
            switch (c)
            {
            case 'M':
            case 'm':
            {
                if (num + 1 >= list.Count)
                {
                    return streamGeometry;
                }
                double num8 = Num(list[num++]);
                double num9 = Num(list[num++]);
                if (c == 'm')
                {
                    num8 += val.X;
                    num9 += val.Y;
                }
                val = new Point(num8, num9);
                val2 = val;
                streamGeometryContext.BeginFigure(val, isFilled: true, isClosed: true);
                c = ((c == 'm') ? 'l' : 'L');
                break;
            }
            case 'L':
            case 'l':
            {
                if (num + 1 >= list.Count)
                {
                    return streamGeometry;
                }
                double num28 = Num(list[num++]);
                double num29 = Num(list[num++]);
                if (c == 'l')
                {
                    num28 += val.X;
                    num29 += val.Y;
                }
                val = new Point(num28, num29);
                streamGeometryContext.LineTo(val, isStroked: true, isSmoothJoin: false);
                break;
            }
            case 'H':
            case 'h':
            {
                if (num >= list.Count)
                {
                    return streamGeometry;
                }
                double num10 = Num(list[num++]);
                if (c == 'h')
                {
                    num10 += val.X;
                }
                val = new Point(num10, val.Y);
                streamGeometryContext.LineTo(val, isStroked: true, isSmoothJoin: false);
                break;
            }
            case 'V':
            case 'v':
            {
                if (num >= list.Count)
                {
                    return streamGeometry;
                }
                double num27 = Num(list[num++]);
                if (c == 'v')
                {
                    num27 += val.Y;
                }
                val = new Point(val.X, num27);
                streamGeometryContext.LineTo(val, isStroked: true, isSmoothJoin: false);
                break;
            }
            case 'C':
            case 'c':
            {
                if (num + 5 >= list.Count)
                {
                    return streamGeometry;
                }
                double num17 = Num(list[num++]);
                double num18 = Num(list[num++]);
                double num19 = Num(list[num++]);
                double num20 = Num(list[num++]);
                double num21 = Num(list[num++]);
                double num22 = Num(list[num++]);
                if (c == 'c')
                {
                    num17 += val.X;
                    num18 += val.Y;
                    num19 += val.X;
                    num20 += val.Y;
                    num21 += val.X;
                    num22 += val.Y;
                }
                Point point = new Point(num17, num18);
                Point val9 = new Point(num19, num20);
                Point val10 = new Point(num21, num22);
                streamGeometryContext.BezierTo(point, val9, val10, isStroked: true, isSmoothJoin: false);
                val = val10;
                val3 = val9;
                break;
            }
            case 'Q':
            case 'q':
            {
                if (num + 3 >= list.Count)
                {
                    return streamGeometry;
                }
                double num11 = Num(list[num++]);
                double num12 = Num(list[num++]);
                double num13 = Num(list[num++]);
                double num14 = Num(list[num++]);
                if (c == 'q')
                {
                    num11 += val.X;
                    num12 += val.Y;
                    num13 += val.X;
                    num14 += val.Y;
                }
                Point val5 = new Point(num11, num12);
                Point val6 = new Point(num13, num14);
                streamGeometryContext.QuadraticBezierTo(val5, val6, isStroked: true, isSmoothJoin: false);
                val = val6;
                val3 = val5;
                break;
            }
            case 'T':
            case 't':
            {
                if (num + 1 >= list.Count)
                {
                    return streamGeometry;
                }
                double num15 = Num(list[num++]);
                double num16 = Num(list[num++]);
                if (c == 't')
                {
                    num15 += val.X;
                    num16 += val.Y;
                }
                Point val7 = new Point(2.0 * val.X - val3.X, 2.0 * val.Y - val3.Y);
                Point val8 = new Point(num15, num16);
                streamGeometryContext.QuadraticBezierTo(val7, val8, isStroked: true, isSmoothJoin: false);
                val3 = val7;
                val = val8;
                break;
            }
            case 'S':
            case 's':
            {
                if (num + 3 >= list.Count)
                {
                    return streamGeometry;
                }
                double num23 = Num(list[num++]);
                double num24 = Num(list[num++]);
                double num25 = Num(list[num++]);
                double num26 = Num(list[num++]);
                if (c == 's')
                {
                    num23 += val.X;
                    num24 += val.Y;
                    num25 += val.X;
                    num26 += val.Y;
                }
                Point point2 = new Point(2.0 * val.X - val3.X, 2.0 * val.Y - val3.Y);
                Point val11 = new Point(num23, num24);
                Point val12 = new Point(num25, num26);
                streamGeometryContext.BezierTo(point2, val11, val12, isStroked: true, isSmoothJoin: false);
                val3 = val11;
                val = val12;
                break;
            }
            case 'A':
            case 'a':
            {
                if (num + 6 >= list.Count)
                {
                    num = list.Count;
                    break;
                }
                double num2 = Num(list[num++]);
                double num3 = Num(list[num++]);
                double angleDeg = Num(list[num++]);
                double num4 = Num(list[num++]);
                double num5 = Num(list[num++]);
                double num6 = Num(list[num++]);
                double num7 = Num(list[num++]);
                if (c == 'a')
                {
                    num6 += val.X;
                    num7 += val.Y;
                }
                if (num2 < 0.0)
                {
                    num2 = 0.0 - num2;
                }
                if (num3 < 0.0)
                {
                    num3 = 0.0 - num3;
                }
                Point val4 = new Point(num6, num7);
                if (num2 > 1E-06 && num3 > 1E-06)
                {
                    foreach (Bezier item in ArcToBeziers(val, num2, num3, angleDeg, num4 > 0.5, num5 > 0.5, val4))
                    {
                        streamGeometryContext.BezierTo(item.C1, item.C2, item.End, isStroked: true, isSmoothJoin: false);
                    }
                }
                else
                {
                    streamGeometryContext.LineTo(val4, isStroked: true, isSmoothJoin: false);
                }
                val = val4;
                break;
            }
            case 'Z':
            case 'z':
                streamGeometryContext.BeginFigure(val2, isFilled: true, isClosed: true);
                val = val2;
                break;
            default:
                num++;
                break;
            }
        }
        return streamGeometry;
    }

    private static List<Bezier> ArcToBeziers(Point p0, double rx, double ry, double angleDeg, bool largeArc, bool sweep, Point p1)
    {

        List<Bezier> list = new List<Bezier>();
        double num = angleDeg * Math.PI / 180.0;
        double cosPhi = Math.Cos(num);
        double sinPhi = Math.Sin(num);
        double num2 = (p0.X - p1.X) / 2.0;
        double num3 = (p0.Y - p1.Y) / 2.0;
        double num4 = cosPhi * num2 + sinPhi * num3;
        double num5 = (0.0 - sinPhi) * num2 + cosPhi * num3;
        double num6 = Math.Abs(rx);
        double num7 = Math.Abs(ry);
        double num8 = num4 * num4 / (num6 * num6) + num5 * num5 / (num7 * num7);
        if (num8 > 1.0)
        {
            double num9 = Math.Sqrt(num8);
            num6 *= num9;
            num7 *= num9;
        }
        double num10 = ((largeArc != sweep) ? 1 : (-1));
        double num11 = num6 * num6 * num7 * num7 - num6 * num6 * num5 * num5 - num7 * num7 * num4 * num4;
        double num12 = num6 * num6 * num5 * num5 + num7 * num7 * num4 * num4;
        double num13 = ((num12 == 0.0) ? 0.0 : (num10 * Math.Sqrt(Math.Max(0.0, num11 / num12))));
        double num14 = num13 * (num6 * num5 / num7);
        double num15 = num13 * ((0.0 - num7) * num4 / num6);
        double num16 = cosPhi * num14 - sinPhi * num15 + (p0.X + p1.X) / 2.0;
        double num17 = sinPhi * num14 + cosPhi * num15 + (p0.Y + p1.Y) / 2.0;
        double num18 = Angle(1.0, 0.0, (num4 - num14) / num6, (num5 - num15) / num7);
        double num19 = Angle((num4 - num14) / num6, (num5 - num15) / num7, (0.0 - num4 - num14) / num6, (0.0 - num5 - num15) / num7);
        if (!sweep && num19 > 0.0)
        {
            num19 -= Math.PI * 2.0;
        }
        else if (sweep && num19 < 0.0)
        {
            num19 += Math.PI * 2.0;
        }
        int num20 = (int)Math.Ceiling(Math.Abs(num19) / (Math.PI / 2.0));
        if (num20 < 1)
        {
            num20 = 1;
        }
        double num21 = num19 / (double)num20;
        double num22 = 1.3333333333333333 * Math.Tan(num21 / 4.0);
        double num23 = num18;
        Point val = p0;
        for (int i = 0; i < num20; i++)
        {
            double num24 = num23 + num21;
            double num25 = Math.Cos(num23);
            double num26 = Math.Sin(num23);
            double num27 = Math.Cos(num24);
            double num28 = Math.Sin(num24);
            double num29 = cosPhi * num6 * num27 - sinPhi * num7 * num28 + num16;
            double num30 = sinPhi * num6 * num27 + cosPhi * num7 * num28 + num17;
            double num31 = Math.Sin(num21) * (Math.Sqrt(4.0 + 3.0 * num22 * num22) - 1.0) / 3.0;
            double x = (0.0 - num6) * num26;
            double y = num7 * num25;
            double x2 = (0.0 - num6) * num28;
            double y2 = num7 * num27;
            Point c = new Point(val.X + num31 * RotX(x, y), val.Y + num31 * RotY(x, y));
            Point c2 = new Point(num29 - num31 * RotX(x2, y2), num30 - num31 * RotY(x2, y2));
            list.Add(new Bezier(c, c2, new Point(num29, num30)));
            val = new Point(num29, num30);
            num23 = num24;
        }
        return list;
        static double Angle(double ux, double uy, double vx, double vy)
        {
            double num32 = ux * vx + uy * vy;
            double num33 = Math.Sqrt((ux * ux + uy * uy) * (vx * vx + vy * vy));
            double num34 = Math.Acos(Math.Clamp((num33 == 0.0) ? 1.0 : (num32 / num33), -1.0, 1.0));
            if (!(ux * vy - uy * vx < 0.0))
            {
                return num34;
            }
            return 0.0 - num34;
        }
        double RotX(double num32, double num33)
        {
            return cosPhi * num32 - sinPhi * num33;
        }
        double RotY(double num32, double num33)
        {
            return sinPhi * num32 + cosPhi * num33;
        }
    }

    public static Geometry? ParsePathForTrace(string? d)
    {
        return ParsePath(d);
    }

    public static List<string> TokenizeForTrace(string d)
    {
        return Tokenize(d);
    }

    private static bool IsCommand(string token)
    {
        if (token.Length == 1)
        {
            return char.IsLetter(token[0]);
        }
        return false;
    }

    private static double Num(string token)
    {
        if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var result))
        {
            return 0.0;
        }
        return result;
    }

    private static List<string> Tokenize(string d)
    {
        List<string> list = new List<string>();
        int i = 0;
        while (i < d.Length)
        {
            char c = d[i];
            if (char.IsLetter(c))
            {
                list.Add(c.ToString());
                i++;
                continue;
            }
            if (char.IsWhiteSpace(c) || c == ',')
            {
                i++;
                continue;
            }
            int num = i;
            char c2 = d[i];
            if ((c2 == '+' || c2 == '-') ? true : false)
            {
                i++;
            }
            for (; i < d.Length && char.IsDigit(d[i]); i++)
            {
            }
            if (i < d.Length && d[i] == '.')
            {
                for (i++; i < d.Length && char.IsDigit(d[i]); i++)
                {
                }
            }
            if (i < d.Length && (d[i] == 'e' || d[i] == 'E'))
            {
                int num2 = i;
                i++;
                if (i < d.Length && (d[i] == '-' || d[i] == '+'))
                {
                    i++;
                }
                if (i < d.Length && char.IsDigit(d[i]))
                {
                    for (; i < d.Length && char.IsDigit(d[i]); i++)
                    {
                    }
                }
                else
                {
                    i = num2;
                }
            }
            if (i > num)
            {
                int num3 = num;
                list.Add(d.Substring(num3, i - num3));
            }
            else
            {
                i++;
            }
        }
        return list;
    }
}
