using System.Windows;
using CapsLockPlus.Core.Input;

namespace CapsLockPlus.Core.Actions;

/// <summary>
/// 独立剪贴板（对应上游 Caps+C / Caps+X / Caps+V 的“剪贴板 1/2”）。
/// 两个互不干扰的剪贴板槽位，粘贴时自动切换系统剪贴板内容，粘完再恢复。
/// </summary>
public sealed class ClipboardService
{
    private readonly object _gate = new();
    private string?[] _slots = new string?[3];
    private int _activeSlot;
    private readonly LinkedList<string> _history = new();
    private const int HistoryLimit = 40;

    public bool Enabled { get; set; } = true;
    public int HistoryCount { get { lock (_gate) return _history.Count; } }
    public bool ClipboardFeatureOn { get; set; } = true;

    public event Action<string>? Notified;

    /// <summary>最近复制的内容（最新在前）。</summary>
    public IReadOnlyList<string> History
    {
        get { lock (_gate) return _history.ToList(); }
    }

    public void RecordHistory(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        lock (_gate)
        {
            var node = _history.First;
            while (node != null)
            {
                var next = node.Next;
                if (string.Equals(node.Value, text, StringComparison.Ordinal)) _history.Remove(node);
                node = next;
            }
            _history.AddFirst(text);
            while (_history.Count > HistoryLimit) _history.RemoveLast();
        }
    }

    public bool Copy(int slot)
    {
        if (!Enabled || !ClipboardFeatureOn) return false;
        string text = ReadSelection();
        if (string.IsNullOrEmpty(text))
        {
            // 没有选区时退化为“复制当前行”
            Text.TextActions.SelectLine();
            Thread.Sleep(20);
            text = ReadSelection();
        }
        if (string.IsNullOrEmpty(text)) { Notified?.Invoke("没有可复制的内容"); return false; }
        lock (_gate)
        {
            _slots[slot] = text;
            _activeSlot = slot;
            RecordHistory(text);
        }
        Notified?.Invoke(Loc.F("已复制到剪贴板 {0}", slot));
        return true;
    }

    public bool Cut(int slot)
    {
        if (!Enabled || !ClipboardFeatureOn) return false;
        string text = ReadSelection();
        if (string.IsNullOrEmpty(text)) return false;
        InputSender.Tap(0x2E); // Delete
        lock (_gate)
        {
            _slots[slot] = text;
            _activeSlot = slot;
            RecordHistory(text);
        }
        Notified?.Invoke(Loc.F("已剪切到剪贴板 {0}", slot));
        return true;
    }

    public bool Paste(int slot)
    {
        if (!Enabled || !ClipboardFeatureOn) return false;
        string? text;
        lock (_gate) text = _slots[slot];
        if (string.IsNullOrEmpty(text)) { Notified?.Invoke(Loc.F("剪贴板 {0} 是空的", slot)); return false; }
        lock (_gate) _activeSlot = slot;
        Text.TextActions.ReplaceSelection(text!);
        return true;
    }

    /// <summary>
    /// 把**系统剪贴板**的内容粘贴到当前光标处。
    ///
    /// 之前这里是 `=> true;` 的空实现 —— 动作表里却把它注册成正式动作
    /// （「粘贴系统剪贴板」），点了完全没反应也不报错。
    ///
    /// 实现用 Shift+Insert：它和 Ctrl+V 效果相同，但**不依赖剪贴板格式协商**，
    /// 在终端、部分自研控件里比 Ctrl+V 更可靠，也不会像自己写剪贴板那样
    /// 覆盖用户原有内容。
    /// </summary>
    public bool PasteSystem()
    {
        try
        {
            InputSender.Chord(0x10 /*VK_SHIFT*/, 0x2D /*VK_INSERT*/);   // Shift+Insert
            return true;
        }
        catch (Exception ex)
        {
            CoreLog.Warn("粘贴系统剪贴板失败：" + ex.Message);
            Notified?.Invoke(UiText.Failed("粘贴", ex.Message));
            return false;
        }
    }

    public bool PasteText(string text)
    {
        Text.TextActions.ReplaceSelection(text);
        return true;
    }

    public bool ToggleEnabled()
    {
        ClipboardFeatureOn = !ClipboardFeatureOn;
        Notified?.Invoke(ClipboardFeatureOn ? "独立剪贴板已开启" : "独立剪贴板已关闭");
        return true;
    }

    public string? SlotText(int slot)
    {
        lock (_gate) return _slots[slot];
    }

    public void ClearSlot(int slot)
    {
        lock (_gate) _slots[slot] = null;
    }

    public void ClearHistory()
    {
        lock (_gate) _history.Clear();
    }

    private static string ReadSelection()
    {
        try
        {
            return Text.TextActions.GetSelectedText();
        }
        catch
        {
            return string.Empty;
        }
    }
}
