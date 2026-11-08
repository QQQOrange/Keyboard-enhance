using System.Runtime.InteropServices;
using System.Windows;

namespace CapsLockPlus.Core.Text;

/// <summary>
/// 剪贴板"先备份、之后还原"的小工具。
///
/// ## 为什么必须有它
///
/// 取词、替换选区、"算式结果写回"这些功能都要借剪贴板中转一次
/// （先 Ctrl+C 读出来，或者先 SetText 再 Shift+Insert 粘进去）。
/// 中转完必须把用户**原来**的剪贴板内容还回去，否则用户随手复制的东西
/// 就没了 —— 而且不会有任何提示。
///
/// 早先这里只记了"纯文本"：原剪贴板是截图 / 一批文件时，
/// `hadText == false`，还原分支直接 `Clipboard.Clear()` ——
/// 那张截图或那批文件被**永久删掉**了。
///
/// 现在抓的是完整的 <see cref="DataObject"/>（所有格式，含位图与文件列表），
/// 还原时按原格式写回；只有确实拿不到时才退回"至少把文字还回去"。
/// </summary>
internal sealed class ClipboardBackup
{
    /// <summary>完整内容（所有格式）。null = 原本剪贴板是空的，或抓取失败。</summary>
    private DataObject? _data;

    /// <summary>原本的纯文本（完整还原失败时的保底）。</summary>
    private string? _text;

    /// <summary>原本剪贴板就是空的。</summary>
    private bool _wasEmpty;

    /// <summary>
    /// 截图快照（已在抓取时冻结）。
    ///
    /// 为什么单独存：位图在剪贴板里是"延迟渲染"的 —— 只拿到格式名/裸 DIB 流时，
    /// 原属主一旦释放，写回去经常变成"格式在、内容空"。
    /// 抓取时就把像素拷成 BitmapSource 并 Freeze，还原才能稳定。
    /// </summary>
    private System.Windows.Media.Imaging.BitmapSource? _image;

    /// <summary>抓取时出了意外 —— 这时绝不能去动剪贴板。</summary>
    private bool _failed;

    /// <summary>快照是否可用；false 时所有调用方都不应再改剪贴板。</summary>
    internal bool IsUsable => !_failed && (_wasEmpty || _data != null || _text != null || _image != null);

    private ClipboardBackup() { }

    /// <summary>
    /// 给剪贴板拍快照。**必须在 STA 线程调用**（动作队列就是 STA）。
    /// 任何异常都被吃掉：拿不到就标记失败，还原时什么都不做。
    /// </summary>
    public static ClipboardBackup Capture()
    {
        var b = new ClipboardBackup();
        try
        {
            b._text = Clipboard.ContainsText() ? Clipboard.GetText() : null;

            IDataObject? src = Clipboard.GetDataObject();
            if (src == null) { b._wasEmpty = true; return b; }

            string[] formats = src.GetFormats(autoConvert: false);
            if (formats.Length == 0) { b._wasEmpty = b._text == null; return b; }

            var copy = new DataObject();
            bool anyCopied = false;
            foreach (string format in formats)
            {
                try
                {
                    object? data = src.GetData(format, autoConvert: false);
                    if (data == null) continue;

                    /* HTML / RTF / 位图这类格式里放的常是 MemoryStream，
                     * 取出来时位置已经在末尾 —— 原样写回会变成空数据。
                     * 统一复制一份从头读的。 */
                    if (data is MemoryStream ms) data = new MemoryStream(ms.ToArray());

                    copy.SetData(format, data, false);
                    anyCopied = true;
                }
                catch
                {
                    /* 单个格式取不到不影响其它格式 */
                }
            }

            /* 位图单独物化一次，覆盖掉 copy 里的裸 DIB 项。 */
            try
            {
                if (Clipboard.ContainsImage())
                {
                    var img = Clipboard.GetImage();
                    if (img != null)
                    {
                        if (img.CanFreeze) img.Freeze();
                        b._image = img;
                        copy.SetData(DataFormats.Bitmap, img, true);
                        anyCopied = true;
                    }
                }
            }
            catch (Exception ex)
            {
                CoreLog.Warn("[剪贴板] 位图快照失败：" + ex.Message);
            }

            if (!anyCopied && b._text == null)
            {
                b._failed = true;
                CoreLog.Warn("[剪贴板] 没有成功复制任何格式，本次不动剪贴板");
                return b;
            }

            b._data = anyCopied ? copy : null;
        }
        catch (Exception ex)
        {
            b._failed = true;
            CoreLog.Warn("[剪贴板] 快照失败，本次不动剪贴板：" + ex.Message);
        }
        return b;
    }

    /// <summary>
    /// 还原。
    /// </summary>
    /// <param name="currentTextGuard">
    /// 非 null 时先核对"当前剪贴板里的文字还是不是这个" —— 不是就说明
    /// 期间用户自己按了 Ctrl+C（或别的程序写了剪贴板），不能再覆盖回去。
    /// </param>
    /// <param name="expectedSequence">
    /// 还原前要求当前剪贴板序号仍等于这个值，用于识别用户在中转期间复制了新内容。
    /// </param>
    public void Restore(string? currentTextGuard = null, uint? expectedSequence = null)
    {
        if (_failed) return;

        if (expectedSequence is uint seq)
        {
            try
            {
                if (GetClipboardSequenceNumber() != seq)
                {
                    CoreLog.Info("[剪贴板] 序号已变化，跳过还原，保留用户的新内容");
                    return;
                }
            }
            catch
            {
                return;
            }
        }

        if (currentTextGuard != null)
        {
            try
            {
                string current = Clipboard.ContainsText() ? Clipboard.GetText() : "";
                if (!string.Equals(current, currentTextGuard, StringComparison.Ordinal))
                {
                    CoreLog.Info("[剪贴板] 已被用户/其它程序改写，跳过还原");
                    return;
                }
            }
            catch
            {
                return;
            }
        }

        for (int attempt = 0; ; attempt++)
        {
            try
            {
                if (_image != null)
                {
                    /* 从完整快照出发，只替换位图项；重建一个空 DataObject
                     * 会把同一份剪贴板里的文件列表 / HTML / RTF 一起丢掉。 */
                    var data = _data ?? new DataObject();
                    data.SetData(DataFormats.Bitmap, _image, true);
                    if (!string.IsNullOrEmpty(_text)) data.SetText(_text!);
                    Clipboard.SetDataObject(data, copy: true);
                }
                else if (_data != null) Clipboard.SetDataObject(_data, copy: true);
                else if (_text != null) Clipboard.SetText(_text);
                else if (_wasEmpty && expectedSequence != null) Clipboard.Clear();
                return;
            }
            catch (ExternalException) when (attempt < 2)
            {
                Thread.Sleep(25);       // 剪贴板被别的程序短暂占用，正常现象
            }
            catch (Exception ex)
            {
                CoreLog.Warn("[剪贴板] 还原失败：" + ex.Message);
                /* 完整还原失败时至少把文字还回去 —— 但只在原本**有**文字时做，
                 * 原本没有文字就保持现状，绝不清空（清空就是把内容弄丢了）。 */
                if (!string.IsNullOrEmpty(_text))
                {
                    try { Clipboard.SetText(_text); } catch { }   /* 预期内：清理动作，失败无副作用 */
                }
                return;
            }
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();
}
