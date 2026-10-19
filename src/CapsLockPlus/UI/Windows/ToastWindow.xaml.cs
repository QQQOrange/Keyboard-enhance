using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace CapsLockPlus.UI.Windows;

/// <summary>轻提示：屏幕右下角浮出一条消息，2 秒后自动淡出。</summary>
public partial class ToastWindow : Window
{
    private static ToastWindow? _current;
    private static DispatcherTimer? _timer;
    private static readonly List<string> Queue = new();

    public ToastWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    public static void Show(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        if (Application.Current?.Dispatcher is not { } d) return;

        if (_current != null)
        {
            // 复用同一个窗口，避免频繁创建
            if (_current.Message.Text == message) return;
            if (Queue.Count < 6) Queue.Add(message);
            return;
        }

        _current = new ToastWindow();
        _current.Message.Text = message;
        _current.Show();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var wa = SystemParameters.WorkArea;
        Left = wa.Right - ActualWidth - 8;
        Top = wa.Bottom - ActualHeight - 8;

        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140));
        BeginAnimation(OpacityProperty, fade);

        _timer?.Stop();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1900) };
        _timer.Tick += (_, _) =>
        {
            _timer!.Stop();
            var outFade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(180));
            outFade.Completed += (_, _) => CloseCurrent();
            BeginAnimation(OpacityProperty, outFade);
        };
        _timer.Start();
    }

    private static void CloseCurrent()
    {
        try { _current?.Close(); } catch { }
        _current = null;
        if (Queue.Count > 0)
        {
            string next = Queue[0];
            Queue.RemoveAt(0);
            Show(next);
        }
    }
}
