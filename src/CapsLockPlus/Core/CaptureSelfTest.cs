using System;
using System.Text;
using System.Windows.Threading;
using CapsLockPlus.Core.Input;
using CapsLockPlus.UI.Windows;

namespace CapsLockPlus.Core;

/// <summary>
/// 自定义按键捕获窗的自检（<c>--capture-test</c>）。
///
/// 背景：用户在「按键映射 → 动作选『自定义按键』→ 按下要发送的键」里按任何键都没反应。
/// 捕获窗有三路输入来源（低级钩子 / Raw Input / 窗口内 PreviewKeyDown），
/// 必须先看清是哪一路断了，才可能一次修对。
///
/// 这个模式会**跟着正常启动流程跑**（热键引擎、Raw Input、钩子宿主都照常），
/// 然后打开捕获窗、注入两次按键、统计三路各收到几次、给结论并退出。
/// </summary>
internal static class CaptureSelfTest
{
    /// <summary>命令行里带了 --capture-test。</summary>
    public static bool Requested { get; set; }

    /// <summary>由 AppHost 在启动完成后调用（此时热键引擎 / Raw Input / 钩子宿主都已就位）。</summary>
    public static void RunInApp()
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== 自定义按键捕获 自检（完整程序 + 宿主模式） ===");
        sb.AppendLine($"时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"管理员权限: {(ElevationHelper.IsElevated ? "是" : "否")}");
        sb.AppendLine();

        KeyCaptureWindow window;
        try
        {
            window = new KeyCaptureWindow();
            window.Show();
        }
        catch (Exception ex)
        {
            sb.AppendLine("打开捕获窗失败：" + ex.Message);
            CliOutput.Report("按键增强 · 捕获自检", "capture-test.txt", sb.ToString());
            Environment.Exit(1);
            return;
        }

        var inject = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        inject.Tick += (_, _) =>
        {
            inject.Stop();
            sb.AppendLine($"注入前：钩子={window.HookHits} RawInput={window.RawHits} 窗口事件={window.WpfHits}");
            try
            {
                InputSender.Tap(0x41);      // A
                System.Threading.Thread.Sleep(80);
                InputSender.Tap(0x14);      // CapsLock：触发键 —— 宿主模式下必须也能录到
            }
            catch (Exception ex) { sb.AppendLine("注入失败：" + ex.Message); }
        };
        inject.Start();

        var finish = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2800) };
        finish.Tick += (_, _) =>
        {
            finish.Stop();

            sb.AppendLine($"注入后：钩子={window.HookHits} RawInput={window.RawHits} 窗口事件={window.WpfHits}");
            sb.AppendLine($"捕获结果: [{window.CapturedCombo ?? "(空)"}]");
            sb.AppendLine();

            int total = window.HookHits + window.RawHits + window.WpfHits;
            sb.AppendLine(total > 0 ? "结论：捕获窗能收到按键 ✓" : "结论：三路都收不到按键 ✗");
            if (window.RawHits == 0)
            {
                sb.AppendLine();
                sb.AppendLine("Raw Input 一路是 0 —— 这一路本来最可靠（不受钩子超时影响）。");
                sb.AppendLine("说明主程序的 Raw Input 没有把按键送进捕获窗，需要查 KeyCaptureSink 的订阅。");
            }

            try { window.Close(); } catch { }
            CliOutput.Report("按键增强 · 捕获自检", "capture-test.txt", sb.ToString());
            Environment.Exit(total > 0 ? 0 : 1);
        };
        finish.Start();
    }
}
