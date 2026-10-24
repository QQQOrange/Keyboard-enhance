using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using CapsLockPlus.Core;
using CapsLockPlus.Core.Actions;
using CapsLockPlus.Core.Icons;
using CapsLockPlus.Core.Input;
using CapsLockPlus.Core.Interop;
using CapsLockPlus.Core.Qbar;
using CapsLockPlus.Core.Settings;
using CapsLockPlus.Core.Text;
using CapsLockPlus.Core.Update;
using CapsLockPlus.Core.Windows;

namespace CapsLockPlus;

/// <summary>
/// 自检：不依赖界面，验证核心逻辑。
/// 用 `KeyboardEnhance.exe --selftest` 运行，退出码 0 表示全部通过。
/// </summary>
public static partial class SelfTest
{

    /* Qbar 面板、搜索引擎、网址识别。
     * 与 SelfTest.cs 同属 partial 类，只按业务域分文件。 */

    /// <summary>
    /// 默认引擎表：国内用拼音首字母、国外用英文前两个字母，且都带图标。
    /// 这些约定一旦被改乱，用户会记不住关键字，所以专门校验。
    /// </summary>
    private static void TestSearchEngines()
    {
        var settings = DefaultSettings.Create();
        var engines = settings.Search
            .Where(x => !x.Key.Equals("default", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Check("引擎数量 >= 25", engines.Count >= 25, engines.Count.ToString());

        // 国内引擎：关键字应为拼音首字母（小写字母）
        var domestic = new (string Key, string Name)[]
        {
            ("bd", "百度"), ("tb", "淘宝"), ("zh", "知乎"), ("bili", "哔哩哔哩"),
            ("wx", "微信"), ("wb", "微博"), ("jd", "京东"), ("xhs", "小红书"),
            ("dy", "抖音"), ("db", "豆瓣"), ("csdn", "CSDN"), ("juejin", "掘金"),
            ("gitee", "Gitee"), ("pdd", "拼多多"), ("tx", "腾讯视频"), ("iqy", "爱奇艺"),
        };
        int domesticOk = 0;
        foreach (var (key, name) in domestic)
        {
            var hit = engines.FirstOrDefault(e => e.Key == key);
            if (hit == null) continue;
            domesticOk++;
            if (!string.Equals(hit.Comment, name, StringComparison.Ordinal))
                CoreLog.Warn($"引擎 {key} 的备注应为 {name}，实际 {hit.Comment}");
        }
        CheckEqual("国内引擎全部存在", domesticOk, domestic.Length);

        // 国外引擎：关键字应为英文前两个字母
        var foreign = new (string Key, string Name)[]
        {
            ("go", "Google"), ("bi", "Bing"), ("gh", "GitHub"),
            ("wi", "维基百科"), ("yo", "YouTube"), ("re", "Reddit"),
            ("st", "Startpage"), ("md", "MDN"), ("am", "Amazon"),
            ("fa", "Facebook"), ("np", "npm"), ("nu", "NuGet"),
        };
        int foreignOk = 0;
        foreach (var (key, name) in foreign)
        {
            var hit = engines.FirstOrDefault(e => e.Key == key);
            if (hit == null) continue;
            foreignOk++;
            if (!string.Equals(hit.Comment, name, StringComparison.Ordinal))
                CoreLog.Warn($"引擎 {key} 的备注应为 {name}，实际 {hit.Comment}");
        }
        CheckEqual("国外引擎全部存在", foreignOk, foreign.Length);

        // 每个引擎都要有图标：内置图标是打包资源（EngineIcon），
        // 不再是磁盘文件，所以这里检查 EngineIcon 非空且能真正加载出来。
        int withIcon = engines.Count(e =>
            !string.IsNullOrEmpty(e.EngineIcon) &&
            Core.Icons.IconCache.FromResource(e.EngineIcon) != null);
        CheckEqual("每个引擎都配了可用图标", withIcon, engines.Count);

        // 关键字不能重复（否则 Qbar 无法区分该用哪个引擎）
        int dup = engines.GroupBy(e => e.Key, StringComparer.OrdinalIgnoreCase)
            .Count(g => g.Count() > 1);
        CheckEqual("引擎关键字无重复", dup, 0);

        // 每个引擎链接都要带 {q} 占位符
        int withQ = engines.Count(e => e.Value.Contains("{q}"));
        CheckEqual("所有引擎链接都含 {q}", withQ, engines.Count);

        // 引擎表版本号必须是有效数字（用于老用户自动补齐缺失引擎）
        Check("引擎表版本号 >= 1", DefaultSettings.BuiltinEnginesVersion >= 1,
            DefaultSettings.BuiltinEnginesVersion.ToString());

        // 内置图标必须优先于旧的磁盘图标路径。
        //
        // 这是"换了图标却没生效"的回归点：老用户的条目上还挂着早先版本生成的
        // .svg 路径，若优先级搞反，界面就会继续显示旧图标。
        var probeEntry = new QEntry
        {
            Key = "bd",
            Comment = "百度",
            Value = "https://www.baidu.com/s?wd={q}",
            IconPath = @"C:\__not_exist__\old.svg",   // 旧的、已失效的路径
            EngineIcon = "bai_du",
        };
        var picked = Core.Icons.IconCache.ForEntry(probeEntry);
        Check("内置图标优先于旧图标路径", picked != null);
        Check("取到的是内置图标（尺寸 64）",
            picked is System.Windows.Media.Imaging.BitmapSource { PixelWidth: 64 },
            (picked as System.Windows.Media.Imaging.BitmapSource)?.PixelWidth.ToString() ?? "null");
    }

    private static void TestEvaluator()
    {
        CheckEqual("1+2*3 = 7", Eval("1+2*3"), "7");
        CheckEqual("(1+2)*3 = 9", Eval("(1+2)*3"), "9");
        CheckEqual("0.1+0.2 = 0.3", Eval("0.1+0.2"), "0.3");
        CheckEqual("2^10 = 1024", Eval("2^10"), "1024");
        CheckEqual("10/4 = 2.5", Eval("10/4"), "2.5");
        CheckEqual("sqrt(16) = 4", Eval("sqrt(16)"), "4");
        CheckEqual("max(3,7) = 7", Eval("max(3,7)"), "7");
        CheckEqual("pi 近似", Eval("pi").StartsWith("3.14159"), true);
        CheckEqual("全角符号 ×", Eval("6×7"), "42");
        Check("除零报错", !ExpressionEvaluator.TryEvaluate("1/0", out _, out _));
        Check("非法表达式报错", !ExpressionEvaluator.TryEvaluate("1+", out _, out _));
        CheckEqual("负数", Eval("-5+2"), "-3");
        CheckEqual("科学计数法", Eval("1e3+1"), "1001");
    }

    private static string Eval(string expr) =>
        ExpressionEvaluator.TryEvaluate(expr, out double v, out _) ? ExpressionEvaluator.Format(v) : "<error>";

    private static void TestQbar()
    {
        var settings = DefaultSettings.Create();
        var qbar = new QbarService(() => settings, () => new List<string> { "hello world", "another clip" });

        var math = qbar.Query("=1+2*3");
        Check("= 开头直接给出计算结果", math.Count > 0 && math[0].Kind == QbarKind.Math, math.FirstOrDefault()?.Title);
        CheckEqual("计算结果正确", math[0].Payload, "7");

        var search = qbar.Query("bd 天气");
        Check("搜索指令命中", search.Count > 0 && search[0].Kind == QbarKind.Search);
        Check("搜索链接带上了关键词", search[0].Payload.Contains("weather") ||
                                      search[0].Payload.Contains(Uri.EscapeDataString("天气")));

        var cmd = qbar.Query("/set");
        Check("内置命令补全", cmd.Any(r => r.Kind == QbarKind.Command && r.Payload == "openSettings"));

        var run = qbar.Query("calc");
        Check("程序名可被搜到", run.Any(r => r.Kind == QbarKind.Run && r.Payload.Contains("calc")));

        var fallback = qbar.Query("一个不存在的关键词");
        Check("兜底默认搜索", fallback.Count > 0 && fallback.Last().Kind == QbarKind.Search);

        var clip = qbar.Query("hello");
        Check("剪贴板历史可被搜到", clip.Any(r => r.Kind == QbarKind.Clipboard));

        var empty = qbar.Query("");
        Check("空输入给出默认建议", empty.Count > 0);

        Check("算式识别", QbarService.LooksLikeMath("12*34+5"));
        Check("普通单词不误判为算式", !QbarService.LooksLikeMath("hello world"));
        Check("带等号算式识别", QbarService.LooksLikeMath("1+1="));
    }

    /// <summary>Qbar 标签：输入 "bd " 之后 bd 变成标签、输入框只剩搜索词。</summary>
    private static void TestChips()
    {
        var settings = DefaultSettings.Create();
        var qbar = new QbarService(() => settings, () => new List<string>());

        var chip = qbar.ResolveChip("bd");
        Check("bd 能解析成标签", chip != null);
        CheckEqual("标签类型是搜索", chip?.Kind, ChipKind.Search);
        CheckEqual("标签名字优先用备注", chip?.Label, "百度");
        Check("bd 自带图标（优先显示图标）", chip?.HasIcon == true, chip?.IconPath);

        var run = qbar.ResolveChip("calc");
        Check("calc 能解析成程序标签", run?.Kind == ChipKind.Run, run?.Kind.ToString());
        Check("程序标签自动带 exe 图标或首字兜底", run != null && (run.HasIcon || run.Initial.Length > 0));

        var web = qbar.ResolveChip("gh");
        Check("gh 解析为搜索（搜索优先于网址）", web?.Kind is ChipKind.Search or ChipKind.Web);

        Check("未知关键字不变成标签", qbar.ResolveChip("不存在的词") == null);
        Check("default 不参与标签", qbar.ResolveChip("default") == null);

        // 标签模式下的查询：只剩搜索词
        var results = qbar.Query("天气", chip);
        Check("标签模式下有结果", results.Count > 0);
        Check("结果用对应引擎", results[0].Payload.Contains("baidu"), results[0].Payload);
        Check("结果带上了关键词", results[0].Payload.Contains(Uri.EscapeDataString("天气")));

        var empty = qbar.Query("", chip);
        Check("标签模式空输入给出提示", empty.Count > 0);

        // 标签模式下 = 仍可计算
        var calc = qbar.Query("=6*7", chip);
        Check("标签模式下仍可直接计算", calc[0].Kind == QbarKind.Math && calc[0].Payload == "42");

        // 没有图标的条目应当给出首字与颜色
        var noIcon = new QEntry { Key = "xx", Value = "https://example.com/?q={q}", Comment = "示例" };
        settings.Search.Add(noIcon);
        var chip2 = qbar.ResolveChip("xx");
        Check("无图标条目可用首字兜底", chip2 != null && !chip2.HasIcon && chip2.Initial == "示");
        Check("首字颜色稳定", IconCache.ColorFor("示例") == IconCache.ColorFor("示例"));

        // 图标缓存
        Check("内置图标文件已生成", File.Exists(IconUrls.Baidu), IconUrls.Baidu);
    }

    /// <summary>
    /// 文字度量：确保任何字号下，输入框高度都够放下文字。
    ///
    /// 这是"文字只显示上半部分"那个 bug 的回归测试：
    /// 只要 MinBoxHeight 算出来的高度小于"文字高 + 上下内边距 + 边框"，
    /// 文字就会被裁，所以这里逐字号验证。
    /// </summary>
    /// <summary>
    /// Qbar 的网址识别与文件路径识别。
    ///
    /// 关键回归点：裸域名（www.baidu.com）要能补协议直达；
    /// 盘符路径（C:\Users）不能被误判成网址，否则会去浏览器搜索一个路径。
    /// </summary>
    private static void TestQbarUrlAndPath()
    {
        Check("识别 https 网址", Core.Qbar.QbarService.LooksLikeUrl("https://example.com"));
        Check("识别 http 网址", Core.Qbar.QbarService.LooksLikeUrl("http://localhost:8080"));
        Check("识别裸域名", Core.Qbar.QbarService.LooksLikeUrl("www.baidu.com"));
        Check("识别 .com 域名", Core.Qbar.QbarService.LooksLikeUrl("google.com"));
        Check("识别 .cn 域名", Core.Qbar.QbarService.LooksLikeUrl("qq.com"));
        Check("识别 localhost", Core.Qbar.QbarService.LooksLikeUrl("localhost"));
        Check("识别 IP", Core.Qbar.QbarService.LooksLikeUrl("192.168.1.1"));

        // 不能误判
        Check("盘符不是网址", !Core.Qbar.QbarService.LooksLikeUrl("C:\\Users"));
        Check("带空格的不是网址", !Core.Qbar.QbarService.LooksLikeUrl("hello world"));
        Check("单个词不是网址", !Core.Qbar.QbarService.LooksLikeUrl("hello"));
        Check("UNC 路径不是网址", !Core.Qbar.QbarService.LooksLikeUrl("\\\\server\\share"));
        Check("空串不是网址", !Core.Qbar.QbarService.LooksLikeUrl(""));

        // 路径识别
        Check("识别盘符路径", Core.Qbar.QbarService.LooksLikePath("C:\\Users"));
        Check("识别斜杠路径", Core.Qbar.QbarService.LooksLikePath("D:/code"));
        Check("识别 UNC 路径", Core.Qbar.QbarService.LooksLikePath("\\\\server\\share"));
        Check("普通词不是路径", !Core.Qbar.QbarService.LooksLikePath("hello"));

        // 真实路径（数据目录一定存在）
        string dir = AppPaths.DataDir;
        Check("数据目录确实存在", Directory.Exists(dir));
        Check("数据目录不是网址", !Core.Qbar.QbarService.LooksLikeUrl(dir));
        Check("数据目录是路径", Core.Qbar.QbarService.LooksLikePath(dir));
    }

    /// <summary>
    /// Qbar 面板内的引擎搜索：按关键字 / 备注 / 内容匹配。
    /// 用户明确要求"主要就通过备注或者关键字来搜索"。
    /// </summary>
    private static void TestQbarEntryFilter()
    {
        var list = new List<QEntry>
        {
            new() { Key = "bd", Value = "https://www.baidu.com/s?wd={q}", Comment = "百度" },
            new() { Key = "zh", Value = "https://www.zhihu.com/search?q={q}", Comment = "知乎" },
            new() { Key = "go", Value = "https://www.google.com/search?q={q}", Comment = "Google" },
            new() { Key = "bi", Value = "https://www.bing.com/search?q={q}", Comment = "必应" },
        };

        CheckEqual("空过滤词返回全部", QEntry.Filter(list, "").Count, 4);
        CheckEqual("null 过滤词返回全部", QEntry.Filter(list, null).Count, 4);
        CheckEqual("只有空格也算不过滤", QEntry.Filter(list, "   ").Count, 4);

        var byKey = QEntry.Filter(list, "bd");
        CheckEqual("按关键字能搜到", byKey.Count, 1);
        CheckEqual("搜到的是百度", byKey[0].Comment, "百度");

        var byComment = QEntry.Filter(list, "知乎");
        CheckEqual("按中文备注能搜到", byComment.Count, 1);
        CheckEqual("搜到的是知乎", byComment[0].Key, "zh");

        var byValue = QEntry.Filter(list, "google.com");
        CheckEqual("按网址内容能搜到", byValue.Count, 1);
        CheckEqual("搜到的是 Google", byValue[0].Key, "go");

        CheckEqual("按大小写不敏感匹配", QEntry.Filter(list, "GOOGLE").Count, 1);
        CheckEqual("英文备注小写也能搜", QEntry.Filter(list, "bing").Count, 1);
        CheckEqual("搜不到时返回空", QEntry.Filter(list, "不存在的词xyz").Count, 0);

        // 真实引擎表规模
        var real = DefaultSettings.Create().Search;
        Check("内置引擎表足够大", real.Count > 200, real.Count.ToString());
        var hit = QEntry.Filter(real, "知乎");
        Check("在真实引擎表里能搜到知乎", hit.Any(e => e.Comment == "知乎"),
            string.Join(",", hit.Select(e => e.Comment)));
        Check("在真实引擎表里能搜到哔哩哔哩",
            QEntry.Filter(real, "哔哩").Any());
    }

    /// <summary>
    /// 「直达网页」分类：网址 + 备注，备注显示在键帽上。
    ///
    /// 验证：
    ///   1. 动作表里有 webPage 且属于「直达网页」分类；
    ///   2. 执行时把（规范化后的）网址交给浏览器；
    ///   3. KeyBinding.Note 能存能读，空值不写进 JSON；
    ///   4. 网址规范化容忍各种写法（不带协议、带 www、内网 http）；
    ///   5. 备注优先于域名显示在键帽上。
    /// </summary>
    private static void TestWebPageCategory()
    {
        var settings = DefaultSettings.Create();
        var ctx = new Core.Actions.ActionContext
        {
            Settings = () => settings,
            Clipboard = new Core.Actions.ClipboardService(),
            Windows = new Core.Actions.WindowActions(),
            WindowBindings = new Core.Actions.WindowBindingService(settings),
            Mouse = new Core.Actions.MouseService(),
            Notify = _ => { },
        };

        string? opened = null;
        Core.Actions.ActionContext.OpenUrlInterceptForTest = u => opened = u;
        string? openedFolder = null;
        Core.Actions.ActionContext.OpenFolderInterceptForTest = p => openedFolder = p;
        string? openedFile = null;
        Core.Actions.ActionContext.OpenFileInterceptForTest = p => openedFile = p;
        try
        {
            var cat = Core.Actions.ActionCatalog.Create(ctx);

            // 1. 两个动作都在「直达链接」分类下
            var wp = cat.Items.FirstOrDefault(i => i.Id == "webPage");
            Check("动作表有 webPage", wp != null,
                string.Join(",", cat.Items.Where(i => i.Category == "直达链接").Select(i => i.Id)));
            CheckEqual("webPage 在「直达链接」分类", wp?.Category, "直达链接");
            CheckEqual("webPage 标题是「直达网址」", wp?.Title, "直达网址");
            Check("webPage 需要参数（网址）", wp?.NeedsArg == true);

            var of = cat.Items.FirstOrDefault(i => i.Id == "openFolder");
            Check("动作表有 openFolder", of != null);
            CheckEqual("openFolder 在「直达链接」分类", of?.Category, "直达链接");
            CheckEqual("openFolder 标题是「直达文件夹」", of?.Title, "直达文件夹");
            Check("openFolder 需要参数（路径）", of?.NeedsArg == true);

            var ofi = cat.Items.FirstOrDefault(i => i.Id == "openFile");
            Check("动作表有 openFile", ofi != null);
            CheckEqual("openFile 在「直达链接」分类", ofi?.Category, "直达链接");
            CheckEqual("openFile 标题是「直达文件」", ofi?.Title, "直达文件");
            Check("openFile 需要参数（路径）", ofi?.NeedsArg == true);
            Check("「直达链接」分类下只有这三个动作",
                cat.Items.Count(i => i.Category == "直达链接") == 3,
                string.Join(",", cat.Items.Where(i => i.Category == "直达链接").Select(i => i.Id)));

            // 2. 执行时把规范化后的网址传出去
            cat.Invoke("webPage", "https://example.com");
            CheckEqual("执行 webPage 传出网址", opened, "https://example.com/");

            opened = null;
            cat.Invoke("webPage", "github.com");
            Check("不带协议的网址被补成 https",
                opened != null && opened.StartsWith("https://github.com", StringComparison.Ordinal),
                opened ?? "(null)");

            // 3. openFolder 把路径整理后交出去（引号 / 环境变量 / ~ 都要处理）
            cat.Invoke("openFolder", "\"C:\\Windows\"");
            CheckEqual("执行 openFolder 去掉包裹引号", openedFolder, @"C:\Windows");

            openedFolder = null;
            cat.Invoke("openFolder", "  ");
            Check("空路径不执行打开", openedFolder == null);

            // 3b. openFile：路径整理后交给默认程序
            cat.Invoke("openFile", "\"D:/文档/方案.docx\"");
            CheckEqual("执行 openFile 归一正斜杠并去引号", openedFile, @"D:\文档\方案.docx");

            openedFile = null;
            cat.Invoke("openFile", "   ");
            Check("空文件路径不执行打开", openedFile == null);
        }
        finally
        {
            Core.Actions.ActionContext.OpenUrlInterceptForTest = null;
            Core.Actions.ActionContext.OpenFolderInterceptForTest = null;
            Core.Actions.ActionContext.OpenFileInterceptForTest = null;
        }

        // 3. Note 字段能存能读，空值不序列化
        var b = new KeyBinding
        {
            Combo = "caps_w", Action = "webPage",
            Arg = "https://example.com", Note = "公司后台",
        };
        CheckEqual("Note 能存", b.Note, "公司后台");

        var noNote = new KeyBinding { Combo = "caps_e", Action = "webPage", Arg = "https://x.com" };
        string json = System.Text.Json.JsonSerializer.Serialize(noNote);
        Check("空备注不出现在 JSON 里", !json.Contains("Note"), json);

        // 中文会被默认转义成 \uXXXX，所以断言"非空"而不是断言字面文本
        string jsonWith = System.Text.Json.JsonSerializer.Serialize(b);
        Check("非空备注出现在 JSON 里", jsonWith.Contains("\"Note\""), jsonWith);

        // 4. 网址规范化
        CheckEqual("裸域名补 https",
            Core.Actions.UrlUtil.Normalize("github.com"), "https://github.com/");
        CheckEqual("已带协议不改写",
            Core.Actions.UrlUtil.Normalize("http://intranet/wiki"), "http://intranet/wiki");
        CheckEqual("空输入返回 null",
            Core.Actions.UrlUtil.Normalize("  "), null);
        CheckEqual("取域名去掉 www",
            Core.Actions.UrlUtil.HostOf("https://www.baidu.com/s?wd=x"), "baidu.com");
        CheckEqual("坏输入不抛异常",
            Core.Actions.UrlUtil.HostOf("!!!not a url!!!"), "网页");

        // 5. 键帽标签：备注优先
        CheckEqual("有备注用备注",
            Core.Actions.UrlUtil.LabelOf("公司后台", "https://example.com"), "公司后台");
        CheckEqual("无备注用域名",
            Core.Actions.UrlUtil.LabelOf("", "https://x.com/a/b"), "x.com");

        // 6. 直达文件夹：路径展开 + 键帽标签
        CheckEqual("去掉包裹引号", Core.Actions.ActionContext.ExpandFolderPath("\"D:\\Work\"") , @"D:\Work");
        CheckEqual("正斜杠归一", Core.Actions.ActionContext.ExpandFolderPath("D:/Work/A"), @"D:\Work\A");
        CheckEqual("去掉尾部反斜杠", Core.Actions.ActionContext.ExpandFolderPath(@"D:\Work\"), @"D:\Work");
        CheckEqual("盘符根目录保留", Core.Actions.ActionContext.ExpandFolderPath(@"D:\"), @"D:\");
        CheckEqual("空输入返回空串", Core.Actions.ActionContext.ExpandFolderPath("   "), "");
        Check("shell: 虚拟路径原样保留",
            Core.Actions.ActionContext.ExpandFolderPath("shell:Downloads")
                .Equals("shell:Downloads", StringComparison.OrdinalIgnoreCase));
        Check("~ 展开成用户目录",
            !Core.Actions.ActionContext.ExpandFolderPath("~").Contains('~'),
            Core.Actions.ActionContext.ExpandFolderPath("~"));

        CheckEqual("文件夹有备注用备注",
            UI.Views.KeysView.FolderLabelOf("项目", @"D:\Work\2026\项目文档"), "项目");
        CheckEqual("文件夹无备注用文件夹名",
            UI.Views.KeysView.FolderLabelOf("", @"D:\Work\项目文档"), "项目文档");
        CheckEqual("文件夹名为空时兜底",
            UI.Views.KeysView.FolderLabelOf("", ""), "文件夹");

        // 7. 直达文件：键帽标签用文件名
        CheckEqual("文件有备注用备注",
            UI.Views.KeysView.FileLabelOf("周报", @"D:\文档\方案.docx"), "周报");
        CheckEqual("文件无备注用文件名（去扩展名）",
            UI.Views.KeysView.FileLabelOf("", @"D:\文档\方案.docx"), "方案");
        CheckEqual("文件名兜底",
            UI.Views.KeysView.FileLabelOf("", ""), "文件");
        CheckEqual("ExpandPath 与 ExpandFolderPath 行为一致",
            Core.Actions.ActionContext.ExpandPath("D:/Work/A/"),
            Core.Actions.ActionContext.ExpandFolderPath("D:/Work/A/"));

        // 8. 打开之后要把目标窗口带到前台：先从 shell 命令里认出"目标程序"
        CheckEqual("带引号的 open 命令能取出 exe",
            Core.Windows.WindowService.ParseExeFromShellCommand(
                "\"C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe\" -- \"%1\""),
            @"C:\Program Files\Google\Chrome\Application\chrome.exe");
        CheckEqual("不带引号（路径有空格）也能取出 exe",
            Core.Windows.WindowService.ParseExeFromShellCommand(
                @"C:\Program Files\Mozilla Firefox\firefox.exe -osint -url %1"),
            @"C:\Program Files\Mozilla Firefox\firefox.exe");
        CheckEqual("环境变量会被展开",
            Core.Windows.WindowService.ParseExeFromShellCommand(
                @"%SystemRoot%\explorer.exe %1"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"));
        CheckEqual("空命令返回空串",
            Core.Windows.WindowService.ParseExeFromShellCommand(""), "");
        CheckEqual("没有 exe 的命令返回空串",
            Core.Windows.WindowService.ParseExeFromShellCommand("rundll32 foo"), "");
        Check("两个 exe 名比较忽略路径与大小写",
            Core.Windows.WindowService.SameExeName("chrome.exe", @"C:\X\CHROME.EXE"));
        Check("不同程序不会被认成同一个",
            !Core.Windows.WindowService.SameExeName("chrome.exe", "msedge.exe"));
    }

    /// <summary>
    /// 「直达链接」的智能识别（系统分类里那个）：选中什么就开什么。
    ///
    /// 这个功能的风险全在**误判**上：把一句话当网址塞进浏览器、
    /// 把 `reade.md` 当域名打开，比"什么都不做"更打扰人。
    /// 所以这里用可控的假文件系统，把各种边界情况逐条钉住：
    ///
    ///   1. 动作必须归在「系统」，和打开网址 / 运行程序并列；
    ///   2. 网址（带协议 / 裸域名 / 引号包裹 / 末尾句号）；
    ///   3. 文件夹与文件按**真实存在性**区分，不存在时给出准确的"不存在"类型；
    ///   4. 路径的各种写法：引号、正斜杠、环境变量、UNC、file:///、shell:；
    ///   5. 不是链接的内容一律不乱开（多行、普通句子、像域名的文件名）。
    /// </summary>
    private static void TestSmartOpen()
    {
        // ---------- 1. 动作归位 ----------
        var settings = DefaultSettings.Create();
        var notes = new List<string>();
        var ctx = new Core.Actions.ActionContext
        {
            Settings = () => settings,
            Clipboard = new Core.Actions.ClipboardService(),
            Windows = new Core.Actions.WindowActions(),
            WindowBindings = new Core.Actions.WindowBindingService(settings),
            Mouse = new Core.Actions.MouseService(),
            Notify = n => notes.Add(n),
        };

        var cat = Core.Actions.ActionCatalog.Create(ctx);
        var so = cat.Items.FirstOrDefault(i => i.Id == "smartOpen");
        Check("动作表有 smartOpen", so != null);
        CheckEqual("smartOpen 在「智能直达」分类", so?.Category, "智能直达");
        CheckEqual("smartOpen 标题是「智能直达」", so?.Title, "智能直达");
        Check("smartOpen 不需要预填参数", so?.NeedsArg == false);
        Check("「智能直达」分类下没有别的可见动作",
            cat.Items.Where(i => i.Category == "智能直达").All(i => i.Id == "smartOpen"),
            string.Join(",", cat.Items.Where(i => i.Category == "智能直达").Select(i => i.Id)));

        // ---------- 2. 识别：用假文件系统，避免依赖测试机磁盘 ----------
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"D:\文档\方案.docx", @"C:\Windows\notepad.exe",
        };
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"D:\Work\项目", @"D:\Work\2026", @"C:\Windows",
        };
        static bool Has(HashSet<string> set, string p) => set.Contains(p);

        Core.Actions.SmartTarget Hit(string text) =>
            Core.Actions.SmartTargetDetector.Detect(text, p => Has(files, p), p => Has(dirs, p));

        // 2a. 网址
        CheckEqual("带协议的网址", Hit("https://github.com/a?b=1").Kind, Core.Actions.SmartKind.Url);
        CheckEqual("http 内网地址", Hit("http://intranet/wiki").Kind, Core.Actions.SmartKind.Url);
        CheckEqual("裸域名", Hit("github.com").Kind, Core.Actions.SmartKind.Url);
        CheckEqual("带 www 与路径", Hit("www.baidu.com/s?wd=x").Kind, Core.Actions.SmartKind.Url);
        CheckEqual("mailto 也算链接", Hit("mailto:a@b.com").Kind, Core.Actions.SmartKind.Url);
        CheckEqual("网址末尾的中文句号被去掉",
            Hit("https://a.com。").Target, "https://a.com");
        CheckEqual("引号包裹的网址也能认",
            Hit("「https://example.com/path」").Target, "https://example.com/path");

        // 2b. 文件夹 / 文件：存在性优先
        CheckEqual("存在的目录 → 文件夹", Hit(@"D:\Work\项目").Kind, Core.Actions.SmartKind.Folder);
        CheckEqual("引号包裹的路径", Hit("\"D:\\Work\\项目\"").Kind, Core.Actions.SmartKind.Folder);
        CheckEqual("正斜杠 + 尾分隔符", Hit("D:/Work/项目/").Target, @"D:\Work\项目");
        CheckEqual("存在的文件 → 文件", Hit(@"D:\文档\方案.docx").Kind, Core.Actions.SmartKind.File);
        CheckEqual("文件目标做了路径归一", Hit("D:/文档/方案.docx").Target, @"D:\文档\方案.docx");
        CheckEqual("带空格路径", Hit(@"C:\Windows\notepad.exe").Kind, Core.Actions.SmartKind.File);

        // 2c. 不存在时也要说清是"文件不存在"还是"文件夹不存在"
        CheckEqual("不存在的文件", Hit(@"D:\文档\没有这个.docx").Kind, Core.Actions.SmartKind.MissingFile);
        CheckEqual("不存在的文件夹", Hit(@"D:\工作\2026规划").Kind, Core.Actions.SmartKind.MissingFolder);
        CheckEqual("结尾带分隔符按文件夹算", Hit(@"D:\没有这个目录\").Kind, Core.Actions.SmartKind.MissingFolder);
        CheckEqual("UNC 路径", Hit(@"\\nas\share\doc").Kind, Core.Actions.SmartKind.MissingFolder);

        // 2d. 特殊写法
        CheckEqual("file:/// 地址", Hit("file:///D:/文档/方案.docx").Kind, Core.Actions.SmartKind.File);
        CheckEqual("shell: 虚拟目录", Hit("shell:Downloads").Kind, Core.Actions.SmartKind.Folder);
        CheckEqual("shell: 目标原样保留", Hit("shell:Downloads").Target, "shell:Downloads");
        Check("环境变量被展开",
            !Hit(@"%USERPROFILE%\Desktop").Target.Contains('%'),
            Hit(@"%USERPROFILE%\Desktop").Target);

        // 2e. 不像链接的内容：一个都不许开
        CheckEqual("多行选区不认", Hit("第一行\r\n第二行").Kind, Core.Actions.SmartKind.None);
        CheckEqual("普通句子不认", Hit("今天天气不错").Kind, Core.Actions.SmartKind.None);
        CheckEqual("没有一个点的单词不认", Hit("hello").Kind, Core.Actions.SmartKind.None);
        CheckEqual("像域名的文件名（readme.md）不认", Hit("readme.md").Kind, Core.Actions.SmartKind.None);
        CheckEqual("像域名的脚本名（build.ps1）不认", Hit("build.ps1").Kind, Core.Actions.SmartKind.None);
        CheckEqual("空输入不认", Hit("   ").Kind, Core.Actions.SmartKind.None);
        CheckEqual("超长文本不认",
            Core.Actions.SmartTargetDetector.Detect(new string('a', 3000),
                _ => false, _ => false).Kind, Core.Actions.SmartKind.None);

        // ---------- 3. 动作执行：三类目标分别落到三个通道 ----------
        string? openedUrl = null, openedFolder = null, openedFile = null;
        Core.Actions.ActionContext.OpenUrlInterceptForTest = u => openedUrl = u;
        Core.Actions.ActionContext.OpenFolderInterceptForTest = p => openedFolder = p;
        Core.Actions.ActionContext.OpenFileInterceptForTest = p => openedFile = p;

        string tmp = Path.Combine(Path.GetTempPath(), "ke_smart_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmp);
        string tmpFile = Path.Combine(tmp, "方案.txt");
        File.WriteAllText(tmpFile, "selftest");

        try
        {
            ctx.CaptureTextForTest = () => tmp;
            cat.Invoke("smartOpen", "");
            CheckEqual("选中文件夹 → 交给资源管理器", openedFolder, tmp);
            Check("选中文件夹 → 不该走浏览器", openedUrl == null);

            openedFolder = null;
            ctx.CaptureTextForTest = () => tmpFile;
            cat.Invoke("smartOpen", "");
            CheckEqual("选中文件 → 交给默认程序", openedFile, tmpFile);

            openedFile = null;
            openedUrl = null;
            ctx.CaptureTextForTest = () => "github.com";
            cat.Invoke("smartOpen", "");
            Check("选中裸域名 → 走浏览器（自动补 https）",
                openedUrl != null && openedUrl.StartsWith("https://github.com", StringComparison.Ordinal),
                openedUrl ?? "(null)");
            Check("裸域名不该走文件通道", openedFile == null && openedFolder == null);

            openedUrl = null;
            notes.Clear();
            ctx.CaptureTextForTest = () => "这是一段普通文字";
            cat.Invoke("smartOpen", "");
            Check("普通文字什么都不开",
                openedUrl == null && openedFolder == null && openedFile == null);
            Check("普通文字给出明确提示",
                notes.Any(n => n.Contains("既不是网址")), string.Join(" | ", notes));

            notes.Clear();
            ctx.CaptureTextForTest = () => Path.Combine(tmp, "没有这个.docx");
            cat.Invoke("smartOpen", "");
            Check("文件不存在时提示「文件不存在」",
                notes.Any(n => n.Contains("文件不存在")), string.Join(" | ", notes));
            Check("文件不存在时不去打开", openedFile == null);

            notes.Clear();
            ctx.CaptureTextForTest = () => "";
            ctx.ManualCopyWaitForTest = _ => "";
            cat.Invoke("smartOpen", "");
            Check("完全没取到内容时提示先选中",
                notes.Any(n => n.Contains("先选中")), string.Join(" | ", notes));

            // 取词失败 → 用户手动 Ctrl+C 的保底路径要能接上
            openedFolder = null;
            ctx.CaptureTextForTest = () => "";
            ctx.ManualCopyWaitForTest = _ => tmp;
            cat.Invoke("smartOpen", "");
            CheckEqual("手动复制的内容也能直达", openedFolder, tmp);
        }
        finally
        {
            Core.Actions.ActionContext.OpenUrlInterceptForTest = null;
            Core.Actions.ActionContext.OpenFolderInterceptForTest = null;
            Core.Actions.ActionContext.OpenFileInterceptForTest = null;
            ctx.CaptureTextForTest = null;
            ctx.ManualCopyWaitForTest = null;
            try { Directory.Delete(tmp, true); } catch { }
        }
    }

    /// <summary>
    /// 网页标题抓取：解析 HTML 的 title，处理实体与空白。
    /// </summary>
    private static void TestWebTitleExtract()
    {
        CheckEqual("基本 title",
            Core.Actions.WebTitleFetcher.ExtractTitle("<html><head><title>Hello</title></head></html>"),
            "Hello");

        CheckEqual("大写标签也认",
            Core.Actions.WebTitleFetcher.ExtractTitle("<TITLE>GitHub</TITLE>"), "GitHub");

        CheckEqual("带属性的 title",
            Core.Actions.WebTitleFetcher.ExtractTitle("<title lang=\"zh\">知乎</title>"), "知乎");

        CheckEqual("HTML 实体解码",
            Core.Actions.WebTitleFetcher.ExtractTitle("<title>A &amp; B &lt;C&gt;</title>"), "A & B <C>");

        CheckEqual("折叠空白",
            Core.Actions.WebTitleFetcher.ExtractTitle("<title>  很多\n   空格  </title>"), "很多 空格");

        Check("没有 title 返回 null",
            Core.Actions.WebTitleFetcher.ExtractTitle("<html>no title</html>") == null);
        Check("空 HTML 返回 null",
            Core.Actions.WebTitleFetcher.ExtractTitle("") == null);

        // 过长标题要截断（备注字段不该塞几百字）
        string longTitle = new string('字', 200);
        var got = Core.Actions.WebTitleFetcher.ExtractTitle($"<title>{longTitle}</title>");
        Check("超长标题被截断", got != null && got.Length <= 60, (got?.Length ?? 0).ToString());

        // 通过测试钩子验证"抓取 → 标题"整条链（不真的发网络请求）
        Core.Actions.WebTitleFetcher.InterceptForTest = _ => "抓到的标题";
        try
        {
            CheckEqual("抓取链返回标题",
                Core.Actions.WebTitleFetcher.Fetch("example.com"), "抓到的标题");
            Check("非法网址不去抓",
                Core.Actions.WebTitleFetcher.Fetch("  ") == null);
        }
        finally
        {
            Core.Actions.WebTitleFetcher.InterceptForTest = null;
        }
    }

    /// <summary>
    /// Qbar 面板配色要按底色亮度推导，浅色主题下不能"白字白底"。
    /// </summary>
    private static void TestQbarThemeContrast()
    {
        var t = typeof(CapsLockPlus.UI.Windows.QbarWindow);
        var m = t.GetMethod("IsLightColor",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Check("QbarWindow 有亮度判定", m != null);
        if (m == null) return;

        bool IsLight(string hex) =>
            (bool)m.Invoke(null, new object[]
                { (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex) })!;

        Check("白色判为浅色", IsLight("#FFFFFF"));
        Check("浅灰判为浅色", IsLight("#F0F0F0"));
        Check("深色判为非浅色", !IsLight("#1C1F27"));
        Check("纯黑判为非浅色", !IsLight("#000000"));
        Check("中蓝判为非浅色", !IsLight("#2B6CF6"));
    }

    /// <summary>
    /// Qbar 里"引擎胶囊"的文字必须两种明暗下都看得清。
    ///
    /// 用户报的是：胶囊里表示引擎（备注）的字颜色不对，白天看着发糊。
    /// 根因有两个，都出在"字色跟着面板明暗走"这件事上：
    ///   1. 引擎名用的是**次级灰**（浅色 #5B6478 / 深色 #C9D6EC），
    ///      压在胶囊那层 13% 叠色上对比度不够；
    ///   2. 首字块底色是**品牌色**（各家深浅不一），跟着面板走就会出现
    ///      "浅色面板 → 深灰字压深蓝牌子"这种隐形组合。
    ///
    /// 现在：引擎名用主文字色；首字块按品牌色自身亮度挑黑白。
    /// 这里用 WCAG 对比度把关（≥ 4.5 是正文级可读的下限）。
    /// </summary>
    private static void TestQbarChipTextContrast()
    {
        Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);

        // 两种面板底色（跟随主题时的实际取值），以及胶囊那层 13% 叠色
        var panels = new (string Name, Color Panel, Color Veil)[]
        {
            ("浅色面板", C("#F4F5F7"), Colors.Black),
            ("深色面板", C("#1C1F27"), Colors.White),
        };

        foreach (var (name, panel, veil) in panels)
        {
            Color chipBase = UI.Windows.QbarWindow.Overlay(panel, veil, 0x22 / 255.0);
            Color text = UI.Windows.QbarWindow.PrimaryOn(panel);

            double ratio = UI.Windows.QbarWindow.Contrast(text, chipBase);
            Check($"{name}：胶囊里的引擎名对比度 ≥ 4.5", ratio >= 4.5, $"{ratio:F2}:1");

            /* 换回旧的次级灰，必须**明确不达标** —— 否则说明这条断言
             * 根本没在管颜色（比如两边都算成同一个值）。 */
            Color old = UI.Windows.QbarWindow.PrimaryOn(panel) == Colors.White
                ? C("#C9D6EC") : C("#5B6478");
            double oldRatio = UI.Windows.QbarWindow.Contrast(old, chipBase);
            Check($"{name}：旧的次级灰确实不达标（回归指向）", oldRatio < ratio,
                $"旧={oldRatio:F2}:1 新={ratio:F2}:1");
        }

        // 首字块：品牌色深浅不一，黑白必须自己挑，且都得达到正文级
        var brands = new (string Name, string Hex)[]
        {
            ("百度蓝", "#2932E1"),
            ("微软蓝", "#0078D4"),
            ("必应青", "#008373"),
            ("谷歌红", "#EA4335"),
            ("必应黄", "#FFB900"),
            ("白", "#FFFFFF"),
            ("黑", "#000000"),
            ("中灰", "#808080"),
        };

        foreach (var (name, hex) in brands)
        {
            Color bg = C(hex);
            Color picked = UI.Windows.QbarWindow.BestTextOn(bg);
            double ratio = UI.Windows.QbarWindow.Contrast(picked, bg);

            Check($"首字块（{name}）上的字色对比度 ≥ 4.5", ratio >= 4.5,
                $"{ratio:F2}:1  color=#{picked.R:X2}{picked.G:X2}{picked.B:X2}");

            // 必须真的是"两者里更高的那个"，不是碰巧蒙对
            Color other = picked == Colors.White ? C("#161A23") : Colors.White;
            Check($"首字块（{name}）选的是更清楚的那个颜色",
                ratio >= UI.Windows.QbarWindow.Contrast(other, bg) - 1e-9);
        }

        // 深色牌子给白字、浅色牌子给深字 —— 这条最直观，也最能挡住"忘了挑"
        Check("深蓝品牌色上用白字", UI.Windows.QbarWindow.BestTextOn(C("#2932E1")) == Colors.White);
        Check("亮黄品牌色上用深字", UI.Windows.QbarWindow.BestTextOn(C("#FFB900")) != Colors.White);
    }

    /// <summary>
    /// 「直接搜索」：用选中文本 + 指定引擎，在默认浏览器里搜索。
    /// </summary>
    private static void TestDirectSearch()
    {
        var settings = DefaultSettings.Create();
        var ctx = new Core.Actions.ActionContext
        {
            Settings = () => settings,
            Clipboard = new Core.Actions.ClipboardService(),
            Windows = new Core.Actions.WindowActions(),
            WindowBindings = new Core.Actions.WindowBindingService(settings),
            Mouse = new Core.Actions.MouseService(),
            Notify = _ => { },
        };

        var cat = Core.Actions.ActionCatalog.Create(ctx);

        // ① 动作存在且分类正确
        var bdAction = cat.Items.FirstOrDefault(i => i.Id == "search:bd");
        Check("动作表有 search:bd", bdAction != null,
            string.Join(",", cat.Items.Where(i => i.Category == "直接搜索").Select(i => i.Id)));
        CheckEqual("search:bd 在「直接搜索」分类", bdAction?.Category, "直接搜索");
        Check("有「直接搜索」分类的多个动作",
            cat.Items.Count(i => i.Category == "直接搜索") >= 5,
            cat.Items.Count(i => i.Category == "直接搜索").ToString());

        // ② v2.8 起直接搜索覆盖**全部**引擎（下拉支持输入过滤，不怕多）
        Check("直接搜索覆盖全部引擎",
            cat.Items.Count(i => i.Category == "直接搜索") >= 50,
            cat.Items.Count(i => i.Category == "直接搜索").ToString());

        // ③ 执行时把选中文本编码进 URL 并交给浏览器
        string? opened = null;
        Core.Actions.ActionContext.OpenUrlInterceptForTest = u => opened = u;
        try
        {
            // 用 overrideText 绕开真实的剪贴板读取
            ctx.SearchSelection("bd", "测试 关键词");
            Check("搜索打开了网址", opened != null, opened ?? "(null)");
            if (opened != null)
            {
                Check("用的是百度的引擎", opened.Contains("baidu.com", StringComparison.OrdinalIgnoreCase), opened);
                Check("关键词被正确编码",
                    opened.Contains(Uri.EscapeDataString("测试 关键词"), StringComparison.Ordinal),
                    opened);
            }
        }
        finally
        {
            Core.Actions.ActionContext.OpenUrlInterceptForTest = null;
        }

        // ④ 取不到选区时不打开浏览器（用测试桩绕开真实剪贴板）
        ctx.CaptureTextForTest = () => "";
        ctx.ManualCopyWaitForTest = _ => "";
        opened = null;
        Core.Actions.ActionContext.OpenUrlInterceptForTest = u => opened = u;
        try
        {
            ctx.SearchSelection("bd", "   ");
            Check("取不到选区时不搜索", opened == null, opened ?? "(null)");
        }
        finally
        {
            Core.Actions.ActionContext.OpenUrlInterceptForTest = null;
        }

        // ⑤ 未知引擎安全返回且不崩
        Check("未知引擎不抛异常", ctx.SearchSelection("no_such_engine", "abc"));

        TestDirectSearchEmptyArgRegression(ctx, cat);
    }

    /// <summary>
    /// 回归测试：按键绑定里 Arg 是**空字符串**时，直接搜索必须照常工作。
    ///
    /// 这是 v3.5.0 修掉的核心 bug —— 绑定 <c>search:bd</c> 的 Arg 默认是 ""，
    /// 老代码用 <c>overrideText ?? …</c> 判断，空串不算 null，
    /// 于是永远判定"没有选中的文字"，按了键什么也不发生。
    /// 这个用例必须走**动作表**（和真实热键同一条路径），不能直接调 SearchSelection，
    /// 否则测不到参数传递这一层。
    /// </summary>
    private static void TestDirectSearchEmptyArgRegression(Core.Actions.ActionContext ctx,
                                                           Core.Actions.ActionCatalog cat)
    {
        Section("直接搜索 · 空参数回归（v3.5.0）");

        ctx.CaptureTextForTest = () => "被选中的文字ABC";
        ctx.ManualCopyWaitForTest = _ => "";

        string? opened = null;
        Core.Actions.ActionContext.OpenUrlInterceptForTest = u => opened = u;
        try
        {
            // ① 空字符串参数（真实绑定的默认值）
            opened = null;
            bool ok = cat.Invoke("search:bd", "");
            Check("空参数时动作执行成功", ok);
            Check("空参数时真的搜索了", opened != null, opened ?? "(null)");
            if (opened != null)
            {
                Check("空参数时关键词正确",
                    opened.Contains(Uri.EscapeDataString("被选中的文字ABC"), StringComparison.Ordinal),
                    opened);
            }

            // ② 纯空白参数（绑定编辑器里手敲空格）
            opened = null;
            cat.Invoke("search:bd", "   ");
            Check("空白参数时真的搜索了", opened != null, opened ?? "(null)");

            // ③ null 参数（老调用方式）
            opened = null;
            cat.Invoke("search:bd", null!);
            Check("null 参数时真的搜索了", opened != null, opened ?? "(null)");

            // ④ 显式参数优先于选区
            opened = null;
            cat.Invoke("search:bd", "显式参数XYZ");
            Check("显式参数优先",
                opened != null && opened.Contains(Uri.EscapeDataString("显式参数XYZ"), StringComparison.Ordinal),
                opened ?? "(null)");

            // ⑤ 主动取词失败 → 保底「手动 Ctrl+C」拿到的内容也要能搜
            ctx.CaptureTextForTest = () => "";
            ctx.ManualCopyWaitForTest = _ => "手动复制的内容";
            opened = null;
            cat.Invoke("search:bd", "");
            Check("保底手动复制路径可用",
                opened != null && opened.Contains(Uri.EscapeDataString("手动复制的内容"), StringComparison.Ordinal),
                opened ?? "(null)");

            // ⑥ 引擎地址里没写 {q} 时不能把关键词丢掉
            var noPlaceholder = DefaultSettings.Create();
            noPlaceholder.Search.Add(new Core.Settings.QEntry
            {
                Key = "noplaceholder",
                Value = "https://example.com/search",
                Comment = "自定义",
            });
            var ctx2 = new Core.Actions.ActionContext
            {
                Settings = () => noPlaceholder,
                Clipboard = new Core.Actions.ClipboardService(),
                Windows = new Core.Actions.WindowActions(),
                WindowBindings = new Core.Actions.WindowBindingService(noPlaceholder),
                Mouse = new Core.Actions.MouseService(),
                Notify = _ => { },
                CaptureTextForTest = () => "关键词QQ",
            };
            opened = null;
            ctx2.SearchSelection("noplaceholder");
            Check("缺 {q} 时仍带上关键词",
                opened != null && opened.Contains(Uri.EscapeDataString("关键词QQ"), StringComparison.Ordinal),
                opened ?? "(null)");
        }
        finally
        {
            Core.Actions.ActionContext.OpenUrlInterceptForTest = null;
            ctx.CaptureTextForTest = null;
            ctx.ManualCopyWaitForTest = null;
        }
    }

    /// <summary>
    /// 「直接搜索」应覆盖 Qbar 面板里的**全部**搜索引擎，而不只是常用的几个。
    /// </summary>
    private static void TestDirectSearchCoversAllEngines()
    {
        var settings = DefaultSettings.Create();
        var ctx = new Core.Actions.ActionContext
        {
            Settings = () => settings,
            Clipboard = new Core.Actions.ClipboardService(),
            Windows = new Core.Actions.WindowActions(),
            WindowBindings = new Core.Actions.WindowBindingService(settings),
            Mouse = new Core.Actions.MouseService(),
            Notify = _ => { },
        };
        var cat = Core.Actions.ActionCatalog.Create(ctx);

        // 设置里有多少可用引擎，就应该有多少 search: 动作
        int engineCount = settings.Search.Count(x =>
            !x.Key.Equals("default", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(x.Value));

        int actionCount = cat.Items.Count(i => i.Category == "直接搜索");

        Check("直接搜索动作数 = 全部引擎数",
            actionCount == engineCount, $"{actionCount} vs {engineCount}");
        Check("引擎数量足够多（>50）", engineCount > 50, engineCount.ToString());

        // 抽查几个非常用引擎也在
        foreach (string key in new[] { "bd", "zh", "go" })
        {
            Check($"常用引擎 {key} 有动作", cat.Items.Any(i => i.Id == "search:" + key));
        }
    }

    private static void TestQbarEngineActions()
    {
        var settings = DefaultSettings.Create();
        string? requested = "(未请求)";
        var ctx = new Core.Actions.ActionContext
        {
            Settings = () => settings,
            Clipboard = new Core.Actions.ClipboardService(),
            Windows = new Core.Actions.WindowActions(),
            WindowBindings = new Core.Actions.WindowBindingService(settings),
            Mouse = new Core.Actions.MouseService(),
            Notify = _ => { },
        };
        ctx.QbarRequested += key => requested = key ?? "(null)";

        var cat = Core.Actions.ActionCatalog.Create(ctx);

        // 常见引擎都必须有动作
        foreach (string key in new[] { "bd", "zh", "bili", "go", "bi", "tb", "jd", "gh", "db", "wx" })
        {
            var item = cat.Items.FirstOrDefault(i => i.Id == "qbar:" + key);
            Check($"引擎 {key} 有可用动作", item != null,
                string.Join(",", cat.Items.Where(i => i.Id.StartsWith("qbar:")).Select(i => i.Id)));
            if (item == null) continue;
            Check($"引擎 {key} 的动作在「Qbar 搜索」分类", item.Category == "Qbar 搜索", item.Category);
        }

        // 不要塞成 300 个（否则功能下拉没法用）
        int engineActions = cat.Items.Count(i => i.Id.StartsWith("qbar:"));
        Check("引擎动作数量受控（不撑爆下拉）", engineActions <= 30, engineActions.ToString());
        Check("引擎动作数量足够用（>= 8）", engineActions >= 8, engineActions.ToString());

        // default 引擎不该成为动作
        Check("default 引擎没有动作", !cat.Items.Any(i => i.Id == "qbar:default"));

        // 执行动作必须把关键字传出来
        cat.Invoke("qbar:bd", "");
        CheckEqual("执行 qbar:bd 时传出引擎关键字", requested, "bd");

        cat.Invoke("qbar:zh", "");
        CheckEqual("执行 qbar:zh 时传出引擎关键字", requested, "zh");
    }
}