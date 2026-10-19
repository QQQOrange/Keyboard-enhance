using System.Windows;
using System.Windows.Controls;

namespace CapsLockPlus.UI.Views;

public partial class TranslateSettingsView : UserControl
{
    private AppHost? _host;
    private bool _suppress;

    public TranslateSettingsView() => InitializeComponent();

    public void Initialize(AppHost host)
    {
        _host = host;
        Refresh();
    }

    public void Refresh()
    {
        if (_host == null) return;
        _suppress = true;
        var t = _host.Settings.Translate;
        ProviderBox.SelectedIndex = t.Provider == "youdao" ? 1 : 0;
        YoudaoPanel.Visibility = t.Provider == "youdao" ? Visibility.Visible : Visibility.Collapsed;
        AppIdBox.Text = t.AppId;
        AppSecretBox.Password = t.AppSecret;
        TargetBox.SelectedIndex = t.TargetLanguage switch
        {
            "en" => 1,
            "ja" => 2,
            "ko" => 3,
            _ => 0,
        };
        _suppress = false;
    }

    private void OnProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_host == null || _suppress) return;
        if (ProviderBox.SelectedItem is not ComboBoxItem item || item.Tag is not string tag) return;
        _host.Settings.Translate.Provider = tag;
        YoudaoPanel.Visibility = tag == "youdao" ? Visibility.Visible : Visibility.Collapsed;
        _host.SaveSettings();
    }

    private void OnCredentialChanged(object sender, TextChangedEventArgs e)
    {
        if (_host == null || _suppress) return;
        _host.Settings.Translate.AppId = AppIdBox.Text.Trim();
        _host.SaveSettings();
    }

    private void OnSecretChanged(object sender, RoutedEventArgs e)
    {
        if (_host == null || _suppress) return;
        _host.Settings.Translate.AppSecret = AppSecretBox.Password;
        _host.SaveSettings();
    }

    private void OnTargetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_host == null || _suppress) return;
        if (TargetBox.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            _host.Settings.Translate.TargetLanguage = tag;
            _host.SaveSettings();
        }
    }

    private async void OnTranslateHere(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        string text = InputBox.Text.Trim();
        if (text.Length == 0) { ResultText.Text = "先输入一点文字"; return; }
        ResultText.Text = "翻译中…";
        var t = _host.Settings.Translate;
        var result = await _host.Translator.TranslateAsync(text, t.Provider, t.AppId, t.AppSecret, t.TargetLanguage);
        ResultText.Text = result.Success ? result.Text : "失败：" + result.Error;
    }

    private async void OnTryClick(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        _host.SaveSettings();
        InputBox.Text = InputBox.Text.Length > 0 ? InputBox.Text : "CapsLock+ 让键盘更好用";
        await Task.Delay(60);
        OnTranslateHere(sender, e);
    }
}
