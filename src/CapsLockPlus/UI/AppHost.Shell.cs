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
/// 托盘图标与剪贴板监听等外壳部分。
/// </summary>
public sealed partial class AppHost
{
    // ---------------- 托盘 ----------------
    private void CreateTray()
    {
        _tray = new TrayIcon(this);
        _tray.SetEnabled(Engine.Enabled);
    }

    public void RefreshTray() => _tray?.SetEnabled(Engine.Enabled);

    /// <summary>
    /// 界面语言切换后调用：把「文本在构造时就写死」的长生命周期界面全部重建一遍。
    ///
    /// 为什么需要这一步 —— 用 <c>{loc:T '…'}</c> 标出来的 XAML 文字是绑定，
    /// 换语言时自己会重画；但下面这几处不是绑定，是**造对象那一刻**就把文字塞进去的：
    ///   · 托盘右键菜单（WinForms ToolStripMenuItem，构造时定死 .Text）；
    ///   · 动作目录（<see cref="RebuildActions"/> 里的 ActionCatalog，Title/Description 是 record 属性）；
    ///   · 主窗口已缓存的分页（各 View 实例留着旧的文本）；
    ///   · 整条动作列表的订阅方（按键映射页、Qbar 的候选列表）。
    /// 不重建这些，用户切完语言会看到「设置页是英文、右键菜单还是中文」。
    /// </summary>
    public void NotifyLocalizedSurfaces()
    {
        try
        {
            RebuildActions();      // 动作目录 → 触发 ActionsChanged，映射页/Qbar 跟着重建
            _tray?.Relocalize();   // 托盘菜单 + 悬浮提示
            _main?.ReloadFromSettings();
            _qbar?.InvalidateStyle();
            SettingsChanged?.Invoke();
        }
        catch (Exception ex)
        {
            CoreLog.Error("切换语言后重建界面失败（部分界面可能仍是旧语言）", ex);
        }
    }

    // ---------------- 剪贴板 ----------------
    private void OnClipboardChanged()
    {
        try
        {
            if (!System.Windows.Clipboard.ContainsText()) return;
            string text = System.Windows.Clipboard.GetText();
            if (string.IsNullOrWhiteSpace(text) || text == _lastClipboardText) return;

            /* 「输入文本」的长文本走剪贴板粘贴时会临时写两下（正文 + 还原）
             * 那份内容不是用户复制的，记进历史纯属噪声。 */
            if (ClipboardPaster.IsSelfWrite(text)) return;

            _lastClipboardText = text;
            Clipboard.RecordHistory(text);
        }
        catch
        {
            // 剪贴板被占用时忽略
        }
    }

    public void Dispose()
    {
        try { SingleInstance.StopWakeListener(); } catch { }
        try { _hookHost?.Kill(); _hookHost = null; } catch { }
        try { _foreground?.Dispose(); _foreground = null; } catch { }
        try { _deviceTracker?.Dispose(); _deviceTracker = null; } catch { }
        try { _wakeWindow?.Dispose(); _wakeWindow = null; } catch { }
        try { Sync?.DisposeClient(); } catch { }
        if (_disposed) return;
        _disposed = true;
        _watchdog.Dispose();
        _clipboard.Dispose();
        Engine.Dispose();
        _tray?.Dispose();
        _queue.Dispose();
        // 隐藏/退出时把内存还给系统（此时不会再有渲染访问）
        MemoryTrimmer.Trim();
    }

}
