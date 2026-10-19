using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using CapsLockPlus.Core;
using CapsLockPlus.Core.Interop;
using CapsLockPlus.Core.Windows;
using NativeMethods = CapsLockPlus.Core.Interop.NativeMethods;

namespace CapsLockPlus.UI.Windows;

/// <summary>
/// 十字准星窗口点选器。
///
/// 打开后是一层半透明遮罩，鼠标变成十字准星；
/// 鼠标经过哪个窗口就在它上面画一个高亮框并显示标题，
/// 点一下即选中该窗口，按 Esc 取消。
///
/// 实现要点：
///   * 遮罩窗口自己也在鼠标底下，所以必须用 WindowFromPoint 再往上找
///     根窗口，并排除本进程自己的窗口；
///   * 用 SetWindowPos 把本窗口置顶且不抢焦点（否则鼠标位置读不到真实目标）。
/// </summary>
public partial class WindowPickerWindow : Window
{
    /// <summary>用户选中的窗口句柄；取消时为 IntPtr.Zero。</summary>
    public IntPtr PickedWindow { get; private set; }

    private IntPtr _selfHandle;

    public WindowPickerWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        PreviewKeyDown += OnKeyDown;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _selfHandle = new WindowInteropHelper(this).Handle;
        // 确保遮罩在最上层但不抢输入焦点，这样鼠标坐标才是桌面真实坐标
        var helper = new WindowInteropHelper(this);
        NativeMethods.SetWindowPos(helper.Handle, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        Activate();
        Focus();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            PickedWindow = IntPtr.Zero;
            Close();
        }
    }

    /// <summary>取鼠标下方、不属于本程序的根窗口。</summary>
    private IntPtr ResolveTargetUnderCursor(out NativeMethods.POINT pt)
    {
        NativeMethods.GetCursorPos(out pt);
        var hwnd = NativeMethods.WindowFromPoint(pt);
        if (hwnd == IntPtr.Zero) return IntPtr.Zero;

        // 找到根窗口（顶层窗口）
        var root = NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT);
        if (root == IntPtr.Zero) root = hwnd;

        // 跳过自己和自己的子窗口
        if (root == _selfHandle) return IntPtr.Zero;
        if (NativeMethods.GetAncestor(_selfHandle, NativeMethods.GA_ROOT) == root) return IntPtr.Zero;

        // 跳过不可见 / 桌面 / 任务栏等
        if (!NativeMethods.IsWindowVisible(root)) return IntPtr.Zero;
        var info = WindowService.Describe(root);
        if (info == null) return IntPtr.Zero;
        if (info.ExeName.Equals(AppInfo.ExeName, StringComparison.OrdinalIgnoreCase)) return IntPtr.Zero;
        if (info.ClassName is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
            return IntPtr.Zero;

        return root;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        var target = ResolveTargetUnderCursor(out var pt);
        if (target == IntPtr.Zero)
        {
            Highlight.Visibility = Visibility.Collapsed;
            HoverInfo.Visibility = Visibility.Collapsed;
            return;
        }

        var info = WindowService.Describe(target);
        var rect = WindowService.GetRect(target);

        // 高亮框：把屏幕坐标换算成这个全屏窗口里的坐标
        var topLeft = PointFromScreen(new Point(rect.Left, rect.Top));
        var bottomRight = PointFromScreen(new Point(rect.Right, rect.Bottom));
        Highlight.Margin = new Thickness(topLeft.X, topLeft.Y,
            Math.Max(0, ActualWidth - bottomRight.X), Math.Max(0, ActualHeight - bottomRight.Y));
        Highlight.Visibility = Visibility.Visible;

        // 提示框跟随鼠标，避免挡在窗口中央
        HoverTitle.Text = string.IsNullOrWhiteSpace(info?.Title) ? "(无标题)" : info!.Title;
        HoverProcess.Text = $"{info?.ExeName}　·　{rect.Width}×{rect.Height}";
        HoverInfo.Visibility = Visibility.Visible;
        double left = Math.Min(pt.X + 18, Math.Max(0, ActualWidth - 460));
        double top = Math.Min(pt.Y + 18, Math.Max(0, ActualHeight - 80));
        HoverInfo.Margin = new Thickness(left, top, 0, 0);
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        var target = ResolveTargetUnderCursor(out _);
        if (target != IntPtr.Zero)
        {
            PickedWindow = target;
            CoreLog.Info($"点选到窗口 0x{target.ToInt64():X}");
        }
        Close();
    }
}
