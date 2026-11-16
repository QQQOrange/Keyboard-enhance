using System;
using System.Collections.Generic;
using System.Text;

namespace CapsLockPlus.Core.Gestures;

/// <summary>
/// 方向识别：把鼠标轨迹压成 <c>↑→↓←</c> 这样的方向串。
///
/// 这套算法照着 FlowMouse 的 <c>js/gesture-recognizer.js</c> 实现，原因很实在：
/// 手势是"画方向"，不是"描形状"。按形状做最小二乘匹配，用户画得快一点、
/// 歪一点、长短差一倍就容易判不中；按方向串匹配，只要拐的弯对就一定中，
/// 而且方向串还能直接显示在界面上（↑→），用户一眼就知道自己配的是什么。
///
/// 三段阈值：
///  · 起手距离：走出这么多像素才算"开始画"，避免右键单击被当成手势；
///  · 自适应转折：换方向要越过 <c>min(最大转折, 起手距离 + 上一段长度 × 转折系数)</c>，
///    上一段越长越不容易被一个抖动带偏（长笔画上的小抖动不该多出一段）；
///  · 最大转折：防止长笔画怎么抖都切不了方向。
/// </summary>
public static class GesturePattern
{
    /// <summary>通配：任何方向都算命中（FlowMouse 里的 <c>*</c>）。</summary>
    public const string Wildcard = "*";

    public const double DefaultActivation = 20;
    public const double DefaultTurnTolerance = 0.10;
    public const double DefaultMaxTurn = 120;

    /// <summary>把位移归到四个方向。斜着走时看哪个轴占优 —— 和 FlowMouse 一致。</summary>
    public static string Direction(double dx, double dy)
    {
        double ax = Math.Abs(dx), ay = Math.Abs(dy);
        if (ax > ay) return dx > 0 ? "→" : "←";
        return dy > 0 ? "↓" : "↑";
    }

    /// <summary>把方向串拆成箭头数组，界面上排版用。</summary>
    public static string[] Arrows(string? pattern)
    {
        if (string.IsNullOrEmpty(pattern)) return Array.Empty<string>();
        var list = new List<string>(pattern.Length);
        foreach (char c in pattern)
        {
            if (c is '↑' or '↓' or '←' or '→') list.Add(c.ToString());
            else if (c == '*') list.Add("*");
        }
        return list.ToArray();
    }

    /// <summary>方向串的可读描述，例如“上 → 右”。空串返回空。</summary>
    public static string Describe(string? pattern)
    {
        var arrows = Arrows(pattern);
        if (arrows.Length == 0) return "";
        var sb = new StringBuilder();
        foreach (var a in arrows)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(a switch
            {
                "↑" => "↑",
                "↓" => "↓",
                "←" => "←",
                "→" => "→",
                _ => "*",
            });
        }
        return sb.ToString();
    }

    /// <summary>从一整条轨迹一次性算方向串（导入、老数据迁移用）。</summary>
    public static string FromPoints(
        IReadOnlyList<GestureMath.Pt> points,
        double activation = DefaultActivation,
        double turnTolerance = DefaultTurnTolerance,
        double maxTurn = DefaultMaxTurn)
    {
        if (points == null || points.Count == 0) return "";
        var tracker = new Tracker(activation, turnTolerance, maxTurn);
        tracker.Start(points[0].X, points[0].Y);
        for (int i = 1; i < points.Count; i++)
            tracker.Move(points[i].X, points[i].Y);
        return tracker.Pattern;
    }

    /// <summary>
    /// 从"已归一化到 1000×1000"的存档路径推算方向串。
    /// 这类路径的最大边固定是 1000，所以阈值按比例给：起手 50、最大转折 300，
    /// 对应真实屏幕上一笔 400px 左右的手势（20px / 120px）。
    /// </summary>
    public static string FromNormalized(IReadOnlyList<GestureMath.Pt> path)
        => FromPoints(path, activation: 50, turnTolerance: DefaultTurnTolerance, maxTurn: 300);

    /// <summary>
    /// 实时追踪器：每来一个鼠标位置喂一次，内部维护当前方向串。
    /// 只在钩子采样线程里用；不要跨线程共享。
    /// </summary>
    public sealed class Tracker
    {
        private readonly double _activation;
        private readonly double _turnTolerance;
        private readonly double _maxTurn;
        private readonly List<string> _pattern = new();
        private readonly List<(double X, double Y)> _history = new();

        private bool _active;
        private double _startX, _startY;
        private double _anchorX, _anchorY;
        private double _lastX, _lastY;
        private double _segmentLength;

        public Tracker(
            double activation = DefaultActivation,
            double turnTolerance = DefaultTurnTolerance,
            double maxTurn = DefaultMaxTurn)
        {
            _activation = Math.Clamp(activation, 4, 400);
            _turnTolerance = Math.Clamp(turnTolerance, 0, 1);
            _maxTurn = Math.Clamp(maxTurn, _activation, 2000);
        }

        public bool Active => _active;

        /// <summary>当前方向串，例如 <c>↑→</c>；没起手时是空串。</summary>
        public string Pattern => string.Concat(_pattern);

        /// <summary>最后一段的方向；没起手时是空串。</summary>
        public string LastDirection => _pattern.Count > 0 ? _pattern[^1] : "";

        public double TotalDistance { get; private set; }

        public void Start(double x, double y)
        {
            _active = false;
            _startX = _anchorX = _lastX = x;
            _startY = _anchorY = _lastY = y;
            _segmentLength = 0;
            TotalDistance = 0;
            _pattern.Clear();
            _history.Clear();
            _history.Add((x, y));
        }

        /// <summary>
        /// 喂一个采样点。返回 true 表示这一下让方向串变长了（界面可以据此刷新）。
        /// </summary>
        public bool Move(double x, double y)
        {
            _lastX = x;
            _lastY = y;
            if (_history.Count < 600) _history.Add((x, y));

            double dxTotal = x - _startX, dyTotal = y - _startY;
            TotalDistance = Math.Sqrt(dxTotal * dxTotal + dyTotal * dyTotal);

            bool changed = false;

            if (!_active)
            {
                if (TotalDistance <= _activation) return false;

                _active = true;

                /* 起手前那一段也要算进方向串，否则"先斜着甩出去再拐弯"会丢第一段。
                 * 重放阈值比起手距离略小（FlowMouse 用 0.7 / 0.8），避免第一段被吞。 */
                double replay = _activation * (_activation < 15 ? 0.8 : 0.7);
                _pattern.Clear();
                _anchorX = _startX;
                _anchorY = _startY;
                _segmentLength = 0;

                foreach (var (hx, hy) in _history)
                {
                    double dx = hx - _anchorX, dy = hy - _anchorY;
                    double dist = Math.Sqrt(dx * dx + dy * dy);
                    if (dist <= replay) continue;

                    string dir = Direction(dx, dy);
                    if (_pattern.Count == 0 || dir != _pattern[^1])
                    {
                        _pattern.Add(dir);
                        _anchorX = hx;
                        _anchorY = hy;
                        _segmentLength = dist;
                    }
                    else
                    {
                        _segmentLength += dist;
                        _anchorX = hx;
                        _anchorY = hy;
                    }
                }

                if (_pattern.Count == 0)
                {
                    _pattern.Add(Direction(dxTotal, dyTotal));
                    _anchorX = x;
                    _anchorY = y;
                    _segmentLength = TotalDistance;
                }

                return true;
            }

            double ddx = x - _anchorX, ddy = y - _anchorY;
            double distance = Math.Sqrt(ddx * ddx + ddy * ddy);
            if (distance <= _activation) return false;

            string direction = Direction(ddx, ddy);
            if (_pattern.Count > 0 && direction == _pattern[^1])
            {
                _segmentLength += distance;
                _anchorX = x;
                _anchorY = y;
                return false;
            }

            double threshold = Math.Min(_maxTurn, _activation + _segmentLength * _turnTolerance);
            if (distance <= threshold) return false;

            _pattern.Add(direction);
            _segmentLength = distance;
            _anchorX = x;
            _anchorY = y;
            changed = true;
            return changed;
        }
    }
}
