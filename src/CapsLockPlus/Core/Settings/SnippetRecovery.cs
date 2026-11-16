namespace CapsLockPlus.Core.Settings;

/// <summary>
/// 从历史副本里"找回"当前配置中已经不在的别名。
///
/// ## 为什么需要它
///
/// 用户反馈过"更新几次之后，别名替换里的一些别名不见了"。真实原因通常是
/// 下面几种，而且都**不会在界面上留下痕迹**：
///
///   · 编辑后 400ms 内进程被强杀（保存是防抖的），最后一条没落盘；
///   · 主文件读不出来时程序用 <c>settings.json.bak</c> 顶替，
///     于是"最近一次保存之后新增的条目"整体退回；
///   · 加密状态下密钥失效 → 进入只读保护，界面上改了但保存被静默跳过。
///
/// 这三种都有同一个特征：**旧的副本还在磁盘上**。所以恢复思路很简单——
/// 把目录里所有设置副本读一遍，找出"当前没有、但副本里有"的触发词，
/// 让用户勾选合并回来。
/// </summary>
public static class SnippetRecovery
{
    /// <summary>每天一份副本的存放目录。</summary>
    public static string SnapshotDir => Path.Combine(AppPaths.DataDir, "backups");

    /// <summary>一份可恢复的别名。</summary>
    public sealed record Candidate(string Trigger, string Text, string Group, string SourceFile, DateTime SourceTime)
    {
        /// <summary>给界面显示的一句话（内容太长就截断，别名里可能是一整段正文）。</summary>
        public string Preview
        {
            get
            {
                string text = (Text ?? "").Replace("\r", "").Replace("\n", " ⏎ ");
                if (text.Length == 0) return "（空内容）";
                return text.Length <= 40 ? text : text[..40] + "…";
            }
        }
    }

    /// <summary>扫描结果。</summary>
    public sealed record ScanResult(List<Candidate> Candidates, int FilesScanned, int FilesUnreadable);

    /// <summary>
    /// 目录里所有值得一看的历史副本：主文件、.bak、.broken*、我手动留的时间戳副本，
    /// 以及 <see cref="SnapshotDir"/> 里的每日快照。按修改时间从新到旧。
    /// </summary>
    public static List<string> Sources()
    {
        var files = new List<string>();
        try
        {
            if (Directory.Exists(AppPaths.DataDir))
            {
                foreach (string f in Directory.GetFiles(AppPaths.DataDir, "settings.json*"))
                {
                    string name = Path.GetFileName(f);
                    if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
                    files.Add(f);
                }
            }

            if (Directory.Exists(SnapshotDir))
                files.AddRange(Directory.GetFiles(SnapshotDir, "*.json"));
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "扫描别名历史副本");
        }

        return files
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(SafeTime)
            .ToList();
    }

    /// <summary>
    /// 找出"历史副本里有、当前配置里没有"的别名。
    ///
    /// 同一个触发词在多个副本里出现时只留**最新那份**的内容 ——
    /// 那是用户最后一次编辑的结果。
    /// </summary>
    public static ScanResult Scan(AppSettings current)
    {
        var have = new HashSet<string>(
            (current?.Global?.Snippets ?? new List<Snippet>())
                .Select(s => (s.Trigger ?? "").Trim())
                .Where(t => t.Length > 0),
            StringComparer.Ordinal);

        var found = new Dictionary<string, Candidate>(StringComparer.Ordinal);
        int scanned = 0, unreadable = 0;

        foreach (string file in Sources())
        {
            /* 主文件本身就是当前配置，读了也只会得出"全都有"，跳过省一次解密。 */
            if (string.Equals(file, AppPaths.SettingsFile, StringComparison.OrdinalIgnoreCase)) continue;

            if (!SettingsStore.TryLoadFile(file, out var loaded, out _))
            {
                unreadable++;
                continue;
            }
            scanned++;

            foreach (var snippet in loaded.Global?.Snippets ?? new List<Snippet>())
            {
                string trigger = (snippet.Trigger ?? "").Trim();
                if (trigger.Length == 0) continue;
                if (have.Contains(trigger)) continue;
                if (found.ContainsKey(trigger)) continue;    // 新→旧遍历，先到的是最新的

                found[trigger] = new Candidate(trigger, snippet.Text ?? "", snippet.Group ?? "",
                                               Path.GetFileName(file), SafeTime(file));
            }
        }

        return new ScanResult(found.Values.ToList(), scanned, unreadable);
    }

    /// <summary>
    /// 把勾选的别名合并回配置。已有同触发词的**不覆盖**（用户现在的版本优先），
    /// 返回实际加进去的条数。
    /// </summary>
    public static int Restore(AppSettings settings, IEnumerable<Candidate> picks)
    {
        if (settings?.Global == null) return 0;

        var have = new HashSet<string>(
            settings.Global.Snippets.Select(s => (s.Trigger ?? "").Trim()),
            StringComparer.Ordinal);

        int added = 0;
        foreach (var pick in picks ?? Enumerable.Empty<Candidate>())
        {
            string trigger = (pick.Trigger ?? "").Trim();
            if (trigger.Length == 0 || !have.Add(trigger)) continue;

            settings.Global.Snippets.Add(new Snippet
            {
                Trigger = trigger,
                Text = pick.Text ?? "",
                Group = (pick.Group ?? "").Trim(),
                Comment = "",
                Apps = "",
                Enabled = true,
            });
            if (!string.IsNullOrWhiteSpace(pick.Group)) SnippetGroupOps.Add(settings, pick.Group.Trim());
            added++;
        }

        return added;
    }

    /// <summary>
    /// 每天给主设置文件留一份原始副本（直接复制字节，不重新序列化）。
    ///
    /// 加密状态也照存不误：副本只能用同一台机器解，但它让"以后再说"成为可能。
    /// 保留最近 30 份，超出的按时间删掉最旧的。
    /// </summary>
    public static void SnapshotFile()
    {
        try
        {
            string source = AppPaths.SettingsFile;
            if (!File.Exists(source)) return;

            Directory.CreateDirectory(SnapshotDir);
            string target = Path.Combine(SnapshotDir, "settings-" + DateTime.Now.ToString("yyyyMMdd") + ".json");
            if (!File.Exists(target))
            {
                File.Copy(source, target, false);
                CoreLog.Info("已保存今天的设置快照：" + Path.GetFileName(target));
            }

            var stale = Directory.GetFiles(SnapshotDir, "settings-*.json")
                .OrderByDescending(SafeTime)
                .Skip(30)
                .ToList();
            foreach (string f in stale)
            {
                try { File.Delete(f); } catch { /* 预期内：文件被占用时下次再清 */ }
            }
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "保存设置快照");
        }
    }

    private static DateTime SafeTime(string path)
    {
        try { return File.GetLastWriteTime(path); }
        catch { return DateTime.MinValue; }
    }
}
