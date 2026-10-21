using System.Windows;
using System.Windows.Input;
using CapsLockPlus.Core.Settings;

namespace CapsLockPlus.UI.Windows;

/// <summary>
/// 密码输入窗口。
///
/// 两种用法，共用同一套界面：
///   * **解锁本地数据**：默认构造，校验方式是把密码交给
///     <see cref="DataVault.UnlockWithPassword"/>（密钥能派生出来就说明对）；
///   * **解开某一份备份**：传入自定义校验器 —— 备份可能是在旧密码下生成的，
///     那时 Vault 里的密钥跟它无关，只能"试着解一次"来判断。
/// </summary>
public partial class PasswordPromptWindow : Window
{
    /// <summary>校验通过的密码（取消时为空）。</summary>
    public string? Password { get; private set; }

    /// <summary>自定义校验器；null = 按解锁本地数据的规则校验。</summary>
    private readonly Func<string, bool>? _verify;

    public PasswordPromptWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => PassBox.Focus();
    }

    /// <summary>
    /// 自定义用途的构造：比如"输入这份备份当时用的密码"。
    /// </summary>
    /// <param name="verify">返回 true 表示密码可用。</param>
    /// <param name="title">窗口标题 / 大标题。</param>
    /// <param name="hint">说明文字。</param>
    /// <param name="okText">确认按钮文字。</param>
    public PasswordPromptWindow(Func<string, bool> verify, string title, string hint,
        string okText = "继续")
        : this()
    {
        _verify = verify;

        Title = title;
        HeaderText.Text = title;
        HintText.Text = hint;

        // 这类用途不存在"先不解锁"，按钮改成取消
        SkipButton.Content = "取消";
        FootText.Text = "备份文件是用生成它时的密码加密的，密码丢了就打不开 —— 只能换一份更早的备份。";

        if (FindName("OkButton") is System.Windows.Controls.Button ok)
            ok.Content = okText;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        string pass = PassBox.Password;

        if (string.IsNullOrEmpty(pass))
        {
            ShowError("请先输入密码");
            return;
        }

        bool ok = _verify != null ? _verify(pass) : DataVault.UnlockWithPassword(pass);
        if (!ok)
        {
            ShowError(_verify != null
                ? "这个密码解不开这份备份 —— 换一个再试。"
                : "密码不对 —— 换一个再试，或者关掉窗口后从备份恢复配置。");
            PassBox.SelectAll();
            PassBox.Focus();
            return;
        }

        Password = pass;
        DialogResult = true;
        Close();
    }

    private void OnSkip(object sender, RoutedEventArgs e)
    {
        Password = null;
        DialogResult = false;
        Close();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OnOk(sender, e);
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
