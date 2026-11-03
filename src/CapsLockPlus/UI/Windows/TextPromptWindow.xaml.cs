using System.Windows;
using System.Windows.Input;

namespace CapsLockPlus.UI.Windows;

/// <summary>
/// 一行文本输入的小对话框（新建/重命名分组之类用）。
///
/// 之所以单独做一个：WPF 没有现成的 InputBox，而 `Microsoft.VisualBasic.Interaction.InputBox`
/// 又丑又不可主题化。这里只做一件事 —— 收一行字、回车确定、Esc 取消。
/// </summary>
public partial class TextPromptWindow : Window
{
    public TextPromptWindow(string title, string hint, string initial = "")
    {
        InitializeComponent();

        /* 标题栏跟随主题。
         * 系统标题栏默认是浅色的，深色界面里会突兀地顶一条白（用户直接指出了这一点）。
         * 必须在 HWND 已经创建之后调，所以挂在 SourceInitialized 上。 */
        SourceInitialized += (_, _) => ApplyChrome();
        Theme.ThemeManager.ThemeChanged += OnThemeChanged;
        Closed += (_, _) => Theme.ThemeManager.ThemeChanged -= OnThemeChanged;

        Title = title;
        HeaderText.Text = title;
        HintText.Text = hint;
        InputBox.Text = initial;

        ContentRendered += (_, _) =>
        {
            InputBox.Focus();
            InputBox.SelectAll();
        };
    }

    /// <summary>用户输入的内容（取消时为 null）。</summary>
    public string? Value { get; private set; }

    /// <summary>弹一个输入框，返回去空白后的内容；取消或空输入返回 null。</summary>
    public static string? Ask(Window? owner, string title, string hint, string initial = "")
    {
        var w = new TextPromptWindow(title, hint, initial) { Owner = owner };
        return w.ShowDialog() == true && !string.IsNullOrWhiteSpace(w.Value)
            ? w.Value!.Trim()
            : null;
    }

    private void ApplyChrome()
    {
        try
        {
            bool dark = Theme.ThemeManager.Current == Theme.ThemeManager.Mode.Dark
                        || (Theme.ThemeManager.Current == Theme.ThemeManager.Mode.System
                            && Theme.ThemeManager.IsDarkSystem);
            Theme.WindowChrome.Apply(this, dark, Theme.WindowChrome.BackdropType.None);
        }
        catch
        {
            // 只是观感，失败不影响功能
        }
    }

    private void OnThemeChanged(bool dark) => Dispatcher.BeginInvoke(ApplyChrome);

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { Accept(); e.Handled = true; }
    }

    private void OnOk(object sender, RoutedEventArgs e) => Accept();

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private void Accept()
    {
        string text = InputBox.Text?.Trim() ?? "";
        if (text.Length == 0)
        {
            /* 空名字没意义，直接把焦点留在输入框里，别关窗。 */
            HintText.Text = Loc.T("名字不能为空。");
            InputBox.Focus();
            return;
        }

        Value = text;
        DialogResult = true;
    }
}
