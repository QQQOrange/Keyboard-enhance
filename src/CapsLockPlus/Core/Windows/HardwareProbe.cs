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

        var backend = BrightnessService.Backend;
        W($"通道：{BrightnessService.BackendName}");

        int? bright = BrightnessService.Get();
        if (bright == null)
        {
            W("✗ 这台设备两条通道都不通（WMI 与 DDC/CI 都读不到）");
            W("  · 笔记本内置屏一般走 WMI，若不可用通常是显卡驱动没装好；");
            W("  · 外接屏需要在显示器菜单里把 DDC/CI 打开。");
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
                W($"回读：{BrightnessService.Get()}%");

                int? down = BrightnessService.Step(-10);
                W($"再 -10% → {down}%");
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
