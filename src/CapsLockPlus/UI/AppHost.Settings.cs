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
/// 设置：保存/广播、启用暂停、导入导出、恢复默认、重新加载。
/// </summary>
public sealed partial class AppHost
{
    // ---------------- 设置 ----------------
    /// <summary>
    /// 保存设置并**广播变更**。
    ///
    /// 之前只写文件、不通知任何人，导致"按键映射页改完绑定，
    /// 已经打开的快捷键速查页、触发键页还显示旧内容"。
    ///
    /// 现在每次保存都触发 <see cref="SettingsChanged"/>，
    /// 各页面自行决定要不要刷新 —— 速查表因此能即时更新。
    /// </summary>
    /// <summary>
    /// 全局搜索选中某条别名时，把触发词放这儿；
    /// 别名页加载后取走并选中 —— 跨页面选中最省事的做法。
    /// </summary>
    public string? PendingAliasTrigger { get; set; }

    /// <summary>取走"待选中的别名"（取一次就清空）。</summary>
    public string? TakePendingAlias()
    {
        string? trigger = PendingAliasTrigger;
        PendingAliasTrigger = null;
        return trigger;
    }

    public void SaveSettings()
    {
        /* 防抖 400ms：参数框是逐字符触发，以前每敲一个字就序列化 + 加密 + 三次磁盘操作。
         * 现在界面线程只记一笔，真正落盘由后台写线程在静默后完成。 */
        SettingsStore.RequestSave(Settings);


        /* 防抖动：打字时每次按键都会调 SaveSettings（参数框的 TextChanged），
         * 直接同步通知会让所有页面跟着重绘，卡顿明显。
         * 这里延迟到下一轮消息循环的空闲时刻再通知，
         * 一轮连续编辑只会刷新一次。 */
        _settingsNotifyPending = true;
        if (_settingsNotifyQueued) return;
        _settingsNotifyQueued = true;

        _dispatcher.BeginInvoke(new Action(() =>
        {
            _settingsNotifyQueued = false;
            if (!_settingsNotifyPending) return;
            _settingsNotifyPending = false;
            SettingsChanged?.Invoke();
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    private bool _settingsNotifyPending;
    private bool _settingsNotifyQueued;

    /// <summary>把待保存的设置立刻落盘（退出、备份、切换加密前调用）。</summary>
    public void FlushSettings()
    {
        SettingsStore.FlushPending();
    }

    /// <summary>
    /// 在界面线程上把当前设置序列化成 JSON，供后台线程（备份/指纹）安全使用。
    /// 后台线程自己序列化活对象时，碰到 UI 正在增删的集合会抛"集合已修改"。
    /// </summary>
    public string SettingsJsonSnapshot()
    {
        if (_dispatcher.CheckAccess())
            return System.Text.Json.JsonSerializer.Serialize(Settings, SettingsStore.JsonOptions);

        try
        {
            /* 带超时的 Invoke：界面线程若正卡在别处，宁可 2 秒后自己退化成
             * 直接序列化，也不要让后台备份线程无限期挂着（更不会死锁退出流程）。 */
            var op = _dispatcher.InvokeAsync(() =>
                System.Text.Json.JsonSerializer.Serialize(Settings, SettingsStore.JsonOptions));

            if (op.Task.Wait(TimeSpan.FromSeconds(2)) && op.Task.IsCompletedSuccessfully)
                return op.Task.Result;

            CoreLog.Warn("界面线程 2 秒内没有交出设置快照，退化为直接序列化");
        }
        catch (Exception ex)
        {
            CoreLog.Warn("获取设置快照失败，退化为直接序列化：" + ex.Message);
        }

        return System.Text.Json.JsonSerializer.Serialize(Settings, SettingsStore.JsonOptions);
    }

    /// <summary>
    /// 把第 1 层（基础层）的按键恢复成所选方案的默认值，其它层不动。
    ///
    /// 具体规则与"为什么必须就地改 List"见
    /// <see cref="DefaultSettings.ResetLayerOne"/> —— 抽成静态是为了让自检
    /// 能直接验，不必为了一个纯数据操作起一整套宿主。
    /// </summary>
    /// <summary>
    /// 以普通权限重启（**只在用户明确点击时**调用）。
    ///
    /// 为什么需要：管理员权限下，部分机器（安全软件 / UIPI）会让键盘钩子收不到任何按键，
    /// 热键整体失效。这是权限边界，程序内部修不了 —— 只能回到普通权限。
    ///
    /// 为什么必须走 explorer：从提权进程直接 Process.Start 出来的子进程会**继承**提权令牌，
    /// 只有让已经在普通权限下的 explorer 去拉起，才是真正的降权。
    /// </summary>
    public void RestartUnelevated()
    {
        try
        {
            Settings.Global.RunAsAdmin = false;   // 否则新实例又会自己提权回来
            SaveSettings();

            string? exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;

            try { SingleInstance.Release(); } catch { }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{exe}\"")
            {
                UseShellExecute = true,
            });

            CoreLog.Warn("用户选择以普通权限重启");
            _dispatcher.BeginInvoke(() => ExitApp());
        }
        catch (Exception ex)
        {
            Notify(Loc.T("重启失败：") + ex.Message);
        }
    }

    /// <summary>
    /// 把工作集还给系统（内存"瘦身"）。
    ///
    /// .NET 启动后保留着大量已经用不到的页面；托盘常驻程序动辄 150 MB 看着不体面。
    /// 这里只做**一次**主动回收（空闲 60 秒 + 最小化到托盘时），
    /// 不做定时回收 —— 那会造成反复缺页，反而更卡。
    /// </summary>
    public void TrimWorkingSet()
    {
        try
        {
            /* 先做一次"压缩 + 回收"。
             * 只调 SetProcessWorkingSetSize 是没用的：页面对象还活着，
             * 系统把页面换出去、下次访问立刻又全换回来，内存数字也不会降。
             * 这里是一次性动作（60 秒空闲 / 收进托盘），阻塞几十毫秒可以接受。 */
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);

            using var p = System.Diagnostics.Process.GetCurrentProcess();
            Native.TrimWorkingSet(p.Handle);
            CoreLog.Info($"已回收工作集，当前 {p.WorkingSet64 / 1024 / 1024} MB");
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "回收工作集"); }
    }

    /// <summary>把指定层恢复成默认（按键映射页的「恢复本页默认设置」用它）。</summary>
    public void ResetLayer(int layerId)
    {
        DefaultSettings.ResetLayer(Settings, layerId, Settings.Global.HotkeyScheme);
        SaveSettings();
    }

    public void ApplyHotkeyScheme(string scheme)
    {
        DefaultSettings.ResetLayerOne(Settings, scheme);
        SaveSettings();
    }

    /// <summary>
    /// 清理"只选了『自定义按键』分类、但从没设置过"的绑定。
    ///
    /// 这类绑定的动作是 customKey，但既没有要发送的键、也没有目标程序 ——
    /// 按下去什么都不会发生。以前它会被原样保存，重启后那个键还显示「自定义按键」，
    /// 用户看到的就是"我没设置成功，它怎么记住了"。
    ///
    /// 现在开机统一改回「无操作」，并写一条日志（可追溯，不会静默改数据）。
    /// </summary>
    internal static int ResetIncompleteCustomKeys(Core.Settings.AppSettings s)
    {
        int fixedCount = 0;
        try
        {
            foreach (var layer in s.Layers)
            {
                foreach (var b in layer.Keys)
                {
                    if (!IsIncompleteAction(b)) continue;

                    b.Action = "doNothing";
                    b.Arg = "";
                    fixedCount++;
                }
            }

            if (fixedCount > 0)
                CoreLog.Warn($"清理了 {fixedCount} 个未完成的自定义按键绑定（没有设过要发送的键），已恢复为「无操作」");
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "清理未完成的自定义按键"); }
        return fixedCount;
    }

    /// <summary>
    /// 按已有触发词**推断**前缀符号。
    ///
    /// 为什么需要：前缀从"任意符号都认"改成"只认一个"以后，默认值是 `/`，
    /// 而老用户的触发词多半是 `;mail` 这种 —— 升级后突然全都不展开了。
    /// 这里在启动时看一眼：现有触发词都以同一个符号开头、而当前前缀对不上，
    /// 就把前缀改成那个符号（并写日志），用户不用手动改。
    /// </summary>
    internal static char? InferSnippetPrefix(Core.Settings.AppSettings s)
    {
        var snippets = s.Global?.Snippets;
        if (snippets == null || snippets.Count == 0) return null;

        string prefix = s.Hotstring?.Prefix ?? "";
        if (prefix.Length > 0 && snippets.Any(x => x.Trigger?.StartsWith(prefix, StringComparison.Ordinal) == true))
            return null;                                  // 当前前缀能用，不动

        var common = snippets
            .Select(x => x.Trigger?.TrimStart() is { Length: > 0 } t ? t[0] : '\0')
            .Where(c => c != '\0' && !char.IsLetterOrDigit(c))
            .GroupBy(c => c)
            .OrderByDescending(g => g.Count())
            .FirstOrDefault();

        if (common == null) return null;
        if (common.Key.ToString() == prefix) return null;

        /* 配置里 "Hotstring": null 时（手改文件 / 旧版本导出的残缺配置）
         * 这里会直接 NRE，整个"自动识别前缀"静默失败。补一个兜底实例。 */
        (s.Hotstring ??= new Core.Settings.HotstringOptions()).Prefix = common.Key.ToString();
        return common.Key;
    }

    /// <summary>
    /// 清除一个键的绑定：动作、参数、**备注**一起清。
    ///
    /// 备注（<see cref="KeyBinding.Note"/>）是"这个绑定叫什么"的说明 ——
    /// 绑定都清了还留着备注，用户看到的就是"文字没了，备注还在"。
    /// </summary>
    internal static void ClearBinding(Core.Settings.KeyBinding b)
    {
        b.Action = "doNothing";
        b.Arg = "";
        b.Note = null;
    }

    /// <summary>
    /// 清理"没有动作却还留着备注"的绑定（历史数据：旧版「清除绑定」只清动作和参数）。
    /// </summary>
    internal static int ClearStaleNotes(Core.Settings.AppSettings s)
    {
        int cleared = 0;
        try
        {
            foreach (var layer in s.Layers)
            {
                foreach (var b in layer.Keys) if (ClearOne(b)) cleared++;
                foreach (var sub in layer.EffectiveSubLayers)
                    foreach (var b in sub.Keys) if (ClearOne(b)) cleared++;
            }

            if (cleared > 0)
                CoreLog.Warn($"清理了 {cleared} 个「没有动作却还留着备注」的绑定");
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "清理残留备注"); }
        return cleared;

        static bool ClearOne(Core.Settings.KeyBinding b)
        {
            if (string.IsNullOrWhiteSpace(b.Note)) return false;
            if (b.Action is not (null or "doNothing")) return false;
            if (!string.IsNullOrWhiteSpace(b.Arg)) return false;

            b.Note = null;
            return true;
        }
    }

    /// <summary>
    /// 这个绑定是不是"选了动作、但内容没填"的空壳。
    ///
    /// 这一类动作（输入文本 / 窗口绑定 / 自定义按键 / 直达网址·文件夹·文件）有个共同点：
    /// 光选分类不填参数，按下去什么都不会发生。而编辑器在"选分类"的那一刻就把动作写进配置了，
    /// 于是重启后那个键还显示着「自定义按键」「输入文本」……用户看到的就是
    /// "我根本没设置成功，它怎么记住了"。
    /// </summary>
    internal static bool IsIncompleteAction(Core.Settings.KeyBinding b)
    {
        switch (b.Action)
        {
            case "customKey":
                return string.IsNullOrWhiteSpace(Core.Actions.CustomKeySpec.FromArg(b.Arg).Key);
            case "typeText":
            case "winBind":
            case "webPage":
            case "openFolder":
            case "openFile":
                return string.IsNullOrWhiteSpace(b.Arg);
            default:
                return false;
        }
    }

    /// <summary>启动时清理，并在真的改动过时落盘。</summary>
    private void ResetIncompleteCustomKeys()
    {
        if (ResetIncompleteCustomKeys(Settings) > 0) SettingsStore.RequestSave(Settings);
    }

    public void ReloadAll()
    {
        Settings = SettingsStore.Load();
        ResetIncompleteCustomKeys();
        ThemeManager.Apply(ThemeManager.Parse(Settings.Global.Theme));
        // 主题一变就作废 Qbar 的样式缓存，否则已打开的面板还留着旧配色
        _qbar?.InvalidateStyle();
        Context.Clipboard.ClipboardFeatureOn = Settings.Global.AllowClipboard;
        Context.Mouse.Speed = Settings.Global.MouseSpeed;
        Context.Mouse.Step = Settings.Global.MouseMoveStep;
        Engine.Suspended = false;
        if (_hookHostReady)
        {
            SendHookHostCommand("SUSPEND 0");
            SendHookHostCommand(Engine.Enabled ? "ENABLED 1" : "ENABLED 0");
        }
        RebuildActions();
        ApplyStartupSetting();
        // 选区监听器在 Start() 里已启动；RebuildActions 会重建 Context，
        // 这里只需把引用补回去，否则取词会退回主动取词那条不可靠的路。
        if (_selectionWatcher != null) Context.SelectionWatcher = _selectionWatcher;

        _tray?.SetEnabled(Engine.Enabled);
        _qbar?.InvalidateStyle();
        _main?.ReloadFromSettings();
        SettingsChanged?.Invoke();
        Notify("配置已重新加载");
    }

}
