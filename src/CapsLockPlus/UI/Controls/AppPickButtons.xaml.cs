using System.Windows;
using System.Windows.Controls;

namespace CapsLockPlus.UI.Controls;

/// <summary>
/// 「选择程序…」+「点选窗口」两个入口的统一控件。
///
/// 只负责"用户点了哪一个"，具体怎么挑（OpenFileDialog / 十字准星窗口）
/// 由宿主页面处理 —— 各页需要的后续动作不同（按应用要写进程名，
/// 窗口绑定要写 exe 路径，别名/设置要拼进程序范围列表）。
/// </summary>
public partial class AppPickButtons : UserControl
{
    public AppPickButtons()
    {
        InitializeComponent();
    }

    /// <summary>点了「选择程序…」。</summary>
    public event RoutedEventHandler? PickFile;

    /// <summary>点了「点选窗口」。</summary>
    public event RoutedEventHandler? PickWindow;

    private void OnFileClick(object sender, RoutedEventArgs e) => PickFile?.Invoke(this, e);

    private void OnWindowClick(object sender, RoutedEventArgs e) => PickWindow?.Invoke(this, e);
}
