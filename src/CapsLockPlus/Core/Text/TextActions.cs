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
    /// <summary>
    /// 剪贴板读写必须串行。取词（先存原文再还原）、替换选区、以及「输入文本」的
    /// 长文本粘贴都在动系统剪贴板 —— 动作队列和宏播放是两个线程，撞上就会
    /// 互相把对方的内容覆盖掉。这个锁是**共用**的，别在别处另开一个。
    /// </summary>
    internal static readonly object ClipGate = new();

    /// <summary>一次“复制”最多等多久（毫秒）。</summary>
    private const int DefaultTimeoutMs = 420;   // 浏览器复制可能较慢，放宽到 420ms

    /// <summary>
    /// 取当前选中的文本；没有选区或取不到时返回空串。
    ///
    /// **三条通道依次尝试**，因为每条都有各自的失效场景：
    ///
    ///   ① **UI Automation**（首选）
    ///      跨进程 COM 调用，直接问程序"你现在选了什么"。
    ///      不碰剪贴板、不模拟按键 —— 因此**不受 UIPI 限制**。
    ///      这是"以管理员身份运行时取词全都不工作"的正解：
    ///      管理员进程用 SendInput 往普通权限的浏览器发按键，会被系统拦掉。
    ///      覆盖：Chromium 系浏览器、Electron、Office、现代编辑器。
    ///
    ///   ② **WM_COPY 窗口消息**
    ///      同样不走输入队列，UIPI 拦不住。用于 UIA 没实现 TextPattern 的程序。
    ///
    ///   ③ **SendInput 模拟 Ctrl+C**
    ///      老路子，只在同权限（或本程序未提权）时有效。
    ///      带扫描码、四次分开发（Chromium / Qt 系才认）。
    ///
    /// 每次尝试都用剪贴板序号严格确认"真的复制到了"，
    /// 且结束后恢复用户原有剪贴板。
    /// </summary>
    public static string GetSelectedText(int timeoutMs = DefaultTimeoutMs)
    {
        /* ===== 分步诊断 =====
         * 每一步都记日志，定位"到底哪一步失败"用。
         * 用户反馈过好几次"取不到选区"，靠推理已经不可靠了。 */
        string fgTitle = "?";
        string fgClass = "?";
        IntPtr fgHwnd = IntPtr.Zero;
        try
        {
            fgHwnd = Core.Windows.WindowService.Foreground;
            var info = Core.Windows.WindowService.Describe(fgHwnd);
            if (info != null) { fgTitle = info.Title; fgClass = info.ClassName; }
        }
        catch { }   /* 预期内：探测/重试型，失败即视为无结果 */
        CoreLog.Info($"[取词] 前台 hwnd=0x{fgHwnd.ToInt64():X} class={fgClass} title={fgTitle}");

        // ① UIA：不碰剪贴板，最干净
        string viaUia = UiaSelectionReader.GetSelectedText();
        CoreLog.Info($"[取词] UIA 结果长度={viaUia?.Length ?? -1} 内容={Trim4(viaUia)}");
        if (!string.IsNullOrWhiteSpace(viaUia)) return viaUia;

        lock (ClipGate)
        {
            string old = string.Empty;
            bool hadText = false;
            try
            {
                if (Clipboard.ContainsText()) { old = Clipboard.GetText(); hadText = true; }
            }
            catch { }   /* 预期内：探测/重试型，失败即视为无结果 */

            /* 记住"我们自己复制出来的内容"。
             * 收尾还原剪贴板时要用它判断：如果剪贴板里已经不是这段文字，
             * 说明期间用户自己按了 Ctrl+C（或者别的程序写了剪贴板），
             * 这时**绝对不能**还原，否则会把用户刚复制的东西搞丢。 */
            string? copiedByUs = null;

            try
            {
                // ② WM_COPY：走窗口消息，绕过输入队列
                bool posted = WindowMessageCopier.PostCopyToFocus();
                CoreLog.Info($"[取词] WM_COPY 已投递={posted}");
                string text = TryCopyVia(() => WindowMessageCopier.PostCopyToFocus(), timeoutMs);
                CoreLog.Info($"[取词] WM_COPY 后剪贴板结果长度={text.Length}");
                if (!string.IsNullOrEmpty(text)) { copiedByUs = text; return text; }

                // ③ SendInput：同权限时有效（0x43 = C）
                text = TryCopyVia(() => InputSender.CopyChord(0x43), timeoutMs);
                if (!string.IsNullOrEmpty(text)) { copiedByUs = text; return text; }

                /* ④ 线程级注入：attach 到目标输入队列 + 同步键盘状态。
                 * 这是最接近真实键盘的一条路，用于 SendInput 被程序过滤的情况。 */
                string viaThread = TryCopyVia(() => ThreadKeyboardSender.SendCtrlChord(0x43), timeoutMs);
                CoreLog.Info($"[取词] 线程级注入结果长度={viaThread.Length}");
                if (!string.IsNullOrEmpty(viaThread)) { copiedByUs = viaThread; return viaThread; }

                // ⑤ 再试 Ctrl+Insert（终端、部分老程序只认这个）
                string last = TryCopyVia(() => InputSender.CopyChord(VK.Insert), timeoutMs);
                CoreLog.Info($"[取词] 最终结果长度={last.Length} 内容={Trim4(last)}");
                if (!string.IsNullOrEmpty(last)) copiedByUs = last;
                return last;
            }
            catch (Exception ex)
            {
                CoreLog.Warn("读取选区失败：" + ex.Message);
                return string.Empty;
            }
            finally
            {
                RestoreClipboard(old, hadText, copiedByUs);
            }
        }
    }

    /// <summary>
    /// 发一次复制快捷键，等剪贴板序号变化，返回新内容（失败返回空串）。
    ///
    /// **不预先清空剪贴板** —— 清空会让前台程序收到"剪贴板被占用"的干扰，
    /// 浏览器尤其容易因此丢弃复制请求。改用"序号是否变化"来判断，
    /// 只要变了就说明这次复制确实发生了。
    /// </summary>
    private static string TryCopyVia(Action sendCopy, int timeoutMs)
    {
        uint before;
        try { before = GetClipboardSequenceNumber(); }
        catch { return string.Empty; }

        try { sendCopy(); } catch { return string.Empty; }

        // 给前台程序一点时间处理按键（浏览器可能要跑一小段 JS/渲染）
        Thread.Sleep(35);

        uint afterSeq = before;
        try { afterSeq = GetClipboardSequenceNumber(); } catch { }   /* 预期内：探测/重试型，失败即视为无结果 */
        bool changed = afterSeq != before;
        CoreLog.Info($"[取词] 剪贴板序号 before={before} after={afterSeq} 变化={changed}");
        if (!changed) return string.Empty;

        /* 序号变了 = 复制确实发生。但内容可能还在路上：
         * 再等一小段，并重试读取，避免"序号变了但读不到文本"。 */
        for (int i = 0; i < 12; i++)
        {
            try
            {
                if (Clipboard.ContainsText())
                {
                    string s = Clipboard.GetText();
                    if (!string.IsNullOrEmpty(s)) return s;
                }
            }
            catch { }   /* 预期内：探测/重试型，失败即视为无结果 */
            Thread.Sleep(12);
        }
        return string.Empty;
    }

    private static string SafeContainsText()
    {
        try { return Clipboard.ContainsText() ? Clipboard.GetText() : "(无文本)"; }
        catch (Exception ex) { return "(异常:" + ex.Message + ")"; }
    }

    private static string Trim4(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "(空)";
        s = s.Replace("\r", " ").Replace("\n", " ");
        return s.Length <= 40 ? s : s[..40] + "…";
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
            catch { }   /* 预期内：探测/重试型，失败即视为无结果 */
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
        catch { }   /* 预期内：探测/重试型，失败即视为无结果 */
    }

    /// <summary>
    /// 取词结束后还原剪贴板。
    ///
    /// <paramref name="copiedByUs"/> 是我们刚才复制出来的内容。还原前先确认
    /// 剪贴板里**还是**这段文字 —— 如果不是，说明期间用户自己按了 Ctrl+C
    /// （或别的程序写了剪贴板），此时还原等于把用户刚复制的东西擦掉，
    /// 属于**帮倒忙**，所以直接放手不管。
    /// </summary>
    private static void RestoreClipboard(string old, bool hadText, string? copiedByUs)
    {
        if (copiedByUs != null)
        {
            try
            {
                string current = Clipboard.ContainsText() ? Clipboard.GetText() : "";
                if (!string.Equals(current, copiedByUs, StringComparison.Ordinal))
                {
                    CoreLog.Info("[取词] 剪贴板已被用户/其它程序改写，跳过还原");
                    return;
                }
            }
            catch { return; }
        }
        RestoreClipboard(old, hadText);
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
            catch { }   /* 预期内：探测/重试型，失败即视为无结果 */

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
                catch { }   /* 预期内：探测/重试型，失败即视为无结果 */
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
            catch { }   /* 预期内：探测/重试型，失败即视为无结果 */

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

    /// <summary>
    /// 最后的保底通道：请用户**亲自**按一下 Ctrl+C，我们顺手接住。
    ///
    /// 为什么这条一定有效：程序合成的 Ctrl+C 会被 Chromium / Electron 过滤，
    /// 也可能被 UIPI 拦掉，但**用户真实按下的 Ctrl+C 谁也拦不住**。
    ///
    /// 实现上是"旁观"而不是"参与"：只盯剪贴板序号，一变就读内容。
    /// 期间不写剪贴板，所以不会打乱用户自己的剪贴板。
    /// </summary>
    /// <param name="timeoutMs">最多等多久</param>
    /// <returns>用户复制到的文字；超时返回空串。</returns>
    public static string WaitForManualCopy(int timeoutMs = 4000)
    {
        uint seq;
        try { seq = GetClipboardSequenceNumber(); }
        catch { return string.Empty; }

        long deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            Thread.Sleep(40);

            uint now;
            try { now = GetClipboardSequenceNumber(); }
            catch { return string.Empty; }
            if (now == seq) continue;

            CoreLog.Info("[取词] 检测到剪贴板变化（视为用户手动复制）");

            /* 序号变了不等于数据已经写好：浏览器 / OLE 可能是先占位再填。
             * 给它最多 ~480ms，读到非空文本就走。 */
            for (int i = 0; i < 12; i++)
            {
                Thread.Sleep(40);
                try
                {
                    if (Clipboard.ContainsText())
                    {
                        string text = Clipboard.GetText();
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            CoreLog.Info($"[取词] 手动复制成功 长度={text.Length} 内容={Trim4(text)}");
                            return text;
                        }
                    }
                }
                catch { }   /* 预期内：探测/重试型，失败即视为无结果 */
            }

            // 这次变化读不出文本（可能复制的是图片/文件），继续等下一次
            seq = now;
        }

        CoreLog.Info("[取词] 等待手动复制超时");
        return string.Empty;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    public enum CaseMode { Upper, Lower, Title }
}
