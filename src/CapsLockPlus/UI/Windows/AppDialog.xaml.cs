using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CapsLockPlus.UI.Theme;

namespace CapsLockPlus.UI.Windows;

/// <summary>对话框的语气。决定图标、颜色和默认按钮。</summary>
public enum DialogKind
{
    Info,
    Success,
    Warn,
    Question,
    Error,
}

/// <summary>
/// 统一的提示 / 确认对话框，替代系统 MessageBox。
///
/// 全仓原来有 17 处 <c>MessageBox.Show</c>：外壳永远是系统配色，和深色界面是两套观感；
/// 按钮文字也被系统语言决定，和界面里其它按钮的措辞对不上。
/// 这里只换外观与措辞，**语义完全一样**：<see cref="Show"/> 用 ShowDialog 模态，
/// 同样阻塞调用方、同样返回"用户是否确认"，所以调用点可以逐行平移。
///
/// 用法：
/// <code>
/// AppDialog.Info(this, "一项都没勾，没什么可导出的。", "导出配置");
/// if (!AppDialog.Confirm(this, "确定要继续吗？", "明文导出", DialogKind.Warn)) return;
/// </code>
/// </summary>
public partial class AppDialog : Window
{
    private bool _accepted;

    private AppDialog()
    {
        InitializeComponent();
    }

    /// <summary>弹一个只有一个按钮的提示，返回时用户已经读过。</summary>
    public static void Info(DependencyObject? owner, string text, string title,
        DialogKind kind = DialogKind.Info, string? okText = null)
    {
        Show(owner, text, title, kind, showCancel: false, okText, null);
    }

    /// <summary>成功类提示（绿勾）。</summary>
    public static void Success(DependencyObject? owner, string text, string title)
        => Info(owner, text, title, DialogKind.Success);

    /// <summary>警告类提示（黄色感叹号），只有一个按钮。</summary>
    public static void Warn(DependencyObject? owner, string text, string title)
        => Info(owner, text, title, DialogKind.Warn);

    /// <summary>错误类提示（红色），只有一个按钮。</summary>
    public static void Error(DependencyObject? owner, string text, string title)
        => Info(owner, text, title, DialogKind.Error);

    /// <summary>
    /// 确认框：返回 true 表示用户点了确定。
    /// 和 <c>MessageBox.Show(...) == MessageBoxResult.OK</c> 完全等价。
    /// </summary>
    public static bool Confirm(DependencyObject? owner, string text, string title,
        DialogKind kind = DialogKind.Question, string? okText = null, string? cancelText = null)
        => Show(owner, text, title, kind, showCancel: true, okText, cancelText);

    /// <summary>危险操作的确认（确定按钮走红色）。</summary>
    public static bool ConfirmDanger(DependencyObject? owner, string text, string title,
        string? okText = null)
        => Show(owner, text, title, DialogKind.Warn, showCancel: true, okText, null);

    /// <summary>真正的实现。返回用户是否点了确定。</summary>
    public static bool Show(DependencyObject? owner, string text, string title,
        DialogKind kind, bool showCancel, string? okText, string? cancelText)
    {
        var app = Application.Current;
        if (app == null)
        {
            // 没有 WPF 应用（自检 / 命令行路径）：不做交互，按"用户没确认"处理
            Core.CoreLog.Warn($"无界面环境，跳过对话框「{title}」：{text}");
            return false;
        }

        // 非界面线程调用（后台任务想弹窗）：转交给界面线程，避免跨线程崩溃
        if (!app.Dispatcher.CheckAccess())
        {
            bool result = false;
            app.Dispatcher.Invoke(() =>
                result = Show(owner, text, title, kind, showCancel, okText, cancelText));
            return result;
        }

        var dlg = new AppDialog
        {
            Title = title,
        };

        var win = ResolveOwner(owner);
        if (win != null && win.IsVisible)
        {
            dlg.Owner = win;
            dlg.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        dlg.ApplyKind(kind, text, title, showCancel, okText, cancelText);

        try
        {
            WindowChrome.Apply(dlg, IsDark(), WindowChrome.BackdropType.None);
        }
        catch
        {
            // 云母/圆角只是观感，失败不影响功能
        }

        dlg.ShowDialog();
        return dlg._accepted;
    }

    private void ApplyKind(DialogKind kind, string text, string title,
        bool showCancel, string? okText, string? cancelText)
    {
        TitleText.Text = title;
        BodyText.Text = text;

        (string glyph, string brushKey, string ok) = kind switch
        {
            DialogKind.Success => ("\uE73E", "Brush.Success", "知道了"),
            DialogKind.Warn => ("\uE7BA", "Brush.Warning", "知道了"),
            DialogKind.Question => ("\uE9CE", "Brush.Accent", "确定"),
            DialogKind.Error => ("\uEA39", "Brush.Danger", "知道了"),
            _ => ("\uE946", "Brush.Accent", "知道了"),
        };

        IconText.Text = glyph;
        if (TryBrush(brushKey) is { } brush) IconText.Foreground = brush;

        OkButton.Content = okText ?? ok;
        CancelButton.Content = cancelText ?? "取消";
        CancelButton.Visibility = showCancel ? Visibility.Visible : Visibility.Collapsed;

        // 危险类操作把确定按钮改成红色，和界面里的删除按钮一致
        if (kind == DialogKind.Warn && showCancel && TryStyle("Button.Danger") is { } danger)
            OkButton.Style = danger;
    }

    private static Brush? TryBrush(string key)
        => Application.Current?.TryFindResource(key) as Brush;

    private static Style? TryStyle(string key)
        => Application.Current?.TryFindResource(key) as Style;

    private static bool IsDark()
        => ThemeManager.Current == ThemeManager.Mode.Dark;

    /// <summary>把"任意元素"解析成可当 Owner 的窗口。</summary>
    private static Window? ResolveOwner(DependencyObject? owner) => owner switch
    {
        null => Application.Current?.MainWindow is { IsVisible: true } mw ? mw : null,
        Window w => w,
        FrameworkElement fe => Window.GetWindow(fe),
        _ => Application.Current?.MainWindow,
    };

    private void OnOk(object sender, RoutedEventArgs e)
    {
        _accepted = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        _accepted = false;
        Close();
    }
}
