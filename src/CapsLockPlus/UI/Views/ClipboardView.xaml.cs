using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace CapsLockPlus.UI.Views;

public partial class ClipboardView : UserControl
{
    private AppHost? _host;

    public ClipboardView() => InitializeComponent();

    public void Initialize(AppHost host)
    {
        _host = host;
        Refresh();
    }

    public void Refresh()
    {
        if (_host == null) return;
        EnableBox.IsChecked = _host.Clipboard.ClipboardFeatureOn;
        Slot1Text.Text = Preview(_host.Clipboard.SlotText(1));
        Slot2Text.Text = Preview(_host.Clipboard.SlotText(2));
        HistoryList.ItemsSource = _host.Clipboard.History
            .Select(t => new HistoryVm(t))
            .ToList();
    }

    private static string Preview(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "（空）";
        string flat = text.Replace("\r", "").Replace("\n", " ⏎ ");
        return flat.Length > 400 ? flat[..400] + "…" : flat;
    }

    private void OnToggleEnabled(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        _host.Clipboard.ClipboardFeatureOn = EnableBox.IsChecked == true;
        _host.Settings.Global.AllowClipboard = _host.Clipboard.ClipboardFeatureOn;
        _host.SaveSettings();
        _host.Notify(_host.Clipboard.ClipboardFeatureOn ? "独立剪贴板已开启" : "独立剪贴板已关闭");
    }

    private void OnPaste1(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        if (string.IsNullOrEmpty(_host.Clipboard.SlotText(1))) { _host.Notify("剪贴板 1 是空的"); return; }
        _host.Clipboard.Paste(1);
    }

    private void OnPaste2(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        if (string.IsNullOrEmpty(_host.Clipboard.SlotText(2))) { _host.Notify("剪贴板 2 是空的"); return; }
        _host.Clipboard.Paste(2);
    }

    private void OnClear1(object sender, RoutedEventArgs e)
    {
        _host?.Clipboard.ClearSlot(1);
        Refresh();
    }

    private void OnClear2(object sender, RoutedEventArgs e)
    {
        _host?.Clipboard.ClearSlot(2);
        Refresh();
    }

    private void OnClearHistory(object sender, RoutedEventArgs e)
    {
        _host?.Clipboard.ClearHistory();
        Refresh();
    }

    private void OnHistoryDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_host == null || HistoryList.SelectedItem is not HistoryVm vm) return;
        _host.Clipboard.PasteText(vm.Text);
        _host.Notify("已粘贴");
    }

    private sealed class HistoryVm
    {
        public HistoryVm(string text) => Text = text;
        public string Text { get; }
        public string Preview
        {
            get
            {
                string flat = Text.Replace("\r", "").Replace("\n", " ⏎ ");
                return flat.Length > 160 ? flat[..160] + "…" : flat;
            }
        }
    }
}
