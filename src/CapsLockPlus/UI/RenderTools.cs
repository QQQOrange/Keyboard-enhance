using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CapsLockPlus.Core;
using CapsLockPlus.UI.Windows;

namespace CapsLockPlus.UI;

/// <summary>
/// 离屏渲染工具（诊断 / 文档用）。
///
/// 用法：KeyboardEnhance.exe --render &lt;输出目录&gt;
/// 会把每个页面和 Qbar 面板渲染成 PNG。
/// 相比截屏，它不受桌面遮挡、DPI 缩放和焦点动画的影响，结果稳定可复现，
/// 也方便以后做界面回归对比。
/// </summary>
public static class RenderTools
{
    /// <summary>是否输出布局诊断文件（由 --render-diag 打开，避免污染正常截图目录）。</summary>
    public static bool DiagnosticsRequested { get; set; }

    private static readonly string[] Pages =
    {
        // 「bindings」页已移除：窗口绑定并入按键映射，作为动作处理
        "guide", "keys", "layers", "macros", "qbar",
        "clipboard", "backup", "settings", "test", "help", "about",
    };

    public static int Run(AppHost host, string outDir)
    {
        Directory.CreateDirectory(outDir);
        int count = 0;

        var main = host.CreateMainWindow();
        main.WindowStartupLocation = WindowStartupLocation.Manual;
        main.Left = -32000;
        main.Top = -32000;
        main.ShowInTaskbar = false;

        /* 支持指定出图尺寸：CAPSLOCKPLUS_RENDER_SIZE=1240x800
         *
         * 默认尺寸只能验证"最大化好看"这一种情况。用户报的键盘被压扁
         * 恰恰只在**缩小窗口**时出现，所以出图必须能换尺寸，
         * 否则这类回归永远看不出来。 */
        string? size = Environment.GetEnvironmentVariable("CAPSLOCKPLUS_RENDER_SIZE");
        if (!string.IsNullOrWhiteSpace(size))
        {
            string[] parts = size.Split('x', 'X');
            if (parts.Length == 2
                && double.TryParse(parts[0], out double w) && w > 200
                && double.TryParse(parts[1], out double h) && h > 200)
            {
                main.Width = w;
                main.Height = h;
            }
            else
            {
                CoreLog.Warn("CAPSLOCKPLUS_RENDER_SIZE 格式不对（要 宽x高，如 1240x800）：" + size);
            }
        }

        main.Show();
        Pump();

        foreach (var page in Pages)
        {
            main.Navigate(page);
            Pump(220);
            Save(main, Path.Combine(outDir, $"{page}.png"));

            /* 额外截一张"窗口绑定面板"的样子。
             *
             * 这个面板只有选中「窗口绑定」动作时才出现，普通 keys.png 里看不到，
             * 所以单独渲染一张，用于确认它真的画出来了、布局没崩。 */
            if (page == "keys" &&
                main.GetPage("keys") is CapsLockPlus.UI.Views.KeysView kv)
            {
                /* 键盘档位先出图：这时页面的分类/选中键都还是初始状态，
                 * 图里不会残留上一个面板（比如「自定义按键」）的内容。
                 * 每张图都把窗口设成**该档位的最小尺寸**，所以看到的就是
                 * 用户打开这个页面时的第一眼。 */
                RenderKeyboardSizes(main, kv, outDir);

                try
                {
                    if (kv.ShowWinBindPanelForRender())
                    {
                        Pump(200);
                        Save(main, Path.Combine(outDir, "keys-winbind.png"));
                    }
                }
                catch (Exception ex)
                {
                    CoreLog.Warn("渲染窗口绑定面板失败：" + ex.Message);
                }
                kv.ResetRenderState();
                Pump(120);

                // 同理：直达链接面板（目标 + 自动识别 + 备注）也只有选那个分类才出现。
                // 一次性把三种识别结果都拍下来：网址 / 文件夹 / 文件。
                try
                {
                    if (kv.ShowWebPagePanelForRender())
                    {
                        /* 编辑区在键盘下方，800 高的窗口里它在折叠线以下。
                         * 不滚过去的话，这三张图拍到的全是键盘 —— 徽标和提示
                         * 一个像素都看不见，等于没验。 */
                        kv.ScrollToEditorForRender();
                        Pump(200);

                        /* 网址：填完立刻拍（不等 700ms 的标题抓取防抖），
                         * 免得截图里那行状态是"正在获取网页标题…"。 */
                        kv.FillLinkTargetForRender("https://github.com/QQQOrange/Keyboard-enhance");
                        Pump(300);
                        Save(main, Path.Combine(outDir, "keys-webpage.png"));

                        /* 文件夹 / 文件：用 C:\Windows 这类任何机器上都存在的路径，
                         * 徽标才会是"文件夹存在"而不是"路径不存在"，
                         * 截图不依赖谁在哪台机器上渲染。 */
                        kv.FillLinkTargetForRender(@"C:\Windows");
                        Pump(260);
                        Save(main, Path.Combine(outDir, "keys-link-folder.png"));

                        kv.FillLinkTargetForRender(@"C:\Windows\notepad.exe");
                        Pump(260);
                        Save(main, Path.Combine(outDir, "keys-link-file.png"));
                    }
                }
                catch (Exception ex)
                {
                    CoreLog.Warn("渲染直达网页面板失败：" + ex.Message);
                }
                kv.ResetRenderState();
                Pump(120);

                // 智能直达（系统 → 直达链接）也要能截到，确认它真在功能下拉里
                try
                {
                    if (kv.ShowSmartOpenForRender())
                    {
                        Pump(200);
                        Save(main, Path.Combine(outDir, "keys-smartopen.png"));
                    }
                }
                catch (Exception ex)
                {
                    CoreLog.Warn("渲染智能直达失败：" + ex.Message);
                }
                // 自定义按键面板：确认"键 / 应用 / 两个开关 / 提示"都画出来了
                try
                {
                    if (kv.ShowCustomKeyForRender())
                    {
                        Pump(200);
                        Save(main, Path.Combine(outDir, "keys-customkey.png"));

                        /* 编辑区在键盘**下方**，窗口只有 800 高时它在折叠线以下。
                         * 滚过去再拍一张，才能确认面板里的键 / 应用 / 开关
                         * 都真的画出来了，而不是被折叠线挡住、看起来"没做"。 */
                        kv.ScrollToEditorForRender();
                        Pump(220);
                        Save(main, Path.Combine(outDir, "keys-editor.png"));
                    }
                }
                catch (Exception ex)
                {
                    CoreLog.Warn("渲染自定义按键失败：" + ex.Message);
                }

                kv.ResetRenderState();
                Pump(120);

                /* 「恢复默认」的二次确认框。
                 *
                 * 这是整页唯一的破坏性按钮，必须能被截图回归：警告图标、
                 * 红色确定键、逐条写清"丢什么 / 留什么 / 能不能撤销"。
                 * 平时只有点按钮才会出现，而且 ShowDialog 是模态阻塞的 ——
                 * 这里用 CreateForRender 造实例、摆到屏幕外再截，绝不真弹出来。 */
                try
                {
                    var dlg = AppDialog.CreateForRender(main,
                        "把基础层的按键全部恢复成默认值？\n"
                        + "· 你在这一层改过的绑定会被覆盖（功能、参数、网址、文本、程序、备注）；\n"
                        + "· 你自己新增的组合键会保留，其它 9 层完全不动；\n"
                        + "· 这一步不能撤销。",
                        "恢复默认按键", DialogKind.Warn,
                        showCancel: true, okText: "恢复默认", cancelText: null);
                    dlg.WindowStartupLocation = WindowStartupLocation.Manual;
                    dlg.Left = -32000;
                    dlg.Top = -32000;
                    dlg.ShowInTaskbar = false;
                    dlg.Show();
                    Pump(220);
                    Save(dlg, Path.Combine(outDir, "keys-reset-confirm.png"));
                    dlg.Close();
                }
                catch (Exception ex)
                {
                    CoreLog.Warn("渲染恢复默认确认框失败：" + ex.Message);
                }
            }

            /* 宏页额外截一张"正在录制"的样子。
             *
             * 实时列表只在录制时出现，平时截不到 —— 而它恰恰是这次改动的重点，
             * 必须能被截图回归确认（按钮变红、状态点呼吸、按键一行行往下滚）。 */
            if (page == "macros" &&
                main.GetPage("macros") is CapsLockPlus.UI.Views.MacrosView mv)
            {
                try
                {
                    if (mv.ShowRecordingForRender())
                    {
                        Pump(200);
                        Save(main, Path.Combine(outDir, "macros-recording.png"));
                    }
                }
                catch (Exception ex)
                {
                    CoreLog.Warn("渲染录制状态失败：" + ex.Message);
                }
                mv.ResetRenderState();
                Pump(120);
            }

            /* 备份页额外截一张恢复对话框。
             *
             * 它是本次新增的交互（先拉清单、再选覆盖/合并），
             * 平时只有点「从备份恢复…」才出现，必须能被截图回归。 */
            if (page == "backup")
            {
                try
                {
                    var rw = new RestoreWindow(host)
                    {
                        WindowStartupLocation = WindowStartupLocation.Manual,
                        Left = -32000,
                        Top = -32000,
                        ShowInTaskbar = false,
                        /* 截图时去掉标题栏：带标题栏的窗口里，
                         * 内容相对窗口有一个"标题栏高度"的视觉偏移，
                         * 直接对内容做 RenderTargetBitmap 会整体下移、
                         * 右下角被切掉一截（截出来顶部还有一条黑边）。 */
                        WindowStyle = WindowStyle.None,
                        ResizeMode = ResizeMode.NoResize,
                    };
                    rw.Show();
                    Pump(320);
                    CoreLog.Info($"[渲染] 恢复对话框 Window={rw.Width}x{rw.Height} "
                                 + $"Actual={rw.ActualWidth}x{rw.ActualHeight} "
                                 + $"Content={((FrameworkElement)rw.Content).ActualWidth}"
                                 + $"x{((FrameworkElement)rw.Content).ActualHeight}");
                    Save(rw, Path.Combine(outDir, "restore.png"));
                    rw.Close();
                    Pump(80);
                }
                catch (Exception ex)
                {
                    CoreLog.Warn("渲染恢复对话框失败：" + ex.Message);
                }
            }

            /* 设置页的「外观」区：主题、键盘尺寸、界面语言都在这一区，
             * 而它默认正好在折叠线以下 —— 不滚过去截一张，
             * "语言下拉里到底显示了什么"就没人看得见
             * （这个下拉曾经渲染成 `LocLanguage { … }`，全靠这一张才发现）。 */
            if (page == "settings" &&
                main.GetPage("settings") is CapsLockPlus.UI.Views.SettingsView sv)
            {
                try
                {
                    sv.ScrollToAppearanceForRender();
                    Pump(240);
                    Save(main, Path.Combine(outDir, "settings-appearance.png"));
                }
                catch (Exception ex)
                {
                    CoreLog.Warn("渲染设置页外观区失败：" + ex.Message);
                }
                sv.ResetRenderStateForRender();
                Pump(120);
            }

            // 诊断文件只在显式要求时输出（--render-diag <目录>），
            // 免得把一堆中间产物混进 docs/screenshots。
            if (DiagnosticsRequested)
            {
                try
                {
                    DumpTextBoxMetrics(main, Path.Combine(outDir, $"metrics-{page}.txt"), page);
                    DumpUntranslatedAudit(main, Path.Combine(outDir, $"untranslated-{page}.txt"), page);
                }
                catch (Exception ex)
                {
                    CoreLog.Warn($"输出 {page} 输入框诊断失败：" + ex.Message);
                }
            }
            count++;
        }

        main.Close();

        /* 亮度横条。
         *
         * 只出亮度这一张：音量已经交给系统音量键，Windows 自己弹它的横条，
         * 我们不再画第二条（v4.3.0 删掉了自绘音量横条）。
         * 这张图是自绘浮层，"长什么样、出现在哪"必须能一眼看到。 */
        foreach (var (file, pct) in new[]
                 {
                     ("osd-brightness.png", 40),
                     ("osd-p00.png", 0),
                     ("osd-p100.png", 100),
                 })
        {
            var osd = OsdWindow.CreateDetached();
            osd.ShowInTaskbar = false;
            osd.WindowStartupLocation = WindowStartupLocation.Manual;
            osd.Left = -32000;
            osd.Top = -32000;
            osd.Show();
            osd.PrepareForRender(pct);
            Pump(160);
            osd.UpdateLayout();
            Save(osd, Path.Combine(outDir, file));
            osd.Close();
            count++;
        }

        // Qbar 面板（深色浮层，单独渲染）
        var qbar = new QbarWindow(host) { ShowInTaskbar = false, Left = -32000, Top = -32000 };
        qbar.Open("bd 天气");
        qbar.ParkOffScreenForRender();
        Pump(300);
        qbar.Height = qbar.ActualHeight;
        qbar.UpdateLayout();
        Pump(120);
        Save(qbar, Path.Combine(outDir, "qbar-panel.png"));
        if (DiagnosticsRequested)
        {
            DumpTextBoxMetrics(qbar, Path.Combine(outDir, "metrics-win-qbar-panel.txt"), "Qbar 面板");
            DumpUntranslatedAudit(qbar, Path.Combine(outDir, "untranslated-win-qbar-panel.txt"), "Qbar 面板");
        }
        count++;

        // 标签形态：输入 "bd " 之后 bd 变成标签，输入框只剩搜索词
        qbar.OpenForRender("bd");
        qbar.ParkOffScreenForRender();
        Pump(260);
        qbar.Height = qbar.ActualHeight;
        qbar.UpdateLayout();
        Pump(120);
        Save(qbar, Path.Combine(outDir, "qbar-chip.png"));
        count++;

        /* 引擎预填直达：走**生产路径** Open("zh ")，
         * 验证 qbar:<key> 动作绑到按键后，弹出的面板真的已经带上引擎标签
         * （而不是只把 "zh " 当普通文字塞进输入框）。 */
        qbar.Open("zh ");
        qbar.ParkOffScreenForRender();
        Pump(260);
        qbar.Height = qbar.ActualHeight;
        qbar.UpdateLayout();
        Pump(120);
        Save(qbar, Path.Combine(outDir, "qbar-engine-prefill.png"));
        count++;

        // 标签 + 已输入搜索词
        qbar.OpenForRender("bd", "天气 预报");
        qbar.ParkOffScreenForRender();
        Pump(260);
        qbar.Height = qbar.ActualHeight;
        qbar.UpdateLayout();
        Pump(120);
        Save(qbar, Path.Combine(outDir, "qbar-chip-typing.png"));
        count++;

        // 程序标签（自动取 exe 图标）
        qbar.OpenForRender("calc");
        qbar.ParkOffScreenForRender();
        Pump(260);
        qbar.Height = qbar.ActualHeight;
        qbar.UpdateLayout();
        Pump(120);
        Save(qbar, Path.Combine(outDir, "qbar-chip-run.png"));
        count++;

        /* 这两个窗口的 XAML 写的是 WindowStartupLocation=CenterScreen，
         * 它会**盖过**手动设的 Left/Top —— 不显式改成 Manual 的话，
         * 后台渲染时它们会正正地弹在用户屏幕中央。 */
        var math = new MathBoardWindow
        {
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000,
        };
        math.Show();
        Pump(200);
        Save(math, Path.Combine(outDir, "mathboard.png"));
        if (DiagnosticsRequested)
        {
            DumpTextBoxMetrics(math, Path.Combine(outDir, "metrics-win-mathboard.txt"), "计算板");
            DumpUntranslatedAudit(math, Path.Combine(outDir, "untranslated-win-mathboard.txt"), "计算板");
        }
        count++;
        math.Close();

        // 剪贴板历史
        // 剪贴板历史（同样有 CenterScreen，必须改 Manual 才不会弹在屏幕中央）
        var clip = new ClipboardHistoryWindow(host)
        {
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000,
        };
        clip.Show();
        Pump(240);
        Save(clip, Path.Combine(outDir, "clipboard.png"));
        if (DiagnosticsRequested)
        {
            DumpTextBoxMetrics(clip, Path.Combine(outDir, "metrics-win-clipboard.txt"), "剪贴板历史");
            DumpUntranslatedAudit(clip, Path.Combine(outDir, "untranslated-win-clipboard.txt"), "剪贴板历史");
        }
        count++;
        clip.Close();

        // 十字准星点选器：展示“点选窗口”时的遮罩界面。
        // ⚠ 这个窗口是 Topmost + 铺满虚拟屏的，正常显示会盖住用户整个桌面；
        //   出图必须走 renderOffScreen，只截内容、不打扰用户。
        var picker = new WindowPickerWindow(renderOffScreen: true) { ShowInTaskbar = false };
        picker.Show();
        Pump(260);
        Save(picker, Path.Combine(outDir, "window-picker.png"));
        count++;
        picker.Close();

        CoreLog.Info($"离屏渲染完成：{count} 张 → {outDir}");
        return 0;
    }

    /// <summary>
    /// 「英文界面中文残留」审计：把视觉树里**当前真正显示出来**的文案全抓出来，
    /// 挑出还含汉字的，连同它来自哪个控件一起写成清单。
    ///
    /// 为什么要这个：语言包齐不齐自检能查（键都在、占位符都对），
    /// 但"某处文案到底有没有走翻译出口"自检查不出来 —— 只有把界面搭起来、
    /// 把每个 TextBlock / Content / ToolTip 的实际文本抓一遍才看得见。
    /// 逐张看截图也行，但 30 多张图靠眼睛扫一定会漏。
    /// </summary>
    public static void DumpUntranslatedAudit(DependencyObject root, string outPath, string page = "")
    {
        var hits = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        WalkAll(root, node =>
        {
            /* 顺序有讲究：HeaderedContentControl 本身就是 ContentControl，
             * 先匹配 ContentControl 的话 Header 永远取不到。
             * 占位文字不用单独取 —— 它是模板里的 TextBlock，走 TextBlock 那条就抓到了。 */
            string? text = node switch
            {
                System.Windows.Controls.TextBlock tb => tb.Text,
                System.Windows.Controls.TextBox box => box.Text,
                System.Windows.Controls.HeaderedContentControl hc => (hc.Content as string) ?? (hc.Header as string),
                System.Windows.Controls.HeaderedItemsControl hi => hi.Header as string,   // MenuItem / TabItem
                System.Windows.Controls.ContentControl cc => cc.Content as string,
                _ => null,
            };
            Collect(text, node);

            if (node is FrameworkElement fe) Collect(fe.ToolTip as string, node);
            if (node is Window win) Collect(win.Title, node);
        });

        WriteAudit(page);

        void Collect(string? s, DependencyObject node)
        {
            if (string.IsNullOrWhiteSpace(s) || !HasCjk(s)) return;
            string key = s!;
            if (!seen.Add(key)) return;   // 同一句只报一次，避免长列表刷屏
            hits.Add($"{node.GetType().Name,-22} {(node as FrameworkElement)?.Name ?? "",-20} {key.Replace("\n", " ⏎ ")}");
        }

        void WriteAudit(string pg)
        {
            var all = new List<string> { $"未翻译文案审计（页面：{pg}，语言：{Core.Localization.Loc.Language}）" };
            all.Add(hits.Count == 0 ? "  没有发现中文残留 ✓" : $"  发现 {hits.Count} 条：");
            all.AddRange(hits.Select(r => "  " + r));
            File.WriteAllLines(outPath, all);
        }
    }

    private static readonly System.Text.RegularExpressions.Regex CjkText =
        new(@"[\u4e00-\u9fff]", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static bool HasCjk(string s) => CjkText.IsMatch(s);

    private static void WalkAll(DependencyObject node, Action<DependencyObject> visit)
    {
        visit(node);
        int n = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < n; i++) WalkAll(VisualTreeHelper.GetChild(node, i), visit);
    }

    /// <summary>
    /// 诊断：dump 每个 TextBox 的真实布局，包括文字实际画在哪里。
    ///
    /// 只比较"控件高度"和"字号"不够——真正决定会不会被裁的，
    /// 是文字块相对内容区的位置。这里直接算出来，才能判断是顶到上边还是超出下边。
    /// </summary>
    public static void DumpTextBoxMetrics(System.Windows.Media.Visual root, string outPath, string page = "")
    {
        var lines = new List<string>();
        lines.Add($"TextBox 布局诊断（页面：{page}）");
        lines.Add($"DPI scale = {VisualTreeHelper.GetDpi(root).DpiScaleX}");
        lines.Add("");
        lines.Add("名称            控件高  内容顶  内容底  文字底  文字高   上余   下余  判定");

        Walk(root, tb =>
        {
            if (tb.ActualHeight <= 0) return;

            double contentTop = tb.BorderThickness.Top + tb.Padding.Top;
            double contentBottom = tb.ActualHeight - tb.BorderThickness.Bottom - tb.Padding.Bottom;

            var typeface = new Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight, tb.FontStretch);
            var ft = new FormattedText(
                string.IsNullOrEmpty(tb.Text) ? "Ag中qg" : tb.Text,
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, typeface, tb.FontSize, Brushes.Black,
                VisualTreeHelper.GetDpi(tb).PixelsPerDip);
            double textH = ft.Height;

            // 内容区垂直居中时文字块的位置
            double avail = contentBottom - contentTop;
            double textTop = contentTop + Math.Max(0, (avail - textH) / 2);
            double textBottom = textTop + textH;
            double topGap = textTop - contentTop;
            double bottomGap = contentBottom - textBottom;

            bool clipped = textBottom > contentBottom + 0.5 || textH > avail + 0.5;
            string verdict = clipped ? "裁切 <--" : (bottomGap < 1.5 ? "贴边" : "正常");

            lines.Add($"{tb.Name,-14} {tb.ActualHeight,6:0.0} {contentTop,7:0.0} {contentBottom,7:0.0} " +
                      $"{textBottom,7:0.0} {textH,7:0.0} {topGap,6:0.0} {bottomGap,6:0.0}  {verdict}");
        });

        File.WriteAllLines(outPath, lines);
    }

    private static void Walk(DependencyObject node, Action<System.Windows.Controls.TextBox> visit)
    {
        if (node is System.Windows.Controls.TextBox tb) visit(tb);
        int n = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < n; i++) Walk(VisualTreeHelper.GetChild(node, i), visit);
    }

    private static T? FindChild<T>(DependencyObject node) where T : DependencyObject
    {
        int n = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (child is T t) return t;
            var deeper = FindChild<T>(child);
            if (deeper != null) return deeper;
        }
        return null;
    }

    /// <summary>
    /// 按四个键盘档位各出一张图，外加一张"高窗口"图。
    ///
    /// 每张图都把窗口设成**该档位的最小尺寸**，这样看到的就是用户打开这个页面
    /// 时的第一眼：键盘刚好铺满，没有多余横向空白。最后那张高窗口图用来看
    /// "键盘下方原本空着的地方现在装了什么"。
    /// </summary>
    private static void RenderKeyboardSizes(MainWindow main, CapsLockPlus.UI.Views.KeysView kv, string outDir)
    {
        double savedW = main.Width, savedH = main.Height;
        double savedMinW = main.MinWidth, savedMinH = main.MinHeight;

        foreach (var preset in CapsLockPlus.Core.Settings.KeyboardSizePreset.All)
        {
            kv.SetSizePresetForRender(preset.Id);
            var (w, h) = kv.RequiredWindowSize();

            /* 先解除最小限制再设尺寸：MinWidth 已经按档位抬上去了，
             * 直接设更小的 Width 会被它顶回来。 */
            main.MinWidth = 0;
            main.MinHeight = 0;
            main.Width = w;
            main.Height = h;
            Pump(300);

            Save(main, Path.Combine(outDir, $"keys-size-{preset.Id}.png"));
        }

        /* 整页：键盘大小**不变**，多出来的高度全部给下方编辑区。
         *
         * 尺寸取「大」档的最小宽度 + 刚好装下编辑卡片的高度（860）：
         * 编辑区压缩成多列之后内容只要 800 出头，再高就是一片空白，
         * 图放到 README 里不好看；再矮编辑卡会被折掉。
         * 这张用来看"编辑区在键盘正下方"之后的整页观感。 */
        kv.SetSizePresetForRender("large");
        main.Width = kv.RequiredWindowSize().Width;
        main.Height = 860;
        Pump(300);
        Save(main, Path.Combine(outDir, "keys-full.png"));

        kv.ClearSizePresetForRender();
        main.MinWidth = savedMinW;
        main.MinHeight = savedMinH;
        main.Width = savedW;
        main.Height = savedH;
        Pump(200);
    }

    private static void Pump(int ms = 120)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.ContextIdle)
        {
            Interval = TimeSpan.FromMilliseconds(ms),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void Save(Window window, string path)
    {
        window.UpdateLayout();
        var root = (FrameworkElement)window.Content;
        double dpiScale = VisualTreeHelper.GetDpi(root).DpiScaleX;
        double dpi = 96 * dpiScale;
        double scale = dpi / 96.0;

        double logicalW = root.ActualWidth;
        double logicalH = root.ActualHeight;

        // 只有“随内容自动定高”的窗口（如 Qbar）才需要按内容的期望高度补足，
        // 主窗口是固定尺寸，不能这么做，否则会把滚动区的内容全展开成一张长图。
        if (window.SizeToContent != SizeToContent.Manual)
        {
            root.Measure(new Size(logicalW, double.PositiveInfinity));
            double desiredH = root.DesiredSize.Height;
            if (desiredH > logicalH + 1)
            {
                window.Height = window.ActualHeight + (desiredH - logicalH);
                window.UpdateLayout();
                Pump(120);
                root.Measure(new Size(logicalW, double.PositiveInfinity));
                desiredH = root.DesiredSize.Height;
            }
            logicalH = Math.Max(logicalH, desiredH);
        }

        int w = (int)Math.Ceiling(logicalW * scale);
        int ht = (int)Math.Ceiling(logicalH * scale);
        if (w <= 0 || ht <= 0) return;

        var bmp = new RenderTargetBitmap(w, ht, dpi, dpi, PixelFormats.Pbgra32);
        bmp.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }
}
