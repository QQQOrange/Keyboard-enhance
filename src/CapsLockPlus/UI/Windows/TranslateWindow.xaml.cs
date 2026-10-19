using System.Windows;
using System.Windows.Controls;
using CapsLockPlus.Core.Text;

namespace CapsLockPlus.UI.Windows;

public partial class TranslateWindow : Window
{
    private readonly AppHost _host;
    private string _translated = "";
    private bool _suppress;

    public TranslateWindow(AppHost host)
    {
        _host = host;
        InitializeComponent();
        _suppress = true;
        TargetBox.SelectedIndex = _host.Settings.Translate.TargetLanguage switch
        {
            "en" => 1,
            "ja" => 2,
            "ko" => 3,
            _ => 0,
        };
        _suppress = false;
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Hide(); };
    }

    /// <summary>打开窗口时自动读取当前选区并翻译。</summary>
    public void StartWithSelection()
    {
        string sel = TextActions.GetSelectedText();
        if (!string.IsNullOrWhiteSpace(sel))
        {
            SourceText.Text = sel;
            _ = TranslateAsync();
        }
        else
        {
            SourceText.Focus();
        }
    }

    private void OnTargetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || TargetBox.SelectedItem is not ComboBoxItem item || item.Tag is not string tag) return;
        _host.Settings.Translate.TargetLanguage = tag;
        _host.SaveSettings();
    }

    private void OnUseSelection(object sender, RoutedEventArgs e)
    {
        string sel = TextActions.GetSelectedText();
        if (string.IsNullOrWhiteSpace(sel))
        {
            StatusText.Text = "没有读到选中的文本";
            return;
        }
        SourceText.Text = sel;
    }

    private async void OnTranslate(object sender, RoutedEventArgs e) => await TranslateAsync();

    private async Task TranslateAsync()
    {
        string text = SourceText.Text.Trim();
        if (text.Length == 0)
        {
            StatusText.Text = "先输入或选中要翻译的文字";
            return;
        }
        StatusText.Text = "翻译中…";
        var t = _host.Settings.Translate;
        var result = await _host.Translator.TranslateAsync(text, t.Provider, t.AppId, t.AppSecret, t.TargetLanguage);
        if (result.Success)
        {
            _translated = result.Text;
            ResultText.Text = result.Text;
            StatusText.Text = $"完成（{result.SourceLanguage} → {result.TargetLanguage}）";
        }
        else
        {
            ResultText.Text = result.Error;
            StatusText.Text = "翻译失败";
        }
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        if (_translated.Length == 0) return;
        try
        {
            System.Windows.Clipboard.SetText(_translated);
            StatusText.Text = "已复制到剪贴板";
        }
        catch (Exception ex)
        {
            StatusText.Text = "复制失败：" + ex.Message;
        }
    }

    private void OnReplace(object sender, RoutedEventArgs e)
    {
        if (_translated.Length == 0) return;
        Hide();
        Dispatcher.BeginInvoke(new Action(() => TextActions.ReplaceSelection(_translated)),
            System.Windows.Threading.DispatcherPriority.Background);
    }

    private void OnClose(object sender, RoutedEventArgs e) => Hide();
}
