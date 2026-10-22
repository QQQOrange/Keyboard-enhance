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
///   * 动作反馈（"音量 65%"）—— 用来确认组合键有没有被识别
///
/// ⚠ 这里的核心约束是「跟上按键速度」，所以有两条和普通窗口不一样的设计：
///
///   ① **单例复用**。早先是每条消息 new 一个 Window，再排队逐条显示 ——
///      创建窗口 + 解析 XAML + Show + UpdateLayout 大约 30~80ms，
///      连按十下就在界面线程堆十条，后面的提示要等前面的播完才轮到，
///      最后显示的其实是十几秒前的旧消息。现在只有一条窗口，
///      内容直接替换、计时器重置，不存在"排队等播完"。
///
///   ② **只保留最新一条**。调用方在钩子/动作线程上，消息统一甩到界面线程。
///      如果界面线程正忙，旧写法会让每条消息各排一次 BeginInvoke，
///      忙完一口气全执行（前面的又盖后面的，白干）。现在用一个槽位
///      存"最新待显示消息"，只排一次 dispatch，执行时取走槽里最新的那条 ——
///      中间过时的消息自动被丢弃，这正是"新的立刻覆盖旧的"。
/// </summary>
public partial class ToastWindow : Window
{
    private static ToastWindow? _instance;
    private static DispatcherTimer? _timer;

    /// <summary>待显示消息槽：永远只放最新的一条。</summary>
    private static readonly ToastQueue Slot = new();

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
        if (dispatcher.CheckAccess())
        {
            ShowCore(message, kind);
            return;
        }

        if (!Slot.Post(message, kind)) return;   // 已经排了一个，它会取走最新值

        /* Send 是最高优先级：不排在渲染/布局任务后面。
         * 连按时这一条会插到队首，把上一条还没显示的消息顶掉。 */
        dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(Flush));
    }

    /// <summary>界面线程上取走槽里最新的消息并显示。</summary>
    private static void Flush()
    {
        if (Slot.Take() is { } it) ShowCore(it.Text, it.Kind);
    }

    private static void ShowCore(string message, string kind)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null) return;
        if (!dispatcher.CheckAccess()) { Show(message, kind); return; }

        var w = _instance ??= new ToastWindow();
        Apply(w, message, kind);

        bool wasVisible = w.IsVisible;
        if (!wasVisible) w.Show();

        w.RefreshToast(wasVisible);
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
            w.Card.Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x16, 0x1A, 0x23));
            w.IconBox.Visibility = Visibility.Visible;
            w.Message.FontSize = 13;
            w.Message.Foreground = new SolidColorBrush(Color.FromRgb(0xF2, 0xF5, 0xFA));
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => Position();

    /// <summary>
    /// 显示/更新一条提示：重新量尺寸、贴右下角、重放淡入、重置计时器。
    /// 复用时只有这一段在跑，省掉了建窗口与 XAML 解析。
    /// </summary>
    private void RefreshToast(bool alreadyVisible)
    {
        // 文本变长/变短都会改变窗口宽度，必须重新量一次再定位
        UpdateLayout();
        Position();

        // 清掉进行中的淡出动画，否则它会把刚显示的窗口又淡掉
        BeginAnimation(OpacityProperty, null);
        Opacity = 1;

        /* 已经显示时从 0.55 快速拉回 1：给一个"这条是新的"的视觉脉冲，
         * 又不至于整块闪一下。首次显示从 0 淡入。 */
        var fadeIn = new DoubleAnimation(alreadyVisible ? 0.55 : 0, 1,
            TimeSpan.FromMilliseconds(alreadyVisible ? 80 : 120));
        BeginAnimation(OpacityProperty, fadeIn);

        StartHoldTimer();
    }

    private void StartHoldTimer()
    {
        // 反馈类提示停留短一些，普通提示长一些
        int holdMs = Card.Padding.Left < 14 ? 900 : 1800;

        _timer?.Stop();
        _timer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(holdMs),
        };
        _timer.Tick += (_, _) =>
        {
            _timer!.Stop();
            FadeOutAndHide();
        };
        _timer.Start();
    }

    private void FadeOutAndHide()
    {
        var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(140));
        fadeOut.Completed += (_, _) =>
        {
            // 只隐藏不关闭：下次直接改内容复用，避免重建窗口的开销
            Hide();
            BeginAnimation(OpacityProperty, null);
            Opacity = 1;
        };
        BeginAnimation(OpacityProperty, fadeOut);
    }

    private void Position()
    {
        var wa = SystemParameters.WorkArea;
        // 内容测量完再定位，避免第一次显示时尺寸还是 0
        UpdateLayout();
        Left = wa.Right - ActualWidth - 10;
        Top = wa.Bottom - ActualHeight - 10;
    }
}
