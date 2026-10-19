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
        "guide", "keys", "qbar", "bindings", "clipboard", "translate", "settings", "help", "about",
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

    /// <summary>诊断：dump 所有 TextBox 的真实布局尺寸，用于排查文字被裁切。</summary>
    public static void DumpTextBoxMetrics(System.Windows.Media.Visual root, string outPath, string page = "")
    {
        var lines = new List<string>();
        lines.Add($"TextBox 布局诊断（页面：{page}）");
        lines.Add($"DPI scale = {VisualTreeHelper.GetDpi(root).DpiScaleX}");
        lines.Add("");
        lines.Add($"{"名称",-14} {"控件高",7} {"内容区高",8} {"内边距",12} {"字号",6} {"文字高",7} {"是否裁切",9}");

        Walk(root, tb =>
        {
            var host = FindChild<System.Windows.Controls.ScrollViewer>(tb);
            double controlH = tb.ActualHeight;
            double hostH = host?.ActualHeight ?? 0;
            var padding = tb.Padding;
            double fontSize = tb.FontSize;

            // 用当前文本量一下字形高度
            var typeface = new Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight, tb.FontStretch);
            var ft = new FormattedText(
                string.IsNullOrEmpty(tb.Text) ? "Ag中" : tb.Text,
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, typeface, fontSize, Brushes.Black,
                VisualTreeHelper.GetDpi(tb).PixelsPerDip);
            double textH = ft.Height;

            bool clipped = hostH > 0 && textH > hostH + 0.5;
            string pad = $"{padding.Left:0},{padding.Top:0}";
            lines.Add($"{tb.Name,-14} {controlH,7:0.0} {hostH,8:0.0} {pad,12} " +
                      $"{fontSize,6:0.0} {textH,7:0.0} {(clipped ? "是 <--" : "否"),9}");
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
