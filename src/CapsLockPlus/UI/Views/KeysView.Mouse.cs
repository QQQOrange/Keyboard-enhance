using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CapsLockPlus.Core;
using CapsLockPlus.Core.Settings;

namespace CapsLockPlus.UI.Views;

/// <summary>
/// 按键页里"鼠标按键"那一排（左 / 右 / 中 / 侧键 1 / 侧键 2）。
///
/// 键盘图上没有这些键，但改键规则对它们完全一样：点一下＝选中，
/// 下面编辑面板里选的动作就绑到这只鼠标键上；分设备编辑也照样走
/// <see cref="SelectKey"/> 那条路（当前在编辑哪台设备由 _editDevice 决定）。
/// 键名和捕获窗、改键表共用一套（mouseLeft…），运行时由鼠标钩子送来。
/// </summary>
public partial class KeysView
{
    private static readonly (string Name, string Label)[] MouseKeyList =
    {
        ("mouseLeft", "左键"),
        ("mouseRight", "右键"),
        ("mouseMiddle", "中键"),
        ("mouseX1", "侧键 1"),
        ("mouseX2", "侧键 2"),
        // 水平滚轮：Windows 只发 WM_MOUSEHWHEEL，钩子里按"按下+抬起"两条投递
        ("wheelLeft", "滚轮左拨"),
        ("wheelRight", "滚轮右拨"),
    };

    private void BuildMouseKeys()
    {
        if (_host == null || MouseKeyPanel == null) return;
        MouseKeyPanel.Children.Clear();

        if (MouseKeysToggle != null)
            MouseKeysToggle.IsChecked = CurrentMouseKeysEnabled;

        /* 名字在渲染时过一遍 Loc：语言包是运行时可换的，写死在数组里就换不过来。 */
        foreach (var (name, rawLabel) in MouseKeyList)
        {
            string label = Loc.T(rawLabel);
            var binding = FindInCurrentLayer(ComboFor(name));

            /* 和键帽同一个判定：动作不空、不是 doNothing、动作表里查得到。
             * 少一条就会出现"明明没绑却显示成已绑定"。 */
            bool bound = binding is { IsBound: true } && _host.Actions.Get(binding.Action) != null;
            string actionTitle = bound ? DescribeActionOf(binding!) : "";
            bool selected = string.Equals(_selectedKey, name, StringComparison.OrdinalIgnoreCase);

            Brush background, borderBrush, textBrush;
            if (bound)
            {
                background = (Brush)FindResource("Brush.Accent");
                borderBrush = (Brush)FindResource("Brush.AccentHover");
                textBrush = (Brush)FindResource("Brush.Surface");
            }
            else
            {
                background = (Brush)FindResource("Brush.AccentSoft");
                borderBrush = (Brush)FindResource("Brush.AccentSoftBorder");
                textBrush = (Brush)FindResource("Brush.TextSecondary");
            }

            var border = new Border
            {
                Style = (Style)FindResource("Keycap"),
                Background = background,
                BorderBrush = borderBrush,
                BorderThickness = new Thickness(1, 1, 1, 2),
                Height = 34,
                MinWidth = 58,
                Padding = new Thickness(10, 4, 10, 4),
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = bound ? $"{label} → {actionTitle}" : Loc.F("{0}（未绑定）", label),
                Child = new TextBlock
                {
                    Text = bound && actionTitle.Length > 0 ? label + " · " + actionTitle : label,
                    FontSize = 11,
                    Foreground = textBrush,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 104,
                },
            };

            /* 选中态：未绑定的加蓝描边，已绑定的深蓝底上蓝描边看不见，改白描边
             * —— 和键帽的处理保持一致。 */
            if (selected)
            {
                border.BorderThickness = new Thickness(2);
                border.BorderBrush = (Brush)FindResource(bound ? "Brush.Surface" : "Brush.Accent");
            }

            string captured = name;
            border.MouseLeftButtonUp += (_, _) => SelectKey(captured);
            border.MouseEnter += (_, _) => border.Opacity = 0.85;
            border.MouseLeave += (_, _) => border.Opacity = 1.0;
            MouseKeyPanel.Children.Add(border);
        }

        UpdateMouseHint();
    }

    /// <summary>
    /// 当前页（原键替换页或某一层）的鼠标键开关。
    /// 编辑设备方案时读写设备自己的那一份，不污染全局。
    /// </summary>
    private bool CurrentMouseKeysEnabled
    {
        get
        {
            if (_host == null) return true;
            var s = _host.Settings;

            if (_layerId == ReplaceLayerId)
            {
                if (_editDevice.Length == 0) return s.ReplaceMouseKeysEnabled;
                return DeviceProfiles.EnsureFor(s, _editDevice).ReplaceMouseKeysEnabled;
            }

            if (_editDevice.Length > 0)
            {
                var profile = DeviceProfiles.EnsureFor(s, _editDevice);
                var layer = profile.Layers.FirstOrDefault(l => l.Id == _layerId);
                return layer?.MouseKeysEnabled ?? true;
            }

            return s.Layers.FirstOrDefault(l => l.Id == _layerId)?.MouseKeysEnabled ?? true;
        }
        set
        {
            if (_host == null) return;
            var s = _host.Settings;

            if (_layerId == ReplaceLayerId)
            {
                if (_editDevice.Length == 0) s.ReplaceMouseKeysEnabled = value;
                else DeviceProfiles.EnsureFor(s, _editDevice).ReplaceMouseKeysEnabled = value;
                return;
            }

            if (_editDevice.Length > 0)
            {
                var profile = DeviceProfiles.EnsureFor(s, _editDevice);
                var layer = profile.Layers.FirstOrDefault(l => l.Id == _layerId);
                if (layer == null)
                {
                    layer = new DeviceLayer { Id = _layerId };
                    profile.Layers.Add(layer);
                    profile.Layers.Sort((a, b) => a.Id.CompareTo(b.Id));
                }
                layer.MouseKeysEnabled = value;
                return;
            }

            var global = s.Layers.FirstOrDefault(l => l.Id == _layerId);
            if (global != null) global.MouseKeysEnabled = value;
        }
    }

    private void OnMouseKeysToggle(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents || _host == null) return;
        CurrentMouseKeysEnabled = MouseKeysToggle.IsChecked == true;
        _host.SaveSettings();
        BuildMouseKeys();
    }

    /// <summary>
    /// 鼠标键右边的提示。改左右键是"改完就很难点回来"的操作，
    /// 所以选中它们时把后果说在前面，别等用户点不动了才发现。
    /// </summary>
    private void UpdateMouseHint()
    {
        if (MouseHintText == null || _host == null) return;

        bool editingDevice = _editDevice.Length > 0;
        bool risky = _selectedKey is "mouseLeft" or "mouseRight";

        string hint = !CurrentMouseKeysEnabled
            ? Loc.T("本页鼠标键改动已关闭：鼠标事件原样放行")
            : risky
                ? Loc.T("改过的左/右键不再用于点击，先想好怎么点回来")
                : editingDevice
                    ? Loc.T("这些键只属于当前这台设备")
                    : Loc.T("鼠标键和键盘键一样可以绑功能、也可以当发送目标");

        /* WGestures 这类手势软件挂在低级鼠标钩子上，只认硬件事件。
         * 侧键改成中键后，它看到的是我们注入的中键，不会把它当成新的手势起点。
         * 这句话不能省：否则用户会以为是改键没生效。 */
        if (_selectedKey is "mouseX1" or "mouseX2" or "mouseMiddle")
            hint += "  " + Loc.T("手势软件只认硬件按键；要让手势生效，请在 WGestures 里把触发键设成这个侧键，或使用物理中键。");

        MouseHintText.Text = hint;
    }
}
