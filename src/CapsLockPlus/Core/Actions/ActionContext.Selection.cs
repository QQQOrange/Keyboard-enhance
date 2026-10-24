using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using CapsLockPlus.Core.Input;
using CapsLockPlus.Core.Settings;
using CapsLockPlus.Core.Text;
using CapsLockPlus.Core.Windows;
using static CapsLockPlus.Core.Interop.NativeMethods;

namespace CapsLockPlus.Core.Actions;

/// <summary>
/// 选区读取、选中即搜、智能打开、打字与 Tab 脚本。与 ActionContext 同属 partial 类。
/// </summary>
public sealed partial class ActionContext
{

    public bool UndoRedo()
    {
        if (_undoNext) { InputSender.Ctrl(0x59); }   // Ctrl+Y
        else { InputSender.Ctrl(0x5A); }             // Ctrl+Z
        _undoNext = !_undoNext;
        return true;
    }

    /// <summary>
    /// 用指定搜索引擎在默认浏览器里搜索**当前选中的文字**。
    ///
    /// 与「Qbar 搜索」的区别：不弹面板，直接拿选区去搜。
    /// 适合"看到一段文字，按一下就查"这种高频动作。
    /// </summary>
    /// <param name="engineKey">搜索引擎关键字（bd / zh / go…）</param>
    /// <param name="overrideText">自检用：直接指定要搜索的文字</param>
    public bool SearchSelection(string engineKey, string? overrideText = null)
    {
        var entry = Settings().Search.FirstOrDefault(x =>
            x.Key.Equals(engineKey, StringComparison.OrdinalIgnoreCase));

        if (entry == null || string.IsNullOrWhiteSpace(entry.Value))
        {
            Notify("这个搜索引擎还没配置");
            return true;
        }

        /* ⚠ 这是"直接搜索按了没反应"的**真正原因**，别再改回去：
         *
         * 按键绑定里 search:bd 的 Arg 默认是**空字符串**（不是 null）。
         * 而 "" ?? xxx 的结果是 ""（?? 只认 null）——
         * 于是 overrideText 拿着一个空串，后面"去读选区"的分支永远走不到，
         * 直接落到"没有选中的文字"就 return 了：
         * 既不取词，也不开浏览器，日志里连一条 [取词] 都不会有。
         *
         * v3.0 到 v3.4 换了三套取词方案都没修好，就是因为它不在取词通道上，
         * 而在参数判断这一步。所以：空白参数一律归一成 null，回退去读选区。 */
        if (string.IsNullOrWhiteSpace(overrideText)) overrideText = null;

        string? text = overrideText;

        if (text == null)
        {
            text = CaptureSelectionText();
            if (!string.IsNullOrWhiteSpace(text))
                CoreLog.Info($"[搜索] 已取到选中文字 长度={text.Length}");
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            /* 所有程序化通道都失败时的**保底**：请用户亲自按一下 Ctrl+C。
             * 真实按键不会被 Chromium / Electron 过滤，也不受 UIPI 影响，
             * 拿到内容就立刻用它搜索 —— 这样"直接搜索"在任何程序里都能用。 */
            Notify(Core.ElevationHelper.IsElevated
                ? "没读到选中文字（管理员模式取不到普通程序的选区）。请按一下 Ctrl+C，我马上用它搜索"
                : "没读到选中文字，请按一下 Ctrl+C，我马上用它搜索");

            var waitManualCopy = ManualCopyWaitForTest
                                 ?? new Func<int, string>(Text.TextActions.WaitForManualCopy);
            text = waitManualCopy(5000);
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            Notify("没有取到选中的文字 —— 先选中要搜索的内容再按这个键");
            return true;
        }

        text = text.Trim();

        // 太长的话不截断会撑爆 GET 请求长度，也会让搜索页体验很差
        const int maxLen = 1000;
        if (text.Length > maxLen) text = text[..maxLen];

        string encoded = Uri.EscapeDataString(text);
        string url;
        if (entry.Value.Contains("{q}", StringComparison.Ordinal))
        {
            url = entry.Value.Replace("{q}", encoded);
        }
        else
        {
            /* 自定义引擎忘了写 {q} 时，不能把关键词默默丢掉 ——
             * 那样只会打开搜索引擎首页，用户看到的就是"没搜"。
             * 按最常见的约定补一个 q 参数。 */
            CoreLog.Warn($"[搜索] 引擎 {engineKey} 的地址里没有 {{q}}，改为追加 q 参数");
            url = entry.Value + (entry.Value.Contains('?') ? "&q=" : "?q=") + encoded;
        }

        if (!url.Contains("://", StringComparison.Ordinal))
            url = "https://" + url;

        Notify(overrideText == null ? "正在搜索选中的文字…" : "正在搜索…");
        CoreLog.Info($"[搜索] {engineKey} → {url}");
        OpenUrlRaw(url);   // 已编码，跳过规范化避免搜索词被解码/截断
        return true;
    }

    /// <summary>
    /// 取当前选区文字：主动取词优先，其次用「选中即捕获」的新鲜结果。
    ///
    /// 顺序是刻意这样排的：
    ///   ① 主动取词不需要用户额外操作，是"选中 + 按键"一步到位的正路；
    ///   ② 监听器的结果只在**几秒内**才用，否则会搜到很久以前复制的内容。
    /// 两条都拿不到才交给调用方去请用户手动 Ctrl+C。
    /// </summary>
    private string CaptureSelectionText()
    {
        if (CaptureTextForTest != null) return CaptureTextForTest() ?? "";

        // ① UIA → WM_COPY → SendInput → 线程级注入 → Ctrl+Insert
        string text = Text.TextActions.GetSelectedText();
        if (!string.IsNullOrWhiteSpace(text)) return text;

        // ② 用户几秒内自己按过 Ctrl+C，就别再折腾了
        string? fresh = SelectionWatcher?.LastSelectionWithin(TimeSpan.FromSeconds(6));
        if (!string.IsNullOrWhiteSpace(fresh))
        {
            CoreLog.Info($"[搜索] 用选区监听器捕获的内容 长度={fresh.Length}");
            return fresh;
        }

        return string.Empty;
    }

    /// <summary>
    /// 把一段文字直接输入到当前焦点处。
    ///
    /// 走 InputSender.Text（逐字符 Unicode 发送），不用剪贴板 ——
    /// 既不会破坏用户剪贴板内容，也能正常走 IME。
    /// </summary>
    /// <summary>
    /// 「选中即捕获」监听器。用户在任何程序里 Ctrl+C 时，
    /// 这里就存下了选中的文字 —— 比主动取词可靠得多
    /// （Electron / Chromium 会过滤合成输入，但用户的真实按键不会被拦）。
    /// </summary>
    public Core.Text.SelectionWatcher? SelectionWatcher { get; set; }

    /// <summary>
    /// 自检用：替换真实的取词通道，直接返回指定文字。
    /// 自检跑在没有真实选区的环境里，必须能绕开剪贴板/UI Automation。
    /// </summary>
    public Func<string?>? CaptureTextForTest { get; set; }

    /// <summary>
    /// 自检用：替换"请用户按 Ctrl+C 再等待"的保底逻辑，避免自检真的卡 4 秒。
    /// </summary>
    public Func<int, string>? ManualCopyWaitForTest { get; set; }

    public bool TypeText(string text)
    {
        if (string.IsNullOrEmpty(text)) return true;

        try
        {
            // 太长会有明显延迟（逐字符发送），限制一下避免误操作
            const int maxLen = 2000;
            string payload = text.Length > maxLen ? text[..maxLen] : text;

            /* 输入方式跟着设置走：
             *   auto（默认）→ 纯 ASCII 用真实按键（密码框认这个），其它 Unicode；
             *   keys        → 强制真实按键；
             *   unicode     → 强制 Unicode 注入。
             * 老配置里没有这个字段，反序列化后是默认值 auto，行为即"修好密码框"。 */
            string mode = Settings().Global.TextInputMode ?? "auto";
            var input = mode.ToLowerInvariant() switch
            {
                "keys" => InputSender.TextMode.Keys,
                "unicode" => InputSender.TextMode.Unicode,
                _ => InputSender.TextMode.Auto,
            };

            InputSender.Text(payload, input);
        }
        catch (Exception ex)
        {
            Notify(UiText.Failed("输入文字", ex.Message));
            CoreLog.Warn("输入文字失败：" + ex.Message);
        }
        return true;
    }

    /// <summary>
    /// 「直达链接」：把当前**选中的文字**识别成网址 / 文件夹 / 文件并打开。
    ///
    /// 与 <see cref="OpenUrl"/>、<see cref="OpenFolder"/> 的区别：
    /// 那两个是"先填地址、按键就开"，目标在设置里写死；
    /// 这个是"眼前有什么就开什么"，用户选中一段文字按下即可，不用预先配置。
    ///
    /// 也正因为目标来自选区，它被归到「系统」分类，和「打开网址」「运行程序」并列 ——
    /// 三者都是"给什么开什么"，只是参数来源不同（手填 / 选区）。
    /// </summary>
    /// <param name="overrideText">自检用：直接指定"选中的文字"，绕开真实取词。</param>
    public bool SmartOpen(string? overrideText = null)
    {
        string text = overrideText ?? "";

        if (string.IsNullOrWhiteSpace(text))
        {
            text = CaptureSelectionText();
            if (!string.IsNullOrWhiteSpace(text))
                CoreLog.Info($"[直达链接] 已取到选中文字 长度={text.Length}");
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            /* 取词失败时的保底：请用户亲手按一下 Ctrl+C。
             *
             * 这不是偷懒 —— Chromium / Electron 会过滤程序合成的 Ctrl+C，
             * 管理员模式下还会被 UIPI 拦掉，只有"真人按的"一定有效。
             * 和「直接搜索」用的是同一条保底路径。 */
            Notify(Core.ElevationHelper.IsElevated
                ? "没读到选中内容（管理员模式取不到普通程序的选区）。请按一下 Ctrl+C，我马上打开它"
                : "没读到选中内容，请按一下 Ctrl+C，我马上打开它");

            var waitManualCopy = ManualCopyWaitForTest
                                 ?? new Func<int, string>(Text.TextActions.WaitForManualCopy);
            text = waitManualCopy(4000);
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            Notify("先选中一段文字（网址、文件夹或文件路径），再按这个键");
            return true;
        }

        var hit = SmartTargetDetector.Detect(text);
        CoreLog.Info($"[直达链接] 识别结果={hit.Kind} 目标={hit.Target} 原文长度={text.Length}");

        switch (hit.Kind)
        {
            case SmartKind.Url:
                Notify($"打开网址：{UrlUtil.HostOf(hit.Target)}");
                OpenUrl(hit.Target);
                break;

            case SmartKind.Folder:
                Notify("正在打开文件夹…");
                OpenFolder(hit.Target);
                break;

            case SmartKind.File:
                Notify("正在用默认程序打开…");
                OpenFile(hit.Target);
                break;

            case SmartKind.MissingFolder:
                Notify($"文件夹不存在：{hit.Target}");
                break;

            case SmartKind.MissingFile:
                Notify($"文件不存在：{hit.Target}");
                break;

            default:
                /* 选中了普通文字（一句话、一个单词）。不要试图去搜索 ——
                 * 这个动作的名字是「直达链接」，猜错了打开浏览器比"什么都不做"更烦人。 */
                Notify("选中的内容既不是网址，也不是文件夹或文件路径");
                break;
        }

        return true;
    }

    /// <summary>Caps+Tab：把光标前的算式算出结果 / 展开热字串。</summary>
    public bool RunTabScript()    {
        var settings = Settings();
        string selected = Text.TextActions.GetSelectedText();
        if (!string.IsNullOrEmpty(selected))
        {
            string replaced = ExpandHotString(selected, settings);
            if (replaced == selected && ExpressionEvaluator.TryEvaluate(selected.TrimEnd('='), out var v, out _))
                replaced = ExpressionEvaluator.Format(v);
            if (replaced != selected) Text.TextActions.ReplaceSelection(replaced);
            return true;
        }

        // 没有选区：选中光标左边这一“词”，尝试计算 / 展开
        Text.TextActions.SelectLineToCaret(out string left);
        if (string.IsNullOrEmpty(left)) return true;

        string expanded = ExpandHotString(left, settings);
        if (expanded != left)
        {
            Text.TextActions.ReplaceSelection(expanded);
            return true;
        }

        string trimmed = left.TrimEnd('=', ' ');
        if (ExpressionEvaluator.TryEvaluate(trimmed, out double value, out string err))
        {
            string result = ExpressionEvaluator.Format(value);
            Notify($"{trimmed} = {result}");
            Text.TextActions.ReplaceSelection(left.TrimEnd().EndsWith('=') ? left + result : result);
            return true;
        }

        Notify(UiText.Failed("算出结果", err));
        return true;
    }

    private static string ExpandHotString(string text, AppSettings settings)
    {
        foreach (var hs in settings.HotStrings)
        {
            if (string.IsNullOrEmpty(hs.Key)) continue;
            if (text.EndsWith(hs.Key, StringComparison.Ordinal))
                return text[..^hs.Key.Length] + hs.Value.Replace("\\n", Environment.NewLine);
        }
        return text;
    }
}