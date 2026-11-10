using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Automation;

namespace CapsLockPlus.Core.Text;

/// <summary>
/// 用 UI Automation 直接读取当前选区。
///
/// 这是**不依赖剪贴板、不依赖模拟按键**的取词方式，因此：
///   · 不会改动用户剪贴板
///   · **不受 UIPI 限制** —— 以管理员身份运行时，
///     SendInput 往非管理员的浏览器 / 编辑器投递按键会被系统拦掉，
///     而 UIA 是跨进程 COM 调用，不经过输入队列，照样能读到选区。
///
/// 两个必须做对的细节（参考 QuickSearch 的实测结论）：
///
///  ① **向上遍历父元素**
///     焦点元素常常是个 Pane / 自定义控件，本身没有 TextPattern。
///     要沿父链往上找（最多 10 层）才能找到真正带文本模式的元素。
///
///  ② **先「唤醒」Chromium 的无障碍树**
///     Chromium（Chrome / Edge / WebView2 / Electron）是**懒加载**的：
///     只有检测到「有 UIA 客户端在听」才真正构建无障碍树。
///     被动查询打到的永远是桌面 —— 这就是不唤醒时浏览器里永远读不到的原因。
///
///  ③ **调用方必须有硬超时**（v4.20.5）
///     UIA 是跨进程 COM 调用：目标程序卡死（Electron 卡 UI 线程、远程桌面里的
///     窗口无响应）时，`FocusedElement` / `TryGetCurrentPattern` 会一直等下去。
///     这条路径跑在**单线程动作队列**上，卡住就是"所有快捷键几秒钟没反应"。
///     现在所有 UIA 调用都在一条常驻 STA 工作线程上跑，调用方只等固定预算；
///     超时后丢弃结果、并让 UIA 通道冷却一段时间，直接走剪贴板通道。
/// </summary>
public static class UiaSelectionReader
{
    /// <summary>最多向上找几层父元素（再多就是桌面/窗口框架了，没意义）。</summary>
    private const int MaxParentLevels = 10;

    /// <summary>默认等待预算（毫秒）。正常 UIA 查询在 10~80ms 内返回。</summary>
    public const int DefaultTimeoutMs = 600;

    /// <summary>一次超时后，UIA 通道冷却多久（毫秒）。</summary>
    public const int SkipAfterTimeoutMs = 60_000;

    /// <summary>最多允许排队几个请求（工作线程卡住时别让队列无限长）。</summary>
    private const int MaxQueued = 4;

    private static readonly BlockingCollection<Request> Pending = new(new ConcurrentQueue<Request>(), MaxQueued);
    private static readonly object WorkerGate = new();
    private static Thread? _worker;
    private static long _skipUntilMs;

    private sealed class Request
    {
        public readonly ManualResetEventSlim Done = new(false);
        public string Result = "";
    }

    /// <summary>
    /// 读当前选区文本。取不到（或超时）返回空串，绝不抛异常。
    /// 可以在任意线程调用，内部会转到 STA 工作线程。
    /// </summary>
    public static string GetSelectedText(int timeoutMs = DefaultTimeoutMs)
    {
        if (Environment.TickCount64 < Volatile.Read(ref _skipUntilMs)) return "";

        if (!EnsureWorker())
        {
            /* 工作线程起不来（极老系统 / 线程创建被拒）：退回同步调用。
             * 这时超时保护失效，但至少功能还在，且发生在异常环境里。 */
            return GetSelectedTextCore();
        }

        var req = new Request();
        if (!Pending.TryAdd(req)) return "";     // 上一批还没消化：本次直接放弃

        if (req.Done.Wait(Math.Max(50, timeoutMs))) return req.Result;

        /* 超时：目标程序的 UIA 调用卡住了。工作线程会在将来某刻返回，结果直接丢弃。
         * 冷却一段时间，避免用户连按热键时每次都往卡死的进程里塞一次 600ms。 */
        Volatile.Write(ref _skipUntilMs, Environment.TickCount64 + SkipAfterTimeoutMs);
        CoreLog.Warn($"UIA 取词 {timeoutMs}ms 未返回（目标程序可能无响应），已跳过该通道 " +
                     $"{SkipAfterTimeoutMs / 1000} 秒");
        return "";
    }

    /// <summary>确保常驻 UIA 工作线程已启动。返回 false 表示没能启动。</summary>
    private static bool EnsureWorker()
    {
        if (_worker != null) return true;
        lock (WorkerGate)
        {
            if (_worker != null) return true;
            try
            {
                var thread = new Thread(() =>
                {
                    foreach (var req in Pending.GetConsumingEnumerable())
                    {
                        string result = "";
                        try { result = GetSelectedTextCore(); }
                        catch (Exception ex) { CoreLog.Swallow(ex, "UIA 取词"); }
                        req.Result = result;
                        req.Done.Set();
                    }
                })
                {
                    IsBackground = true,
                    Name = "CapsLockPlus.UIA",
                };
                thread.SetApartmentState(ApartmentState.STA);   // UIAutomationClient 要求 STA
                thread.Start();
                _worker = thread;
                return true;
            }
            catch (Exception ex)
            {
                CoreLog.Warn("UIA 工作线程启动失败，退回同步调用：" + ex.Message);
                return false;
            }
        }
    }

    /// <summary>真正的 UIA 读取（始终在 STA 工作线程上执行）。</summary>
    private static string GetSelectedTextCore()
    {
        try
        {
            // ① 唤醒无障碍树（对 Chromium 系是必需的）
            WakeAccessibilityTree();

            // ② 收集候选元素：焦点元素 + 前台窗口根元素
            var candidates = new List<AutomationElement>();
            try
            {
                var focused = AutomationElement.FocusedElement;
                if (focused != null) candidates.Add(focused);
            }
            catch { }   /* 预期内：探测/重试型，失败即视为无结果 */

            try
            {
                IntPtr fg = Core.Windows.WindowService.Foreground;
                if (fg != IntPtr.Zero)
                {
                    var root = AutomationElement.FromHandle(fg);
                    if (root != null) candidates.Add(root);
                }
            }
            catch { }   /* 预期内：探测/重试型，失败即视为无结果 */

            /* ②b UIA SelectionPattern：某些程序（Win11 记事本等）只在
             * SelectionPattern 里暴露选中项，没有 TextPattern。 */
            foreach (var c in candidates)
            {
                try
                {
                    if (c.TryGetCurrentPattern(SelectionPattern.Pattern, out var selRaw))
                    {
                        var sp = (SelectionPattern)selRaw;
                        var items = sp.Current.GetSelection();
                        if (items.Length > 0)
                        {
                            var sb2 = new System.Text.StringBuilder();
                            foreach (var it in items)
                            {
                                try { sb2.Append(it.Current.Name); } catch { }   /* 预期内：探测/重试型，失败即视为无结果 */
                            }
                            if (sb2.Length > 0) return sb2.ToString();
                        }
                    }
                }
                catch { }   /* 预期内：探测/重试型，失败即视为无结果 */
            }

            // ③ 沿父链找带 TextPattern 的元素
            foreach (var start in candidates)
            {
                AutomationElement? cur = start;
                for (int level = 0; level < MaxParentLevels && cur != null; level++)
                {
                    if (cur.TryGetCurrentPattern(TextPattern.Pattern, out var raw))
                    {
                        if (raw is TextPattern pattern)
                        {
                            string text = ReadSelection(pattern);
                            if (!string.IsNullOrEmpty(text)) return text;
                        }
                    }

                    try { cur = TreeWalker.ControlViewWalker.GetParent(cur); }
                    catch { break; }
                }
            }

            /* 走到这儿说明：沿父链确实找到过 TextPattern，但选区是空的。
             * 这与"完全没找到 TextPattern"对调用方是同一件事 ——
             * 都只能退回模拟复制，所以统一返回空串，不额外区分。 */
            return "";
        }
        catch
        {
            // 取词失败不该影响用户操作，静默返回
            return "";
        }
    }

    /// <summary>读 TextPattern 的所有选区，拼成一段文本。</summary>
    private static string ReadSelection(TextPattern pattern)
    {
        try
        {
            var ranges = pattern.GetSelection();
            if (ranges.Length == 0) return "";

            var sb = new System.Text.StringBuilder();
            foreach (var range in ranges)
            {
                try
                {
                    string piece = range.GetText(-1);
                    if (!string.IsNullOrEmpty(piece)) sb.Append(piece);
                }
                catch { }   /* 预期内：探测/重试型，失败即视为无结果 */
            }
            return sb.ToString();
        }
        catch { return ""; }
    }

    // ---- 唤醒 Chromium 无障碍树 ----

    private static bool _woken;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeoutW(
        IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam,
        uint flags, uint timeout, out IntPtr result);

    private const uint WM_GETOBJECT = 0x003D;
    private const uint OBJID_CLIENT = 0xFFFFFFFC;
    private const uint SMTO_ABORTIFHUNG = 0x0002;

    /// <summary>
    /// 唤醒 Chromium / Electron 的无障碍树。
    ///
    /// 两条路都走：
    ///   ① 订阅焦点变化事件 → 会话级「有 UIA 客户端在听」置位，
    ///      Chromium 轮询到之后把树建起来
    ///   ② 直接 WM_GETOBJECT 要 provider，强制实例化
    ///
    /// 只做一次（进程内缓存），避免每次取词都重复唤醒。
    /// </summary>
    private static void WakeAccessibilityTree()
    {
        if (_woken) return;
        _woken = true;

        // ① 事件订阅：注册一个空的焦点变化监听器
        try
        {
            Automation.AddAutomationFocusChangedEventHandler((s, e) => { });
        }
        catch { }   /* 预期内：探测/重试型，失败即视为无结果 */

        // ② WM_GETOBJECT：直接找窗口要 provider
        try
        {
            IntPtr fg = Core.Windows.WindowService.Foreground;
            if (fg != IntPtr.Zero)
            {
                SendMessageTimeoutW(fg, WM_GETOBJECT, new IntPtr(OBJID_CLIENT),
                    IntPtr.Zero, SMTO_ABORTIFHUNG, 200, out _);
            }
        }
        catch { }   /* 预期内：探测/重试型，失败即视为无结果 */

        // Chromium 建树是异步的，给它一点时间
        try { System.Threading.Thread.Sleep(60); } catch { }   /* 预期内：探测/重试型，失败即视为无结果 */
    }

    /// <summary>自检用：纯函数形式的冷却判定（避免依赖真实时钟）。</summary>
    internal static bool IsSkippingAt(long nowMs, long untilMs) => nowMs < untilMs;

    /// <summary>自检用：清掉冷却，避免一次超时影响后续取词用例。</summary>
    internal static void ResetSkipForTest() => Volatile.Write(ref _skipUntilMs, 0);
}
