using System.Collections.Concurrent;
using System.IO;

namespace CapsLockPlus.Core;

/// <summary>
/// 单线程动作队列。钩子回调里绝对不能做耗时操作（超过 ~300ms 会被系统静默摘掉钩子），
/// 所以所有功能动作都丢进这里按顺序执行。
/// </summary>
public sealed class ActionQueue : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new(new ConcurrentQueue<Action>());
    private readonly Thread _thread;
    private volatile bool _disposed;

    public ActionQueue(string name = "CapsLockPlus.Actions")
    {
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = name,
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    private void Loop()
    {
        foreach (var action in _queue.GetConsumingEnumerable())
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                CoreLog.Error("动作执行失败", ex);
            }
        }
    }

    public void Post(Action action)
    {
        if (_disposed) return;
        try { _queue.Add(action); } catch (InvalidOperationException) { /* 已关闭 */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _queue.CompleteAdding();
    }
}

/// <summary>
/// 程序使用的目录约定。
///
/// **数据跟着软件走**：所有用户数据都放在 exe 同级的 `KeyboardEnhance Data`
/// 文件夹里，备份放在 exe 同级的 `KeyboardEnhance Back` 里（见 SyncService）。
/// 好处很直接：整个文件夹拷到 U 盘 / 另一台机器，软件和全部数据一起搬走，
/// 不用再去 %LocalAppData% 里翻。
///
/// 装到 Program Files 这类**不可写**位置时才回落到 %LocalAppData%\KeyboardEnhance，
/// 并把回落事实暴露给界面（<see cref="DataDirIsFallback"/>），不静默换地方。
/// </summary>
public static class AppPaths
{
    /// <summary>数据目录名（在软件所在目录下）。</summary>
    public const string DataFolderName = "KeyboardEnhance Data";

    /// <summary>改名前的数据目录名（在 %LocalAppData% 下）。只用于迁移。</summary>
    public const string LegacyFolderName = "CapsLockPlus";

    /// <summary>
    /// 数据目录。可用环境变量 CAPSLOCKPLUS_DATA 覆盖（便携模式 / 自检用），
    /// 否则用 exe 同级的 <see cref="DataFolderName"/>。
    /// </summary>
    public static string DataDir { get; } = ResolveDataDir();

    /// <summary>数据目录是不是回落到了 %LocalAppData%（软件目录不可写）。</summary>
    public static bool DataDirIsFallback { get; private set; }

    /// <summary>数据位置的一句话说明（界面直接显示）。</summary>
    public static string DataDirNote => DataDirIsFallback
        ? "软件目录不可写，数据改放在这里（换位置请先给软件目录写权限）"
        : "数据就在软件旁边的「" + DataFolderName + "」文件夹里";

    private static string ResolveDataDir()
    {
        var custom = Environment.GetEnvironmentVariable("CAPSLOCKPLUS_DATA");
        if (!string.IsNullOrWhiteSpace(custom))
        {
            try
            {
                // 必须规范化成绝对路径：托盘程序的当前目录常常是 System32，
                // 相对路径会让数据目录、备份路径甚至 Process.Start 的目标
                // 全都随 CWD 漂移，排查时完全对不上。
                custom = Path.GetFullPath(custom);
                Directory.CreateDirectory(custom);
                return custom;
            }
            catch (Exception ex) { CoreLog.Swallow(ex, "使用自定义数据目录"); }
        }

        /* 首选：exe 同级。先真写一个文件再删 —— 只测 CreateDirectory
         * 骗不过 UAC 虚拟化（它会"成功"地把文件重定向到 VirtualStore）。 */
        string primary = Path.Combine(AppDir, DataFolderName);
        if (TryPrepare(primary))
        {
            MigrateLegacyData(primary);
            return primary;
        }

        // 回落：Program Files 之类不可写位置
        string fallback = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KeyboardEnhance");
        try { Directory.CreateDirectory(fallback); } catch { }   /* 预期内：探测/重试型，失败即视为无结果 */
        DataDirIsFallback = true;
        MigrateLegacyData(fallback);
        return fallback;
    }

    /// <summary>
    /// 目录能不能真的写文件。
    ///
    /// 只调 CreateDirectory 是不够的：在 Program Files 下它会返回成功，
    /// 实际写入被 UAC 虚拟化重定向到 %LocalAppData%\VirtualStore ——
    /// 用户会看到"数据目录建好了"，但文件不在那儿。
    /// </summary>
    private static bool TryPrepare(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            string probe = Path.Combine(dir, ".writable");
            File.WriteAllText(probe, "1");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 把老位置（%LocalAppData%\CapsLockPlus）的数据搬到新目录。
    ///
    /// 三条规矩：
    ///   * 目标已经有 settings.json → 说明新目录在用，一个文件都不动
    ///     （否则会用旧数据覆盖用户刚改的配置）；
    ///   * 只拷目标没有的文件，绝不覆盖；
    ///   * 只拷用户数据，不拷 logs / backups / 自检产物 ——
    ///     logs 是诊断垃圾，backups 由 SyncService 单独搬到 Back 目录。
    ///
    /// 这里**不能写日志**：CoreLog 的路径依赖 DataDir，而 DataDir 正在初始化，
    /// 递归调用会拿到空路径。迁移结果写进 migrated.txt 供排查。
    /// </summary>
    private static void MigrateLegacyData(string target)
    {
        try
        {
            string legacy = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), LegacyFolderName);

            if (!Directory.Exists(legacy)) return;
            if (Path.GetFullPath(legacy).TrimEnd('\\')
                .Equals(Path.GetFullPath(target).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return;

            // 新目录已经有设置 → 不迁移，避免覆盖
            if (File.Exists(Path.Combine(target, "settings.json"))) return;

            int n = CopyTree(legacy, target, 0);
            try
            {
                File.AppendAllText(Path.Combine(target, "migrated.txt"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} 从 {legacy} 迁移 {n} 个文件到 {target}"
                    + Environment.NewLine);
            }
            catch { }   /* 预期内：探测/重试型，失败即视为无结果 */
        }
        catch
        {
            // 迁移失败不能拦住启动：最坏情况是用户看到默认配置，旧数据还在原处
        }
    }

    /// <summary>递归拷目录，跳过诊断/备份类子目录与临时文件。返回拷了几个文件。</summary>
    private static int CopyTree(string from, string to, int depth)
    {
        if (depth > 3) return 0;
        int n = 0;

        try
        {
            Directory.CreateDirectory(to);
            foreach (string file in Directory.EnumerateFiles(from))
            {
                string name = Path.GetFileName(file);
                if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.StartsWith("selftest", StringComparison.OrdinalIgnoreCase)) continue;

                string dest = Path.Combine(to, name);
                if (File.Exists(dest)) continue;       // 不覆盖
                try { File.Copy(file, dest); n++; } catch { }   /* 预期内：探测/重试型，失败即视为无结果 */
            }

            foreach (string dir in Directory.EnumerateDirectories(from))
            {
                string name = Path.GetFileName(dir);
                // logs = 诊断、backups = 由 SyncService 搬到 Back 目录
                if (name.Equals("logs", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.Equals("backups", StringComparison.OrdinalIgnoreCase)) continue;
                n += CopyTree(dir, Path.Combine(to, name), depth + 1);
            }
        }
        catch { }   /* 预期内：探测/重试型，失败即视为无结果 */

        return n;
    }

    /// <summary>仅供自检：跑一次目录复制（不碰真实的老数据目录）。</summary>
    internal static int CopyTreeForTest(string from, string to) => CopyTree(from, to, 0);

    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string BindingsFile => Path.Combine(DataDir, "winsbind.json");
    public static string AppDir => AppContext.BaseDirectory;
}
