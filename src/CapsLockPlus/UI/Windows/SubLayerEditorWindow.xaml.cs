using System.Collections.ObjectModel;
using System.Windows;
using CapsLockPlus.Core.Input;
using CapsLockPlus.Core.Settings;

namespace CapsLockPlus.UI.Windows;

/// <summary>
/// 层内修饰键（子层）编辑器。
///
/// 每层最多 <see cref="LayerOptions.MaxSubLayers"/> 个子层，
/// 覆盖 Ctrl / Shift / Alt / Win 的所有非空组合（共 15 种）。
/// 用户自由勾选这一层要用哪几套 —— 每个子层都有独立的按键绑定。
/// </summary>
public partial class SubLayerEditorWindow : Window
{
    /// <summary>一个可选的修饰键组合。</summary>
    public sealed class Option
    {
        public KeyModifiers Mods { get; init; }
        public string Label { get; set; } = "";
        public bool IsChecked { get; set; }
    }

    private readonly ObservableCollection<Option> _options = new();

    public SubLayerEditorWindow() : this("", new List<KeyModifiers>()) { }

    public SubLayerEditorWindow(string layerName, List<KeyModifiers> selected)
    {
        InitializeComponent();

        TitleText.Text = string.IsNullOrWhiteSpace(layerName)
            ? "层内修饰键（子层）"
            : $"「{layerName}」的层内修饰键（子层）";

        // 15 种非空组合（1..15 的位组合）
        for (int i = 1; i < 16; i++)
        {
            var mods = (KeyModifiers)i;
            _options.Add(new Option
            {
                Mods = mods,
                Label = Describe(mods),
                IsChecked = selected.Contains(mods),
            });
        }

        ModifierList.ItemsSource = _options;
        UpdateCount();
    }

    private static string Describe(KeyModifiers m)
    {
        var parts = new List<string>();
        if (m.HasFlag(KeyModifiers.Ctrl)) parts.Add("Ctrl");
        if (m.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        if (m.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        if (m.HasFlag(KeyModifiers.Win)) parts.Add("Win");
        return parts.Count == 0 ? "（无）" : "+" + string.Join(" + ", parts);
    }

    private void UpdateCount()
    {
        int n = _options.Count(o => o.IsChecked);
        int max = LayerOptions.MaxSubLayers;
        CountText.Text = $"已选 {n} 个（最多 {max} 个）";
        CountText.Foreground = n > max
            ? (System.Windows.Media.Brush)FindResource("Brush.Accent")
            : (System.Windows.Media.Brush)FindResource("Brush.TextTertiary");
    }

    /// <summary>当前勾选的修饰键组合。</summary>
    public List<KeyModifiers> SelectedModifiers =>
        _options.Where(o => o.IsChecked).Select(o => o.Mods).ToList();

    private void OnResetDefaults(object sender, RoutedEventArgs e)
    {
        var defaults = LayerOptions.DefaultSubLayerModifiers()
            .Select(x => (KeyModifiers)x).ToList();
        foreach (var o in _options) o.IsChecked = defaults.Contains(o.Mods);
        ModifierList.Items.Refresh();
        UpdateCount();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var sel = SelectedModifiers;
        if (sel.Count > LayerOptions.MaxSubLayers)
        {
            MessageBox.Show(this,
                $"最多只能选 {LayerOptions.MaxSubLayers} 个修饰键组合。\n当前选了 {sel.Count} 个，请取消一些。",
                "子层太多", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
