using System.Windows;
using System.Windows.Controls;
using CapsLockPlus.Core.Settings;

namespace CapsLockPlus.UI.Windows;

/// <summary>首次运行引导：三步完成基本配置。</summary>
public partial class OnboardingWindow : Window
{
    private readonly AppHost _host;
    private int _step;

    public OnboardingWindow(AppHost host)
    {
        _host = host;
        InitializeComponent();

        Theme.WindowChrome.AttachTo(this);   // 标题栏跟随主题
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) OnSkip(this, e); };
        ShowStep(0);
    }

    private void ShowStep(int step)
    {
        _step = Math.Clamp(step, 0, 2);
        Step1.Visibility = _step == 0 ? Visibility.Visible : Visibility.Collapsed;
        Step2.Visibility = _step == 1 ? Visibility.Visible : Visibility.Collapsed;
        Step3.Visibility = _step == 2 ? Visibility.Visible : Visibility.Collapsed;

        (StepTitle.Text, StepSubtitle.Text) = _step switch
        {
            /* 这里是**本程序**的欢迎页，不是上游 CapsLock+ 的：
             * 标题以前写的是"欢迎使用 CapsLock+"，第一次打开的用户会以为装错了软件。 */
            0 => (Loc.F("欢迎使用 {0}", Core.AppInfo.ProductName),
                  Loc.T("花一分钟做几个选择，之后就能一直用了。")),
            1 => (Loc.T("启动方式"), Loc.T("让它在你需要的时候一定在。")),
            _ => (Loc.T("可以开始用了"), Loc.T("试试这几个组合，感受一下差别。")),
        };
        StepDots.Text = string.Join(" ", Enumerable.Range(0, 3).Select(i => i == _step ? "●" : "○"));
        BackButton.Visibility = _step > 0 ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Content = _step == 2 ? Loc.T("开始使用") : Loc.T("下一步");
        SkipButton.Visibility = _step == 2 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnSchemeChecked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        string scheme = SchemeClassic.IsChecked == true ? "capslock_plus" : "capslox";
        if (_host.Settings.Global.HotkeyScheme != scheme)
        {
            _host.ApplyHotkeyScheme(scheme);
        }
    }

    private void OnNext(object sender, RoutedEventArgs e)
    {
        if (_step < 2)
        {
            ShowStep(_step + 1);
            return;
        }
        Finish();
    }

    private void OnBack(object sender, RoutedEventArgs e) => ShowStep(_step - 1);

    private void OnSkip(object sender, RoutedEventArgs e) => Finish();

    private void Finish()
    {
        bool startup = StartupOn.IsChecked == true;
        _host.Settings.Global.StartWithWindows = startup;

        /* 自启载体要跟"是否管理员模式"走，和设置页用**同一个入口**。
         *
         * 以前这里调的是 StartupManager.SetEnabled（只写注册表 Run 键）：
         * 管理员模式下首次引导留下的自启项是注册表那份，登录时照样弹 UAC，
         * 与"管理员模式改计划任务、登录静默提权"的承诺不一致。 */
        StartupManager.Sync(startup, _host.Settings.Global.RunAsAdmin);

        _host.Settings.Global.OnboardingDone = true;
        _host.SaveSettings();
        _host.RefreshTray();
        Close();
        _host.Notify(Loc.T("设置完成，按住 CapsLock 试试吧"));
    }
}
