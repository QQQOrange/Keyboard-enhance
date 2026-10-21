using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using CapsLockPlus.Core.Input;
using CapsLockPlus.Core.Settings;

namespace CapsLockPlus.UI.Windows;

/// <summary>
/// 层内子层编辑器。
///
/// 每一行是一个子层：
///   · 左边的按钮点一下可以**捕获任意键**（Ctrl、Tab、CapsLock、F13…）
///   · 中间的输入框可以给这个子层起**自定义名字**
///   · 右边可以删除
///
/// 每层最多 10 个子层。
/// </summary>
public partial class SubLayerEditorWindow : Window
{
    /// <summary>一行的视图模型。</summary>
    public sealed class Row : INotifyPropertyChanged
    {
        private string _key = "";
        private string? _name;

        public string Key
        {
            get => _key;
            set
            {
                _key = value ?? "";
                OnPropertyChanged();
                OnPropertyChanged(nameof(KeyLabel));
            }
        }

        public string? Name
        {
            get => _name;
            set { _name = value; OnPropertyChanged(); }
        }

        /// <summary>按钮上显示的文字（键名或"点击设置"）。</summary>
        public string KeyLabel =>
            string.IsNullOrWhiteSpace(Key) ? "点击设置…" : KeyName.Display(Key);

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private readonly ObservableCollection<Row> _rows = new();

    public SubLayerEditorWindow() : this("", new List<SubLayerDef>()) { }

    public SubLayerEditorWindow(string layerName, List<SubLayerDef> current)
    {
        InitializeComponent();

        TitleText.Text = string.IsNullOrWhiteSpace(layerName)
            ? "层内子层设置"
            : $"「{layerName}」的层内子层";

        foreach (var s in current)
            _rows.Add(new Row { Key = s.Key, Name = s.Name });

        if (_rows.Count == 0)
            foreach (var s in LayerOptions.DefaultSubLayers())
                _rows.Add(new Row { Key = s.Key, Name = s.Name });

        SubList.ItemsSource = _rows;
        UpdateCount();
    }

    private void UpdateCount()
    {
        int max = LayerOptions.MaxSubLayers;
        CountText.Text = $"{_rows.Count} / {max} 个子层";
        CountText.Foreground = _rows.Count > max
            ? (System.Windows.Media.Brush)FindResource("Brush.Accent")
            : (System.Windows.Media.Brush)FindResource("Brush.TextTertiary");
    }

    /// <summary>用户确认后的子层定义。</summary>
    public List<SubLayerDef> SelectedSubLayers =>
        _rows.Where(r => !string.IsNullOrWhiteSpace(r.Key))
             .Select(r => new SubLayerDef { Key = r.Key, Name = r.Name })
             .Take(LayerOptions.MaxSubLayers)
             .ToList();

    /// <summary>点"键"按钮：打开按键捕获，支持任意键。</summary>
    private void OnPickKeyClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Row row }) return;

        var capture = new KeyCaptureWindow
        {
            Owner = this,
            Title = "按下要作为子层触发键的按键",
        };
        if (capture.ShowDialog() != true) return;

        string combo = capture.CapturedCombo;
        if (string.IsNullOrWhiteSpace(combo)) return;

        // 捕获到的是 combo ID（caps_ctrl_k），取其中的键名部分
        var parsed = KeyName.ParseCombo(combo);
        if (parsed is { } p)
        {
            var mods = p.Mods;
            var parts = new List<string>();
            if (mods.HasFlag(KeyModifiers.Ctrl)) parts.Add("ctrl");
            if (mods.HasFlag(KeyModifiers.Shift)) parts.Add("shift");
            if (mods.HasFlag(KeyModifiers.Alt)) parts.Add("alt");
            if (mods.HasFlag(KeyModifiers.Win)) parts.Add("win");
            parts.Add(p.Key);
            row.Key = string.Join("_", parts);
        }
        else
        {
            row.Key = combo;
        }
    }

    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        if (_rows.Count >= LayerOptions.MaxSubLayers)
        {
            MessageBox.Show(this, $"最多 {LayerOptions.MaxSubLayers} 个子层。",
                "已达到上限", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _rows.Add(new Row());
        UpdateCount();
    }

    private void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Row row }) _rows.Remove(row);
        UpdateCount();
    }

    private void OnResetDefaults(object sender, RoutedEventArgs e)
    {
        _rows.Clear();
        foreach (var s in LayerOptions.DefaultSubLayers())
            _rows.Add(new Row { Key = s.Key, Name = s.Name });
        UpdateCount();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (_rows.Count > LayerOptions.MaxSubLayers)
        {
            MessageBox.Show(this,
                $"最多 {LayerOptions.MaxSubLayers} 个子层，请先删掉一些。",
                "子层太多", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
