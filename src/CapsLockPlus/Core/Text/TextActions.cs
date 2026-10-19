using System.Windows;
using CapsLockPlus.Core.Input;
using Clipboard = System.Windows.Clipboard;

namespace CapsLockPlus.Core.Text;

/// <summary>
/// 文本相关动作：选区读写、光标移动、选择、删除。
/// 全部通过标准按键（Ctrl+Insert / Shift+Insert / 方向键）实现，
/// 所以对编辑器、浏览器、Office、终端等绝大多数程序都通用。
/// </summary>
public static class TextActions
{
    private static readonly object ClipGate = new();

    /// <summary>一次“复制”最多等多久（毫秒）。</summary>
    private const int DefaultTimeoutMs = 260;

    /// <summary>
    /// 取当前选中的文本；没有选区或取不到时返回空串。
    ///
    /// 关键点：先用寄存器里的“剪贴板序号”判断这次复制究竟有没有产生新内容。
    /// 如果只靠“剪贴板里有没有文本”，那么当用户没有选中任何东西时，
    /// 上一次残留在剪贴板里的内容会被误当成选区，导致 Caps+Tab 之类动作
    /// 凭空多出一段文字。这里用序号变化来严格判定。
    /// </summary>
    public static string GetSelectedText(int timeoutMs = DefaultTimeoutMs)
    {
        lock (ClipGate)
        {
            string old = string.Empty;
            bool hadText = false;
            try
            {
                if (Clipboard.ContainsText())
                {
                    old = Clipboard.GetText();
                    hadText = true;
                }
            }
            catch { /* 剪贴板被别的程序占用 */ }

            try
            {
                uint before = GetClipboardSequenceNumber();
                Clipboard.Clear();
                InputSender.Ctrl(VK.Insert);   // Ctrl+Insert == 复制
                if (!WaitForClipboardChange(before, timeoutMs)) return string.Empty;
                return Clipboard.GetText();
            }
            catch (Exception ex)
            {
                CoreLog.Warn("读取选区失败：" + ex.Message);
                return string.Empty;
            }
            finally
            {
                RestoreClipboard(old, hadText);
            }
        }
    }

    /// <summary>等待剪贴板序号发生变化（= 前台程序确实写入了新内容）。</summary>
    private static bool WaitForClipboardChange(uint before, int timeoutMs)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            try
            {
                if (GetClipboardSequenceNumber() != before)
                {
                    // 序号变了，再确认内容可读（有些程序会先置空再写入）
                    if (Clipboard.ContainsText()) return true;
                }
            }
            catch { }
            Thread.Sleep(10);
        }
        return false;
    }

    private static void RestoreClipboard(string old, bool hadText)
    {
        try
        {
            if (hadText) Clipboard.SetText(old);
            else Clipboard.Clear();
        }
        catch { }
    }

    /// <summary>用给定文本替换当前选区（没有选区则插入到光标处）。</summary>
    public static void ReplaceSelection(string text)
    {
        lock (ClipGate)
        {
            string old = string.Empty;
            bool hadText = false;
            try
            {
                if (Clipboard.ContainsText()) { old = Clipboard.GetText(); hadText = true; }
            }
            catch { }

            try
            {
                Clipboard.SetText(text);
                Thread.Sleep(18);
                InputSender.Shift(VK.Insert);   // Shift+Insert == 粘贴
                Thread.Sleep(40);
            }
            catch (Exception ex)
            {
                CoreLog.Warn("替换选区失败：" + ex.Message);
            }
            finally
            {
                Thread.Sleep(90);
                try
                {
                    if (hadText) Clipboard.SetText(old);
                    else Clipboard.Clear();
                }
                catch { }
            }
        }
    }

    public static void Copy(int timeoutMs = DefaultTimeoutMs)
    {
        InputSender.Ctrl(VK.Insert);
        Thread.Sleep(10);
    }

    public static void Cut() => InputSender.Ctrl(0x58); // X

    public static void Paste() => InputSender.Shift(VK.Insert);

    /// <summary>选中当前行（Home + Shift+End）。</summary>
    public static void SelectLine()
    {
        InputSender.Tap(VK.Home);
        Thread.Sleep(6);
        InputSender.Shift(VK.End);
    }

    /// <summary>把选中的文本包起来，例如选中 foo 按 Caps+9 得到 (foo)。</summary>
    public static void WrapSelection(string open, string close)
    {
        string sel = GetSelectedText();
        if (string.IsNullOrEmpty(sel))
        {
            ReplaceSelection(open + close);
            // 把光标放回括号中间
            for (int i = 0; i < close.Length; i++) InputSender.Tap(VK.Left);
            return;
        }
        ReplaceSelection(open + sel + close);
    }

    public static void ChangeCase(CaseMode mode)
    {
        string sel = GetSelectedText();
        if (string.IsNullOrEmpty(sel)) return;
        string result = mode switch
        {
            CaseMode.Upper => sel.ToUpperInvariant(),
            CaseMode.Lower => sel.ToLowerInvariant(),
            CaseMode.Title => System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(sel.ToLowerInvariant()),
            _ => sel,
        };
        if (result != sel) ReplaceSelection(result);
    }

    /// <summary>
    /// 选中“从行首到光标”的那一段，并返回其文本（对应 Caps+Tab 的算式识别）。
    /// </summary>
    public static void SelectLineToCaret(out string text)
    {
        lock (ClipGate)
        {
            string old = string.Empty;
            bool hadText = false;
            try
            {
                if (Clipboard.ContainsText()) { old = Clipboard.GetText(); hadText = true; }
            }
            catch { }

            try
            {
                Clipboard.Clear();
                InputSender.Shift(VK.Home);
                Thread.Sleep(12);
                uint before = GetClipboardSequenceNumber();
                InputSender.Ctrl(VK.Insert);
                if (!WaitForClipboardChange(before, 240))
                {
                    text = string.Empty;
                    InputSender.Tap(VK.Right);   // 取消选择，避免影响用户
                    return;
                }
                text = Clipboard.GetText();
            }
            catch
            {
                text = string.Empty;
            }
            finally
            {
                RestoreClipboard(old, hadText);
            }
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    public enum CaseMode { Upper, Lower, Title }
}
