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
/// WebDAV 同步与自动备份的调度（真正的 IO 在 SyncService 里）。
/// </summary>
public sealed partial class AppHost
{
    // ---------------- WebDAV 同步 ----------------

    /// <summary>同步服务（界面与自动同步共用）。</summary>
    public Core.Sync.SyncService Sync { get; private set; } = null!;

    /// <summary>最近一次同步的提示，供界面显示。</summary>
    public string LastSyncMessage { get; private set; } = "";

    /// <summary>
    /// 启动同步：延后几秒再跑，等界面和钩子都就绪。
    /// 联网在后台线程，不阻塞界面。
    /// </summary>
    public void ScheduleStartupSync()
    {
        var s = Settings.Sync;
        if (!s.SyncOnStart)
        {
            CoreLog.Info("启动同步：未开启，跳过");
            return;
        }

        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(4),
        };
        timer.Tick += async (_, _) =>
        {
            timer.Stop();
            await RunStartupSyncAsync();
        };
        timer.Start();
    }

    private async Task RunStartupSyncAsync()
    {
        try
        {
            CoreLog.Info("启动同步：开始");
            var result = await Task.Run(() => Sync.SyncOnStartAsync()).ConfigureAwait(true);
            LastSyncMessage = result.Message;
            if (!string.IsNullOrEmpty(result.Message))
            {
                CoreLog.Info($"启动同步：{result.Message}");
                if (Settings.Global.ShowToast) Notify("同步：" + result.Message);
            }
            SaveSettings();
            SyncCompleted?.Invoke(result);
        }
        catch (Exception ex)
        {
            CoreLog.Warn("启动同步异常：" + ex.Message);
        }
    }

    /// <summary>退出时同步（由 App.OnExit 调用）。</summary>
    public Core.Sync.SyncResult? RunExitSync()
    {
        try
        {
            if (!Settings.Sync.SyncOnExit) return null;
            CoreLog.Info("退出同步：开始");
            var r = Sync.SyncOnExit();
            CoreLog.Info($"退出同步：{r.Message}");
            SaveSettings();
            return r;
        }
        catch (Exception ex)
        {
            return Core.Sync.SyncResult.Fail(ex.Message);
        }
    }

    /// <summary>同步完成事件（界面刷新用）。</summary>
    public event Action<Core.Sync.SyncResult>? SyncCompleted;

    // ---------------- 自动备份 ----------------

    private System.Windows.Threading.DispatcherTimer? _autoBackupTimer;
    private bool _autoBackupBusy;

    /// <summary>
    /// 重建自动备份定时器。
    ///
    /// 由设置页在开关/间隔改动后调用，也在启动时调一次。
    /// 间隔很短时（最短 5 分钟）不会真的每 5 分钟写一份 ——
    /// 写之前会比对配置文件的写入时间，没变化就跳过，
    /// 否则用户开着软件不用，一天也能堆出几百份一模一样的备份。
    /// </summary>
    public void RestartAutoBackupTimer()
    {
        _autoBackupTimer?.Stop();
        _autoBackupTimer = null;

        var s = Settings.Sync;
        if (!s.AutoBackup)
        {
            CoreLog.Info("自动备份：未开启");
            return;
        }
        if (string.IsNullOrWhiteSpace(s.BackupPassword))
        {
            CoreLog.Warn("自动备份：没设备份密码，无法加密，已跳过");
            return;
        }

        int minutes = Math.Clamp(s.AutoBackupMinutes, 5, 1440);
        _autoBackupTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(minutes),
        };
        _autoBackupTimer.Tick += async (_, _) => await RunAutoBackupAsync().ConfigureAwait(true);
        _autoBackupTimer.Start();
        CoreLog.Info($"自动备份：每 {minutes} 分钟一次");
    }

    private async Task RunAutoBackupAsync()
    {
        if (_autoBackupBusy) return;
        _autoBackupBusy = true;
        try
        {
            var r = await Task.Run(() => Sync.AutoBackupAsync()).ConfigureAwait(true);
            LastSyncMessage = r.Message;
            if (r.Ok)
            {
                CoreLog.Info("自动备份：" + r.Message);
                SaveSettings();
                SyncCompleted?.Invoke(r);
            }
            else
            {
                CoreLog.Warn("自动备份：" + r.Message);
            }
        }
        catch (Exception ex)
        {
            CoreLog.Warn("自动备份异常：" + ex.Message);
        }
        finally
        {
            _autoBackupBusy = false;
        }
    }

    /// <summary>动作表重建后触发（界面需要刷新功能下拉）。</summary>
    public event Action? ActionsChanged;

    /// <summary>
    /// 重建动作表。
    ///
    /// 必须能重建：宏动作（macro:&lt;id&gt;）是在 <c>ActionCatalog.Create</c> 时
    /// 按当时的宏列表注册的。动作表只在启动时建一次的话，
    /// **用户新建的宏永远不会出现在"功能"下拉里** ——
    /// 这正是用户反馈的"按键绑不了宏"。
    ///
    /// 所以任何会改变动作集合的操作（增删宏、重新加载配置）之后都要调它。
    /// </summary>
    public void RebuildActions()
    {
        Actions = ActionCatalog.Create(Context);
        ActionsChanged?.Invoke();
    }

    /// <summary>手动触发一次同步并通知界面。</summary>
    public async Task<Core.Sync.SyncResult> RunSyncNowAsync(bool upload)
    {
        var r = upload
            ? await Sync.UploadAsync().ConfigureAwait(true)
            : await Sync.DownloadAsync().ConfigureAwait(true);
        LastSyncMessage = r.Message;
        SaveSettings();
        SyncCompleted?.Invoke(r);
        return r;
    }

    /// <summary>
    /// 退出前先停输入：停掉宿主、暂停引擎并补发抬起。
    /// 退出同步最长 8 秒，期间窗口和托盘已经消失；如果钩子还活着，
    /// 用户按快捷键会触发看不见的动作，强杀时还可能留下粘住的修饰键。
    /// </summary>
    public void StopForExit()
    {
        try
        {
            _hookHostReady = false;
            Engine.HookDelegatedToHost = false;
            try { _hostPipeCts?.Cancel(); } catch { }
            try { _hookHost?.Kill(); } catch { }
            _hookHost = null;
            Engine.Enabled = false;
            Engine.Suspended = true;
            Core.Input.InputSafety.ReleaseAll();
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "退出前停止输入"); }
    }

    public void ExitApp(bool restart = false)
    {
        Exiting = true;
        StopForExit();
        SaveSettings();
        UsageStats.Flush();      // 统计攒着落盘，退出时补最后一次
        _tray?.Dispose();
        _tray = null;
        try { _selectionWatcher?.Dispose(); _selectionWatcher = null; } catch { }   /* 预期内：清理动作，失败无副作用 */
        if (restart)
        {
            /* 重启自己（权限切换必须重启才生效）。
             * 用 explorer 启动而不是当前进程 —— 否则子进程会继承
             * 当前进程的完整性级别，达不到"降权重启"的目的。 */
            try
            {
                var exe = Environment.ProcessPath ?? AppPaths.AppDir.TrimEnd('\\') + "\\KeyboardEnhance.exe";
                // 先释放互斥体，避免“退出同步”尚未结束时新实例等到超时后退出。
                try { SingleInstance.Release(); } catch { }
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe)
                { UseShellExecute = true, WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory });
            }
            catch (Exception ex) { CoreLog.Warn("重启失败：" + ex.Message); }
        }
        Application.Current.Shutdown();
    }

}
