using System;
using System.IO;
using System.Text.Json;
using CapsLockPlus.Core.Settings;
using CapsLockPlus.Core.Sync;

namespace CapsLockPlus;

/// <summary>
/// 自检：启动密码（与备份密码完全独立的那一套）。
///
/// 覆盖四件事：
///   1. 合规规则（长短、空白、空值）—— 界面和落库必须同一把尺子；
///   2. 哈希行为（正确/错误/大小写/不修剪、盐随机、迭代次数记录在案）；
///   3. 文件真相（写→读往返、删文件即解除、损坏文件不把人关在外面）；
///   4. 边界（测试模式跳过密码门、导出不带密码、与备份密码互不干扰）。
///
/// ⚠ 全部跑在临时目录里（DirOverride）：自检绝不能碰真实数据目录，
/// 否则跑一次 --selftest 就在用户机器上留下一把锁。
/// </summary>
public static partial class SelfTest
{
    private static void TestStartupPassword()
    {
        string tmp = Path.Combine(Path.GetTempPath(),
            "clp-selftest-startup-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmp);
        StartupPassword.DirOverride = tmp;

        try
        {
            var sec = new SecurityOptions();

            /* ---- 1. 合规规则 ---- */
            Check("初始状态：没有启动密码", !StartupPassword.IsSet());
            Check("没设密码时校验一律不过", !StartupPassword.Verify("anything"));
            Check("空密码被拒", StartupPassword.ValidateNew("") != null);
            Check("null 被拒", StartupPassword.ValidateNew(null) != null);
            Check("全是空格被拒", StartupPassword.ValidateNew("   ") != null);
            Check("3 位太短被拒", StartupPassword.ValidateNew("abc") != null);
            Check("4 位可以", StartupPassword.ValidateNew("abcd") == null);
            Check("64 位可以", StartupPassword.ValidateNew(new string('a', 64)) == null);
            Check("65 位超长被拒", StartupPassword.ValidateNew(new string('a', 65)) != null);
            Check("TrySet 拒绝不合规密码", !StartupPassword.TrySet("ab", out _));
            Check("被拒后不会留下文件", !File.Exists(StartupPassword.FilePath));

            /* ---- 2. 哈希行为 ---- */
            Check("TrySet 合规密码成功",
                StartupPassword.TrySet("correct-horse", out string setErr), setErr);
            Check("设完文件存在", StartupPassword.IsSet());
            Check("正确密码通过", StartupPassword.Verify("correct-horse"));
            Check("错误密码不过", !StartupPassword.Verify("wrong-horse"));
            Check("大小写敏感", !StartupPassword.Verify("Correct-Horse"));
            Check("密码不做修剪（前后多个空格不过）",
                !StartupPassword.Verify(" correct-horse "));
            Check("空输入不过", !StartupPassword.Verify(""));

            string saltBefore = File.ReadAllText(StartupPassword.FilePath);
            Check("改成新密码成功", StartupPassword.TrySet("brand-new-pass", out _));
            string saltAfter = File.ReadAllText(StartupPassword.FilePath);
            Check("同一个密码两次设置，盐和派生值都不同（随机盐）",
                saltBefore != saltAfter);
            Check("改密码后旧密码失效", !StartupPassword.Verify("correct-horse"));
            Check("改密码后新密码有效", StartupPassword.Verify("brand-new-pass"));

            StartupPassword.MirrorTo(sec);
            Check("镜像：开关打开", sec.StartupPasswordEnabled);
            Check("镜像：盐非空", sec.StartupPasswordSalt.Length > 0);
            Check("镜像：派生值非空", sec.StartupPasswordHash.Length > 0);
            Check("镜像：迭代次数记录在案",
                sec.StartupPasswordIterations == StartupPassword.Iterations,
                $"iter={sec.StartupPasswordIterations}");
            Check("HasPassword 与文件一致", sec.HasPassword);

            /* ---- 3. 文件真相 ---- */
            StartupPassword.Clear();
            Check("清除后没有启动密码", !StartupPassword.IsSet());
            Check("清除后校验不过", !StartupPassword.Verify("correct-horse"));
            StartupPassword.MirrorTo(sec);
            Check("清除后镜像归零",
                !sec.StartupPasswordEnabled && !sec.HasPassword,
                $"enabled={sec.StartupPasswordEnabled}");

            // 损坏文件 = 视为没设（宁可放行也不把人永久关在门外）
            File.WriteAllText(StartupPassword.FilePath, "{ 这不是有效 JSON");
            Check("文件损坏视为未设置", !StartupPassword.IsSet());
            Check("文件损坏时校验不过", !StartupPassword.Verify("correct-horse"));
            File.Delete(StartupPassword.FilePath);

            /* ---- 4. 边界 ---- */
            Check("--selftest 跳过密码门", StartupPassword.ShouldSkip(new[] { "--selftest" }));
            Check("--render 跳过密码门", StartupPassword.ShouldSkip(new[] { "--render" }));
            Check("--render-diag 跳过密码门", StartupPassword.ShouldSkip(new[] { "--render-diag" }));
            Check("--osd 跳过密码门", StartupPassword.ShouldSkip(new[] { "--osd", "50" }));
            Check("--diag 跳过密码门", StartupPassword.ShouldSkip(new[] { "--diag" }));
            Check("--hwtest 跳过密码门", StartupPassword.ShouldSkip(new[] { "--hwtest" }));
            Check("大小写不敏感地识别跳过标志", StartupPassword.ShouldSkip(new[] { "--SelfTest" }));
            Check("正常启动不跳过", !StartupPassword.ShouldSkip(Array.Empty<string>()));
            Check("--tray 不跳过（托盘启动也要密码）",
                !StartupPassword.ShouldSkip(new[] { "--tray" }));
            Check("--updated 不跳过（更新重启也要密码）",
                !StartupPassword.ShouldSkip(new[] { "--updated" }));
            Check("--page= 不跳过", !StartupPassword.ShouldSkip(new[] { "--page=keys" }));

            // 导出不带启动密码：跟着配置走会把用户锁在导入后的机器外
            Check("明文导出剔除启动密码开关",
                Array.Exists(ExportImport.NeverExport,
                    p => p == "Security.StartupPasswordEnabled"));
            Check("明文导出剔除启动密码盐",
                Array.Exists(ExportImport.NeverExport,
                    p => p == "Security.StartupPasswordSalt"));
            Check("明文导出剔除启动密码哈希",
                Array.Exists(ExportImport.NeverExport,
                    p => p == "Security.StartupPasswordHash"));
            Check("明文导出剔除启动密码迭代次数",
                Array.Exists(ExportImport.NeverExport,
                    p => p == "Security.StartupPasswordIterations"));

            /* ---- 5. 与备份密码完全独立 ---- */
            var settings = new AppSettings
            {
                Sync = { BackupPassword = "backup-secret" },
            };
            Check("设启动密码成功", StartupPassword.TrySet("gate-secret", out _));
            StartupPassword.MirrorTo(settings.Security);

            // 备份加密照常工作，且启动密码镜像不被它碰
            string secret = "一些用户数据";
            string box = Core.Sync.CryptoBox.Encrypt(secret, settings.Sync.BackupPassword);
            Check("备份密码仍能加密", Core.Sync.CryptoBox.LooksEncrypted(box));
            CheckEqual("备份密码仍能解密",
                Core.Sync.CryptoBox.Decrypt(box, settings.Sync.BackupPassword), secret);
            Check("启动密码镜像没被备份密码影响",
                settings.Security.StartupPasswordEnabled && settings.Security.HasPassword);

            // 整套设置往返（JSON）后两套密码各在各的区段
            var json = JsonSerializer.Serialize(settings, SettingsStore.JsonOptions);
            var back = JsonSerializer.Deserialize<AppSettings>(json, SettingsStore.JsonOptions)!;
            Check("往返后启动密码还在", back.Security.HasPassword);
            Check("往返后启动密码开关还在", back.Security.StartupPasswordEnabled);
            CheckEqual("往返后备份密码没被串位", back.Sync.BackupPassword, "backup-secret");
            Check("两份镜像内容一致",
                back.Security.StartupPasswordSalt == settings.Security.StartupPasswordSalt
                && back.Security.StartupPasswordHash == settings.Security.StartupPasswordHash);

            // 改启动密码不动备份密码，反之亦然
            StartupPassword.TrySet("gate-secret-2", out _);
            StartupPassword.MirrorTo(back.Security);
            CheckEqual("改启动密码后备份密码原样", back.Sync.BackupPassword, "backup-secret");
            back.Sync.BackupPassword = "another-backup";
            Check("改备份密码后启动密码镜像原样",
                StartupPassword.Verify("gate-secret-2"));

            StartupPassword.Clear();
        }
        finally
        {
            StartupPassword.DirOverride = null;
            try { Directory.Delete(tmp, true); }
            catch { /* 临时目录，删不掉也不影响结果 */ }
        }
    }
}
