using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CapsLockPlus.Core;
using CapsLockPlus.Core.Input;
using CapsLockPlus.Core.Settings;

namespace CapsLockPlus.UI.Views;

/// <summary>
/// 「按键映射」里的**按应用自动启用层**卡片。
///
/// 原来放在「设置」页，和按键映射隔了两个页面 —— 但它本质上就是按键行为的一部分
/// （切到某程序时叠加一层），放在这里改起来才顺手。
/// </summary>
public partial class KeysView
{
    /// <summary>填充层下拉 + 已配置规则列表。</summary>
    private void RefreshAppLayerCard()
    {
        if (_host == null || AppLayerLayerBox == null) return;

        _suppressEvents = true;
        try
        {
            AppLayerLayerBox.Items.Clear();
            foreach (var layer in _host.Settings.Layers.OrderBy(l => l.Id))
            {
                AppLayerLayerBox.Items.Add(new ComboBoxItem
                {
                    Content = LayerName.Display(layer.Id, layer.Name),
                    Tag = layer.Id,
                });
            }
            if (AppLayerLayerBox.SelectedIndex < 0 && AppLayerLayerBox.Items.Count > 0)
                AppLayerLayerBox.SelectedIndex = 0;

            AppLayerList.Items.Clear();
            foreach (var rule in _host.Settings.Global.AppLayers.ToList())
            {
                var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var process = new TextBlock
                {
                    Text = rule.Process,
                    VerticalAlignment = VerticalAlignment.Center,
                    Style = (Style)FindResource("Text.Body"),
                };
                Grid.SetColumn(process, 0);
                row.Children.Add(process);

                var layerName = _host.Settings.Layers.FirstOrDefault(l => l.Id == rule.LayerId);
                var target = new TextBlock
                {
                    Text = LayerName.Display(rule.LayerId, layerName?.Name),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(10, 0, 10, 0),
                    Foreground = (Brush)FindResource("Brush.TextTertiary"),
                };
                Grid.SetColumn(target, 1);
                row.Children.Add(target);

                var remove = new Button
                {
                    Content = Loc.T("移除"),
                    Style = (Style)FindResource("Button.Ghost"),
                    Padding = new Thickness(10, 3, 10, 3),
                    Tag = rule,
                };
                remove.Click += OnAppLayerRemoveClick;
                Grid.SetColumn(remove, 2);
                row.Children.Add(remove);

                AppLayerList.Items.Add(row);
            }

            AppLayerHint.Text = _host.Settings.Global.AppLayers.Count == 0
                ? Loc.T("还没有规则")
                : Loc.F("{0} 条规则", _host.Settings.Global.AppLayers.Count);
        }
        finally { _suppressEvents = false; }
    }

    /// <summary>
    /// 手打进程名后按回车就加。卡片上不再有「添加」按钮：
    /// 和学习成本相比，一个按钮只值一次回车 —— 而「选择程序…」「点选窗口」
    /// 这两个真正好用的入口必须露在外面，不能藏在弹窗里。
    /// </summary>
    private void OnAppLayerProcessKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        e.Handled = true;

        if (AddAppLayerRule(AppLayerProcessBox.Text)) AppLayerProcessBox.Text = "";
    }

    /// <summary>
    /// 入口一：从资源管理器挑 exe（适合还没启动的程序）。
    ///
    /// 用户报"点添加没反应"的根因就是这里只有一个手打的输入框：
    /// 输入框是空的，点添加只会弹一句"先填进程名"，看起来就像按钮坏了。
    /// </summary>
    private void OnAppLayerPickFile(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;

        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = Loc.T("选择要自动启用层的程序"),
            Filter = Loc.T("程序 (*.exe)|*.exe|所有文件|*.*"),
            CheckFileExists = true,
        };
        if (dlg.ShowDialog() != true) return;

        string name = "";
        try { name = System.IO.Path.GetFileName(dlg.FileName); } catch { /* 预期内：路径异常按空处理 */ }
        if (name.Length == 0) return;

        AppLayerProcessBox.Text = name;
        AddAppLayerRule(name);
    }

    /// <summary>
    /// 入口二：十字准星点选已开的窗口，自动取它的进程名。
    ///
    /// 复用「窗口绑定」那套 <see cref="UI.Windows.WindowPickerWindow"/> ——
    /// 用户要求的就是"点一下那个应用"，多打一个字都是多余。
    /// </summary>
    private void OnAppLayerPickWindow(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;

        var picker = new UI.Windows.WindowPickerWindow();
        if (Window.GetWindow(this) is { } owner) picker.Owner = owner;
        picker.ShowDialog();

        var h = picker.PickedWindow;
        if (h == IntPtr.Zero) return;       // 用户按了 Esc

        var info = Core.Windows.WindowService.Describe(h);
        if (info == null || string.IsNullOrEmpty(info.ExePath))
        {
            _host.Notify(Loc.T("取不到这个窗口的程序，改用「选择程序…」挑 exe"));
            return;
        }

        string name = "";
        try { name = System.IO.Path.GetFileName(info.ExePath); } catch { }
        if (name.Length == 0) return;

        AppLayerProcessBox.Text = name;
        AddAppLayerRule(name);
    }

    /// <summary>
    /// 把「进程名 → 层」写进设置。两个挑程序的入口与手打输入框共用这一段，
    /// 保证三条路径的校验、去重、提示完全一致。
    /// </summary>
    private bool AddAppLayerRule(string rawProcess)
    {
        if (_host == null) return false;

        string process = AppLayerRules.Normalize(rawProcess);
        if (string.IsNullOrWhiteSpace(process))
        {
            _host.Notify(Loc.T("先填进程名，例如 Code.exe"));
            return false;
        }
        if (AppLayerLayerBox.SelectedItem is not ComboBoxItem { Tag: int layerId })
        {
            _host.Notify(Loc.T("先选一个层"));
            return false;
        }

        var list = _host.Settings.Global.AppLayers;
        var existing = list.FirstOrDefault(r =>
            AppLayerRules.Normalize(r.Process) == process);
        if (existing != null) existing.LayerId = layerId;
        else list.Add(new AppLayerRule { Process = process, LayerId = layerId });

        _host.SaveSettings();
        _host.RefreshAppLayer();
        RefreshAppLayerCard();
        _host.Notify(Loc.F("已设置：{0} → {1}", process, LayerName.Display(layerId, null)));
        return true;
    }

    private void OnAppLayerRemoveClick(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        if (sender is not Button { Tag: AppLayerRule rule }) return;

        _host.Settings.Global.AppLayers.Remove(rule);
        _host.SaveSettings();
        _host.RefreshAppLayer();
        RefreshAppLayerCard();
    }
}
