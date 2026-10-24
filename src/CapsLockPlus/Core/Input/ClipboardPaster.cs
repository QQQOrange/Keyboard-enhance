using System.Runtime.InteropServices;
using System.Windows;
using static CapsLockPlus.Core.Interop.NativeMethods;

namespace CapsLockPlus.Core.Input;

/// <summary>
/// 「输入文本」的长文本通道：写剪贴板 → 发 Ctrl+V → 还原剪贴板。
///
/// **为什么需要它**：逐字注入（真实按键 / Unicode）在长文本上会被目标程序的
/// 输入吞吐卡住。实测 Win11 记事本里 86 字一批能丢 10%，逐字放慢到 10ms 也只有
/// 2/3 可靠，英文布局下同样复现 —— 不是输入法的问题，是这条通道本身的上限
/// （量化见 `tools/probe_batch_typing.py`）。剪贴板粘贴是一次原子操作，
/// 长度再长也不会丢字。
///
/// **三条硬约束**（每一条都是为了"别帮倒忙"）：
///   1. **不破坏用户剪贴板**。写之前把整个剪贴板（所有格式）快照下来，
///      粘完原样还回去。图片/文件这类用户数据拿不到就干脆不做 ——
///      宁可逐字注入慢一点，也不能把用户刚复制的图弄丢。
///   2. **不覆盖用户后来复制的东西**。粘贴期间用户按了 Ctrl+C（或别的程序
///      写了剪贴板），剪贴板序号会变 —— 此时跳过还原，不抢用户的新内容。
///   3. **不在自检/离屏渲染里动桌面**。干跑（<see cref="InputSender.DryRun"/>）
///      直接返回 false，调用方照原路走。
/// </summary>
internal static class ClipboardPaster
{
    /// <summary>
    /// 多长算"长文本"、该改走剪贴板。
    ///
    /// 取自实测（`tools/probe_batch_typing.py` 的长度梯度）：一批（32 字符）
    /// 之内失败率明显更低，跨过一批之后逐字注入的丢字率迅速变差，64 字基本全错。
    /// 这里取 40 —— 比一批略宽，让邮箱地址、常用短语这类"短文本"仍然走原来的
    /// 通道（不碰剪贴板 = 不动用户的东西），更长的才交给剪贴板。
    ///
    /// 阈值不追求精确：剪贴板路径的可靠性与长度无关，取大一点只是少动一次剪贴板。
    /// </summary>
    public const int Threshold = 40;

    /// <summary>
    /// 剪贴板被别的程序占用时的重试次数与间隔。
    ///
    /// 实测会撞上 `CLIPBRD_E_CANT_OPEN`：刚写过剪贴板之后，Windows 的剪贴板
    /// 历史服务（Win+V 那套）会来读快照，这期间谁也打不开剪贴板。
    /// 4×45ms 不够（e2e 里被抓到过一次），给到 ~500ms。
    /// 真正撞上的代价是长文本退回逐字注入 → 可能丢字，所以宁可多等。
    /// </summary>
    private const int RetryCount = 8;
    private const int RetryGapMs = 60;

    /// <summary>发完 Ctrl+V 后先等这么久（让目标线程处理粘贴消息）。</summary>
    private const int PasteSettleMs = 120;

    /// <summary>目标程序读剪贴板时会持有它；最多再等这么久等它放手。</summary>
    private const int ClipboardIdleWaitMs = 400;

    /// <summary>自己写入的文本在多久内算"自己写的"（给剪贴板历史过滤用）。</summary>
    private const int SelfWriteTtlSeconds = 5;

    /// <summary>
    /// 这些格式一旦原本存在就必须能快照下来，否则放弃剪贴板粘贴。
    /// 前四个是"用户数据"（丢了就是数据丢失），后两个是富文本格式
    /// （丢了会把粘贴降级成纯文本，用户能感觉到）。
    /// </summary>
    private static readonly string[] MustKeepFormats =
    [
        "FileDrop", "FileNameW", "DeviceIndependentBitmap", "PNG",
        "HTML Format", "Rich Text Format",
    ];

    private static readonly object TagGate = new();
    private static readonly List<(string Text, DateTime At)> SelfWrites = [];

    /// <summary>WPF 的剪贴板 API 要求 STA 线程（动作队列本身就是 STA）。</summary>
    public static bool OnStaThread =>
        Thread.CurrentThread.GetApartmentState() == ApartmentState.STA;

    /// <summary>
    /// 把 <paramref name="text"/> 粘贴到当前焦点处。返回 false = 这条路走不通，
    /// 调用方应退回逐字注入（未对剪贴板做任何改动）。
    /// </summary>
    public static bool TryPaste(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        if (InputSender.DryRun) return false;
        if (!OnStaThread) return false;
        if (GetForegroundWindow() == IntPtr.Zero) return false;

        /* 和取词、替换选区共用一把锁：动作队列与宏播放是两个线程，
         * 同时动剪贴板会互相覆盖对方的内容。 */
        lock (Core.Text.TextActions.ClipGate)
        {
            var snapshot = Capture();
            if (snapshot == null) return false;

            uint seqBefore = GetClipboardSequenceNumber();
            if (!SetText(text))
            {
                /* SetText 一般是"连剪贴板都没打开"就失败了（没动过内容）。
                 * 但万一它是清空之后才失败的，用户的剪贴板就空了 ——
                 * 序号变了说明确实动过，这时无条件还回去。 */
                if (GetClipboardSequenceNumber() != seqBefore) Restore(snapshot, expectedSeq: null);
                CoreLog.Warn("[输入文本] 剪贴板写入失败，退回逐字注入");
                return false;
            }
            TagSelfWrite(text);

            /* 记下写入后的序号：还原前如果它变了，说明这期间用户自己复制了东西
             * （或别的程序写了剪贴板），那时候**不能**还原 —— 否则会把用户刚复制
             * 的内容擦掉。这比对比文本更准：复制了同样的文字也算被改写。 */
            uint seq = GetClipboardSequenceNumber();

            try
            {
                /* Ctrl+V。走 CopyChord 而不是 Ctrl()：它带扫描码、分四次发，
                 * Chromium / Qt 系程序才认（这也是"复制在浏览器里收不到"的修法）。 */
                InputSender.CopyChord(0x56 /* V */);
                WaitForPasteConsumed();
            }
            finally
            {
                Restore(snapshot, seq);
            }
        }

        CoreLog.Info($"[输入文本] 走剪贴板粘贴 长度={text.Length}");
        return true;
    }

    /// <summary>
    /// 这段文本是不是本程序刚才为了粘贴而写进剪贴板的。
    ///
    /// 剪贴板历史（`AppHost.OnClipboardChanged`）用它过滤掉自己写的内容 ——
    /// 否则用户每按一次"输入文本"，历史里就多出一条他并没有复制过的记录，
    /// 还原那一下还会再记一次。
    /// </summary>
    public static bool IsSelfWrite(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        lock (TagGate)
        {
            PruneSelfWrites();
            return SelfWrites.Exists(w => string.Equals(w.Text, text, StringComparison.Ordinal));
        }
    }

    private static void TagSelfWrite(string text)
    {
        lock (TagGate)
        {
            PruneSelfWrites();
            SelfWrites.Add((text, DateTime.Now));
        }
    }

    private static void PruneSelfWrites()
    {
        var cutoff = DateTime.Now.AddSeconds(-SelfWriteTtlSeconds);
        SelfWrites.RemoveAll(w => w.At < cutoff);
    }

    // ---------------- 快照 / 还原 ----------------

    private sealed class Snapshot
    {
        /// <summary>完整的剪贴板内容；null = 原本剪贴板就是空的。</summary>
        public DataObject? Data;

        /// <summary>原本的纯文本（完整快照还原失败时的保底）。</summary>
        public string? Text;
    }

    /// <summary>
    /// 给剪贴板拍快照。返回 null = 拿不全、不敢动它（调用方应放弃粘贴）。
    /// </summary>
    private static Snapshot? Capture()
    {
        var snap = new Snapshot();
        try
        {
            IDataObject? src = Clipboard.GetDataObject();

            // 原本有文本就记下来，万一完整快照还原失败，至少把文字还回去
            if (Clipboard.ContainsText()) snap.Text = Clipboard.GetText();

            if (src == null) return snap;                       // 空剪贴板

            string[] formats = src.GetFormats(autoConvert: false);
            if (formats.Length == 0) return snap;

            var copy = new DataObject();
            var kept = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string format in formats)
            {
                try
                {
                    if (src.GetData(format, autoConvert: false) is not { } data) continue;

                    /* 有些格式（HTML / RTF / 位图）塞的是 MemoryStream，取出来时
                     * 位置已经在末尾 —— 原样写回去会得到长度为 0 的数据，用户
                     * 看到的是"格式还在、内容是空的"。统一复制一份从头读的。 */
                    if (data is MemoryStream stream) data = new MemoryStream(stream.ToArray());

                    copy.SetData(format, data, false);
                    kept.Add(format);
                }
                catch
                {
                    /* 单个格式取不到不影响其它格式；关键格式缺失会在下面被拦下 */
                }
            }

            if (kept.Count == 0)
            {
                CoreLog.Warn("[输入文本] 剪贴板内容无法快照，退回逐字注入");
                return null;
            }

            foreach (string needed in MustKeepFormats)
            {
                if (!formats.Contains(needed, StringComparer.OrdinalIgnoreCase)) continue;
                if (kept.Contains(needed)) continue;
                CoreLog.Warn($"[输入文本] 剪贴板里的 {needed} 无法快照，退回逐字注入（不留数据丢失的风险）");
                return null;
            }

            snap.Data = copy;
            return snap;
        }
        catch (Exception ex)
        {
            CoreLog.Warn("[输入文本] 剪贴板快照失败，退回逐字注入：" + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// 把剪贴板还回去。
    ///
    /// <paramref name="expectedSeq"/> 是写入我们的文本之后的序号 ——
    /// 现在不一致就说明期间别人改过剪贴板，直接放手不管。
    /// 传 null 表示不校验（只在"已经确认动过内容、必须还回去"时用）。
    /// </summary>
    private static void Restore(Snapshot snapshot, uint? expectedSeq)
    {
        if (expectedSeq is { } seq && GetClipboardSequenceNumber() != seq)
        {
            CoreLog.Info("[输入文本] 粘贴期间剪贴板被改写，跳过还原");
            return;
        }

        for (int attempt = 0; ; attempt++)
        {
            try
            {
                if (snapshot.Data == null) Clipboard.Clear();
                else Clipboard.SetDataObject(snapshot.Data, copy: true);

                if (snapshot.Data != null && !string.IsNullOrEmpty(snapshot.Text))
                    TagSelfWrite(snapshot.Text);
                return;
            }
            catch (ExternalException) when (attempt < RetryCount - 1)
            {
                Thread.Sleep(RetryGapMs);      // 剪贴板被别的程序短暂占用，正常现象
            }
            catch (Exception ex)
            {
                CoreLog.Warn("[输入文本] 还原剪贴板失败：" + ex.Message);
                RestoreTextOnly(snapshot.Text);
                return;
            }
        }
    }

    /// <summary>完整快照还原不了时的保底：至少把原来的文字放回去。</summary>
    private static void RestoreTextOnly(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            Clipboard.SetText(text);
            TagSelfWrite(text);
        }
        catch (Exception ex)
        {
            CoreLog.Warn("[输入文本] 还原剪贴板文本也失败：" + ex.Message);
        }
    }

    // ---------------- 剪贴板读写 ----------------

    /// <summary>写纯文本，占用重试（剪贴板随时可能被别的程序短暂锁住）。</summary>
    private static bool SetText(string text)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch (ExternalException) when (attempt < RetryCount - 1)
            {
                Thread.Sleep(RetryGapMs);
            }
            catch (Exception ex)
            {
                CoreLog.Warn("[输入文本] 写剪贴板失败：" + ex.Message);
                return false;
            }
        }
    }

    /// <summary>
    /// 等目标程序把剪贴板读走。
    ///
    /// 只靠固定延迟不够稳：Electron / 远程桌面这类程序处理粘贴消息偏慢，
    /// 还原早了它读到的就是旧内容（症状是"粘出来的是上一条"）。
    /// 所以先给一段基础时间，再等"没人持有剪贴板"为止 —— 程序读剪贴板时
    /// 必须先 OpenClipboard，读完立刻关，这个信号比猜时间可靠。
    /// </summary>
    private static void WaitForPasteConsumed()
    {
        Thread.Sleep(PasteSettleMs);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < ClipboardIdleWaitMs && GetOpenClipboardWindow() != IntPtr.Zero)
            Thread.Sleep(20);
    }
}
