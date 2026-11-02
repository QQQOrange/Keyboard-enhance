using System.Diagnostics;
using CapsLockPlus.Core.Settings;
using CapsLockPlus.Core.Windows;

namespace CapsLockPlus.Core.Actions;

/// <summary>
/// 「切换窗口」动作的实现：按**程序路径**找到它的窗口，
/// 切过去 / 已在前台则最小化 / 没开就启动并等窗口出现。
///
/// ## 为什么只按程序路径
///
/// 上游 CapsLock+ 用 10 个固定槽位（Caps+1~0），用户得先"把当前窗口绑到槽位 3"。
/// 这一版取消了槽位：程序路径直接存在 <c>KeyBinding.Arg</c> 里，动作 ID 是 "winBind"。
/// 于是它和别的动作完全同构 —— 一样出现在「按键映射」的功能下拉里、
/// 一样按层独立、一样能导入导出，而且**不需要额外的绑定步骤**。
///
/// 旧的槽位实现（Bind/Activate/BindByExe/…）在 4.5.59 里删掉了：
/// 它们只被自检调用，运行时零调用，留着只会让后来的人以为还有第二个入口。
/// </summary>
public sealed class WindowBindingService
{
    private readonly object _gate = new();

    /* 上次激活的是**哪个窗口**（句柄），而不是"第几个"。
     *
     * 存下标看着简单，但窗口列表是按句柄排序的：用户关掉一个窗口，
     * 后面的下标全部前移一位 —— 再按窗口键就会"跳着走"，
     * 或者刚切过的那个窗口又被切一次。存句柄就免疫这种变化：
     * 找不到（窗口关了）就当从头开始。 */
    private readonly Dictionary<int, long> _lastActivated = new();

    public event Action<string>? Notified;

    /// <summary>
    /// 仅供自检：拦截"启动程序"这一步，返回是否假装启动成功。
    /// 置为 null 时走真实的 Process.Start。
    /// </summary>
    public static Func<string, bool>? LaunchInterceptForTest { get; set; }

    /// <summary>
    /// 两个句柄是否指向同一个顶层窗口。
    ///
    /// 前台窗口可能是目标程序的子窗口 / 对话框（HWND 不同但同属一个根窗口），
    /// 这种情况也应视为"已在前台"，否则再按一次快捷键不会收起。
    /// </summary>
    private static bool IsSameWindow(IntPtr a, IntPtr b)
    {
        if (a == b) return true;
        if (a == IntPtr.Zero || b == IntPtr.Zero) return false;
        var ra = WindowService.RootOf(a);
        var rb = WindowService.RootOf(b);
        return ra == rb || ra == b || a == rb;
    }

    // ================= 任意按键绑定窗口（不占槽位） =================
    //
    // 上游 CapsLock+ 只有 10 个固定槽位（Caps+1~0）。这一版让**任意按键**
    // 都能直接绑一个程序，做法是把程序路径存在 KeyBinding.Arg 里，
    // 动作 ID 用 "winBind"。这样它就和别的动作完全同构：
    // 一样出现在「按键映射」的功能下拉里、一样按层独立、一样能导入导出。
    //
    // 下面这组方法只认 exe 路径。

    /// <summary>
    /// 激活（或启动）某个程序，用于"任意按键绑定窗口"。
    ///
    /// 行为与槽位版一致：
    ///   * 程序已在运行 → 切到它的窗口；已在前台则最小化（再按一次收起）；
    ///   * 多窗口 → 反复按循环切换；
    ///   * 程序没运行 → 启动它，并等窗口出现后再激活。
    /// </summary>
    /// <param name="exePath">可执行文件完整路径</param>
    /// <param name="stateKey">用于记忆"循环到第几个窗口"的键（通常是组合键 ID）</param>
    public bool ActivateExe(string exePath, string stateKey)
    {
        if (string.IsNullOrWhiteSpace(exePath))
        {
            Notified?.Invoke("这个按键还没选要绑定的程序");
            return false;
        }

        if (!File.Exists(exePath))
        {
            Notified?.Invoke(Loc.F("程序不存在：{0}", exePath));
            return false;
        }

        string name = Path.GetFileNameWithoutExtension(exePath);

        lock (_gate)
        {
            // 收集该程序当前所有可见顶层窗口
            var handles = WindowService.EnumerateTopLevel()
                .Where(w => string.Equals(w.ExePath, exePath, StringComparison.OrdinalIgnoreCase))
                .Select(w => w.Handle)
                .Where(h => h != IntPtr.Zero)
                .ToList();

            /* 按窗口句柄排序，保证**多次按键之间顺序稳定**。
             *
             * EnumWindows 返回的是 Z 序，用户每切一次窗口它就会变；
             * 不排序的话"循环到下一个窗口"会随机跳，看起来像乱切。
             * 句柄值在同一会话内是稳定且唯一的，拿来定序最合适。 */
            handles.Sort((a, b) => a.ToInt64().CompareTo(b.ToInt64()));

            if (handles.Count == 0)
            {
                // 没在运行 → 启动并等窗口出现
                LaunchAndTrackExe(exePath, name);
                return true;
            }

            var fg = WindowService.Foreground;

            /* 先把"最小化的窗口"单独挑出来。
             *
             * ⚠ 这是最容易漏的一处：用户按一次键把窗口最小化，再按一次
             * 期望它回来 —— 但如果直接对最小化窗口调 SetForegroundWindow，
             * Windows **不会**帮你还原，表现就是"按键没反应"。
             * 所以先挑第一个最小化的窗口，还原它。 */
            var minimized = handles.FirstOrDefault(h => WindowService.IsMinimized(h));
            if (minimized != IntPtr.Zero)
            {
                WindowService.Restore(minimized);
                WindowService.Activate(minimized);
                Notified?.Invoke(Loc.F("已恢复 {0}", name));
                return true;
            }

            // 单个窗口：前台就最小化，否则切过去
            if (handles.Count == 1)
            {
                var h = handles[0];
                if (IsSameWindow(fg, h))
                {
                    WindowService.Minimize(h);
                    Notified?.Invoke(Loc.F("已最小化 {0}", name));
                }
                else
                {
                    WindowService.Activate(h);
                    Notified?.Invoke(Loc.F("已切到 {0}", name));
                }
                return true;
            }

            // 多窗口：循环
            var ids = handles.Select(h => h.ToInt64()).ToList();
            int pick = CycleNext(StableHash(stateKey), ids, fg);
            WindowService.Activate(handles[pick]);
            Notified?.Invoke(Loc.F("已切到 {0}（{1} 个窗口循环）", name, handles.Count));
            return true;
        }
    }

    /// <summary>
    /// 多窗口循环：算出"下一个该切到哪个"的下标。
    ///
    /// 起点优先用**当前前台**在这个列表里的位置（用户可能自己点过其中一个窗口），
    /// 否则用上次切到的那个窗口；都找不到就从第一个开始。
    ///
    /// 以前这里是"存下标 + 加一"，两个坑：
    ///   ① 列表按句柄排序，关掉一个窗口后面全部前移，下标会指到别的窗口上（跳着走）；
    ///   ② 后面那段"又按回同一个就最小化"永远不成立 —— 加一之后必然不是前台那个，
    ///      所以多窗口的"再按一次收起"从来没生效过。循环就该老实循环。
    /// </summary>
    private int CycleNext(int key, List<long> ids, IntPtr fg)
    {
        int count = ids.Count;
        if (count == 0) return 0;

        int idx = ids.FindIndex(id => IsSameWindow(fg, new IntPtr(id)));
        if (idx < 0 && _lastActivated.TryGetValue(key, out long last))
            idx = ids.IndexOf(last);

        int next = NextIndex(idx, count);
        _lastActivated[key] = ids[next];
        return next;
    }

    /// <summary>
    /// "从 <paramref name="current"/> 往后数一个"的下标（纯函数，自检直接断言）。
    /// current 为 -1（还不知道在哪）时回到第一个；到末尾回绕。
    /// </summary>
    internal static int NextIndex(int current, int count)
        => count <= 0 ? 0 : (current + 1) % count;

    /// <summary>
    /// 字符串键的稳定哈希（不能用 string.GetHashCode —— 它每次进程启动都不同，
    /// 循环索引会莫名其妙复位）。
    /// </summary>
    private static int StableHash(string s)
    {
        unchecked
        {
            int h = 17;
            foreach (char c in s) h = h * 31 + c;
            return h;
        }
    }

    /// <summary>启动程序并等它的窗口出现后激活（任意按键版）。</summary>
    private void LaunchAndTrackExe(string exePath, string name)
    {
        if (LaunchInterceptForTest != null)
        {
            bool ok = LaunchInterceptForTest(exePath);
            Notified?.Invoke(ok ? Loc.F("正在启动 {0}…", name) : "启动失败：被拦截");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Notified?.Invoke(UiText.Failed("启动这个程序", ex.Message));
            CoreLog.Warn($"绑定窗口启动失败 {exePath}: {ex.Message}");
            return;
        }

        Notified?.Invoke(Loc.F("正在启动 {0}…", name));
        CoreLog.Info($"按键绑定：启动 {exePath}，等待窗口出现");

        var th = new Thread(() =>
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            IntPtr found = IntPtr.Zero;

            while (DateTime.UtcNow < deadline && found == IntPtr.Zero)
            {
                Thread.Sleep(350);
                var hit = WindowService.EnumerateTopLevel()
                    .FirstOrDefault(w => string.Equals(w.ExePath, exePath,
                        StringComparison.OrdinalIgnoreCase));
                if (hit is { } w2 && w2.Handle != IntPtr.Zero) found = w2.Handle;
            }

            if (found == IntPtr.Zero)
            {
                CoreLog.Warn($"按键绑定：等了 15 秒仍未看到 {name} 的窗口");
                Notified?.Invoke(Loc.F("{0} 好像还没启动完成，再按一次试试", name));
                return;
            }

            Thread.Sleep(400);        // 让窗口画完，避免激活到空白框
            WindowService.Activate(found);
            CoreLog.Info($"按键绑定：等到窗口 0x{found.ToInt64():X}，已激活");
        })
        {
            IsBackground = true,
            Name = "winbind-launch",
        };
        th.Start();
    }
}
