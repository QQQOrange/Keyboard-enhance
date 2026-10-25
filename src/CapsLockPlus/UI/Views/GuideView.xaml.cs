using System.Windows;
using System.Windows.Controls;

namespace CapsLockPlus.UI.Views;

public partial class GuideView : UserControl
{
    private AppHost? _host;

    public GuideView() => InitializeComponent();

    public void Initialize(AppHost host)
    {
        _host = host;
        Refresh();
    }

    public void Refresh()
    {
        if (_host == null) return;
        var s = _host.Settings;
        RunStateText.Text = _host.Engine.Enabled ? Loc.T("已启用") : Loc.T("已暂停");
        RunStateHint.Text = _host.Engine.Enabled
            ? Loc.T("按住 CapsLock 即可使用全部功能")
            : Loc.T("点击标题栏的「恢复」重新启用");
        StartupText.Text = s.Global.StartWithWindows ? Loc.T("已开启") : Loc.T("未开启");

        var keys = new List<QuickKey>
        {
            new("Q", Loc.T("打开 Qbar"), Loc.T("搜索 / 启动程序 / 计算")),
            new("C", Loc.T("复制到剪贴板 1"), Loc.T("独立的第二个剪贴板")),
            new("V", Loc.T("粘贴剪贴板 1"), Loc.T("不会覆盖系统剪贴板")),
            new("S", Loc.T("光标左移"), Loc.T("D E F 分别对应下 / 上 / 右")),
            new("H", Loc.T("向左选中"), Loc.T("J K L 对应左 / 下 / 右")),
            // 文案必须与默认布局一致（caps_w = backspace、caps_r = delete），
            // 之前写成"删除前一个词"，会让人以为能删整词。
            new("W", Loc.T("退格（删前一个字符）"), Loc.T("R 是删除（删后一个字符）")),
            new("Tab", Loc.T("计算 / 展开"), Loc.T("把光标前的算式算出结果")),
            new("F6", Loc.T("窗口置顶"), Loc.T("再按一次取消")),
        };
        QuickKeys.ItemsSource = keys;
    }

    /// <summary>
    /// 内容至少铺满可视区域。
    ///
    /// 最大化时整页内容只有几百 DIP 高，ScrollViewer 下方会留下大片空白
    /// （用户的原话是"快速上手页面最大化之后，右边和下边是空的"）。
    /// 右边靠横向铺满解决；下边靠这里：把视口高度同步成内容的最小高度，
    /// 最后那张卡片所在的 * 行就会把剩余空间吃掉。
    ///
    /// 不能写死 MinHeight：窗口可以拉成任意大小，视口高度只有运行时才知道。
    /// </summary>
    private void OnPageSizeChanged(object sender, SizeChangedEventArgs e)
    {
        double chrome = PageBody.Margin.Top + PageBody.Margin.Bottom;
        PageBody.MinHeight = Math.Max(0, PageScroll.ViewportHeight - chrome);
    }

    private void OnStartSetup(object sender, RoutedEventArgs e)
    {
        _host?.ShowOnboarding();
    }

    private void OnOpenHelp(object sender, RoutedEventArgs e)
    {
        (Window.GetWindow(this) as Windows.MainWindow)?.Navigate("help");
    }

    private sealed record QuickKey(string Key, string Title, string Desc);
}
