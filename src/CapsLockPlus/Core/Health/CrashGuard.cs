using System;
using System.Globalization;
using System.IO;

namespace CapsLockPlus.Core.Health;

/// <summary>
/// 上次是不是"正常退出"的标记。
///
/// ## 为什么需要
///
/// 用户遇到"软件好像坏了"时，最有用的第一个问题是：
/// **上次有没有正常退出？** 崩溃、被任务管理器结束、断电，
/// 都会留下没写完的队列文件或半截日志，而用户自己并不知道。
///
/// 做法很土但可靠：启动时**先读后写**一个标记文件，正常退出时删掉它。
///   · 启动时标记已存在 → 上次没走正常退出 → 记日志 + 界面上提示一次；
///   · 正常退出 → 删除标记，下次启动自然就"干净"。
///
/// 标记文件很小（一行时间戳），即使盘满也不会写坏什么；
/// 所有 IO 都吞异常 —— 它只是"提示"，绝不该成为新的崩溃点。
/// </summary>
public static class CrashGuard
{
    /// <summary>标记文件名（放在数据目录里）。</summary>
    public const string FlagName = "running.flag";

    /// <summary>标记文件的完整路径。</summary>
    public static string FlagPath(string dataDir) => Path.Combine(dataDir, FlagName);

    /// <summary>
    /// 启动时调用：返回**上一次**是否异常退出，然后写下本次的标记。
    /// </summary>
    /// <param name="dataDir">数据目录（自检里可以传临时目录）。</param>
    /// <param name="startedAt">本次启动时间，便于事后对照日志。</param>
    public static bool MarkRunning(string dataDir, DateTime? startedAt = null)
    {
        string path = FlagPath(dataDir);
        bool unclean = false;

        try
        {
            if (File.Exists(path))
            {
                unclean = true;

                /* 把上次留下的时间戳读出来写进本次的日志，
                 * 这样"上次到底什么时候挂的"能直接对上。 */
                string prev;
                try { prev = File.ReadAllText(path).Trim(); }
                catch { prev = ""; }

                if (!string.IsNullOrEmpty(prev))
                    CoreLog.Warn($"上次没有正常退出（标记时间 {prev}），本次启动为 {Now(startedAt)}");
                else
                    CoreLog.Warn("上次没有正常退出（标记文件在，但内容读不出来）");
            }
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "检查异常退出标记");
        }

        try
        {
            Directory.CreateDirectory(dataDir);
            File.WriteAllText(path, Now(startedAt));
        }
        catch (Exception ex)
        {
            // 写不了标记不影响使用，只是下次可能误报"上次异常退出"
            CoreLog.Swallow(ex, "写入异常退出标记");
        }

        return unclean;
    }

    /// <summary>正常退出时调用：删掉标记。异常一律吞掉。</summary>
    public static void MarkCleanExit(string dataDir)
    {
        try
        {
            string path = FlagPath(dataDir);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "清除异常退出标记");
        }
    }

    private static string Now(DateTime? at)
        => (at ?? DateTime.Now).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
}
