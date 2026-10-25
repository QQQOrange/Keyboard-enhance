using System;
using System.Windows;
using System.Windows.Input;
using CapsLockPlus.Core.Settings;

namespace CapsLockPlus.UI.Windows;

/// <summary>
/// 设置新密码的窗口（新密码 + 确认）。
///
/// 只负责"把两遍输成一样的、合规的密码交出来"：合规规则复用
/// <see cref="StartupPassword.ValidateNew"/>（界面和落库同一把尺子），
/// 真正写盘由调用方做 —— 这里不碰文件，也不管这密码是启动密码还是别的用途。
/// </summary>
public partial class NewPasswordWindow : Window
{
    /// <summary>用户设的密码（取消 / 关闭时为 null）。</summary>
    public string? Password { get; private set; }

    /// <param name="title">窗口标题 / 大标题。</param>
    /// <param name="hint">标题下的说明文字。</param>
    public NewPasswordWindow(string title = "设置启动密码", string? hint = null)
    {
        InitializeComponent();

        Title = title;
        HeaderText.Text = title;
        if (!string.IsNullOrEmpty(hint)) HintText.Text = hint;

        Loaded += (_, _) => PassBox.Focus();
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        string a = PassBox.Password;
        string b = ConfirmBox.Password;

        string? bad = StartupPassword.ValidateNew(a);
        if (bad != null)
        {
            ShowError(bad);
            return;
        }

        if (!string.Equals(a, b, StringComparison.Ordinal))
        {
            ShowError("两次输入的密码不一致");
            ConfirmBox.SelectAll();
            ConfirmBox.Focus();
            return;
        }

        Password = a;
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
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
