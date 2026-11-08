using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CapsLockPlus.Core;
using CapsLockPlus.Core.Search;

namespace CapsLockPlus.UI.Windows;

/// <summary>
/// 全局搜索面板（Ctrl+K）。
///
/// 设计取舍：
///   · 只做**导航**，不执行动作 —— 搜索框里回车就"打开程序"太容易误触；
///   · 空查询时列出页面和前几条别名，直接回车就是"打开上次想看的地方"；
///   · 全部键盘可操作：↑↓ 选择、Enter 打开、Esc 关闭。
/// </summary>
public partial class SearchPaletteWindow : Window
{
    private readonly List<GlobalHit> _all;
    private List<GlobalHit> _view = new();

    /* 会话级记忆：再打开时接着上次的搜索词，空查询时把上次打开的结果放最前。
     * 不写进配置 —— 这是"手的记忆"，不是需要同步的设置。 */
    private static string _lastQuery = "";
    private static GlobalHit? _lastPick;

    /// <summary>用户选中的结果；取消则为 null。</summary>
    public GlobalHit? Selected { get; private set; }

    private SearchPaletteWindow(List<GlobalHit> all)
    {
        InitializeComponent();
        PreviewKeyDown += OnWindowKeyDown;

        _all = all;
        HintText.Text = Loc.T("↑↓ 选择 · Enter 打开 · Esc 关闭");

        SourceInitialized += (_, _) => Theme.WindowChrome.AttachTo(this);

        Loaded += (_, _) =>
        {
            QueryBox.Text = _lastQuery;      // 触发 TextChanged → Refresh
            QueryBox.CaretIndex = QueryBox.Text.Length;
            QueryBox.Focus();
            QueryBox.SelectAll();

            Refresh(QueryBox.Text);
        };
    }

    /// <summary>打开面板并等用户选择；取消返回 null。</summary>
    public static GlobalHit? Pick(Window? owner, List<GlobalHit> all)
    {
        var window = new SearchPaletteWindow(all) { Owner = owner };
        window.ShowDialog();
        return window.Selected;
    }

    private void Refresh(string query)
    {
        _view = GlobalSearch.Query(_all, query);

        /* 空查询时把"上次打开的那条"提到最前 —— 常见动作是来回看同一处。 */
        if (query.Length == 0 && _lastPick != null)
        {
            int index = _view.FindIndex(h => h.Kind == _lastPick.Kind && h.Title == _lastPick.Title);
            if (index > 0)
            {
                var hit = _view[index];
                _view.RemoveAt(index);
                _view.Insert(0, hit);
            }
        }

        ResultList.ItemsSource = _view;

        if (_view.Count > 0) ResultList.SelectedIndex = 0;

        HintText.Text = _view.Count == 0
            ? Loc.T("没有匹配的结果")
            : Loc.T("↑↓ 选择 · Enter 打开 · Esc 关闭");
    }

    private void OnQueryChanged(object sender, TextChangedEventArgs e) => Refresh(QueryBox.Text);

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Close();
                e.Handled = true;
                break;

            case Key.Enter:
                Accept();
                e.Handled = true;
                break;

            case Key.Down:
                Move(1);
                e.Handled = true;
                break;

            case Key.Up:
                Move(-1);
                e.Handled = true;
                break;
        }
    }

    private void Move(int step)
    {
        if (_view.Count == 0) return;

        int index = ResultList.SelectedIndex + step;
        if (index < 0) index = _view.Count - 1;
        if (index >= _view.Count) index = 0;

        ResultList.SelectedIndex = index;
        ResultList.ScrollIntoView(ResultList.SelectedItem);
    }

    private void Accept()
    {
        if (ResultList.SelectedItem is not GlobalHit hit) return;

        Selected = hit;
        _lastQuery = QueryBox.Text;
        _lastPick = hit;
        Close();
    }

    private void OnResultDoubleClick(object sender, MouseButtonEventArgs e) => Accept();
}
