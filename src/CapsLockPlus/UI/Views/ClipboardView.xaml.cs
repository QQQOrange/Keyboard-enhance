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
        if (string.IsNullOrEmpty(text)) return Loc.T("（空）");
        string flat = text.Replace("\r", "").Replace("\n", " ⏎ ");
        return flat.Length > 400 ? flat[..400] + "…" : flat;
    }

    private void OnToggleEnabled(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        _host.Clipboard.ClipboardFeatureOn = EnableBox.IsChecked == true;
        _host.Settings.Global.AllowClipboard = _host.Clipboard.ClipboardFeatureOn;
        _host.SaveSettings();
        _host.Notify(_host.Clipboard.ClipboardFeatureOn ? Loc.T("独立剪贴板已开启") : Loc.T("独立剪贴板已关闭"));
    }

    /* 这两个按钮以前是「粘贴」—— 但在设置窗口里点它时，前台窗口**就是设置窗口自己**，
     * 于是 Paste(slot) 只是往设置页发了个 Ctrl+V：页面上没有输入框，
     * 用户看到的就是"点了没反应"。改成复制到系统剪贴板，结果确定且随时可用。 */
    private void OnCopy1(object sender, RoutedEventArgs e) => _host?.Clipboard.CopySlotToSystem(1);

    private void OnCopy2(object sender, RoutedEventArgs e) => _host?.Clipboard.CopySlotToSystem(2);

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
        /* 双击一条历史 = 把它拿走（复制到系统剪贴板）。
         * 以前这里是 PasteText → 翻成快捷键发往**设置窗口自己**，同样"没反应"。 */
        _host.Clipboard.CopyTextToSystem(vm.Text);
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
