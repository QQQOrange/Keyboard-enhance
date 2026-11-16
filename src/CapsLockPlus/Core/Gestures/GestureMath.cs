using System;
using System.Collections.Generic;
using System.Linq;

namespace CapsLockPlus.Core.Gestures;

/// <summary>
/// 手势的几何计算：归一化、重采样、形状距离。
///
/// 一律用 double 计算，最后落盘时才转成 int —— 归一化到 0~1000 的整数
/// 精度足够（1 个单位 ≈ 千分之一屏宽），但中间过程用整数会把形状磨圆。
/// </summary>
public static class GestureMath
{
    public readonly record struct Pt(double X, double Y)
    {
        public static Pt operator -(Pt a, Pt b) => new(a.X - b.X, a.Y - b.Y);
        public static Pt operator +(Pt a, Pt b) => new(a.X + b.X, a.Y + b.Y);
        public double Length => Math.Sqrt(X * X + Y * Y);
        public Pt Scale(double k) => new(X * k, Y * k);
    }

    /// <summary>归一化的目标尺寸：最长边缩放到这个值。</summary>
    public const int Size = 1000;

    /// <summary>
    /// 把原始轨迹平移到原点、等比缩放到最长边 = <see cref="Size"/>。
    ///
    /// **不**做旋转/镜像对齐：手势的方向是语义的一部分，
    /// "向上划"和"向下划"必须是两条不同的手势。
    /// </summary>
    public static List<Pt> Normalize(IReadOnlyList<Pt> raw)
    {
        var result = new List<Pt>();
        if (raw.Count == 0) return result;

        Pt origin = raw[0];
        double max = 0;
        foreach (var p in raw)
        {
            double dx = Math.Abs(p.X - origin.X);
            double dy = Math.Abs(p.Y - origin.Y);
            if (dx > max) max = dx;
            if (dy > max) max = dy;
        }

        // 原地画一个点：没有方向，全部压到原点，交给"单击"分支处理。
        if (max < 0.001)
        {
            foreach (var _ in raw) result.Add(new Pt(0, 0));
            return result;
        }

        double k = Size / max;
        foreach (var p in raw)
            result.Add(new Pt((p.X - origin.X) * k, (p.Y - origin.Y) * k));
        return result;
    }

    /// <summary>归一化并按 <paramref name="count"/> 等距重采样（按弧长）。</summary>
    public static List<Pt> Resample(IReadOnlyList<Pt> points, int count = 32)
    {
        var result = new List<Pt>(count);
        if (points.Count == 0) return result;
        if (points.Count == 1 || count <= 1)
        {
            for (int i = 0; i < count; i++) result.Add(points[0]);
            return result;
        }

        var cumulative = new double[points.Count];
        double total = 0;
        for (int i = 1; i < points.Count; i++)
        {
            total += (points[i] - points[i - 1]).Length;
            cumulative[i] = total;
        }

        if (total < 0.001)
        {
            for (int i = 0; i < count; i++) result.Add(points[0]);
            return result;
        }

        double step = total / (count - 1);
        result.Add(points[0]);
        int seg = 1;
        for (int i = 1; i < count - 1; i++)
        {
            double target = step * i;
            while (seg < points.Count - 1 && cumulative[seg] < target) seg++;
            double segStart = cumulative[seg - 1];
            double segLen = cumulative[seg] - segStart;
            double t = segLen < 0.001 ? 0 : (target - segStart) / segLen;
            if (t < 0) t = 0;
            if (t > 1) t = 1;
            var a = points[seg - 1];
            var b = points[seg];
            result.Add(new Pt(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t));
        }
        result.Add(points[^1]);
        return result;
    }

    /// <summary>
    /// 两条轨迹的平均偏差（0 = 完全一样，1 ≈ 差了整整一个手势尺寸）。
    ///
    /// 输入必须已经归一化；这里只负责重采样后逐点比距离。
    /// 不做 DTW —— 手势笔画通常一笔到底、点数稳定，DTW 的容错反而会让
    /// "L 形"和"斜线"被认成同一条。真正的鲁棒性来自"起点对齐 + 等比缩放"。
    /// </summary>
    public static double Distance(IReadOnlyList<Pt> a, IReadOnlyList<Pt> b, int samples = 32)
    {
        if (a.Count == 0 || b.Count == 0) return 1;
        var ra = Resample(a, samples);
        var rb = Resample(b, samples);
        double sum = 0;
        for (int i = 0; i < samples; i++) sum += (ra[i] - rb[i]).Length;
        return Math.Min(1, sum / samples / Size);
    }

    /// <summary>原始轨迹的包围盒对角线长度（像素），用来判断"这算不算画了一笔"。</summary>
    public static double Extent(IReadOnlyList<Pt> points)
    {
        if (points.Count < 2) return 0;
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        foreach (var p in points)
        {
            if (p.X < minX) minX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.X > maxX) maxX = p.X;
            if (p.Y > maxY) maxY = p.Y;
        }
        double dx = maxX - minX, dy = maxY - minY;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>扁平化 + 四舍五入，用于落盘。</summary>
    public static List<int> ToFlat(IReadOnlyList<Pt> points)
    {
        var flat = new List<int>(points.Count * 2);
        foreach (var p in points)
        {
            flat.Add((int)Math.Round(p.X));
            flat.Add((int)Math.Round(p.Y));
        }
        return flat;
    }

    /// <summary>
    /// 找出与 <paramref name="path"/> 最像的一条手势。
    /// 返回 (手势, 分数)，没有低于阈值时为 (null, 最佳分数)。
    /// </summary>
    public static (MouseGesture? Best, double Score) Match(
        IEnumerable<MouseGesture> candidates, IReadOnlyList<Pt> path, double threshold)
    {
        MouseGesture? best = null;
        double bestScore = double.MaxValue;
        var normalized = Normalize(path);

        foreach (var g in candidates)
        {
            if (!g.HasPath) continue;
            double score = Distance(normalized, g.Points());
            if (score < bestScore)
            {
                bestScore = score;
                best = g;
            }
        }

        return bestScore <= threshold ? (best, bestScore) : (null, bestScore);
    }
}
