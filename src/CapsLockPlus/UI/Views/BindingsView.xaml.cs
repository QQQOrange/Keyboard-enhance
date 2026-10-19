using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CapsLockPlus.Core.Icons;
using CapsLockPlus.Core.Settings;
using CapsLockPlus.Core.Windows;
using CapsLockPlus.UI.Windows;
using Microsoft.Win32;

namespace CapsLockPlus.UI.Views;

public partial class BindingsView : UserControl
{
    private AppHost? _host;
    private bool _suppress;

    /// <summary>槽位与按键的对应：1~9 用数字键，第 10 个用 0。</summary>
    private static string SlotKeyLabel(int slot) => slot == 10 ? "0" : slot.ToString();

    public BindingsView() => InitializeComponent();

    public void Initialize(AppHost host)
    {
        _host = host;
        _suppress = true;
        SlotPicker.Items.Clear();
        for (int slot = 1; slot <= 10; slot++)
        {
            SlotPicker.Items.Add(new ComboBoxItem
            {
                Content = $"槽位 {SlotKeyLabel(slot)}　（Caps+{SlotKeyLabel(slot)}）",
                Tag = slot,
            });
        }
        SlotPicker.SelectedIndex = 0;
        _suppress = false;
        Refresh();
    }

    /// <summary>当前选中的目标槽位。</summary>
    private int CurrentSlot =>
        SlotPicker.SelectedItem is ComboBoxItem { Tag: int slot } ? slot : 1;

    public void Refresh()
    {
        if (_host == null) return;

        var list = new List<SlotVm>();
        for (int slot = 1; slot <= 10; slot++)
        {
            var b = _host.Settings.Bindings.FirstOrDefault(x => x.Slot == slot);
            list.Add(new SlotVm(slot, b));
        }
        SlotList.ItemsSource = list;

        var binding = _host.Settings.Bindings.FirstOrDefault(x => x.Slot == CurrentSlot);
        SlotHint.Text = binding == null || binding.IsEmpty
            ? $"槽位 {SlotKeyLabel(CurrentSlot)} 还没有绑定"
            : _host.Bindings.DescribeSlot(CurrentSlot);
    }

    private void OnSlotPickerChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        Refresh();
    }

    // ---------------- 方式一：选择 exe 文件 ----------------

    private void OnPickExe(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        var dlg = new OpenFileDialog
        {
            Title = $"选择要绑定到槽位 {SlotKeyLabel(CurrentSlot)} 的程序",
            Filter = "程序文件|*.exe|所有文件|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog() != true) return;

        int slot = CurrentSlot;
        if (_host.Bindings.BindByExe(slot, dlg.FileName))
        {
            _host.SaveSettings();
            Refresh();
            ShowHint($"槽位 {SlotKeyLabel(slot)} 已绑定到 {System.IO.Path.GetFileName(dlg.FileName)}。" +
                     $"现在按 Caps+{SlotKeyLabel(slot)} 就能切到它。");
        }
    }

    // ---------------- 方式二：十字准星点选 ----------------

    private void OnStartPick(object sender, RoutedEventArgs e) => StartPick(CurrentSlot);

    private void OnRowPick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: int slot }) StartPick(slot);
    }

    private void StartPick(int slot)
    {
        if (_host == null) return;

        // 十字准星覆盖层：鼠标变成十字，点哪个窗口就绑定哪个
        var picker = new WindowPickerWindow();
        picker.ShowDialog();

        var picked = picker.PickedWindow;
        if (picked == IntPtr.Zero)
        {
            ShowHint("已取消点选。");
            return;
        }

        if (_host.Bindings.BindWindow(slot, picked))
        {
            _host.SaveSettings();
            Refresh();
            var info = WindowService.Describe(picked);
            ShowHint($"槽位 {SlotKeyLabel(slot)} 已绑定到「{info?.Title ?? info?.ExeName}」。" +
                     $"按 Caps+{SlotKeyLabel(slot)} 即可切回。");
        }
    }

    // ---------------- 行内操作 ----------------

    private void OnActivateSlot(object sender, RoutedEventArgs e)
    {
        if (_host == null || sender is not Button { Tag: int slot }) return;
        if (!_host.Bindings.Activate(slot))
            ShowHint($"槽位 {SlotKeyLabel(slot)} 还不能切换。");
    }

    private void OnClearSlot(object sender, RoutedEventArgs e)
    {
        if (_host == null || sender is not Button { Tag: int slot }) return;
        _host.Bindings.Unbind(slot);
        _host.SaveSettings();
        Refresh();
        ShowHint($"槽位 {SlotKeyLabel(slot)} 已清除。");
    }

    private void ShowHint(string text)
    {
        HintBar.Visibility = Visibility.Visible;
        HintText.Text = text;
    }

    /// <summary>列表行视图模型。</summary>
    private sealed class SlotVm
    {
        public SlotVm(int slot, WinBinding? b)
        {
            Slot = slot;
            SlotLabel = slot == 10 ? "0" : slot.ToString();

            if (b == null || b.IsEmpty)
            {
                Title = "未绑定";
                Detail = $"按 Caps+{SlotLabel} 还没有对应窗口。用上面的两种方式之一绑定即可。";
                return;
            }

            Title = string.IsNullOrEmpty(b.DisplayName) ? "已绑定" : b.DisplayName;
            Detail = b.WindowIds.Count switch
            {
                0 => $"{b.ExePath}　·　当前未运行，按 Caps+{SlotLabel} 会自动打开",
                1 => $"{b.ExePath}　·　1 个窗口",
                _ => $"{b.ExePath}　·　{b.WindowIds.Count} 个窗口，反复按 Caps+{SlotLabel} 循环切换",
            };

            // exe 图标（拿不到时由 XAML 里的占位图标兜底）
            var icon = IconCache.FromFile(b.ExePath);
            if (icon != null)
            {
                Icon = icon;
                HasIcon = true;
            }
        }

        public int Slot { get; }
        public string SlotLabel { get; }
        public string Title { get; }
        public string Detail { get; }
        public ImageSource? Icon { get; }
        public bool HasIcon { get; }
    }
}
