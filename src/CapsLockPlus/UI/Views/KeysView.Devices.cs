using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using CapsLockPlus.Core;
using CapsLockPlus.Core.Input;
using CapsLockPlus.Core.Settings;
using CapsLockPlus.UI.Windows;

namespace CapsLockPlus.UI.Views;

/// <summary>
/// 「按键映射」页的设备维度（partial 拆分）：
/// 工具栏那枚胶囊、设备清单、生效勾选、「全部生效」。
///
/// ## 数据怎么走
///
/// 胶囊选中的目标存在 <see cref="_editDevice"/>：
///   · 空字符串 = 通用设置，改的是全局层绑定（历史行为，所有设备都吃）；
///   · 设备 key（`VID_xxxx&amp;PID_yyyy` / `MS:VID_…`）= 只改这台设备，
///     写进它自己的 <see cref="DeviceProfile.Layers"/>，缺方案时按需建一个。
///
/// 页面上"读"和"写"都走 <see cref="CurrentLayerKeys"/>，所以只要那个属性
/// 跟着 _editDevice 走，编辑、计数、判空、恢复默认就全部自动分设备了 ——
/// 不需要在每个动作里各写一遍判断。
/// </summary>
public partial class KeysView
{
    /// <summary>当前正在编辑哪台设备（空 = 通用设置）。</summary>
    private string _editDevice = "";

    /// <summary>胶囊上显示的设备名（拿不到就退回 key 的短名）。</summary>
    private string EditDeviceTitle()
        => _editDevice.Length == 0 ? Loc.T("通用设置") : InputDevices.DisplayNameOf(_editDevice);

    /// <summary>刷新胶囊文字 / 图标 / 勾选状态。任何一处改完设置都要调。</summary>
    private void RefreshDeviceScope()
    {
        if (_host == null) return;

        var s = _host.Settings;

        /* 选中的设备被拔掉了：留着这个选择会让用户在一个"看不见的设备"上改键。
         * 但不要立刻弹回去 —— 拔插过程中 Refresh 可能被调很多次，
         * 只在方案里确实找不到这台设备时才回退。 */
        if (_editDevice.Length > 0 && DeviceProfiles.ForDevice(s, _editDevice) == null
            && !InputDevices.All().Any(d => string.Equals(d.Key, _editDevice, StringComparison.OrdinalIgnoreCase)))
        {
            _editDevice = "";
        }

        DeviceCapsuleText.Text = EditDeviceTitle();
        DeviceCapsuleIcon.Text = _editDevice.Length == 0 ? "⌨" : (InputDevices.IsMouse(_editDevice) ? "🖱" : "⌨");

        /* 「备注」只在选中某台设备时出现：通用设置本来就没有名字可起。 */
        DeviceAliasButton.Visibility = _editDevice.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

        bool all = s.Global?.DeviceScopeAll ?? true;
        _suppressEvents = true;
        try
        {
            ScopeAllBox.IsChecked = all;
            ScopeAllCheck.IsChecked = all;
        }
        finally { _suppressEvents = false; }

        RebuildDeviceList();
    }

    private void OnDeviceCapsuleClick(object sender, RoutedEventArgs e)
    {
        RebuildDeviceList();
        DevicePopup.IsOpen = true;
    }

    // ---------------------------------------------------------------- 设备备注

    /// <summary>
    /// 「备注」：给选中的这台设备起个名字。
    ///
    /// 系统里的设备名经常五条一模一样（五条「HID Keyboard Device」），
    /// 光看名字根本分不出哪只是手里这只 —— 备注之后，胶囊、清单、改键说明
    /// 全都换成这个名字，按设备分别设置才真的可用。
    /// </summary>
    private void OnDeviceAliasClick(object sender, RoutedEventArgs e)
    {
        if (_host == null || _editDevice.Length == 0) return;

        string alias = InputDevices.AliasOf(_editDevice);
        string system = SystemOnlyNameOf(_editDevice);

        DeviceAliasSystemText.Text = Loc.F("系统里的名字：{0}", system);
        DeviceAliasBox.Text = alias.Length > 0 ? alias : "";
        DeviceAliasPopup.IsOpen = true;

        /* 弹层刚打开时焦点还在按钮上，不摆一下光标用户得自己点输入框。 */
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                DeviceAliasBox.Focus();
                DeviceAliasBox.SelectAll();
            }
            catch (Exception ex) { CoreLog.Swallow(ex, "备注输入框聚焦"); }
        }), System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>系统给的原始名字（忽略备注）—— 备注弹层里显示它，用户好对照。</summary>
    private static string SystemOnlyNameOf(string key)
    {
        string k = key.Trim();
        if (k.Length == 0) return "";
        return InputDevices.IsMouse(k)
            ? MouseDevices.DisplayNameOf(k)
            : KeyboardDevices.DisplayNameOf(k);
    }

    private void OnDeviceAliasConfirm(object sender, RoutedEventArgs e) => ApplyAlias(DeviceAliasBox.Text);

    /// <summary>回车＝确定：这类"改个名字"的小弹层不该逼用户去够按钮。</summary>
    private void OnDeviceAliasKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            e.Handled = true;
            ApplyAlias(DeviceAliasBox.Text);
        }
        else if (e.Key == System.Windows.Input.Key.Escape)
        {
            e.Handled = true;
            DeviceAliasPopup.IsOpen = false;
        }
    }

    private void OnDeviceAliasClear(object sender, RoutedEventArgs e) => ApplyAlias("");

    private void OnDeviceAliasCancel(object sender, RoutedEventArgs e) => DeviceAliasPopup.IsOpen = false;

    private void ApplyAlias(string? text)
    {
        if (_host == null || _editDevice.Length == 0) return;

        string name = (text ?? "").Trim();
        string before = InputDevices.AliasOf(_editDevice);
        if (string.Equals(before, name, StringComparison.Ordinal)) { DeviceAliasPopup.IsOpen = false; return; }

        DeviceProfiles.SetAlias(_host.Settings, _editDevice, name);
        _host.SaveSettings();
        DeviceAliasPopup.IsOpen = false;

        RefreshDeviceScope();
        _host.Notify(name.Length == 0
            ? Loc.F("已恢复系统名称：{0}", SystemOnlyNameOf(_editDevice))
            : Loc.F("这台设备现在叫「{0}」", name));
    }

    private void OnScopeAllClick(object sender, RoutedEventArgs e)
    {
        if (_host == null || _suppressEvents) return;

        /* 两个「全部生效」是同一个开关（工具栏一个、弹层里一个），
         * 谁被点都以它的状态为准，然后同步另一个。 */
        bool all = sender == ScopeAllCheck
            ? ScopeAllCheck.IsChecked == true
            : ScopeAllBox.IsChecked == true;

        _host.Settings.Global ??= new GlobalOptions();
        _host.Settings.Global.DeviceScopeAll = all;

        _suppressEvents = true;
        try
        {
            ScopeAllBox.IsChecked = all;
            ScopeAllCheck.IsChecked = all;
        }
        finally { _suppressEvents = false; }

        _host.SaveSettings();
        RebuildDeviceList();
        _host.Notify(all ? Loc.T("已设为：所有键盘鼠标都生效")
                         : Loc.T("已设为：只有打勾的设备生效"));
    }

    private void OnRefreshDevicesClick(object sender, RoutedEventArgs e)
    {
        RebuildDeviceList();
        if (DeviceHintText != null && !string.IsNullOrEmpty(DeviceHintText.Text))
            _host?.Notify(Loc.T("设备列表已刷新"));
    }

    /// <summary>
    /// 重建设备清单：通用设置一行 + 每只键盘/鼠标一行。
    ///
    /// 每行右侧是"生效"方框（对应 <see cref="DeviceProfile.Active"/>），
    /// 左边点名字 = 切过去只改它。键盘和鼠标用不同图标区分，
    /// 因为用户插着两个同名设备时只能靠这个分辨。
    /// </summary>
    private void RebuildDeviceList()
    {
        if (_host == null || DeviceScopeList == null) return;

        var s = _host.Settings;
        DeviceScopeList.Items.Clear();

        DeviceScopeList.Items.Add(DeviceRow("", Loc.T("通用设置"),
            Loc.T("对所有设备生效"), false, null));

        var devices = InputDevices.All().ToList();

        /* 同一批设备里重名的（"HID Keyboard Device" 能连着来五条）用 VID:PID 区分，
         * 否则用户根本没法知道"我勾掉的到底是哪一只"。名字独一无二时不加后缀，
         * 免得满屏都是十六进制。
         *
         * 备注过的不算重名：用户已经把它们分开了，副标题留给系统名（好对照），
         * 再叠一串十六进制只会把「我给它起的名字」淹掉。 */
        var dup = devices
            .GroupBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        /* 一只物理设备一行，分节：键盘 / 键盘+鼠标 / 鼠标 / 虚拟设备
         * （虚拟的排在最后，Feizhi 这类不跟真键盘挤在一起）。 */
        int shownCat = -1;
        foreach (var d in devices)
        {
            int cat = InputDevices.IsVirtual(d) ? 3
                : d.HasKeyboard && d.IsMouse ? 1
                : d.IsMouse ? 2 : 0;
            if (cat != shownCat)
            {
                shownCat = cat;
                DeviceScopeList.Items.Add(SectionRow(cat switch
                {
                    1 => Loc.T("键盘 + 鼠标"),
                    2 => Loc.T("鼠标"),
                    3 => Loc.T("虚拟设备"),
                    _ => Loc.T("键盘"),
                }));
            }

            var profile = DeviceProfiles.ForDevice(s, d.Key);
            bool active = profile?.Active ?? true;

            /* 重名的用 VID:PID 当副标题（而不是拼进名字）：两行对比时数字对齐，
             * 一眼就能分出"我勾掉的到底是哪一只"。不重名的照常显示类别。
             *
             * 有备注时名字列用备注、副标题给系统名 —— 备注是用户自己起的，
             * 系统名留着才能对上"这到底是设备管理器里哪一条"。 */
            string alias = InputDevices.AliasOf(d.Key);
            string detail = alias.Length > 0
                ? d.Name
                : dup.Contains(d.Name) ? CompactId(d.Key) : d.Kind;
            /* 图标按"它主要是键盘还是鼠标"给：触控板两头都报，用键盘图标更好认。 */
            DeviceScopeList.Items.Add(DeviceRow(d.Key,
                alias.Length > 0 ? alias : d.Name, detail, d.IsMouse && !d.HasKeyboard, active));
        }

        DeviceHintText.Text = devices.Count == 0
            ? Loc.T("没找到键盘或鼠标：插上设备后点「刷新」。")
            : _host.DeviceTrackingAvailable
                ? Loc.F("共 {0} 台设备。取消打勾＝这台设备完全不归本软件管。", devices.Count)
                : Loc.T("设备识别未启用（Raw Input 拿不到设备），现在所有设备共用同一份设置。");
    }

    /// <summary>清单里的分组标题（键盘 / 鼠标）。</summary>
    private FrameworkElement SectionRow(string text)
    {
        return new TextBlock
        {
            Text = text,
            Margin = new Thickness(6, 10, 0, 2),
            Opacity = 0.6,
            FontSize = 11,
        };
    }

    /// <summary>设备标识压成能并排对比的短写法：VID_1532&amp;PID_00A4 → 1532:00A4。</summary>
    private static string CompactId(string key)
    {
        string k = InputDevices.Normalize(key);
        if (k.StartsWith(InputDevices.MousePrefix, StringComparison.Ordinal))
            k = k[InputDevices.MousePrefix.Length..];

        int vid = k.IndexOf("VID_", StringComparison.Ordinal);
        int pid = k.IndexOf("PID_", StringComparison.Ordinal);
        if (vid >= 0 && pid >= 0 && k.Length >= pid + 8 && k.Length >= vid + 8)
            return k.Substring(vid + 4, 4) + ":" + k.Substring(pid + 4, 4);

        return k.Length <= 10 ? k : k[..10];
    }

    private FrameworkElement DeviceRow(string key, string name, string detail, bool isMouse, bool? active)
    {
        bool selected = string.Equals(key, _editDevice, StringComparison.OrdinalIgnoreCase);

        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var pick = new Button
        {
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(6, 5, 6, 5),
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = detail.Length > 0 ? name + " · " + detail : name,
            /* 清单里不需要键盘焦点：弹层打开时焦点落到第一行，
             * 那行会带着系统高亮，看起来像"已经被选中"的其实是随便一行。 */
            Focusable = false,
            FocusVisualStyle = null,
        };
        if (selected) pick.FontWeight = FontWeights.SemiBold;

        var line = new StackPanel { Orientation = Orientation.Horizontal };
        line.Children.Add(new TextBlock
        {
            Text = key.Length == 0 ? "⌨" : (isMouse ? "🖱" : "⌨"),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        line.Children.Add(new TextBlock
        {
            Text = name,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 150,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
        });
        if (detail.Length > 0 && !string.Equals(detail, name, StringComparison.Ordinal))
        {
            /* 型号名常常一模一样（两只 "HID Keyboard Device"），类别跟在后面才好认。 */
            line.Children.Add(new TextBlock
            {
                Text = detail,
                Margin = new Thickness(6, 0, 0, 0),
                Opacity = 0.55,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 112,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap,
            });
        }
        if (selected)
            line.Children.Add(new TextBlock
            {
                Text = Loc.T("正在编辑"),
                Margin = new Thickness(8, 0, 0, 0),
                Opacity = 0.65,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
            });

        pick.Content = line;
        pick.Click += (_, _) =>
        {
            _editDevice = key;
            DevicePopup.IsOpen = false;
            RefreshDeviceScope();
            Refresh();
            _host?.Notify(key.Length == 0
                ? Loc.T("现在编辑：通用设置")
                : Loc.F("现在只改这台设备：{0}", InputDevices.DisplayNameOf(key)));
        };
        Grid.SetColumn(pick, 0);
        grid.Children.Add(pick);

        /* 通用设置没有"生效"概念：它本来就是给所有设备兜底的，
         * 关掉它等于把整套通用规则删了 —— 那不该由一个勾选框悄悄完成。 */
        if (active is bool on)
        {
            var check = new CheckBox
            {
                IsChecked = on,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 4, 0),
                ToolTip = Loc.T("打勾＝这台设备受本软件影响；空＝完全不接管它"),
            };
            check.Click += (_, _) =>
            {
                if (_host == null) return;

                /* 「全部生效」开着的时候逐台勾选是被它盖住的。用户既然动手取消了
                 * 某台设备的勾，意图很明确：这台不要。所以顺手关掉「全部生效」，
                 * 否则会出现"我取消打勾了却照样生效"的死角。 */
                bool want = check.IsChecked == true;
                if (!want && _host.Settings.Global.DeviceScopeAll)
                {
                    _host.Settings.Global.DeviceScopeAll = false;
                    _host.SaveSettings();
                    RebuildDeviceList();
                }

                DeviceProfiles.SetActive(_host.Settings, key, want);
                _host.SaveSettings();
                RebuildDeviceList();
            };
            Grid.SetColumn(check, 1);
            grid.Children.Add(check);
        }

        return grid;
    }

    /// <summary>
    /// 恢复**这台设备**当前这一层。
    ///
    /// 语义与全局那个按钮一致，只是范围缩小到一只设备：
    /// 清掉它的绑定 → 它自动回落到通用设置（若它继承）或者变成空层（若独立）。
    /// </summary>
    private void ResetDeviceLayer()
    {
        if (_host == null) return;

        string name = InputDevices.DisplayNameOf(_editDevice);
        var profile = DeviceProfiles.ForDevice(_host.Settings, _editDevice);

        /* 原键替换层：范围同样是"这台设备"，只是数据不在 Layers 里。 */
        if (_layerId == ReplaceLayerId)
        {
            if (profile == null || profile.Replacements.Count == 0)
            {
                _host.Notify(Loc.F("原键替换层在「{0}」上本来就是空的", name));
                return;
            }

            string ask = Loc.F("把「{0}」的原键替换层清空？\n", name) +
                         Loc.T("· 只影响这一台设备，通用设置和其它设备都不动；\n") +
                         Loc.T("· 清空后它回到通用设置的替换层（除非你把它设成了「独立方案」）；\n") +
                         Loc.T("· 这一步不能撤销。");
            if (!AppDialog.ConfirmDanger(Window.GetWindow(this), ask,
                    Loc.T("清空原键替换层"), okText: Loc.T("清空"))) return;

            int gone = profile.Replacements.Count;
            profile.Replacements.Clear();
            DeviceProfiles.InvalidateBindings();
            _host.Settings.Global ??= new GlobalOptions();
            _host.Settings.Global.BindingsVersion++;
            _host.SaveSettings();
            Refresh();
            _host.Notify(Loc.F("「{0}」的原键替换层已清空（{1} 条）", name, gone));
            return;
        }

        var layer = profile?.Layers.FirstOrDefault(l => l.Id == _layerId);

        if (profile == null || layer == null || layer.Keys.Count == 0)
        {
            _host.Notify(Loc.F("第 {0} 层在「{1}」上本来就是空的", _layerId, name));
            return;
        }

        string text = Loc.F("把「{0}」第 {1} 层的设置清空？\n", name, _layerId) +
                      Loc.T("· 只影响这一台设备，通用设置和其它设备都不动；\n") +
                      Loc.T("· 清空后它回到通用设置（除非你把它设成了「独立方案」）；\n") +
                      Loc.T("· 这一步不能撤销。");

        if (!AppDialog.ConfirmDanger(Window.GetWindow(this), text,
                Loc.T("恢复本页默认设置"), okText: Loc.T("恢复本页默认设置"))) return;

        int removed = layer.Keys.Count;
        layer.Keys.Clear();
        profile.Layers.RemoveAll(l => l.Keys.Count == 0);

        DeviceProfiles.InvalidateBindings();
        _host.Settings.Global ??= new GlobalOptions();
        _host.Settings.Global.BindingsVersion++;
        _host.SaveSettings();

        Refresh();
        _host.Notify(Loc.F("「{0}」第 {1} 层已清空（{2} 条）", name, _layerId, removed));
    }

    /// <summary>
    /// 仅供离屏渲染：把设备弹层打开，返回它的内容元素。
    ///
    /// 它是个 Popup —— 主窗口的截图里没有它，而"设备清单到底填进去没有"
    /// 恰恰是最容易悄悄坏掉的地方（一台设备都没有时只剩那行「通用设置」）。
    /// 调用方截完图要调 <see cref="CloseDeviceScopeForRender"/>。
    /// </summary>
    internal FrameworkElement? OpenDeviceScopeForRender()
    {
        try
        {
            RefreshDeviceScope();
            DevicePopup.IsOpen = true;
            return DevicePopup.Child as FrameworkElement;
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "渲染设备弹层");
            return null;
        }
    }

    /// <summary>离屏渲染收尾：关掉设备弹层。</summary>
    internal void CloseDeviceScopeForRender()
    {
        try { DevicePopup.IsOpen = false; } catch { /* 预期内：关窗路径，失败无所谓 */ }
    }

    /// <summary>
    /// 离屏渲染：直接切到某一层（0 = 原键替换）。
    ///
    /// 走的是和用户点下拉同样的这条路（<c>_layerId</c> + 重建），
    /// 所以截出来的图能当真：文案、计数、键帽高亮都是真实渲染结果。
    /// </summary>
    internal void SelectLayerForRender(int layerId)
    {
        _layerId = layerId;
        _layer = KeyModifiers.None;
        _subLayerKey = "";
        BuildLayerBox();
        SelectKey(_selectedKey);
    }

    /// <summary>
    /// 离屏渲染：切到某台设备并打开「备注」弹层。
    ///
    /// 备注按钮平时是 Collapsed（编辑通用设置时没有"这台设备"），
    /// 所以截图前必须先真的选中一台设备 —— 顺带也验证了这个显隐逻辑。
    /// </summary>
    internal FrameworkElement? OpenAliasForRender(string deviceKey)
    {
        try
        {
            _editDevice = deviceKey;
            RefreshDeviceScope();
            OnDeviceAliasClick(DeviceAliasButton, new RoutedEventArgs());
            return DeviceAliasPopup.Child as FrameworkElement;
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "渲染备注弹层");
            return null;
        }
    }

    /// <summary>离屏渲染收尾：关掉备注弹层并回到通用设置。</summary>
    internal void CloseAliasForRender()
    {
        try
        {
            DeviceAliasPopup.IsOpen = false;
            _editDevice = "";
            RefreshDeviceScope();
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "关闭备注弹层"); }
    }
}
