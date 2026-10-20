using System.Diagnostics;
using CapsLockPlus.Core.Settings;
using CapsLockPlus.Core.Windows;

namespace CapsLockPlus.Core.Actions;

/// <summary>
/// 窗口绑定（对应上游的 Caps+1~0 / Caps+LAlt+1~0 / Caps+Win+1~0）。
/// 每个槽位可以：
///   * 只绑一个窗口（窗口关掉后按槽位会自动重开程序）；
///   * 绑多个窗口，反复按循环切换；
///   * 绑定某个程序的全部窗口（新开的窗口自动加入）。
/// </summary>
public sealed class WindowBindingService
{
    /// <summary>
    /// 设置读取委托。
    ///
    /// ⚠ 必须用委托而不是直接存 AppSettings 引用。
    /// "重新加载配置 / 重置全部设置 / 导入旧版 ini / 从备份恢复"
    /// 都会整体替换 AppHost.Settings 对象；如果这里抓着旧引用，
    /// 就会出现「界面显示的是新绑定，按快捷键却按旧配置切窗口」，
    /// 而且新建的绑定会写进那个已经被丢弃的对象里，看起来像"绑定丢了"。
    ///
    /// 其它服务（HotkeyEngine / QbarService / SyncService）本来就是委托，
    /// 只有这里漏了。
    /// </summary>
    private readonly Func<AppSettings> _getSettings;

    private AppSettings _settings => _getSettings();

    private readonly object _gate = new();
    private DateTime _lastTap = DateTime.MinValue;
    private int _lastSlot = -1;
    private int _tapCount;
    private readonly Dictionary<int, int> _cycleIndex = new();

    public event Action<string>? Notified;

    /// <summary>推荐用法：传一个读取最新设置的委托。</summary>
    public WindowBindingService(Func<AppSettings> getSettings) => _getSettings = getSettings;

    /// <summary>兼容用法：直接传设置对象（内部包成固定委托）。</summary>
    public WindowBindingService(AppSettings settings) : this(() => settings) { }

    public WinBinding GetOrCreate(int slot)
    {
        lock (_gate)
        {
            var b = _settings.Bindings.FirstOrDefault(x => x.Slot == slot);
            if (b == null)
            {
                b = new WinBinding { Slot = slot };
                _settings.Bindings.Add(b);
            }
            return b;
        }
    }

    public IReadOnlyList<WinBinding> All => _settings.Bindings;

    /// <summary>
    /// 仅供自检：拦截"启动程序"这一步，返回是否假装启动成功。
    /// 置为 null 时走真实的 Process.Start。
    /// </summary>
    public static Func<string, bool>? LaunchInterceptForTest { get; set; }

    /// <summary>把当前前台窗口绑定到槽位（连续按多次 = 追加更多窗口）。</summary>
    public bool Bind(int slot)
    {
        var info = WindowService.ForegroundInfo();
        if (info == null) { Notified?.Invoke("没有可绑定的窗口"); return false; }
        if (info.ExeName.Equals("CapsLockPlus.exe", StringComparison.OrdinalIgnoreCase))
        {
            Notified?.Invoke("不能绑定 CapsLock+ 自己的窗口");
            return false;
        }

        bool multi = _lastSlot == slot && (DateTime.Now - _lastTap).TotalMilliseconds < 1200;
        _tapCount = multi ? _tapCount + 1 : 1;
        _lastSlot = slot;
        _lastTap = DateTime.Now;

        var b = GetOrCreate(slot);
        lock (_gate)
        {
            b.ExePath = info.ExePath;
            b.ClassName = info.ClassName;
            b.DisplayName = info.ExeName;
            long id = info.Handle.ToInt64();
            if (_tapCount >= 3)
            {
                // 连按三次：绑定该程序的所有窗口
                b.Mode = WinBindMode.Application;
                b.WindowIds.Clear();
                foreach (var w in WindowService.EnumerateTopLevel())
                {
                    if (string.Equals(w.ExePath, info.ExePath, StringComparison.OrdinalIgnoreCase))
                        b.WindowIds.Add(w.Handle.ToInt64());
                }
                Notified?.Invoke($"槽位 {slot}：已绑定 {info.ExeName} 的全部窗口");
            }
            else if (_tapCount == 1)
            {
                b.Mode = WinBindMode.Single;
                b.WindowIds.Clear();
                b.WindowIds.Add(id);
                Notified?.Invoke($"槽位 {slot} ← {info.Display}");
            }
            else
            {
                b.Mode = WinBindMode.Multi;
                if (!b.WindowIds.Contains(id)) b.WindowIds.Add(id);
                Notified?.Invoke($"槽位 {slot}：已加入 {info.Display}（共 {b.WindowIds.Count} 个窗口）");
            }
        }
        return true;
    }

    /// <summary>激活槽位（不存在则尝试启动对应程序）。</summary>
    public bool Activate(int slot)
    {
        var b = _settings.Bindings.FirstOrDefault(x => x.Slot == slot);
        if (b == null || b.IsEmpty)
        {
            Notified?.Invoke($"槽位 {slot} 还没绑定窗口（先按 Caps+LAlt+{slot} 绑定）");
            return false;
        }

        lock (_gate)
        {
            // 清掉已经关闭的窗口
            b.WindowIds.RemoveAll(id => !WindowService.Exists(new IntPtr(id)));

            if (b.Mode == WinBindMode.Application)
            {
                foreach (var w in WindowService.EnumerateTopLevel())
                {
                    if (string.Equals(w.ExePath, b.ExePath, StringComparison.OrdinalIgnoreCase) &&
                        !b.WindowIds.Contains(w.Handle.ToInt64()))
                        b.WindowIds.Add(w.Handle.ToInt64());
                }
            }

            if (b.WindowIds.Count == 0)
            {
                if (!string.IsNullOrEmpty(b.ExePath) && File.Exists(b.ExePath))
                {
                    LaunchAndTrack(slot, b);
                    return true;
                }
                Notified?.Invoke($"槽位 {slot} 的窗口已经关闭");
                return false;
            }

            var fg = WindowService.Foreground;
            if (b.WindowIds.Count == 1)
            {
                var h = new IntPtr(b.WindowIds[0]);

                // “是不是已经在前台”要用**根窗口**判断：
                // 前台可能是同一程序弹出的对话框（另一个 HWND），
                // 直接比 HWND 会误判成“没激活过”，于是再按一次不会收起。
                bool alreadyFront = IsSameWindow(fg, h);

                if (alreadyFront)
                {
                    // 再按一次：最小化（恢复原样）
                    WindowService.Minimize(h);
                    Notified?.Invoke($"已最小化 {DescribeSlot(slot)}");
                    return true;
                }

                // 切过去
                WindowService.Activate(h);
                Notified?.Invoke($"已切到 {DescribeSlot(slot)}");
                return true;
            }

            // 多窗口：循环切换到下一个
            int idx = _cycleIndex.TryGetValue(slot, out var ci) ? ci : -1;
            int fgIdx = b.WindowIds.FindIndex(id => IsSameWindow(fg, new IntPtr(id)));
            if (fgIdx >= 0) idx = fgIdx;
            idx = (idx + 1) % b.WindowIds.Count;
            var next = new IntPtr(b.WindowIds[idx]);
            if (IsSameWindow(next, fg))
            {
                // 又按回同一个：最小化
                WindowService.Minimize(next);
                Notified?.Invoke($"已最小化 {DescribeSlot(slot)}");
                return true;
            }
            _cycleIndex[slot] = idx;
            WindowService.Activate(next);
            Notified?.Invoke($"已切到 {DescribeSlot(slot)}");
            return true;
        }
    }

    /// <summary>
    /// 判断两个句柄是否指向同一个顶层窗口。
    ///
    /// 前台窗口可能是目标程序的子窗口 / 对话框（HWND 不同但同属一个根窗口），
    /// 这种情况也应视为“已在前台”，否则再按一次快捷键不会收起。
    /// </summary>
    /// <summary>
    /// 启动槽位绑定的程序，并等待它的窗口真正出现后再切过去。
    ///
    /// 常见痛点：软件没开时按槽位键，只是把程序拉起来，
    /// 窗口还在初始化，用户看到的是桌面 —— 以为没反应。
    /// 所以这里启动后会轮询等窗口出现（最多 15 秒），
    /// 一旦出现就立即激活，真正实现"按一下就到那个窗口"。
    /// </summary>
    private void LaunchAndTrack(int slot, WinBinding b)
    {
        string exe = b.ExePath;

        // 自检可以拦截启动动作：验证"要不要启动"的决策，
        // 而不用真的把程序拉起来（真拉起来会留一个 15 秒的轮询线程）。
        if (LaunchInterceptForTest != null)
        {
            bool ok = LaunchInterceptForTest(exe);
            Notified?.Invoke(ok ? $"正在启动 {b.DisplayName}…" : "启动失败：被拦截");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Notified?.Invoke("启动失败：" + ex.Message);
            CoreLog.Warn($"窗口绑定启动失败 {exe}: {ex.Message}");
            return;
        }

        Notified?.Invoke($"正在启动 {b.DisplayName}…");
        CoreLog.Info($"窗口绑定槽位 {slot}：启动 {exe}，等待窗口出现");

        // 放到后台线程等，避免卡住按键回调
        var th = new Thread(() =>
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            IntPtr found = IntPtr.Zero;

            while (DateTime.UtcNow < deadline && found == IntPtr.Zero)
            {
                Thread.Sleep(350);

                // 单窗口模式：按程序路径找窗口
                var hit = WindowService.EnumerateTopLevel()
                    .FirstOrDefault(w => string.Equals(w.ExePath, exe,
                        StringComparison.OrdinalIgnoreCase));
                if (hit.Handle != IntPtr.Zero)
                {
                    found = hit.Handle;
                    lock (_gate)
                    {
                        b.WindowIds.Clear();
                        b.WindowIds.Add(hit.Handle.ToInt64());
                    }
                    break;
                }
            }

            if (found == IntPtr.Zero)
            {
                CoreLog.Warn($"窗口绑定槽位 {slot}：等了 15 秒仍未看到 {b.DisplayName} 的窗口");
                Notified?.Invoke($"{b.DisplayName} 好像还没启动完成，再按一次槽位键试试");
                return;
            }

            // 窗口出现了 —— 切过去
            CoreLog.Info($"窗口绑定槽位 {slot}：等到窗口 0x{found.ToInt64():X}，激活");
            Thread.Sleep(400);            // 让窗口把内容画完，避免激活到一个空白框
            WindowService.Activate(found);
        })
        {
            IsBackground = true,
            Name = "bind-launch",
        };
        th.Start();
    }

    private static bool IsSameWindow(IntPtr a, IntPtr b)
    {
        if (a == b) return true;
        if (a == IntPtr.Zero || b == IntPtr.Zero) return false;
        var ra = WindowService.RootOf(a);
        var rb = WindowService.RootOf(b);
        return ra == rb || ra == b || a == rb;
    }

    /// <summary>
    /// 按可执行文件绑定槽位。
    ///
    /// 与“绑定当前窗口”不同，这里记录的是**程序**而不是某个窗口实例：
    /// 只要该程序有窗口存在，按槽位就能切过去；一个都没有时会帮你启动它。
    /// 适合“这个键永远对应 Photoshop”这类用法。
    /// </summary>
    public bool BindByExe(int slot, string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
        {
            Notified?.Invoke("找不到这个程序文件");
            return false;
        }

        string? runningPath = null;
        try { runningPath = WindowService.GetProcessPath((uint)Environment.ProcessId); }
        catch { }
        if (runningPath != null &&
            Path.GetFullPath(exePath).Equals(Path.GetFullPath(runningPath), StringComparison.OrdinalIgnoreCase))
        {
            Notified?.Invoke("不能绑定 CapsLock+ 自己");
            return false;
        }

        var b = GetOrCreate(slot);
        lock (_gate)
        {
            b.ExePath = exePath;
            b.DisplayName = Path.GetFileNameWithoutExtension(exePath);
            b.ClassName = "";
            b.Mode = WinBindMode.Application;   // 绑定该程序的所有窗口
            b.WindowIds.Clear();
            foreach (var w in WindowService.EnumerateTopLevel())
            {
                if (string.Equals(w.ExePath, exePath, StringComparison.OrdinalIgnoreCase))
                    b.WindowIds.Add(w.Handle.ToInt64());
            }
        }

        Notified?.Invoke(b.WindowIds.Count > 0
            ? $"槽位 {slot} ← {b.DisplayName}（当前已开 {b.WindowIds.Count} 个窗口）"
            : $"槽位 {slot} ← {b.DisplayName}（还没运行，按 {slot} 时会帮你打开）");
        return true;
    }

    /// <summary>
    /// 用“十字准星点选”的方式绑定：由界面取到目标窗口句柄后调用。
    /// 与 Bind() 的区别是目标明确，不受“当前前台窗口”影响。
    /// </summary>
    public bool BindWindow(int slot, IntPtr hwnd)
    {
        var info = WindowService.Describe(hwnd);
        if (info == null)
        {
            Notified?.Invoke("选中的不是有效窗口");
            return false;
        }
        if (info.ExeName.Equals("CapsLockPlus.exe", StringComparison.OrdinalIgnoreCase))
        {
            Notified?.Invoke("不能绑定 CapsLock+ 自己的窗口");
            return false;
        }

        var b = GetOrCreate(slot);
        lock (_gate)
        {
            b.ExePath = info.ExePath;
            b.ClassName = info.ClassName;
            b.DisplayName = info.ExeName;
            b.Mode = WinBindMode.Single;
            b.WindowIds.Clear();
            b.WindowIds.Add(info.Handle.ToInt64());
        }
        Notified?.Invoke($"槽位 {slot} ← {info.ExeName}");
        return true;
    }

    /// <summary>槽位当前状态的简短描述（供界面显示）。</summary>
    public string DescribeSlot(int slot)
    {
        var b = _settings.Bindings.FirstOrDefault(x => x.Slot == slot);
        if (b == null || b.IsEmpty) return "未绑定";
        string name = string.IsNullOrEmpty(b.DisplayName) ? "已绑定" : b.DisplayName;
        return b.WindowIds.Count switch
        {
            0 => $"{name}（未运行）",
            1 => $"{name}（1 个窗口）",
            _ => $"{name}（{b.WindowIds.Count} 个窗口循环）",
        };
    }

    public void Unbind(int slot)
    {
        lock (_gate)
        {
            var b = _settings.Bindings.FirstOrDefault(x => x.Slot == slot);
            if (b != null) _settings.Bindings.Remove(b);
        }
    }
}
