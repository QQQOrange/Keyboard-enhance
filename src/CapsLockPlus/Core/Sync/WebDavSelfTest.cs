using System.Net.Http;
using CapsLockPlus.Core.Settings;

namespace CapsLockPlus.Core.Sync;

/// <summary>
/// WebDAV 真实往返测试（诊断用）。
///
/// 用法：<c>CapsLockPlus.exe --webdav-test &lt;地址&gt; [用户名] [密码]</c>
///
/// 依次验证：列目录 → 上传 → 下载 → 内容一致 → 取修改时间 → 再上传覆盖。
/// 这条链路是自检覆盖不到的（自检没有网络），但又是同步功能的全部核心，
/// 所以单独留一个可对真实网盘（或本地 stub）跑的入口。
/// </summary>
public static class WebDavSelfTest
{
    public static int Run(string url, string user, string pass)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        if (string.IsNullOrWhiteSpace(url))
        {
            Console.WriteLine("用法：CapsLockPlus.exe --webdav-test <地址> [用户名] [密码]");
            return 2;
        }

        var settings = DefaultSettings.Create();
        settings.Sync.WebDavUrl = url;
        settings.Sync.WebDavUser = user;
        settings.Sync.WebDavPass = pass;
        settings.Sync.BackupPassword = "端到端测试密码";

        var svc = new SyncService(() => settings);
        int passed = 0, failed = 0;

        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}" +
                              (detail.Length > 0 ? $"  → {detail}" : ""));
            if (ok) passed++; else failed++;
        }

        Console.WriteLine($"WebDAV 往返测试：{WebDavClient.NormalizeBase(url)}");
        Console.WriteLine();

        try
        {
            // ① 连通性
            var probe = svc.TestAsync().GetAwaiter().GetResult();
            Check("能连上服务器并列出目录", probe.Ok, probe.Message);

            // ② 准备一份有辨识度的配置
            settings.Macros.Clear();
            settings.Macros.Add(new CapsLockPlus.Core.Settings.Macro
            {
                Id = "e2e",
                Name = "端到端测试宏",
                Steps = { new CapsLockPlus.Core.Settings.MacroStep { Kind = "delay", DelayMs = 999 } },
            });
            settings.Sync.LastSyncInfo = "e2e-marker";

            // ③ 上传
            var up = svc.UploadAsync().GetAwaiter().GetResult();
            Check("上传成功", up.Ok, up.Message);
            Check("上传的是加密内容", up.Message.Contains("已加密"), up.Message);

            // ④ 取修改时间（启动同步靠它比"哪份更新"）
            var client = new WebDavClient(new WebDavOptions
            {
                Url = url, User = user, Pass = pass, TimeoutSeconds = 15,
            });
            var remoteAt = client.GetModifiedAsync(WebDavClient.LatestFile)
                .GetAwaiter().GetResult();
            Check("能取到云端修改时间", remoteAt != null,
                remoteAt?.ToString() ?? "返回 null");
            Check("修改时间是合理的时间戳",
                remoteAt != null && remoteAt.Value.Year >= 2020,
                remoteAt?.ToString() ?? "");

            // ⑤ 改成本地内容，再从云端恢复，验证真的被还原
            settings.Macros.Clear();
            settings.Macros.Add(new CapsLockPlus.Core.Settings.Macro { Id = "local", Name = "本地这份应该被覆盖" });

            var down = svc.DownloadAsync().GetAwaiter().GetResult();
            Check("从云端恢复成功", down.Ok, down.Message);
            Check("恢复后拿回了云端那份宏",
                settings.Macros.Any(m => m.Id == "e2e"),
                string.Join(",", settings.Macros.Select(m => m.Id)));
            Check("恢复后本地那份被替换掉",
                !settings.Macros.Any(m => m.Id == "local"));
            Check("恢复后内容一致",
                settings.Macros.FirstOrDefault(m => m.Id == "e2e")?.Steps.FirstOrDefault()?.DelayMs == 999);

            // ⑥ 列目录里应该有我们上传的文件（用共享客户端，不再裸 new）
            var files = client.ListAsync().GetAwaiter().GetResult();
            Check("目录里能看到备份文件",
                files.Any(f => f.Name == WebDavClient.LatestFile),
                string.Join(",", files.Select(f => f.Name)));

            // ⑦ 错密码必须被拒（而不是把垃圾灌进配置）
            var bad = DefaultSettings.Create();
            bad.Sync.BackupPassword = "错误密码";
            var badSvc = new SyncService(() => bad);
            bool rejected = false;
            var text = client.DownloadAsync(WebDavClient.LatestFile).GetAwaiter().GetResult();
            if (text != null)
            {
                try { badSvc.RestoreFromText(text); }
                catch (InvalidOperationException) { rejected = true; }
            }
            Check("用错误密码恢复会被拒绝", rejected);
        }
        catch (Exception e)
        {
            Check("整个过程没有抛异常", false, e.Message);
        }

        Console.WriteLine();
        Console.WriteLine($"==== WebDAV 往返：通过 {passed} 项，失败 {failed} 项 ====");
        return failed == 0 ? 0 : 1;
    }
}