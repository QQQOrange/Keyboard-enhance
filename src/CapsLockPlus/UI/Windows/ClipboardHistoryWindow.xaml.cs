using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace CapsLockPlus.UI.Windows;

public partial class ClipboardHistoryWindow : Window
{
    private readonly AppHost _host;
    private List<Item> _all = new();

    public ClipboardHistoryWindow(AppHost host)
    {
        _host = host;
        InitializeComponent();
        FilterBox.ToolTip = Loc.T("输入关键字筛选");
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Hide(); };
    }

    public void RefreshItems()
    {
        _all = _host.Clipboard.History.Select(t => new Item(t)).ToList();
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        string f = FilterBox.Text?.Trim() ?? "";
        var filtered = f.Length == 0
            ? _all
            : _all.Where(i => i.Text.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
        Items.ItemsSource = filtered;
        if (filtered.Count > 0) Items.SelectedIndex = 0;
        StatusText.Text = Loc.F("共 {0} 条", filtered.Count);
    }

    private void OnFilterChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { PasteSelected(); e.Handled = true; }
    }

    private void OnDoubleClick(object sender, MouseButtonEventArgs e) => PasteSelected();

    private void PasteSelected()
    {
        if (Items.SelectedItem is not Item item) return;
        Hide();
        Dispatcher.BeginInvoke(new Action(() => _host.Clipboard.PasteText(item.Text)),
            System.Windows.Threading.DispatcherPriority.Background);
    }

    private void OnPaste(object sender, RoutedEventArgs e) => PasteSelected();

    private void OnCopyToSystem(object sender, RoutedEventArgs e)
    {
        if (Items.SelectedItem is not Item item) return;
        try
        {
            System.Windows.Clipboard.SetText(item.Text);
            StatusText.Text = Loc.T("已复制到系统剪贴板");
        }
        catch (Exception ex)
        {
            StatusText.Text = Loc.T("复制失败：") + ex.Message;
        }
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        _host.Clipboard.ClearHistory();
        RefreshItems();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Hide();

    private sealed class Item
    {
        public Item(string text)
        {
            Text = text;
            Time = Loc.FormatShortTime(DateTime.Now);
        }

        public string Text { get; }
        public string Time { get; }

        public string Preview
        {
            get
            {
                string flat = Text.Replace("\r", "").Replace("\n", " ⏎ ");
                return flat.Length > 150 ? flat[..150] + "…" : flat;
            }
        }
    }
}
