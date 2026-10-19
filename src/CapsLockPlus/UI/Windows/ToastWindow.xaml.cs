using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace CapsLockPlus.UI.Windows;

/// <summary>
/// 轻提示：屏幕右下角浮出一条消息，短暂停留后自动淡出。
///
/// 两种用途：
///   * 普通提示（"已复制到剪贴板"）
///   * 动作反馈（"左移"）—— 用来确认组合键有没有被识别
/// </summary>
public partial class ToastWindow : Window
{
    private static ToastWindow? _current;
    private static DispatcherTimer? _timer;

    /// <summary>待显示的消息队列（同一时刻只显示一条，避免叠一堆窗口）。</summary>
    private static readonly Queue<(string Text, string Kind)> Pending = new();

    public ToastWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    public static void Show(string message) => Show(message, "info");

    public static void Show(string message, string kind)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        if (Application.Current?.Dispatcher is not { } dispatcher) return;

        // 统一调度到界面线程：调用方可能来自钩子线程或动作线程
        if (!dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => Show(message, kind));
            return;
        }

        if (_current != null)
        {
            // 已经在显示：同一条消息忽略，不同消息排队
            if (_current.Message.Text == message) return;
            if (Pending.Count < 8) Pending.Enqueue((message, kind));
            return;
        }

        var w = new ToastWindow();
        Apply(w, message, kind);
        _current = w;
        w.Show();
    }

    private static void Apply(ToastWindow w, string message, string kind)
    {
        w.Message.Text = message;
        if (kind == "feedback")
        {
            // 反馈样式：更小、更淡、带强调色左边框，不干扰视线
            w.Card.Padding = new Thickness(12, 8, 14, 8);
            w.Card.Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x16, 0x1A, 0x23));
            w.IconBox.Visibility = Visibility.Collapsed;
            w.Message.FontSize = 12.5;
            w.Message.Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xEF, 0xFF));
        }
        else
        {
            w.Card.Padding = new Thickness(16, 11, 16, 11);
            w.IconBox.Visibility = Visibility.Visible;
            w.Message.FontSize = 13;
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Position();

        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(130));
        BeginAnimation(OpacityProperty, fadeIn);

        // 反馈类提示停留短一些，普通提示长一些
        int holdMs = Card.Padding.Left < 14 ? 900 : 1800;

        _timer?.Stop();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(holdMs) };
        _timer.Tick += (_, _) =>
        {
            _timer!.Stop();
            var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(170));
            fadeOut.Completed += (_, _) => CloseCurrent();
            BeginAnimation(OpacityProperty, fadeOut);
        };
        _timer.Start();
    }

    private void Position()
    {
        var wa = SystemParameters.WorkArea;
        // 内容测量完再定位，避免第一次显示时尺寸还是 0
        UpdateLayout();
        Left = wa.Right - ActualWidth - 10;
        Top = wa.Bottom - ActualHeight - 10;
    }

    private static void CloseCurrent()
    {
        var closing = _current;
        _current = null;
        try { closing?.Close(); } catch { }

        if (Pending.Count > 0)
        {
            var next = Pending.Dequeue();
            Show(next.Text, next.Kind);
        }
    }
}
