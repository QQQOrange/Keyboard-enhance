using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace CapsLockPlus.UI.Windows;

/// <summary>
/// 手势轨迹覆盖层：铺满整个虚拟屏幕的一张透明画布。
///
/// 三条硬约束（少一条就会毁掉手感）：
/// <list type="number">
/// <item>**不能抢焦点** —— 画手势时前台窗口必须保持原样，否则右键菜单、
/// 输入焦点全会丢。靠 WS_EX_NOACTIVATE + ShowActivated=false 保证。</item>
/// <item>**不能挡点击** —— 它盖在所有窗口上面，必须 WS_EX_TRANSPARENT 点击穿透，
/// 否则用户画完手势、下一次点击就点在覆盖层上了。</item>
/// <item>**坐标要按像素换算** —— 钩子给的是物理像素，WPF 用的是 DIP；
/// 高分屏下不换算，轨迹会缩到左上角四分之一。</item>
/// </list>
/// </summary>
public sealed partial class GestureOverlayWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;

    private double _scale = 1;

    public GestureOverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE,
            ex | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        _scale = GetDpiForWindow(hwnd) / 96.0;
        if (_scale <= 0.1) _scale = 1;
    }

    /// <summary>物理像素 → 画布坐标（DIP，相对虚拟屏幕左上角）。</summary>
    private Point ToCanvas(double px, double py)
        => new(px / _scale - SystemParameters.VirtualScreenLeft,
               py / _scale - SystemParameters.VirtualScreenTop);

    public void Begin(string color, double thickness)
    {
        Trail.Points.Clear();
        LabelBox.Visibility = Visibility.Collapsed;
        try { Trail.Stroke = new BrushConverter().ConvertFromString(color) as Brush ?? Brushes.DodgerBlue; }
        catch { Trail.Stroke = Brushes.DodgerBlue; }
        Trail.StrokeThickness = thickness <= 0 ? 3 : thickness;
    }

    public void AddPoint(double px, double py)
    {
        var p = ToCanvas(px, py);
        Trail.Points.Add(p);
    }

    public void ReplaceLast(double px, double py)
    {
        if (Trail.Points.Count == 0) { AddPoint(px, py); return; }
        Trail.Points[^1] = ToCanvas(px, py);
    }

    /// <summary>在轨迹末端显示识别结果（"关闭窗口"这类），过一会儿由服务收起。</summary>
    public void ShowLabel(string text, double px, double py)
    {
        LabelText.Text = text;
        LabelBox.Visibility = Visibility.Visible;
        LabelBox.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = LabelBox.DesiredSize;
        var p = ToCanvas(px, py);
        double x = Math.Min(Math.Max(0, p.X + 18), Math.Max(0, Width - size.Width - 4));
        double y = Math.Min(Math.Max(0, p.Y + 18), Math.Max(0, Height - size.Height - 4));
        Canvas.SetLeft(LabelBox, x);
        Canvas.SetTop(LabelBox, y);
    }

    public void Clear()
    {
        Trail.Points.Clear();
        LabelBox.Visibility = Visibility.Collapsed;
    }

    /// <summary>让窗口"存在但不打扰"：不激活、不进 Alt+Tab、不抢鼠标。</summary>
    public void ShowPassive()
    {
        if (!IsVisible) Show();
        // 每次显示都重新压到最上层：别的程序可能刚好也置顶了。
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
            SetWindowPos(hwnd, new IntPtr(-1), 0, 0, 0, 0,
                0x0001 | 0x0002 | 0x0010); // NOSIZE | NOMOVE | NOACTIVATE
    }

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
}
