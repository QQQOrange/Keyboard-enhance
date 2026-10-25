using System;
using System.Windows;
using System.Windows.Input;

namespace CapsLockPlus.UI.Windows;

/// <summary>
/// 启动密码输入窗口（软件的前门）。
///
/// 三种用途，共用同一套界面：
///   * **启动门**：App 启动时拦在主界面之前，错了就不让进；
///   * **关闭保护**：在设置里关开关前，先证明你是机主；
///   * **修改密码**：换新密码前，先验旧密码。
///
/// 校验逻辑由调用方给（<c>verify</c> 委托）—— 这里只管"要密码、比对、
/// 报错、放行"，不关心密码存在哪、怎么比。启动门的校验真相是
/// 数据目录的 startup.json（见 Core.Settings.StartupPassword），
/// 与备份密码（DataVault / PasswordPromptWindow）完全是两套东西。
/// </summary>
public partial class StartupPasswordWindow : Window
{
    /// <summary>校验通过（用户点了确认且密码正确）。取消 / 关闭时为 false。</summary>
    public bool Passed { get; private set; }

    private readonly Func<string, bool> _verify;

    /// <param name="verify">密码校验器，返回 true 放行。</param>
    /// <param name="title">窗口标题 / 大标题。</param>
    /// <param name="hint">标题下的说明文字。</param>
    /// <param name="okText">确认按钮文字（"进入" / "关闭保护" / "下一步"）。</param>
    /// <param name="cancelText">取消按钮文字。</param>
    public StartupPasswordWindow(Func<string, bool> verify, string title, string hint,
        string okText = "进入", string cancelText = "退出")
    {
        InitializeComponent();

        _verify = verify ?? throw new ArgumentNullException(nameof(verify));

        Title = title;
        HeaderText.Text = title;
        HintText.Text = hint;
        OkButton.Content = okText;
        CancelButton.Content = cancelText;

        Loaded += (_, _) => PassBox.Focus();
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        string pass = PassBox.Password;

        if (string.IsNullOrEmpty(pass))
        {
            ShowError("请先输入密码");
            return;
        }

        bool ok;
        try
        {
            ok = _verify(pass);
        }
        catch (Exception ex)
        {
            // 校验本身出错（文件损坏之类）：不能当成"通过"，也不能泄露细节
            Core.CoreLog.Error("启动密码校验异常", ex);
            ShowError("校验出错，请重试（若一直失败，可删除数据目录里的 startup.json 解除保护）");
            return;
        }

        if (!ok)
        {
            ShowError("密码不正确 —— 换一个再试。");
            PassBox.SelectAll();
            PassBox.Focus();
            return;
        }

        Passed = true;
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        Passed = false;
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
