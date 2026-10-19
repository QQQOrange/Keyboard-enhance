using System.Windows;
using System.Windows.Controls;
using CapsLockPlus.Core.Settings;
using CapsLockPlus.Core.Windows;

namespace CapsLockPlus.UI.Views;

public partial class BindingsView : UserControl
{
    private AppHost? _host;

    public BindingsView() => InitializeComponent();

    public void Initialize(AppHost host)
    {
        _host = host;
        Refresh();
    }

    public void Refresh()
    {
        if (_host == null) return;
        var list = new List<SlotVm>();
        int emptyShown = 0;
        for (int slot = 1; slot <= 10; slot++)
        {
            var b = _host.Settings.Bindings.FirstOrDefault(x => x.Slot == slot);
            bool empty = b == null || b.IsEmpty;
            if (empty)
            {
                // 空槽位默认只显示前 3 个，避免新手一进来就被 10 行“未绑定”淹没
                emptyShown++;
                if (!_showAllEmpty && emptyShown > 3) continue;
            }
            list.Add(new SlotVm(slot, b));
        }
        SlotList.ItemsSource = list;
        int totalEmpty = Enumerable.Range(1, 10)
            .Count(i => _host.Settings.Bindings.FirstOrDefault(x => x.Slot == i) is not { } w || w.IsEmpty);
        ShowAllButton.Visibility = totalEmpty > 3 && !_showAllEmpty ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool _showAllEmpty;

    private void OnShowAll(object sender, RoutedEventArgs e)
    {
        _showAllEmpty = true;
        Refresh();
    }

    private void OnBindSlot(object sender, RoutedEventArgs e)
    {
        if (_host == null || sender is not Button btn || btn.Tag is not int slot) return;
        _host.Bindings.Bind(slot);
        _host.SaveSettings();
        Refresh();
    }

    private void OnActivateSlot(object sender, RoutedEventArgs e)
    {
        if (_host == null || sender is not Button btn || btn.Tag is not int slot) return;
        _host.Bindings.Activate(slot);
    }

    private void OnClearSlot(object sender, RoutedEventArgs e)
    {
        if (_host == null || sender is not Button btn || btn.Tag is not int slot) return;
        _host.Bindings.Unbind(slot);
        _host.SaveSettings();
        Refresh();
        _host.Notify($"槽位 {slot} 已清除");
    }

    private void OnBindForeground(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        _host.Bindings.Bind(1);
        _host.SaveSettings();
        Refresh();
    }

    private sealed class SlotVm
    {
        private readonly WinBinding? _b;
        public SlotVm(int slot, WinBinding? b)
        {
            Slot = slot;
            SlotLabel = slot == 10 ? "0" : slot.ToString();
            _b = b;
            if (b == null || b.IsEmpty)
            {
                Title = "未绑定";
                Detail = $"按 Caps+{SlotLabel} 还没有任何窗口；先切到目标窗口再点「绑定」";
                ModeVisibility = Visibility.Collapsed;
                ModeLabel = "";
            }
            else
            {
                Title = string.IsNullOrEmpty(b.DisplayName) ? "已绑定" : b.DisplayName;
                Detail = b.WindowIds.Count switch
                {
                    0 => "窗口已关闭，按「切换」会尝试重新启动程序",
                    1 => $"已绑定 1 个窗口 · {b.ExePath}",
                    _ => $"已绑定 {b.WindowIds.Count} 个窗口 · 反复按 Caps+{SlotLabel} 循环切换",
                };
                ModeVisibility = Visibility.Visible;
                ModeLabel = b.Mode switch
                {
                    WinBindMode.Application => "整个程序",
                    WinBindMode.Multi => "多窗口",
                    _ => "单窗口",
                };
            }
        }

        public int Slot { get; }
        public string SlotLabel { get; }
        public string Title { get; }
        public string Detail { get; }
        public Visibility ModeVisibility { get; }
        public string ModeLabel { get; }
    }
}
