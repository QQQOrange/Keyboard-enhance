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

        // 引擎下拉框：从 TranslateService.Providers 动态生成，
        // 以后加引擎只改那一处即可。
        if (ProviderBox.Items.Count == 0)
        {
            foreach (var (id, name, _) in Core.Translate.TranslateService.Providers)
                ProviderBox.Items.Add(new ComboBoxItem
                {
                    Content = name,
                    Tag = id,
                });
        }

        ProviderBox.SelectedItem = null;
        foreach (ComboBoxItem it in ProviderBox.Items)
        {
            if (it.Tag as string == t.Provider) { ProviderBox.SelectedItem = it; break; }
        }
        ProviderBox.SelectedItem ??= ProviderBox.Items.Count > 0 ? ProviderBox.Items[0] : null;

        ApplyProviderUi(t.Provider);
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

    /// <summary>按所选引擎切换密钥区的显示与提示文案。</summary>
    private void ApplyProviderUi(string provider)
    {
        var info = Core.Translate.TranslateService.Providers
            .FirstOrDefault(p => p.Id.Equals(provider, StringComparison.OrdinalIgnoreCase));
        bool needsKey = info.NeedsKey;
        KeyPanel.Visibility = needsKey ? Visibility.Visible : Visibility.Collapsed;

        (KeyIdLabel.Text, KeySecretLabel.Text, KeyHint.Text) = provider.ToLowerInvariant() switch
        {
            "youdao" => ("应用 ID（AppKey）", "应用密钥（AppSecret）",
                "在 https://ai.youdao.com/console 创建「文本翻译」应用后可以获得。"),
            "baidu" => ("百度 APP ID", "百度密钥（Secret Key）",
                "在 https://api.fanyi.baidu.com/ 注册「通用翻译」后可以获得。"),
            "deepl" => ("（此项留空）", "DeepL Auth Key",
                "在 https://www.deepl.com/pro-api 申请 API Key。免费额度每月 50 万字符。"),
            "tencent" => ("腾讯云 SecretId", "腾讯云 SecretKey",
                "在 https://console.cloud.tencent.com/cam/capi 创建密钥。"),
            _ => ("应用 ID", "应用密钥", ""),
        };
    }

    private void OnProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_host == null || _suppress) return;
        if (ProviderBox.SelectedItem is not ComboBoxItem item || item.Tag is not string tag) return;
        _host.Settings.Translate.Provider = tag;
        ApplyProviderUi(tag);
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
