using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CapsLockPlus.Core;
using CapsLockPlus.Core.Localization;
using CapsLockPlus.Core.Settings;

namespace CapsLockPlus.UI.Windows;

/// <summary>
/// 「找回丢失的别名」：把历史副本里有、当前配置里没有的别名列出来，勾选后合并回去。
///
/// 不做"自动恢复"：删掉一条别名和弄丢一条别名，在磁盘上看起来完全一样。
/// 猜错就会把用户明明删掉的条目塞回去，所以这里只提供证据 + 一键勾选。
/// </summary>
public partial class SnippetRecoverWindow : Window
{
    private readonly List<(SnippetRecovery.Candidate Candidate, CheckBox Box)> _rows = new();

    /// <summary>用户勾选的条目；点了「取消」时为空。</summary>
    public List<SnippetRecovery.Candidate> Selected { get; } = new();

    private SnippetRecoverWindow(SnippetRecovery.ScanResult result)
    {
        InitializeComponent();
        Theme.WindowChrome.AttachTo(this);
        Build(result);
    }

    private void Build(SnippetRecovery.ScanResult result)
    {
        int n = result.Candidates.Count;
        SubtitleText.Text = Loc.F("在 {0} 份历史副本里找到 {1} 条当前配置没有的别名（另有 {2} 份读不出来）。",
            result.FilesScanned, n, result.FilesUnreadable);

        foreach (var candidate in result.Candidates)
        {
            var box = new CheckBox
            {
                IsChecked = true,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
            };

            var text = new StackPanel();
            text.Children.Add(new TextBlock
            {
                Text = candidate.Trigger,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            text.Children.Add(new TextBlock
            {
                Text = candidate.Preview,
                Style = (Style)FindResource("Text.Tertiary"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 380,
            });

            var source = new TextBlock
            {
                Text = Loc.F("{0} · {1}", candidate.SourceFile, candidate.SourceTime.ToString("MM-dd HH:mm")),
                Style = (Style)FindResource("Text.Tertiary"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
            };

            var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Grid.SetColumn(box, 0);
            Grid.SetColumn(text, 1);
            Grid.SetColumn(source, 2);
            row.Children.Add(box);
            row.Children.Add(text);
            row.Children.Add(source);

            RowPanel.Children.Add(row);
            _rows.Add((candidate, box));
        }

        RestoreButton.IsEnabled = n > 0;
    }

    private void OnSelectAllClick(object sender, RoutedEventArgs e)
    {
        /* 全选/全不选：只要有一条没勾，就先全勾上。 */
        bool anyOff = _rows.Any(r => r.Box.IsChecked != true);
        foreach (var r in _rows) r.Box.IsChecked = anyOff;
    }

    private void OnRestoreClick(object sender, RoutedEventArgs e)
    {
        Selected.Clear();
        foreach (var (candidate, box) in _rows)
        {
            if (box.IsChecked == true) Selected.Add(candidate);
        }

        if (Selected.Count == 0)
        {
            AppDialog.Warn(this, Loc.T("先勾选要恢复的别名"), Loc.T("找回丢失的别名"));
            return;
        }

        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>截图回归用：只建窗口不显示。</summary>
    internal static SnippetRecoverWindow CreateForRender(SnippetRecovery.ScanResult result)
        => new(result);

    /// <summary>
    /// 弹出窗口让用户挑。返回 null = 取消；否则是勾选的条目。
    /// </summary>
    public static List<SnippetRecovery.Candidate>? Ask(Window? owner, SnippetRecovery.ScanResult result)
    {
        var window = new SnippetRecoverWindow(result);
        if (owner != null && !ReferenceEquals(owner, window)) window.Owner = owner;

        bool ok = window.ShowDialog() == true;
        return ok || window.Selected.Count > 0 ? window.Selected : null;
    }
}
