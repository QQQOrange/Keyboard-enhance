using System.Diagnostics;
using System.Text.Json;
using CapsLockPlus.Core.Settings;

namespace CapsLockPlus.Core.Qbar;

/// <summary>
/// Qbar 的内置内容源：开始菜单应用、浏览器书签、Windows 设置、固定项和本地文件。
///
/// 这一层刻意和 <see cref="QbarService"/> 分开：QbarService 负责“怎么把结果排好”，
/// 这里只负责“有哪些东西可以搜”。所有索引都有缓存，Query 每个字符都会调用，
/// 不能每次都去扫开始菜单或者读一遍书签文件。
/// </summary>
internal static class QbarProviders
{
    internal sealed record AppItem(string Title, string Path);

    internal sealed record FileItem(string Name, string Path, bool IsDirectory);

    internal sealed record SettingItem(string Title, string Uri, params string[] Aliases);

    private static readonly object Gate = new();
    private static List<AppItem>? _apps;
    private static DateTime _appsAt;
    private static List<FileItem>? _files;
    private static DateTime _filesAt;
    private static Task? _fileBuild;
    private static List<QbarResult>? _bookmarks;
    private static DateTime _bookmarksAt;
    private static string? _esExe;
    private static bool _esChecked;

    /// <summary>Windows 设置 / 控制面板的常用入口。别名同时收中英文，输入“显示”或“display”都能命中。</summary>
    private static readonly SettingItem[] Settings =
    {
        new("显示设置", "ms-settings:display", "显示", "屏幕", "分辨率", "display", "screen", "resolution"),
        new("声音设置", "ms-settings:sound", "声音", "音量", "输出", "sound", "audio", "volume"),
        new("通知和操作", "ms-settings:notifications", "通知", "notification"),
        new("电源和睡眠", "ms-settings:powersleep", "电源", "睡眠", "power", "sleep", "battery"),
        new("存储感知", "ms-settings:storagesense", "存储", "空间", "storage"),
        new("已安装的应用", "ms-settings:appsfeatures", "应用", "卸载", "程序", "apps", "uninstall", "programs"),
        new("启动应用", "ms-settings:startupapps", "开机启动", "startup"),
        new("Wi-Fi", "ms-settings:network-wifi", "无线", "wifi", "wireless", "网络"),
        new("网络和 Internet", "ms-settings:network", "网络", "internet", "network"),
        new("VPN", "ms-settings:network-vpn", "vpn", "代理", "proxy"),
        new("蓝牙和其他设备", "ms-settings:bluetooth", "蓝牙", "bluetooth", "设备", "devices"),
        new("打印机和扫描仪", "ms-settings:printers", "打印机", "扫描仪", "printer", "scanner"),
        new("鼠标设置", "ms-settings:mousetouchpad", "鼠标", "指针", "mouse", "pointer"),
        new("键盘设置", "ms-settings:keyboard", "键盘", "keyboard"),
        new("笔和 Windows Ink", "ms-settings:pen", "手写笔", "pen", "ink"),
        new("区域和语言", "ms-settings:regionlanguage", "区域", "语言", "输入法", "language", "region", "ime"),
        new("日期和时间", "ms-settings:dateandtime", "时间", "日期", "时区", "date", "time", "timezone"),
        new("隐私和安全性", "ms-settings:privacy", "隐私", "权限", "privacy", "security"),
        new("摄像头", "ms-settings:privacy-webcam", "相机", "摄像头", "camera", "webcam"),
        new("麦克风", "ms-settings:privacy-microphone", "话筒", "麦克风", "microphone"),
        new("Windows 更新", "ms-settings:windowsupdate", "更新", "补丁", "update", "windows update"),
        new("恢复", "ms-settings:recovery", "重置", "恢复", "recovery", "reset"),
        new("开发者选项", "ms-settings:developers", "开发", "开发者", "developer"),
        new("关于本机", "ms-settings:about", "关于", "系统信息", "about", "system info"),
        new("任务栏设置", "ms-settings:taskbar", "任务栏", "taskbar"),
        new("个性化", "ms-settings:personalization", "主题", "颜色", "壁纸", "personalization", "theme", "color"),
        new("锁屏界面", "ms-settings:lockscreen", "锁屏", "lockscreen"),
        new("账户设置", "ms-settings:yourinfo", "账户", "账号", "account", "profile"),
        new("登录选项", "ms-settings:signinoptions", "登录", "密码", "pin", "sign in"),
        new("默认应用", "ms-settings:defaultapps", "默认程序", "default apps"),
        new("字体", "ms-settings:fonts", "字体", "font"),
        new("控制面板", "control.exe", "控制面板", "control panel", "control"),
        new("设备管理器", "devmgmt.msc", "设备管理器", "驱动", "device manager", "driver"),
        new("服务", "services.msc", "服务", "services"),
        new("磁盘管理", "diskmgmt.msc", "磁盘", "分区", "disk", "partition"),
        new("电源选项", "powercfg.cpl", "电源计划", "power options"),
        new("程序和功能", "appwiz.cpl", "卸载程序", "programs and features"),
        new("系统属性", "sysdm.cpl", "环境变量", "系统属性", "system properties", "environment"),
        new("网络连接", "ncpa.cpl", "网卡", "网络连接", "network connections", "adapter"),
        new("Windows 功能", "optionalfeatures.exe", "功能", "windows features", "optional features"),
    };

    /// <summary>把固定项、应用、书签、设置、文件全部并进 Qbar 候选。</summary>
    public static IEnumerable<QbarResult> Search(AppSettings settings, string text, int limit)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;
        string q = text.Trim();

        var other = new List<QbarResult>();
        other.AddRange(MatchPinned(settings.Qbar.Pinned, q));
        other.AddRange(MatchApps(q));
        other.AddRange(MatchBookmarks(q));
        other.AddRange(MatchSettings(q));

        foreach (var r in other) yield return r;

        /* 文件索引没建好时 MatchFiles 会返回一条“正在建立文件索引…”占位。
         * 如果应用/书签/设置已经命中了，这条占位只会把列表撑乱——
         * 用户看着“显示设置”下面挂一条“正在建立文件索引”，并不知道这是
         * 给文件搜索用的。只在完全没有其它结果时才展示它。 */
        foreach (var r in MatchFiles(settings, q, limit))
        {
            if (r.Payload.Length == 0 && other.Count > 0) continue;
            yield return r;
        }
    }

    /// <summary>固定项：用户主动放进 Qbar 的文件、文件夹、网址或程序。</summary>
    public static IEnumerable<QbarResult> MatchPinned(IEnumerable<QEntry>? pinned, string text)
    {
        if (pinned == null) yield break;
        foreach (var entry in pinned)
        {
            if (!entry.Enabled) continue;
            string haystack = $"{entry.Key} {entry.Comment} {entry.Value}";
            if (!haystack.Contains(text, StringComparison.OrdinalIgnoreCase)) continue;

            string title = string.IsNullOrWhiteSpace(entry.Comment) ? entry.Key : entry.Comment;
            if (Uri.TryCreate(entry.Value, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                yield return new QbarResult(QbarKind.Web, title, entry.Value, "固定", entry.Value)
                { Score = -2, IconPath = entry.IconPath };
                continue;
            }

            if (Directory.Exists(entry.Value) || File.Exists(entry.Value))
            {
                yield return new QbarResult(QbarKind.File, title, entry.Value, "固定", entry.Value)
                { Score = -2, IconPath = entry.IconPath };
                continue;
            }

            yield return new QbarResult(QbarKind.Run, title, entry.Value, "固定", entry.Value.Trim('"'),
                entry.IconPath, entry.RunAsAdmin, entry.Arguments)
            { Score = -2 };
        }
    }

    private static IEnumerable<QbarResult> MatchApps(string text)
    {
        var apps = Apps();
        int shown = 0;
        foreach (var app in apps)
        {
            if (!app.Title.Contains(text, StringComparison.OrdinalIgnoreCase)) continue;
            yield return new QbarResult(QbarKind.Run, app.Title, "开始菜单应用", "应用", app.Path)
            { Score = app.Title.StartsWith(text, StringComparison.OrdinalIgnoreCase) ? 4 : 12 };
            if (++shown >= 5) yield break;
        }
    }

    private static IEnumerable<QbarResult> MatchSettings(string text)
    {
        foreach (var item in Settings)
        {
            bool hit = item.Title.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                       item.Aliases.Any(a => a.Contains(text, StringComparison.OrdinalIgnoreCase));
            if (!hit) continue;
            yield return new QbarResult(QbarKind.Run, item.Title, "Windows 设置", "设置", item.Uri)
            { Score = item.Title.StartsWith(text, StringComparison.OrdinalIgnoreCase) ? 16 : 22 };
        }
    }

    private static IEnumerable<QbarResult> MatchBookmarks(string text)
    {
        var marks = Bookmarks();
        int shown = 0;
        foreach (var mark in marks)
        {
            if (!mark.Title.Contains(text, StringComparison.OrdinalIgnoreCase) &&
                !mark.Subtitle.Contains(text, StringComparison.OrdinalIgnoreCase)) continue;
            yield return mark with { Score = 14 };
            if (++shown >= 4) yield break;
        }
    }

    private static IEnumerable<QbarResult> MatchFiles(AppSettings settings, string text, int limit)
    {
        if (text.Length < 2) yield break;

        // Everything 的 es.exe 在装过 Everything 的机器上极快，优先用它；
        // 没装则退回后台建立的家目录索引。
        var fromEverything = SearchEverything(text, Math.Min(6, limit));
        if (fromEverything.Count > 0)
        {
            foreach (var r in fromEverything) yield return r;
            yield break;
        }

        EnsureFileIndex();
        var snapshot = _files;
        if (snapshot == null)
        {
            /* 首次搜索时索引还在后台建。给一条可见的占位，别让用户以为
             * “文件搜索根本没做”；几百毫秒后再输入就能看到结果。 */
            yield return new QbarResult(QbarKind.Command, "正在建立文件索引…",
                "首次搜索需要几秒，稍后再输入一次", "文件", "", null) { Score = 27 };
            yield break;
        }

        int shown = 0;
        foreach (var file in snapshot)
        {
            if (!file.Name.Contains(text, StringComparison.OrdinalIgnoreCase)) continue;
            yield return new QbarResult(QbarKind.File, file.Name,
                file.IsDirectory ? "文件夹" : file.Path, file.IsDirectory ? "文件夹" : "文件", file.Path)
            { Score = 25 };
            if (++shown >= 6) yield break;
        }

        /* 刚创建的文件不可能出现在旧快照里。空结果且索引已老于 20 秒时
         * 丢掉快照重扫；下一次输入就能看到，不用等 3 分钟 TTL。 */
        if (shown == 0 && DateTime.UtcNow - _filesAt > TimeSpan.FromSeconds(20))
        {
            lock (Gate) _files = null;
            EnsureFileIndex();
        }
    }

    private static List<AppItem> Apps()
    {
        lock (Gate)
        {
            if (_apps != null && DateTime.UtcNow - _appsAt < TimeSpan.FromMinutes(10)) return _apps;
        }

        var list = new List<AppItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                     Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
                 })
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;
            try
            {
                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    MaxRecursionDepth = 4,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
                };
                foreach (var file in Directory.EnumerateFiles(root, "*", options))
                {
                    string ext = Path.GetExtension(file);
                    if (!ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase) &&
                        !ext.Equals(".url", StringComparison.OrdinalIgnoreCase) &&
                        !ext.Equals(".appref-ms", StringComparison.OrdinalIgnoreCase)) continue;
                    string title = Path.GetFileNameWithoutExtension(file);
                    if (title.Length == 0 || !seen.Add(title)) continue;
                    list.Add(new AppItem(title, file));
                    if (list.Count >= 6000) break;
                }
            }
            catch (Exception ex) { CoreLog.Swallow(ex, "索引开始菜单"); }
        }

        lock (Gate)
        {
            _apps = list;
            _appsAt = DateTime.UtcNow;
        }
        return list;
    }

    private static readonly TimeSpan FileIndexTtl = TimeSpan.FromMinutes(3);

    private static void EnsureFileIndex()
    {
        lock (Gate)
        {
            if (_files != null && DateTime.UtcNow - _filesAt < FileIndexTtl) return;
            if (_fileBuild is { IsCompleted: false }) return;
            _fileBuild = Task.Run(BuildFileIndex);
        }
    }

    private static void BuildFileIndex()
    {
        var list = new List<FileItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /* 家目录三件套 + 非系统盘根目录。
         *
         * Everything 的 es.exe 只在装过 Everything 的机器上存在；没装时总不能
         * 只搜桌面 —— 用户把项目放在 D:\ 是常态。这里只扫固定磁盘，跳过
         * Windows / Program Files / AppData 这类噪音目录；索引在后台建，
         * Qbar 查询不会被它拖住。 */
        var roots = new List<string>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
        };
        /* shell 的桌面可能被重定向到非系统盘；SpecialFolder 不一定能反映。 */
        try
        {
            if (Microsoft.Win32.Registry.GetValue(
                    @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders",
                    "Desktop", null) is string shellDesktop)
            {
                string expanded = Environment.ExpandEnvironmentVariables(shellDesktop);
                if (Directory.Exists(expanded)) roots.Insert(0, expanded);
            }
        }
        catch { }
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                /* C: 只索引用户目录，整盘扫会掉进 Windows/WinSxS。 */
                if (drive.Name.StartsWith("C:", StringComparison.OrdinalIgnoreCase)) continue;
                roots.Add(drive.RootDirectory.FullName);

                /* 桌面/文档可能被用户重定向到 D: 等非系统盘，
                 * Environment.SpecialFolder 仍返回 C:\Users\...，这里补上
                 * 各盘根下的同名目录，避免只靠整盘扫描才能找到桌面文件。 */
                foreach (var name in new[] { "Desktop", "Documents", "Downloads" })
                {
                    string redirected = Path.Combine(drive.RootDirectory.FullName, name);
                    if (Directory.Exists(redirected)) roots.Add(redirected);
                }
            }
            catch { }
        }

        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Windows", "Program Files", "Program Files (x86)", "ProgramData",
            "$Recycle.Bin", "System Volume Information", "AppData",
            "node_modules", ".git", ".vs", "bin", "obj", "packages",
        };

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
        };

        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;
            var pending = new Stack<(DirectoryInfo Dir, int Depth)>();
            pending.Push((new DirectoryInfo(root), 0));
            while (pending.Count > 0 && list.Count < 60000)
            {
                var (dir, depth) = pending.Pop();
                try
                {
                    foreach (var info in dir.EnumerateFileSystemInfos("*", options))
                    {
                        string name = info.Name;
                        if (name.Length == 0 || skip.Contains(name)) continue;

                        bool isDir = (info.Attributes & FileAttributes.Directory) != 0;
                        if (seen.Add(info.FullName))
                            list.Add(new FileItem(name, info.FullName, isDir));

                        if (isDir && depth + 1 <= 4)
                            pending.Push(((DirectoryInfo)info, depth + 1));
                        if (list.Count >= 60000) break;
                    }
                }
                catch (Exception ex) { CoreLog.Swallow(ex, "索引本地文件"); }
            }
            if (list.Count >= 60000) break;

            /* 每扫完一个根就发布一次：桌面/文档往往几毫秒就好，用户不必等
             * 整个 D: 盘扫完才看到第一条文件结果。 */
            lock (Gate)
            {
                _files = list.ToList();
                _filesAt = DateTime.UtcNow;
            }
        }
    }

    private static List<QbarResult> SearchEverything(string text, int limit)
    {
        var result = new List<QbarResult>();
        string? es = EsExe();
        if (es == null) return result;

        try
        {
            var psi = new ProcessStartInfo(es)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-n");
            psi.ArgumentList.Add(limit.ToString());
            psi.ArgumentList.Add(text);

            using var proc = Process.Start(psi);
            if (proc == null) return result;
            if (!proc.WaitForExit(220))
            {
                try { proc.Kill(true); } catch { }
                return result;
            }

            string output = proc.StandardOutput.ReadToEnd();
            foreach (var raw in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!File.Exists(raw) && !Directory.Exists(raw)) continue;
                bool dir = Directory.Exists(raw);
                result.Add(new QbarResult(QbarKind.File, Path.GetFileName(raw), raw,
                    dir ? "文件夹" : "文件", raw) { Score = 24 });
                if (result.Count >= limit) break;
            }
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "Everything 搜索"); }
        return result;
    }

    private static string? EsExe()
    {
        if (_esChecked) return _esExe;
        _esChecked = true;
        foreach (var dir in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                 })
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            string candidate = Path.Combine(dir, "Everything", "es.exe");
            if (File.Exists(candidate)) return _esExe = candidate;
        }
        return _esExe;
    }

    private static List<QbarResult> Bookmarks()
    {
        lock (Gate)
        {
            if (_bookmarks != null && DateTime.UtcNow - _bookmarksAt < TimeSpan.FromMinutes(5)) return _bookmarks;
        }

        var list = new List<QbarResult>();
        foreach (var path in BookmarkFiles())
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (!doc.RootElement.TryGetProperty("roots", out var roots)) continue;
                foreach (var root in roots.EnumerateObject()) WalkBookmark(root.Value, list);
            }
            catch (Exception ex) { CoreLog.Swallow(ex, "读取浏览器书签"); }
        }

        lock (Gate)
        {
            _bookmarks = list;
            _bookmarksAt = DateTime.UtcNow;
        }
        return list;
    }

    private static void WalkBookmark(JsonElement node, List<QbarResult> list)
    {
        if (list.Count >= 4000) return;
        if (node.ValueKind != JsonValueKind.Object) return;

        string type = node.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
        if (type.Equals("url", StringComparison.OrdinalIgnoreCase) &&
            node.TryGetProperty("url", out var urlEl) &&
            node.TryGetProperty("name", out var nameEl))
        {
            string url = urlEl.GetString() ?? "";
            string name = nameEl.GetString() ?? url;
            if (url.Length > 0)
                list.Add(new QbarResult(QbarKind.Web, name, url, "书签", url));
        }

        if (node.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in children.EnumerateArray()) WalkBookmark(child, list);
        }
    }

    private static IEnumerable<string> BookmarkFiles()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var (browser, profileRoot) in new[]
                 {
                     ("Google/Chrome", Path.Combine(local, "Google", "Chrome", "User Data")),
                     ("Microsoft/Edge", Path.Combine(local, "Microsoft", "Edge", "User Data")),
                     ("BraveSoftware/Brave-Browser", Path.Combine(local, "BraveSoftware", "Brave-Browser", "User Data")),
                 })
        {
            if (!Directory.Exists(profileRoot)) continue;
            yield return Path.Combine(profileRoot, "Default", "Bookmarks");
            foreach (var dir in Directory.EnumerateDirectories(profileRoot, "Profile *"))
                yield return Path.Combine(dir, "Bookmarks");
        }
    }
}
