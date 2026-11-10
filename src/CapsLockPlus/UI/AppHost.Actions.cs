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
/// 动作分发：把层里的动作调用到各服务，并做失败提示去重。
/// </summary>
public sealed partial class AppHost
{
    // ---------------- 动作 ----------------
    /// <summary>
    /// 本次动作调用内是否已经弹过提示。
    /// 动作自己通常会解释原因（"没能打开文件夹：xxx"），
    /// 这时不该再补一条，否则同一个失败会弹两次。
    /// </summary>
    private bool _notifiedInCall;

    public bool InvokeAction(string action, string arg)
    {
        _notifiedInCall = false;

        bool ok;
        try
        {
            ok = Actions.Invoke(action, arg);
        }
        catch (Exception ex)
        {
            /* 动作里抛异常以前会一路冒到动作队列线程（最坏情况把队列打死），
             * 现在在这里收口：记日志 + 按失败通知，主流程继续。 */
            CoreLog.Error($"动作执行异常：{action}（{ex.GetType().Name}）", ex);
            NotifyActionFailed(action, ex.Message);
            return false;
        }

        if (ok)
        {
            UsageStats.Record(action);
            CoreLog.Info($"动作：{action}{(string.IsNullOrEmpty(arg) ? "" : " " + arg)}");
            return true;
        }

        // 未注册（未知 id）或执行了但没完成，都算失败
        CoreLog.Warn($"动作未执行：{action}（未注册，或执行了但没完成）");
        NotifyActionFailed(action, null);
        return false;
    }

    /// <summary>
    /// 动作失败通知。
    ///
    /// 三条原则：
    ///   ① **与成功提示同一条出口**（同一个 Toast、同一个开关 `ShowToast`），
    ///      否则用户会觉得"失败时界面没反应"；
    ///   ② 文案统一为 `没能<动作名>：<原因> · <时间>`，一眼能看出是失败；
    ///   ③ **不重复**：动作自己通常已经解释过原因（如"没能打开文件夹：不存在"），
    ///      本次调用内已经弹过就不再补一条，避免同一个失败弹两次。
    /// </summary>
    private void NotifyActionFailed(string action, string? reason)
    {
        if (_notifiedInCall) return;

        string title = ActionCatalog.TitleOf(action) ?? action;

        /* 前缀沿用产品里既有的失败标记「✗ 」（与设置页状态行同一套约定），
         * 这样不用改 Toast 样式也能一眼看出"这条是失败"。 */
        NotifyTranslated(UiText.FailLine(UiText.FailedAt(title, reason, DateTime.Now)));
    }

    /// <summary>诊断开关：把钩子收到的每个按键写进日志。</summary>
    public void EnableKeyTrace() => Engine.EnableKeyTrace();

    /// <summary>
    /// 弹一条 Toast 提示。
    ///
    /// 这里是**显示出口**，翻译放在这一层：全仓几十处调用点
    /// （`Notify("已恢复默认设置")`）都照旧写中文，不必逐处包 Loc.T。
    /// 查不到译文就原样显示，所以带参数的拼串（"已同步 N 项"）最坏也只是保持中文。
    /// </summary>
    public void Notify(string message)
    {
        if (string.IsNullOrWhiteSpace(message) || !Settings.Global.ShowToast) return;
        _notifiedInCall = true;
        /* 直接交给 ToastWindow：它内部会调度到界面线程，且只显示最新一条。
         * 连按音量键时，前一条还没画出来就被后一条顶掉，不会积压。 */
        ToastWindow.Show(Loc.T(message));
    }

    /// <summary>
    /// 已经翻译好的提示文本直接上屏。
    ///
    /// 失败通知是"格式化后的整句"（含动作名、原因、时间），
    /// 再过一遍 <see cref="Loc.T"/> 当键查表没有意义，也会让译文对不上。
    /// </summary>
    private void NotifyTranslated(string message)
    {
        if (string.IsNullOrWhiteSpace(message) || !Settings.Global.ShowToast) return;
        _notifiedInCall = true;
        ToastWindow.Show(message);
    }

    public void ToggleEnabled() => SetEnabled(!Engine.Enabled);

    public void SetEnabled(bool enabled)
    {
        if (Engine.Enabled == enabled) return;
        Engine.Enabled = enabled;

        /* 钩子在宿主进程里时，宿主也有一份 Enabled 状态；只改主进程字段
         * 会出现“界面显示已暂停、实际热键仍触发”。 */
        if (_hookHostReady)
            SendHookHostCommand(enabled ? "ENABLED 1" : "ENABLED 0");

        /* 暂停前把可能"按着没抬起来"的键补一次抬起：
         * 暂停后引擎不再管按键，粘住就没人修了。 */
        if (!enabled) Core.Input.InputSafety.ReleaseAll();

        _tray?.SetEnabled(enabled);
        EnabledChanged?.Invoke(enabled);
        Notify(enabled ? AppInfo.ShortName + " 已启用" : AppInfo.ShortName + " 已暂停");
    }

}
