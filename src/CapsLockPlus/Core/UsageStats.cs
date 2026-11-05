using CapsLockPlus.Core.Security;
using System.IO;
using System.Text.Json;

namespace CapsLockPlus.Core;

/// <summary>
/// 使用统计：本机执行过多少次动作、最常用的是哪个。
///
/// ## 为什么要有
///
/// 产品的卖点是"省时间"，但用户感知不到——除非把它量化出来。
/// 这里只做**最小可用的三件事**：累计次数、按动作排行、按天留存（30 天）。
/// 不记按键内容、不记窗口标题、不上传，纯本地文件。
///
/// ## 性能
///
/// 每次动作都会调 <see cref="Record"/>，所以：
///   · 只在内存里累加；
///   · 每 20 次才落盘一次，避免频繁写文件；
///   · 退出时由 <see cref="Flush"/> 补一次。
/// </summary>
public static class UsageStats
{
    private const int FlushEvery = 20;
    private const int KeepDays = 30;

    private static readonly object Gate = new();
    private static readonly Dictionary<string, int> ByAction = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, int> ByDay = new(StringComparer.Ordinal);
    private static int _total;
    private static int _dirty;
    private static bool _loaded;

    /// <summary>自检用：把这些文件写到别处，不污染真实数据目录。</summary>
    internal static string? PathOverride { get; set; }

    private static string FilePath
    {
        get
        {
            string dir = PathOverride ?? AppPaths.DataDir;
            return Path.Combine(dir, "usage.json");
        }
    }

    /// <summary>记一次成功执行的动作。</summary>
    public static void Record(string? actionId)
    {
        if (string.IsNullOrWhiteSpace(actionId)) return;

        lock (Gate)
        {
            EnsureLoaded();

            _total++;
            ByAction[actionId] = ByAction.TryGetValue(actionId, out int n) ? n + 1 : 1;

            string day = DateTime.Now.ToString("yyyy-MM-dd");
            ByDay[day] = ByDay.TryGetValue(day, out int d) ? d + 1 : 1;

            if (++_dirty >= FlushEvery) SaveLocked();
        }
    }

    /// <summary>汇总：总次数、最常用动作、今日次数。</summary>
    public static (int Total, string TopAction, int TopCount, int Today) Summary()
    {
        lock (Gate)
        {
            EnsureLoaded();

            string top = "";
            int topCount = 0;
            foreach (var kv in ByAction)
                if (kv.Value > topCount) { top = kv.Key; topCount = kv.Value; }

            ByDay.TryGetValue(DateTime.Now.ToString("yyyy-MM-dd"), out int today);
            return (_total, top, topCount, today);
        }
    }

    /// <summary>落盘（幂等；没有改动就不写）。</summary>
    public static void Flush()
    {
        lock (Gate) SaveLocked();
    }

    /// <summary>清空统计（设置页提供）。</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            ByAction.Clear();
            ByDay.Clear();
            _total = 0;
            _dirty = 0;
            _loaded = true;
            try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch { }
        }
    }

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;

        try
        {
            if (!File.Exists(FilePath)) return;

            var data = JsonSerializer.Deserialize<Persisted>(SecureFile.ReadAllText(FilePath));
            if (data == null) return;

            _total = data.Total;
            foreach (var kv in data.ByAction ?? new()) ByAction[kv.Key] = kv.Value;
            foreach (var kv in data.ByDay ?? new()) ByDay[kv.Key] = kv.Value;
        }
        catch (Exception ex)
        {
            // 统计坏了不能影响主流程，直接当空账
            CoreLog.Warn("读取使用统计失败（将重新累计）：" + ex.Message);
        }
    }

    private static void SaveLocked()
    {
        if (_dirty == 0) return;
        _dirty = 0;

        try
        {
            // 只保留最近 N 天，避免文件无限增长
            if (ByDay.Count > KeepDays)
            {
                foreach (string key in ByDay.Keys.OrderBy(k => k, StringComparer.Ordinal)
                             .Take(ByDay.Count - KeepDays).ToList())
                    ByDay.Remove(key);
            }

            string dir = Path.GetDirectoryName(FilePath) ?? "";
            if (dir.Length > 0) Directory.CreateDirectory(dir);

            var data = new Persisted
            {
                Total = _total,
                ByAction = new Dictionary<string, int>(ByAction, StringComparer.OrdinalIgnoreCase),
                ByDay = new Dictionary<string, int>(ByDay, StringComparer.Ordinal),
            };
            SecureFile.WriteAllText(FilePath, JsonSerializer.Serialize(data));
        }
        catch (Exception ex)
        {
            CoreLog.Warn("保存使用统计失败：" + ex.Message);
        }
    }

    /// <summary>把使用统计序列化成一段 JSON（备份/导出用）。</summary>
    public static string ExportJson()
    {
        lock (Gate)
        {
            EnsureLoaded();
            var data = new Persisted
            {
                Total = _total,
                ByAction = new Dictionary<string, int>(ByAction, StringComparer.OrdinalIgnoreCase),
                ByDay = new Dictionary<string, int>(ByDay, StringComparer.Ordinal),
            };
            return JsonSerializer.Serialize(data);
        }
    }

    /// <summary>从备份/导出里恢复使用统计（整体替换）。</summary>
    public static bool ImportJson(string json)
    {
        try
        {
            var data = JsonSerializer.Deserialize<Persisted>(json);
            if (data == null) return false;

            lock (Gate)
            {
                _total = data.Total;
                ByAction.Clear();
                if (data.ByAction != null)
                    foreach (var kv in data.ByAction) ByAction[kv.Key] = kv.Value;
                ByDay.Clear();
                if (data.ByDay != null)
                    foreach (var kv in data.ByDay) ByDay[kv.Key] = kv.Value;

                _dirty = 0;
                SaveLocked();
            }

            return true;
        }
        catch (Exception ex)
        {
            CoreLog.Warn("恢复使用统计失败：" + ex.Message);
            return false;
        }
    }

    private sealed class Persisted
    {
        public int Total { get; set; }
        public Dictionary<string, int>? ByAction { get; set; }
        public Dictionary<string, int>? ByDay { get; set; }
    }
}
