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
        RunStateText.Text = _host.Engine.Enabled ? "已启用" : "已暂停";
        RunStateHint.Text = _host.Engine.Enabled
            ? "按住 CapsLock 即可使用全部功能"
            : "点击标题栏的「恢复」重新启用";
        SchemeText.Text = s.Global.HotkeyScheme == "capslock_plus" ? "经典 CapsLock+ 3.0" : "Capslox（推荐）";
        StartupText.Text = s.Global.StartWithWindows ? "已开启" : "未开启";

        var keys = new List<QuickKey>
        {
            new("Q", "打开 Qbar", "搜索 / 启动程序 / 计算"),
            new("C", "复制到剪贴板 1", "独立的第二个剪贴板"),
            new("V", "粘贴剪贴板 1", "不会覆盖系统剪贴板"),
            new("S", "光标左移", "D E F 分别对应下 / 上 / 右"),
            new("H", "向左选中", "J K L 对应左 / 下 / 右"),
            new("W", "删除前一个词", "R 是删除一个字符"),
            new("Tab", "计算 / 展开", "把光标前的算式算出结果"),
            new("F6", "窗口置顶", "再按一次取消"),
        };
        QuickKeys.ItemsSource = keys;
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
