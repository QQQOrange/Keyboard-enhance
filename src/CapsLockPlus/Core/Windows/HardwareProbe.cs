using System;

namespace CapsLockPlus.Core.Windows;

/// <summary>
/// 硬件自检：真的调一次音量和亮度，把过程打出来，再还原原值。
///
/// 为什么不放进 <c>--selftest</c>：
///   · 写亮度会让屏幕闪一下，自检有一千多条断言，中间闪屏很打扰；
///   · 写音量即便先静音，也会改变用户的音量滑块。
///
/// 这条路径专门用来回答"按了没反应到底是代码问题还是设备问题"：
/// 用户报"音量/亮度不管用"时，让他跑 <c>KeyboardEnhance.exe --hwtest</c>，
/// 输出里能直接看到走的是哪条通道、能不能写、回读是多少。
/// </summary>
public static class HardwareProbe
{
    public static int Run()
    {
        // 控制台默认是系统 OEM 代码页（936），重定向到 bash / CI 时会变乱码
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }

        var lines = new System.Collections.Generic.List<string>();
        void W(string s)
        {
            lines.Add(s);
            Console.WriteLine(s);
        }

        int problems = 0;

        // ---------------- 音量 ----------------
        W("=== 音量（Core Audio） ===");

        int? vol = VolumeService.Get();
        bool? mute = VolumeService.GetMute();

        if (vol == null)
        {
            W("✗ 没有取到默认播放设备，音量调不了");
            problems++;
        }
        else
        {
            W($"当前音量：{vol}%    静音：{mute}");
            int original = vol.Value;
            bool muteOriginal = mute ?? false;

            try
            {
                VolumeService.SetMute(true);   // 测的时候别出声
                W("已临时静音（避免测试出声）");

                int target = original >= 50 ? 30 : 70;
                W($"写入 {target}% …… {(VolumeService.Set(target) ? "成功" : "失败")}");
                if (!VolumeService.Set(target)) problems++;

                System.Threading.Thread.Sleep(120);
                int readBack = VolumeService.Get() ?? -1;
                W($"回读：{readBack}%");

                int? up = VolumeService.Step(5);
                W($"再 +5% → {up}%");
                int? down = VolumeService.Step(-5);
                W($"再 -5% → {down}%");

                /* ---- 系统音量键 ----
                 *
                 * 按键映射的音量动作就是发这个键：由 shell 自己调音量、
                 * 自己弹 Windows 那条横条。这里确认"发出去之后音量真的变了"，
                 * 变了就说明横条也弹出来了（同一个处理函数里画的）。 */
                W("");
                W("=== 系统音量键（按键映射用的就是它） ===");
                int keyBefore = VolumeService.Get() ?? -1;
                Core.Input.InputSender.Tap(Core.Input.VK.VolumeUp);
                System.Threading.Thread.Sleep(240);
                int keyAfter = VolumeService.Get() ?? -1;
                W($"{keyBefore}% → {keyAfter}%  "
                  + (keyAfter != keyBefore ? "有响应（系统横条已弹出）" : "没响应（会走兜底）"));
                if (keyAfter == keyBefore) problems++;

                // 自绘横条（亮度用）：确认能叫起来而不抛异常
                W("");
                W("=== 自绘横条（亮度用） ===");
                try
                {
                    UI.Windows.OsdWindow.Show(BrightnessService.Get() ?? 50);
                    System.Threading.Thread.Sleep(240);
                    W("已显示");
                }
                catch (Exception ex)
                {
                    W($"显示失败：{ex.Message}");
                    problems++;
                }
            }
            finally
            {
                VolumeService.Set(original);
                VolumeService.SetMute(muteOriginal);
                W($"已还原：{VolumeService.Get()}% / 静音 {VolumeService.GetMute()}");
            }
        }

        // ---------------- 亮度 ----------------
        W("");
        W("=== 亮度 ===");

        foreach (var line in BrightnessService.Describe()) W(line);

        int? bright = BrightnessService.Get();
        if (bright == null)
        {
            W("✗ 这台设备两条通道都不通（WMI 与 DDC/CI 都读不到）");
            W("  · 笔记本内置屏一般走 WMI，若不可用通常是显卡驱动没装好；");
            W("  · 外接屏需要在显示器菜单里把 DDC/CI 打开；");
            W("  · AMD 显卡驱动里的「自定义颜色」会抢占 DDC/CI，需要关掉；");
            W("  · 走 VGA/DVI 或经过扩展坞的 HDMI/DP，可能不支持 DDC/CI。");
            problems++;
        }
        else
        {
            W($"当前亮度：{bright}%");
            int original = bright.Value;

            try
            {
                int target = Math.Clamp(original + 10, 0, 100);
                bool ok = BrightnessService.Set(target);
                W($"写入 {target}% …… {(ok ? "成功" : "失败")}");
                if (!ok) problems++;

                // DDC/CI 有延迟，等显示器把新值稳定下来再读
                System.Threading.Thread.Sleep(350);
                int after = BrightnessService.Get() ?? -1;
                W($"回读：{after}%");
                if (ok && Math.Abs(after - target) > 1)
                {
                    W($"  ⚠ 写入与回读不一致：显像管没接受这个值（差 {Math.Abs(after - target)}%）");
                    problems++;
                }

                int? down = BrightnessService.Step(-10);
                W($"再 -10% → {down}%");

                /* 动作链路：上面测的是服务层，这里从动作表调一次，
                 * 确认「按键 → 动作目录 → 参数解析 → 亮度服务」整条链也通。
                 * 曾经出过"服务能调、但动作参数解析成 0"的问题。 */
                var settings = Core.Settings.DefaultSettings.Create();
                var ctx = new Core.Actions.ActionContext
                {
                    Settings = () => settings,
                    Clipboard = new Core.Actions.ClipboardService(),
                    Windows = new Core.Actions.WindowActions(),
                    WindowBindings = new Core.Actions.WindowBindingService(settings),
                    Mouse = new Core.Actions.MouseService(),
                    Notify = n => W($"  [提示] {n}"),
                };
                var cat = Core.Actions.ActionCatalog.Create(ctx);

                W("动作链路：调用「亮度 +」……");
                int beforeInvoke = BrightnessService.Get() ?? -1;
                cat.Invoke("brightnessUp", "");
                System.Threading.Thread.Sleep(350);
                int afterInvoke = BrightnessService.Get() ?? -1;
                W($"  {beforeInvoke}% → {afterInvoke}%");
                if (afterInvoke <= beforeInvoke)
                {
                    W("  ⚠ 动作执行后亮度没有上升，调用链有问题");
                    problems++;
                }
            }
            finally
            {
                BrightnessService.Set(original);
                W($"已还原：{BrightnessService.Get()}%");
            }
        }

        W("");
        W(problems == 0 ? "=== 全部通过 ===" : $"=== 有 {problems} 项问题 ===");

        // 命令行被重定向时 Console 可能拿不到，落一份文件兜底
        try
        {
            System.IO.File.WriteAllText(
                System.IO.Path.Combine(AppPaths.DataDir, "hwtest.txt"),
                string.Join(Environment.NewLine, lines));
        }
        catch { }

        return problems == 0 ? 0 : 1;
    }
}
