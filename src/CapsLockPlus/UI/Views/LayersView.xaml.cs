using System.Windows;
using System.Windows.Controls;
using CapsLockPlus.Core.Input;
using CapsLockPlus.Core.Settings;
using CapsLockPlus.UI.Windows;
using KeyModifiers = CapsLockPlus.Core.Input.KeyModifiers;

namespace CapsLockPlus.UI.Views;

/// <summary>
/// 触发键与键盘层管理。
///
/// 左侧管理触发键（可多个，每个绑定不同层、各自的长按阈值与短按动作）；
/// 右侧是 10 个层的总览，可跳转到按键映射页编辑某一层。
/// </summary>
public partial class LayersView : UserControl
{
    private AppHost? _host;
    private TriggerKey? _editing;
    private bool _suppress;

    public LayersView() => InitializeComponent();

    public void Initialize(AppHost host)
    {
        _host = host;
        Reload();
    }

    private TriggerKey? Selected => TriggerList.SelectedItem as TriggerKey;

    // ---------------- 列表 ----------------

    /// <summary>重新载入列表与状态（页面再次显示时也会调用）。</summary>
    public void Reload()
    {
        if (_host == null) return;

        _suppress = true;
        TriggerList.ItemsSource = null;
        TriggerList.ItemsSource = _host.Settings.Triggers.ToList();
        if (_editing != null)
        {
            TriggerList.SelectedItem = _host.Settings.Triggers
                .FirstOrDefault(t => ReferenceEquals(t, _editing));
        }
        TriggerList.SelectedItem ??= _host.Settings.Triggers.FirstOrDefault();
        _suppress = false;

        BindEditor();
        ReloadLayers();
    }

    private void ReloadLayers()
    {
        if (_host == null) return;

        var rows = new List<LayerRow>();
        foreach (var layer in _host.Settings.Layers.OrderBy(l => l.Id))
        {
            var trigger = _host.Settings.Triggers.FirstOrDefault(t => t.LayerId == layer.Id);
            string badge = layer.Id switch
            {
                1 => "1", 2 => "2", 3 => "3", 4 => "4", 5 => "5",
                6 => "6", 7 => "7", 8 => "8", 9 => "9", 10 => "0",
                _ => "?",
            };
            rows.Add(new LayerRow(
                Id: layer.Id,
                Badge: badge,
                Name: layer.Name,
                Detail: trigger != null
                    ? $"触发键 {KeyName.Display(trigger.Key)}（长按 {trigger.HoldMs}ms）　·　{layer.Keys.Count} 个绑定"
                    : $"未被触发键绑定　·　{layer.Keys.Count} 个绑定"));
        }
        LayerList.ItemsSource = rows;
    }

    private void BindEditor()
    {
        var t = _editing ?? Selected;
        if (_host == null) return;

        if (t == null)
        {
            NoTriggerHint.Visibility = Visibility.Visible;
            TriggerEditor.Visibility = Visibility.Collapsed;
            return;
        }

        NoTriggerHint.Visibility = Visibility.Collapsed;
        TriggerEditor.Visibility = Visibility.Visible;

        TriggerKeyLabel.Text = KeyName.Display(t.Key);

        _suppress = true;

        // 层下拉
        LayerBox.Items.Clear();
        foreach (var layer in _host.Settings.Layers.OrderBy(l => l.Id))
        {
            LayerBox.Items.Add(new ComboBoxItem
            {
                Content = $"{layer.Id}. {layer.Name}",
                Tag = layer.Id,
            });
            if (layer.Id == t.LayerId) LayerBox.SelectedIndex = LayerBox.Items.Count - 1;
        }
        if (LayerBox.SelectedIndex < 0 && LayerBox.Items.Count > 0) LayerBox.SelectedIndex = 0;

        HoldMsBox.Text = t.HoldMs.ToString();
        HoldOnOtherBox.IsChecked = t.HoldOnOtherKey;

        // 动作下拉（带"无"选项）
        TapActionBox.Items.Clear();
        DoubleTapBox.Items.Clear();
        foreach (var item in ActionItems())
        {
            TapActionBox.Items.Add(item);
            DoubleTapBox.Items.Add(new ComboBoxItem
            {
                Content = item.Content,
                Tag = item.Tag,
            });
        }
        SelectByTag(TapActionBox, t.TapAction, "toggleCapsLock");
        SelectByTag(DoubleTapBox, string.IsNullOrEmpty(t.DoubleTapAction) ? "none" : t.DoubleTapAction, "none");

        _suppress = false;
    }

    private IEnumerable<ComboBoxItem> ActionItems()
    {
        yield return new ComboBoxItem { Content = "（无）", Tag = "none" };
        if (_host == null) yield break;
        foreach (var a in _host.Actions.Items
                     .Where(i => i.Id != "doNothing")
                     .OrderBy(i => i.Category).ThenBy(i => i.Title))
        {
            yield return new ComboBoxItem
            {
                Content = $"[{a.Category}] {a.Title}",
                Tag = a.Id,
            };
        }
    }

    private static void SelectByTag(ComboBox box, string tag, string fallback)
    {
        foreach (ComboBoxItem it in box.Items)
        {
            if (it.Tag as string == tag) { box.SelectedItem = it; return; }
        }
        foreach (ComboBoxItem it in box.Items)
        {
            if (it.Tag as string == fallback) { box.SelectedItem = it; return; }
        }
        if (box.Items.Count > 0) box.SelectedIndex = 0;
    }

    // ---------------- 触发键事件 ----------------

    private void OnTriggerSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        _editing = Selected;
        BindEditor();
    }

    private void OnTriggerEnabledChanged(object sender, RoutedEventArgs e)
    {
        if (_host == null || sender is not CheckBox { Tag: int idx }) return;
        var t = _host.Settings.Triggers.ElementAtOrDefault(idx);
        if (t == null) return;
        t.Enabled = ((CheckBox)sender).IsChecked == true;
        _host.SaveSettings();
        ReloadLayers();
    }

    private void OnAddTrigger(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;

        // 捕获一个新键作为触发键
        var dlg = new KeyCaptureWindow { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true || string.IsNullOrEmpty(dlg.CapturedCombo)) return;

        // 触发键必须是单键（不带修饰键），否则语义混乱
        if (dlg.CapturedMods != Core.Input.KeyModifiers.None)
        {
            _host.Notify("触发键不能带修饰键，请直接按一个键");
            return;
        }

        string key = dlg.CapturedKey ?? dlg.CapturedCombo;
        if (_host.Settings.Triggers.Any(t =>
                t.Key.Equals(key, StringComparison.OrdinalIgnoreCase)))
        {
            _host.Notify("这个键已经是触发键了");
            return;
        }

        // 找一个还没被占用的层，绑上去
        int layerId = _host.Settings.Layers
            .FirstOrDefault(l => _host.Settings.Triggers.All(t => t.LayerId != l.Id))?.Id ?? 1;

        var trigger = new TriggerKey
        {
            Key = key,
            LayerId = layerId,
            HoldMs = 200,
            TapAction = "toggleCapsLock",
            DoubleTapAction = "",
            Enabled = true,
        };
        _host.Settings.Triggers.Add(trigger);
        _editing = trigger;
        _host.SaveSettings();
        Reload();
        _host.Notify($"已添加触发键 {KeyName.Display(key)} → 第 {layerId} 层");
    }

    private void OnDeleteTrigger(object sender, RoutedEventArgs e)
    {
        if (_host == null || _editing == null) return;
        if (_host.Settings.Triggers.Count <= 1)
        {
            _host.Notify("至少保留一个触发键");
            return;
        }
        string name = KeyName.Display(_editing.Key);
        _host.Settings.Triggers.Remove(_editing);
        _editing = null;
        _host.SaveSettings();
        Reload();
        _host.Notify($"已删除触发键 {name}");
    }

    private void OnChangeTriggerKey(object sender, RoutedEventArgs e)
    {
        if (_host == null || _editing == null) return;

        var dlg = new KeyCaptureWindow { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true || string.IsNullOrEmpty(dlg.CapturedCombo)) return;
        if (dlg.CapturedMods != Core.Input.KeyModifiers.None)
        {
            _host.Notify("触发键不能带修饰键，请直接按一个键");
            return;
        }

        string key = dlg.CapturedKey ?? dlg.CapturedCombo;
        if (_host.Settings.Triggers.Any(t =>
                !ReferenceEquals(t, _editing) &&
                t.Key.Equals(key, StringComparison.OrdinalIgnoreCase)))
        {
            _host.Notify("这个键已经是触发键了");
            return;
        }

        _editing.Key = key;
        _host.SaveSettings();
        BindEditor();
        ReloadLayers();
    }

    private void OnTriggerLayerChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_host == null || _suppress || _editing == null) return;
        if (LayerBox.SelectedItem is not ComboBoxItem { Tag: int id }) return;
        _editing.LayerId = id;
        _host.SaveSettings();
        ReloadLayers();
    }

    private void OnHoldMsChanged(object sender, TextChangedEventArgs e)
    {
        if (_host == null || _suppress || _editing == null) return;
        if (int.TryParse(HoldMsBox.Text, out int ms))
        {
            _editing.HoldMs = Math.Clamp(ms, 50, 2000);
            _host.SaveSettings();
        }
    }

    private void OnHoldOnOtherChanged(object sender, RoutedEventArgs e)
    {
        if (_host == null || _suppress || _editing == null) return;
        _editing.HoldOnOtherKey = HoldOnOtherBox.IsChecked == true;
        _host.SaveSettings();
    }

    private void OnTapActionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_host == null || _suppress || _editing == null) return;
        if (TapActionBox.SelectedItem is not ComboBoxItem { Tag: string tag }) return;
        _editing.TapAction = tag == "none" ? "" : tag;
        _host.SaveSettings();
    }

    private void OnDoubleTapActionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_host == null || _suppress || _editing == null) return;
        if (DoubleTapBox.SelectedItem is not ComboBoxItem { Tag: string tag }) return;
        _editing.DoubleTapAction = tag == "none" ? "" : tag;
        _host.SaveSettings();
    }

    private void OnEditLayerKeys(object sender, RoutedEventArgs e)
    {
        if (_host == null || sender is not Button { Tag: int layerId }) return;
        // 跳到按键映射页，并把层切过去
        _host.ShowMainWindow("keys");
        _host.SelectKeysLayer(layerId);
    }

    private sealed record LayerRow(int Id, string Badge, string Name, string Detail);
}
