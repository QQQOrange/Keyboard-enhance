using System.Windows;
using System.Windows.Threading;
using CapsLockPlus.Core;
using CapsLockPlus.Core.Actions;
using CapsLockPlus.Core.Input;
using CapsLockPlus.Core.Localization;
using CapsLockPlus.Core.Qbar;
using CapsLockPlus.Core.Settings;
using CapsLockPlus.Core.Update;
using CapsLockPlus.Core.Windows;
using CapsLockPlus.UI.Theme;
using CapsLockPlus.UI.Windows;

namespace CapsLockPlus.UI;

/// <summary>
/// 窗口：主窗口/Qbar/数学板/剪贴板历史的创建、显示、贴边与唤醒。
/// </summary>
public sealed partial class AppHost
{
    // ---------------- 窗口 ----------------
    private System.Windows.Interop.HwndSource? _wakeWindow;

    /// <summary>
    /// 建一个**隐藏的顶层窗口**，专门接收"第二次启动本程序"的唤醒消息。
    ///
    /// 为什么需要它：默认启动不显示主窗口（只驻留托盘），这时
    /// `Process.MainWindowHandle` 是 0（它只认可见窗口），于是
    /// <see cref="SingleInstance.ActivateExisting"/> 走"广播消息"那条路 ——
    /// 而早先**没有任何窗口处理这个消息**：用户双击第二次，
    /// 等 6 秒（互斥体超时），然后什么都没有发生。
    ///
    /// 注意不能用 message-only 窗口（HWND_MESSAGE）：广播消息只发给顶层窗口，
    /// 消息窗口收不到。这里建的是普通顶层窗口，只是从来不 Show。
    /// </summary>
    private void EnsureWakeWindow()
    {
        if (_wakeWindow != null) return;
        try
        {
            var p = new System.Windows.Interop.HwndSourceParameters("KeyboardEnhance.Wake")
            {
                Width = 0,
                Height = 0,
                WindowStyle = 0,                 // 不要 WS_VISIBLE
                ExtendedWindowStyle = 0x80,      // WS_EX_TOOLWINDOW：不进 Alt+Tab
            };
            _wakeWindow = new System.Windows.Interop.HwndSource(p);
            _wakeWindow.AddHook(OnWakeMessage);

            /* 提权实例的窗口默认会过滤掉低权限进程的 WM_COPYDATA 之外的自定义消息。
             * 普通权限第二次启动时发的是 WmShowWindow，必须显式放行，否则用户
             * 双击 exe 只会看到进程退出、主窗口永远不出来。 */
            Core.Interop.NativeMethods.ChangeWindowMessageFilterEx(
                _wakeWindow.Handle, (uint)SingleInstance.WmShowWindow,
                Core.Interop.NativeMethods.MSGFLT_ALLOW, IntPtr.Zero);

            /* 命名事件比自定义窗口消息可靠：即使主窗口完全隐藏、处于不同完整性级别，
             * 第二个实例也能直接唤醒这个托盘实例。 */
            SingleInstance.StartWakeListener(() => ShowMainWindow());
        }
        catch (Exception ex)
        {
            CoreLog.Warn("创建唤醒窗口失败（再启动一次不会自动打开主窗口）：" + ex.Message);
        }
    }

    private IntPtr OnWakeMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != SingleInstance.WmShowWindow) return IntPtr.Zero;
        handled = true;
        CoreLog.Info("收到「再打开一次」的唤醒消息，显示主窗口");
        ShowMainWindow();
        return IntPtr.Zero;
    }

    public void ShowMainWindow(string? page = null)
    {
        _dispatcher.BeginInvoke(() =>
        {
            if (_main == null)
            {
                _main = new MainWindow(this);
                /* 窗口被真的关掉之后（用户关掉「关闭时最小化到托盘」），
                 * 这个字段必须清空：WPF 的已关闭窗口不能再 Show()，
                 * 否则托盘图标、托盘菜单、「设置」动作从此全部弹
                 * "Cannot set Visibility or call Show after a Window has closed"，
                 * 只有重启进程才能恢复。 */
                _main.Closed += (_, _) => _main = null;
            }
            if (page != null) _main.Navigate(page);
            _main.Show();
            if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
            _main.Activate();
            _main.Topmost = true;
            _main.Topmost = false;
            _main.Focus();
        });
    }

    /// <summary>
    /// 只在窗口隐藏 / 最小化之后回收内存。
    ///
    /// 注意：不要对“正在显示”的窗口调用 SetProcessWorkingSetSize ——
    /// WPF 随后会重新访问被换出的渲染页，触发大量缺页中断，
    /// 实测空闲 CPU 会从 0% 涨到 15% 左右，得不偿失。
    /// </summary>
    private void TrimWorkingSetWhenIdle()
    {
        if (_main is { IsVisible: true } && _main.WindowState != WindowState.Minimized) return;
        MemoryTrimmer.Trim();
    }

    public void ShowQbar(string? prefill = null)
    {
        /* 和计算器同一个坑：窗口被**关闭**过之后不能再 Show()，
         * 而 Qbar 的字段没人清空 —— 于是"关掉一次以后 Caps+Q 再也唤不出来"。
         * 现在关掉就把字段清空，并且整段兜住异常：宁可重建一次，也不能什么都不弹。 */
        try
        {
            if (_qbar == null)
            {
                _qbar = new QbarWindow(this);
                _qbar.Closed += (_, _) =>
                {
                    CoreLog.Info("Qbar：窗口已关闭，下次重新创建");
                    _qbar = null;
                };
            }
            _qbar.Open(prefill);
        }
        catch (Exception ex)
        {
            CoreLog.Error("Qbar 打开失败（将重建窗口再试一次）", ex);
            _qbar = null;
            try
            {
                _qbar = new QbarWindow(this);
                _qbar.Closed += (_, _) => _qbar = null;
                _qbar.Open(prefill);
            }
            catch (Exception ex2)
            {
                CoreLog.Error("Qbar 重建后仍然打不开", ex2);
                Notify(Loc.T("Qbar 打不开，详情见日志"));
            }
        }
    }

    /// <summary>
    /// 把动作带的引擎关键字转成 Qbar 的预填文本。
    ///
    /// 面板本身就是"关键字 + 空格 + 内容"的语法（bd 关键词 = 百度搜），
    /// 所以预填成 "bd " 让用户直接接着打字即可，不必新增一套输入协议。
    /// </summary>
    private string? EnginePrefill(string? engineKey)
    {
        if (string.IsNullOrWhiteSpace(engineKey)) return null;
        string key = engineKey.Trim();
        // 只为真实存在的引擎生成预填，别把乱码塞进输入框
        return Settings.Search.Any(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase)
                                        && !x.Key.Equals("default", StringComparison.OrdinalIgnoreCase))
            ? key + " "
            : null;
    }

    public void ShowMathBoard()
    {
        /* 窗口被用户关掉（Esc / 标题栏 ×）之后不能再复用：Show() 一个已关闭的
         * 窗口会抛异常。而 `IsLoaded` 在关闭之后**仍然是 true**（WPF 不复位它），
         * 拿它当"还能用吗"的判据等于永远判"能用" ——
         * 实际表现是：计算器按 Esc 关掉一次，之后 Caps+F2 再也打不开，
         * 只会弹一个"出错了：…"。
         * 正确做法是让 Closed 把字段清空，下次自然新建。 */
        if (_math == null)
        {
            _math = new MathBoardWindow();
            _math.Closed += (_, _) => _math = null;
        }
        _math.Show();
        _math.Activate();
    }

    public void ShowClipboardHistory()
    {
        if (_clipHistory == null)
        {
            _clipHistory = new ClipboardHistoryWindow(this);
            _clipHistory.Closed += (_, _) => _clipHistory = null;
        }
        _clipHistory.RefreshItems();
        _clipHistory.Show();
        _clipHistory.Activate();
    }

    public void ShowOnboarding()
    {
        var w = new OnboardingWindow(this);
        w.ShowDialog();
    }

    /// <summary>启动时发现新版本：弹窗询问是否下载（由“自动更新”开关控制）。</summary>
    public void ShowUpdatePrompt(UpdateInfo info)
    {
        _dispatcher.BeginInvoke(() =>
        {
            try
            {
                var w = new UpdatePromptWindow(this, info);
                w.ShowDialog();
            }
            catch (Exception ex)
            {
                CoreLog.Error("显示更新提示失败", ex);
            }
        });
    }

    /// <summary>启动后延迟检查更新，避免和界面初始化抢资源。</summary>
    public void ScheduleUpdateCheck()
    {
        if (!Settings.Global.AutoUpdate) return;
        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle)
        {
            Interval = TimeSpan.FromSeconds(6),
        };
        timer.Tick += async (_, _) =>
        {
            timer.Stop();
            try
            {
                var service = new UpdateService();
                var result = await service.CheckAsync(Settings.Global.GitHubToken);
                Settings.Global.LastUpdateCheck = DateTime.Now;
                SaveSettings();
                if (!result.Success || !result.IsNewer || result.Update == null) return;
                if (result.Update.Version == Settings.Global.SkippedVersion) return;
                ShowUpdatePrompt(result.Update);
            }
            catch (Exception ex)
            {
                CoreLog.Warn("自动检查更新失败：" + ex.Message);
            }
        };
        timer.Start();
    }

    /// <summary>供离屏渲染使用：创建主窗口但不走“显示并置前”的流程。</summary>
    public MainWindow CreateMainWindow() => new(this);

}
