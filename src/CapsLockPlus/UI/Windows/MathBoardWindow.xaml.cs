using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CapsLockPlus.Core.Text;

namespace CapsLockPlus.UI.Windows;

/// <summary>计算器（对应上游 Caps+F2 的 Math Board），带历史记录与一键复制。</summary>
public partial class MathBoardWindow : Window
{
    private readonly List<HistoryItem> _history = new();
    private string _lastResult = "";

    public MathBoardWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => Expression.Focus();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };
    }

    private void OnExpressionChanged(object sender, TextChangedEventArgs e)
    {
        string text = Expression.Text.Trim();
        if (text.Length == 0)
        {
            Result.Text = "0";
            ErrorText.Text = "";
            _lastResult = "";
            return;
        }
        string expr = text.EndsWith('=') ? text[..^1] : text;
        if (ExpressionEvaluator.TryEvaluate(expr, out double value, out string error))
        {
            _lastResult = ExpressionEvaluator.Format(value);
            Result.Text = _lastResult;
            ErrorText.Text = "";
        }
        else
        {
            Result.Text = "?";
            ErrorText.Text = error;
            _lastResult = "";
        }
    }

    private void OnExpressionKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Commit();
            e.Handled = true;
        }
    }

    private void Commit()
    {
        string text = Expression.Text.Trim();
        if (text.Length == 0 || _lastResult.Length == 0) return;
        string expr = text.TrimEnd('=', ' ');
        _history.Insert(0, new HistoryItem(expr, _lastResult));
        if (_history.Count > 60) _history.RemoveAt(_history.Count - 1);
        History.ItemsSource = null;
        History.ItemsSource = _history;
        Expression.Text = _lastResult;
        Expression.CaretIndex = Expression.Text.Length;
        try { System.Windows.Clipboard.SetText(_lastResult); } catch { }   /* 预期内：清理动作，失败无副作用 */
    }

    private void OnEquals(object sender, RoutedEventArgs e) => Commit();

    private void OnClear(object sender, RoutedEventArgs e)
    {
        Expression.Text = "";
        Expression.Focus();
    }

    private void OnBackspace(object sender, RoutedEventArgs e)
    {
        if (Expression.Text.Length == 0) return;
        Expression.Text = Expression.Text[..^1];
        Expression.CaretIndex = Expression.Text.Length;
    }

    private void OnInsert(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Content is not string s) return;
        string insert = s switch { "×" => "*", "÷" => "/", "−" => "-", _ => s };
        int caret = Expression.CaretIndex;
        Expression.Text = Expression.Text.Insert(caret, insert);
        Expression.CaretIndex = caret + insert.Length;
        Expression.Focus();
    }

    private void OnCopyResult(object sender, RoutedEventArgs e)
    {
        if (_lastResult.Length == 0) return;
        try
        {
            System.Windows.Clipboard.SetText(_lastResult);
            ErrorText.Text = "已复制到剪贴板";
        }
        catch (Exception ex)
        {
            ErrorText.Text = "复制失败：" + ex.Message;
        }
    }

    private void OnClearHistory(object sender, RoutedEventArgs e)
    {
        _history.Clear();
        History.ItemsSource = null;
    }

    private void OnHistoryDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (History.SelectedItem is not HistoryItem item) return;
        Expression.Text = item.Value;
        Expression.CaretIndex = Expression.Text.Length;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Hide();

    private sealed record HistoryItem(string Expression, string Value);
}
