using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using CapsLockPlus.Core;
using CapsLockPlus.Core.Gestures;
using CapsLockPlus.Core.Input;

namespace CapsLockPlus.UI.Windows;

/// <summary>
/// 录制一条手势：在一个专门的窗口里画一笔，窗口自己收鼠标事件。
///
/// 为什么不复用全局钩子录制：设置页点「录制」之后，用户要把焦点切到
/// 别的窗口上画，焦点一变、按钮就丢了；而且录完还得判断"刚才那颗是侧键
/// 还是中键"，全靠猜。在这里画，按的是哪颗键、轨迹什么样，都是确定的。
///
/// 侧键（XButton1/2）WPF 的 MouseDown 不一定报，所以额外挂了一个窗口消息钩子
/// 收 WM_XBUTTONDOWN / WM_XBUTTONUP，两条路共用同一套开始/结束逻辑。
/// </summary>
public partial class GestureRecordWindow : Window
{
    private const int WM_XBUTTONDOWN = 0x020B;
    private const int WM_XBUTTONUP = 0x020C;

    private readonly List<GestureMath.Pt> _points = new();
    private GesturePattern.Tracker? _tracker;
    private int _vk;
    private bool _tracking;
    private HwndSource? _source;

    /// <summary>画完之后的结果；没画或点取消时是 null。</summary>
    public RecordedGesture? Result { get; private set; }

    /// <summary>采样参数，由设置页传入当前的 GestureOptions。</summary>
    public GestureOptions Options { get; set; } = new();

    public GestureRecordWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        Closed += (_, _) => DetachHook();
        KeyDown += OnKeyDown;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _source = PresentationSource.FromVisual(this) as HwndSource;
        _source?.AddHook(WndProc);
    }

    private void DetachHook()
    {
        try { _source?.RemoveHook(WndProc); } catch { }
        _source = null;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg is WM_XBUTTONDOWN or WM_XBUTTONUP)
        {
            int which = (int)((wParam.ToInt64() >> 16) & 0xFFFF);
            int vk = which == 2 ? 0x06 : 0x05;     // XBUTTON1=1 / XBUTTON2=2

            if (msg == WM_XBUTTONDOWN)
            {
                if (!_tracking) StartAt(vk, ScreenToPad(lParam));
                handled = true;
            }
            else
            {
                if (_tracking && _vk == vk) Finish();
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    private void OnPadDown(object sender, MouseButtonEventArgs e)
    {
        if (_tracking) return;
        int vk = e.ChangedButton switch
        {
            MouseButton.Left => 0x01,
            MouseButton.Right => 0x02,
            MouseButton.Middle => 0x04,
            MouseButton.XButton1 => 0x05,
            MouseButton.XButton2 => 0x06,
            _ => 0
        };
        if (vk == 0) return;

        StartAt(vk, e.GetPosition(Pad));
        Pad.CaptureMouse();
        e.Handled = true;
    }

    private void OnPadMove(object sender, MouseEventArgs e)
    {
        if (!_tracking) return;
        Add(e.GetPosition(Pad));
    }

    private void OnPadUp(object sender, MouseButtonEventArgs e)
    {
        if (!_tracking) return;
        int vk = e.ChangedButton switch
        {
            MouseButton.Left => 0x01,
            MouseButton.Right => 0x02,
            MouseButton.Middle => 0x04,
            MouseButton.XButton1 => 0x05,
            MouseButton.XButton2 => 0x06,
            _ => 0
        };
        if (vk != _vk) return;

        if (Pad.IsMouseCaptured) Pad.ReleaseMouseCapture();
        Finish();
        e.Handled = true;
    }

    private void OnPadLostCapture(object sender, MouseEventArgs e)
    {
        if (_tracking) Finish();
    }

    // ───────────────────────── 采样 ─────────────────────────

    private void StartAt(int vk, Point p)
    {
        var opts = Options;
        _points.Clear();
        _vk = vk;
        _tracking = true;
        Result = null;
        UseButton.IsEnabled = false;

        var start = new GestureMath.Pt(p.X, p.Y);
        _points.Add(start);
        _tracker = new GesturePattern.Tracker(opts.ActivationDistance, opts.TurnTolerance, opts.MaxTurnDistance);
        _tracker.Start(p.X, p.Y);

        TrailLine.Points.Clear();
        TrailLine.Points.Add(p);
        PadHint.Visibility = Visibility.Collapsed;
        LiveBadge.Visibility = Visibility.Visible;
        LiveButton.Text = ButtonName(vk);
        LivePattern.Text = "";
        StatusText.Text = Loc.T("画吧，松手就识别。");

        /* 侧键走的是窗口消息，不 Capture 的话鼠标一离开画板就收不到抬起，
         * 状态会卡在"正在画"。CaptureMouse 之后抬起一定回到这里。 */
        if (!Pad.IsMouseCaptured) Pad.CaptureMouse();
    }

    private void Add(Point p)
    {
        if (_points.Count > 0)
        {
            var last = _points[^1];
            double dx = p.X - last.X, dy = p.Y - last.Y;
            if (dx * dx + dy * dy < 2.0) return;      // 1.4px 以下的抖动不记
        }

        _points.Add(new GestureMath.Pt(p.X, p.Y));
        TrailLine.Points.Add(p);

        if (_tracker != null && _tracker.Move(p.X, p.Y))
            LivePattern.Text = string.Join(" ", GesturePattern.Arrows(_tracker.Pattern));
    }

    private void Finish()
    {
        _tracking = false;
        if (Pad.IsMouseCaptured) Pad.ReleaseMouseCapture();

        var opts = Options;
        double extent = GestureMath.Extent(_points);
        string pattern = _tracker?.Pattern ?? "";
        if (string.IsNullOrEmpty(pattern) && _points.Count > 1)
            pattern = GesturePattern.FromPoints(_points, opts.ActivationDistance, opts.TurnTolerance, opts.MaxTurnDistance);

        LivePattern.Text = string.IsNullOrEmpty(pattern)
            ? ""
            : string.Join(" ", GesturePattern.Arrows(pattern));

        if (extent < 24 || pattern.Length == 0)
        {
            Result = null;
            UseButton.IsEnabled = false;
            StatusText.Text = Loc.T("这一笔太短了（或者只是点了一下）。再画长一点，至少甩出二三十像素。");
            return;
        }

        List<int> flat = GestureMath.ToFlat(GestureMath.Normalize(_points));
        Result = new RecordedGesture(ButtonNameValue(_vk), flat, pattern);
        UseButton.IsEnabled = true;
        StatusText.Text = Loc.F("认出来了：{0} · {1}　——　点「用这一笔」填进手势。",
            ButtonName(_vk), string.Join(" ", GesturePattern.Arrows(pattern)));
    }

    // ───────────────────────── 按钮 ─────────────────────────

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        _tracking = false;
        Result = null;
        _points.Clear();
        _tracker = null;
        TrailLine.Points.Clear();
        LiveBadge.Visibility = Visibility.Collapsed;
        PadHint.Visibility = Visibility.Visible;
        UseButton.IsEnabled = false;
        StatusText.Text = Loc.T("重画：按住鼠标键，在框里再画一笔。");
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        Result = null;
        DialogResult = false;
    }

    private void OnUseClick(object sender, RoutedEventArgs e)
    {
        if (Result == null) return;
        DialogResult = true;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Result = null;
            DialogResult = false;
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && Result != null)
        {
            DialogResult = true;
            e.Handled = true;
        }
    }

    // ───────────────────────── 小工具 ─────────────────────────

    private static string ButtonName(int vk)
        => Loc.T(GestureButtons.Display(GestureButtons.FromVk(vk)));

    private static string ButtonNameValue(int vk) => GestureButtons.FromVk(vk);

    private Point ScreenToPad(IntPtr lParam)
    {
        int x = unchecked((short)(lParam.ToInt64() & 0xFFFF));
        int y = unchecked((short)((lParam.ToInt64() >> 16) & 0xFFFF));
        try { return Pad.PointFromScreen(new Point(x, y)); }
        catch { return new Point(0, 0); }
    }
}

/// <summary>录制结果：起始键（right/middle/x1/x2…）、归一化轨迹、方向串。</summary>
public sealed record RecordedGesture(string Button, List<int> Path, string Pattern);
