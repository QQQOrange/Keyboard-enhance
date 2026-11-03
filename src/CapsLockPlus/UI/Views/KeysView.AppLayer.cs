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

    private void OnAppLayerAddClick(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;

        string process = AppLayerRules.Normalize(AppLayerProcessBox.Text);
        if (string.IsNullOrWhiteSpace(process))
        {
            _host.Notify(Loc.T("先填进程名，例如 Code.exe"));
            return;
        }
        if (AppLayerLayerBox.SelectedItem is not ComboBoxItem { Tag: int layerId })
        {
            _host.Notify(Loc.T("先选一个层"));
            return;
        }

        var list = _host.Settings.Global.AppLayers;
        var existing = list.FirstOrDefault(r =>
            AppLayerRules.Normalize(r.Process) == process);
        if (existing != null) existing.LayerId = layerId;
        else list.Add(new AppLayerRule { Process = process, LayerId = layerId });

        _host.SaveSettings();
        _host.RefreshAppLayer();
        AppLayerProcessBox.Text = "";
        RefreshAppLayerCard();
        _host.Notify(Loc.F("已设置：{0} → {1}", process, LayerName.Display(layerId, null)));
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
