using System.Windows;
using System.Windows.Input;
using CapsLockPlus.Core.Settings;

namespace CapsLockPlus.UI.Windows;

/// <summary>
/// 本地数据解锁窗口。
///
/// 只在"数据已加密、但 Windows 凭据缓存失效"时出现
/// （换机器、换 Windows 账户、手动删了 vault.cache）。
/// 日常在同一台机器上启动不会弹这个窗口。
/// </summary>
public partial class PasswordPromptWindow : Window
{
    /// <summary>校验通过的密码（取消时为空）。</summary>
    public string? Password { get; private set; }

    public PasswordPromptWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => PassBox.Focus();
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        string pass = PassBox.Password;

        if (string.IsNullOrEmpty(pass))
        {
            ShowError("请先输入备份密码");
            return;
        }

        if (!DataVault.UnlockWithPassword(pass))
        {
            ShowError("密码不对 —— 换一个再试，或者关掉窗口后从备份恢复配置。");
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
