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
/// 用法：CapsLockPlus.exe --render &lt;输出目录&gt;
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
        "clipboard", "translate", "backup", "settings", "help", "about",
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
        main.Show();
        Pump();

        foreach (var page in Pages)
        {
            main.Navigate(page);
            Pump(220);
            Save(main, Path.Combine(outDir, $"{page}.png"));
            // 诊断文件只在显式要求时输出（--render-diag <目录>），
            // 免得把一堆中间产物混进 docs/screenshots。
            if (DiagnosticsRequested)
            {
                try
                {
                    DumpTextBoxMetrics(main, Path.Combine(outDir, $"metrics-{page}.txt"), page);
                }
                catch (Exception ex)
                {
                    CoreLog.Warn($"输出 {page} 输入框诊断失败：" + ex.Message);
                }
            }
            count++;
        }

        main.Close();

        // Qbar 面板（深色浮层，单独渲染）
        var qbar = new QbarWindow(host) { ShowInTaskbar = false, Left = -32000, Top = -32000 };
        qbar.Open("bd 天气");
        Pump(300);
        qbar.Height = qbar.ActualHeight;
        qbar.UpdateLayout();
        Pump(120);
        Save(qbar, Path.Combine(outDir, "qbar-panel.png"));
        if (DiagnosticsRequested) DumpTextBoxMetrics(qbar, Path.Combine(outDir, "metrics-win-qbar-panel.txt"), "Qbar 面板");
        count++;

        // 标签形态：输入 "bd " 之后 bd 变成标签，输入框只剩搜索词
        qbar.OpenForRender("bd");
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
        Pump(260);
        qbar.Height = qbar.ActualHeight;
        qbar.UpdateLayout();
        Pump(120);
        Save(qbar, Path.Combine(outDir, "qbar-engine-prefill.png"));
        count++;

        // 标签 + 已输入搜索词
        qbar.OpenForRender("bd", "天气 预报");
        Pump(260);
        qbar.Height = qbar.ActualHeight;
        qbar.UpdateLayout();
        Pump(120);
        Save(qbar, Path.Combine(outDir, "qbar-chip-typing.png"));
        count++;

        // 程序标签（自动取 exe 图标）
        qbar.OpenForRender("calc");
        Pump(260);
        qbar.Height = qbar.ActualHeight;
        qbar.UpdateLayout();
        Pump(120);
        Save(qbar, Path.Combine(outDir, "qbar-chip-run.png"));
        count++;

        var math = new MathBoardWindow { ShowInTaskbar = false, Left = -32000, Top = -32000 };
        math.Show();
        Pump(200);
        Save(math, Path.Combine(outDir, "mathboard.png"));
        if (DiagnosticsRequested) DumpTextBoxMetrics(math, Path.Combine(outDir, "metrics-win-mathboard.txt"), "计算板");
        count++;
        math.Close();

        // 翻译窗口
        var tr = new TranslateWindow(host) { ShowInTaskbar = false, Left = -32000, Top = -32000 };
        tr.Show();
        Pump(240);
        Save(tr, Path.Combine(outDir, "translate.png"));
        if (DiagnosticsRequested) DumpTextBoxMetrics(tr, Path.Combine(outDir, "metrics-win-translate.txt"), "翻译窗口");
        count++;
        tr.Close();

        // 剪贴板历史
        var clip = new ClipboardHistoryWindow(host) { ShowInTaskbar = false, Left = -32000, Top = -32000 };
        clip.Show();
        Pump(240);
        Save(clip, Path.Combine(outDir, "clipboard.png"));
        if (DiagnosticsRequested) DumpTextBoxMetrics(clip, Path.Combine(outDir, "metrics-win-clipboard.txt"), "剪贴板历史");
        count++;
        clip.Close();

        // 十字准星点选器：展示“点选窗口”时的遮罩界面
        var picker = new WindowPickerWindow { ShowInTaskbar = false };
        picker.Show();
        Pump(260);
        Save(picker, Path.Combine(outDir, "window-picker.png"));
        count++;
        picker.Close();

        CoreLog.Info($"离屏渲染完成：{count} 张 → {outDir}");
        return 0;
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
