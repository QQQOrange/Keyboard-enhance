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
            0 => ("欢迎使用 CapsLock+", "花一分钟做三个选择，之后就能一直用了。"),
            1 => ("启动方式", "让它在你需要的时候一定在。"),
            _ => ("可以开始用了", "试试这几个组合，感受一下差别。"),
        };
        StepDots.Text = string.Join(" ", Enumerable.Range(0, 3).Select(i => i == _step ? "●" : "○"));
        BackButton.Visibility = _step > 0 ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Content = _step == 2 ? "开始使用" : "下一步";
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
        StartupManager.SetEnabled(startup);
        _host.Settings.Global.StartWithWindows = startup;
        _host.Settings.Global.OnboardingDone = true;
        _host.SaveSettings();
        _host.RefreshTray();
        Close();
        _host.Notify("设置完成，按住 CapsLock 试试吧");
    }
}
