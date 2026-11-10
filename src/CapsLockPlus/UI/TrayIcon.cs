using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using CapsLockPlus.Core;
using CapsLockPlus.Core.Input;
using CapsLockPlus.Core.Localization;
using CapsLockPlus.Core.Settings;
using CapsLockPlus.UI.Theme;
using Application = System.Windows.Application;
using MenuItem = System.Windows.Forms.ToolStripMenuItem;

namespace CapsLockPlus.UI;

/// <summary>系统托盘图标与右键菜单（用 WinForms NotifyIcon，稳定且支持 Win11 通知区域）。</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly AppHost _host;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _menuTitle;
    private readonly ToolStripMenuItem _enabledItem;
    private readonly ToolStripMenuItem _capsStateItem;
    private readonly ToolStripMenuItem _openItem;
    private readonly ToolStripMenuItem _qbarItem;
    private readonly ToolStripMenuItem _mathItem;
    private readonly ToolStripMenuItem _bindingsItem;
    private readonly ToolStripMenuItem _aliasesItem;
    private readonly ToolStripMenuItem _helpItem;
    private readonly ToolStripMenuItem _reloadItem;
    private readonly ToolStripMenuItem _exitItem;
    private Icon? _customIcon;

    /// <summary>
    /// 双击托盘图标落到哪一页。
    ///
    /// 以前双击只是 `ShowMainWindow()` —— 打开的是**上次停留的页面**，
    /// 用户从托盘里点进来多半是想改设置，结果停在别的页还得自己找。
    /// 现在固定进「设置」。
    /// </summary>
    internal const string DoubleClickPage = "settings";

    public TrayIcon(AppHost host)
    {
        _host = host;
        _customIcon = LoadIcon();

        /* 菜单项一律先建空壳、文字统一交给 ApplyText() 填。
         * 之前每一项都在构造时把中文写死，切语言时没法回头改 ——
         * 现在文本只在一个地方出现，Relocalize() 直接再调一次就行。 */
        _menuTitle = new ToolStripMenuItem("") { Enabled = false };
        _enabledItem = new ToolStripMenuItem("", null, (_, _) => host.ToggleEnabled()) { CheckOnClick = false };
        _capsStateItem = new ToolStripMenuItem("", null, (_, _) => ToggleCaps()) { Enabled = false };

        /* 菜单项写着"打开设置…"，就必须真的落到设置页。
         * 之前这里调的是无参 ShowMainWindow()，打开的是上次停留的页面 ——
         * 名字和行为对不上，用户会以为程序记错了位置。
         * 双击和菜单现在指向同一个常量，不会各改各的。 */
        _openItem = new ToolStripMenuItem("", null, (_, _) => host.ShowMainWindow(DoubleClickPage));
        _qbarItem = new ToolStripMenuItem("", null, (_, _) => host.ShowQbar());
        _mathItem = new ToolStripMenuItem("", null, (_, _) => host.ShowMathBoard());
        _bindingsItem = new ToolStripMenuItem("", null, (_, _) => host.ShowMainWindow("keys"));
        _aliasesItem = new ToolStripMenuItem("", null, (_, _) => host.ShowMainWindow("hotstrings"));
        _helpItem = new ToolStripMenuItem("", null, (_, _) => host.ShowMainWindow("help"));
        _reloadItem = new ToolStripMenuItem("", null, (_, _) => host.ReloadAll());
        _exitItem = new ToolStripMenuItem("", null, (_, _) => ExitApp());

        var context = new ContextMenuStrip
        {
            ShowImageMargin = false,
            Font = new Font("Microsoft YaHei UI", 9f),
        };
        _menu = context;
        context.Items.AddRange(new ToolStripItem[]
        {
            _menuTitle,
            new ToolStripSeparator(),
            _enabledItem,
            _capsStateItem,
            new ToolStripSeparator(),
            _openItem,
            _qbarItem,
            _mathItem,
            _bindingsItem,
            _aliasesItem,
            _helpItem,
            new ToolStripSeparator(),
            _reloadItem,
            _exitItem,
        });
        context.Opening += (_, _) => UpdateCapsState();

        /* 菜单配色必须跟着主题走。
         * 一直是硬编码的浅色表：深色主题下弹出菜单是"一片亮白"，
         * 和主界面像是两个程序。现在从主题资源里取色，主题变了就重刷。 */
        ApplyMenuTheme(CurrentIsDark);
        Theme.ThemeManager.ThemeChanged += OnThemeChanged;

        _icon = new NotifyIcon
        {
            Icon = _customIcon ?? SystemIcons.Application,
            Text = EnabledTooltip(true),
            Visible = true,
            ContextMenuStrip = context,
        };

        ApplyText();

        /* 双击 = 直接进「设置」页。
         * 单击不做事：托盘图标单击很容易误触，真要点什么走右键菜单。 */
        _icon.DoubleClick += (_, _) => host.ShowMainWindow(DoubleClickPage);
        _icon.BalloonTipClicked += (_, _) => host.ShowMainWindow();

        /* 启动气泡的正文要跟着**实际配置**走。
         *
         * 之前写死"按住 CapsLock 试试 A/S/D/F，或按 Caps+Q 打开搜索面板"——
         * 一是还在用旧名 CapsLock，二是用户可能已经把触发键改成 F13、
         * 把打开面板的键从 Q 换掉了，那这句提示就是错的。 */
        _icon.ShowBalloonTip(1800, AppInfo.ShortName + Loc.T(" 已启动"), StartupTip(host.Settings),
            ToolTipIcon.Info);
    }

    /// <summary>
    /// 把菜单文字按**当前语言**重填一遍。切换语言后由
    /// <see cref="AppHost.NotifyLocalizedSurfaces"/> 调用。
    ///
    /// 注：菜单上的快捷键提示（"Caps+Q"）是**用户自己的绑定**，不参与翻译 ——
    /// 只有自然语言部分走 <see cref="Loc.T"/>。
    /// </summary>
    public void Relocalize()
    {
        try
        {
            ApplyText();
            UpdateCapsState();
            _icon.Text = TrayTooltip();
            _icon.Icon = _customIcon ?? SystemIcons.Application;
        }
        catch (Exception ex)
        {
            CoreLog.Warn("刷新托盘菜单文字失败：" + ex.Message);
        }
    }

    /// <summary>菜单文字的**唯一**出处。改文案只改这里，别再散回构造函数。</summary>
    private void ApplyText()
    {
        _menuTitle.Text = AppInfo.ProductName;

        string qKey = Loc.T("Q");
        _openItem.Text = Loc.T("打开设置…");
        _qbarItem.Text = Loc.F("打开 Qbar  (Caps+{0})", qKey);
        _mathItem.Text = Loc.T("计算器  (Caps+F2)");
        _bindingsItem.Text = Loc.T("窗口绑定（按键映射页）");
        _aliasesItem.Text = Loc.T("别名替换");
        _helpItem.Text = Loc.T("使用帮助");
        _reloadItem.Text = Loc.T("重新加载配置  (Caps+F5)");
        _exitItem.Text = Loc.T("退出");

        // 这两项的文字还取决于引擎/大写状态，交给 UpdateCapsState 统一算
        UpdateCapsState();
    }

    /// <summary>
    /// 根据当前配置生成一句**真实**的启动提示。
    ///
    /// 之前的实现里写死了"试试 A / S / D / F" —— 默认方案确实是这四个键，
    /// 但用户改过绑定后这句就是错的，而提示是最不该说错话的地方。
    /// 现在示例键从第 1 层的**实际绑定**里取（最多四个、只取无修饰键的），
    /// 一个都没绑时退回"按住即可使用层内绑定"，不再猜。
    ///
    /// 抽成只依赖 <see cref="AppSettings"/> 的静态方法，自检可以直接构造配置验。
    /// </summary>
    internal static string StartupTip(AppSettings s)
    {
        try
        {
            var trigger = s.Triggers
                .Where(t => t.Enabled)
                .OrderBy(t => t.LayerId)
                .FirstOrDefault();

            string keyName = trigger != null ? KeyName.Display(trigger.Key) : Loc.T("触发键");

            var keys = s.Layers.FirstOrDefault(l => l.Id == 1)?.Keys ?? new List<KeyBinding>();

            /* 示例键：取前四个"真的绑了东西"的单键。
             * 带修饰键的组合读起来太长（"Ctrl+K"塞进气泡会换行），跳过。 */
            var samples = new List<string>();
            foreach (var k in keys)
            {
                if (!k.IsBound) continue;
                if (KeyName.ParseCombo(k.Combo) is not { } parsed) continue;
                if (parsed.Mods != KeyModifiers.None) continue;

                string label = KeyName.Display(parsed.Key);
                if (!samples.Contains(label, StringComparer.OrdinalIgnoreCase))
                    samples.Add(label);
                if (samples.Count >= 4) break;
            }

            // "打开搜索面板"这个示例同样按真实绑定取
            string example = "";
            var qbarBinding = keys.FirstOrDefault(k => k.Action == "qbar" && k.IsBound);
            if (qbarBinding != null
                && KeyName.ParseCombo(qbarBinding.Combo) is { } qp
                && qp.Mods == KeyModifiers.None)
            {
                example = Loc.F("，或按 {0}+{1} 打开搜索面板", keyName, KeyName.Display(qp.Key));
            }

            if (samples.Count == 0)
                return Loc.F("按住 {0} 即可使用层内绑定{1}。", keyName, example);

            return Loc.F("按住 {0} 试试 {1}{2}。", keyName, string.Join(" / ", samples), example);
        }
        catch
        {
            return Loc.T("按住触发键即可使用全部功能。");
        }
    }

    private static void ToggleCaps()
    {
        Core.Input.HotkeyEngine.ToggleCapsLock();
    }

    private void UpdateCapsState()
    {
        _enabledItem.Text = _host.Engine.Enabled
            ? Loc.F("暂停 {0}", AppInfo.ShortName)
            : Loc.F("恢复 {0}", AppInfo.ShortName);
        _capsStateItem.Text = Core.Input.HotkeyEngine.CapsLockOn ? Loc.T("大写锁定：开") : Loc.T("大写锁定：关");
        _icon.Icon = _customIcon ?? SystemIcons.Application;
    }

    public void SetEnabled(bool enabled)
    {
        try
        {
            _enabledItem.Text = enabled
                ? Loc.F("暂停 {0}", AppInfo.ShortName)
                : Loc.F("恢复 {0}", AppInfo.ShortName);
            _icon.Text = EnabledTooltip(enabled);
        }
        catch { }   /* 预期内：探测/重试型，失败即视为无结果 */
    }

    /// <summary>
    /// 托盘提示：除了"运行中"，还要写出**当前生效的改键**。
    ///
    /// 用户开了「Ctrl ↔ Alt（左）」之后，Ctrl+C 会变成 Alt+C —— 这是预设语义，
    /// 但界面上没有任何地方告诉他"现在 Ctrl 是 Alt"，只能凭"复制不好使"猜。
    /// 悬停托盘就能看到，至少不用再猜。
    /// </summary>
    private string TrayTooltip()
    {
        string text = EnabledTooltip(_host.Engine.Enabled);

        try
        {
            var remaps = _host.Settings.Global.BaseRemaps;
            if (remaps.Count > 0)
            {
                string pairs = string.Join("、", remaps.Take(2).Select(r => $"{KeyNameOf(r.From)}→{KeyNameOf(r.To)}"));
                if (remaps.Count > 2) pairs += "…";
                text += Environment.NewLine + Loc.F("改键生效：{0}", pairs);
            }
        }
        catch { }

        /* NotifyIcon.Text 有长度上限（127），超了会直接抛。 */
        return text.Length <= 120 ? text : text[..119] + "…";
    }

    private static string KeyNameOf(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return "?";
        return key!.Trim().ToLowerInvariant() switch
        {
            "capslock" => "Caps",
            "lctrl" or "ctrl" => "Ctrl",
            "rctrl" => "RCtrl",
            "lalt" or "alt" => "Alt",
            "ralt" => "RAlt",
            "lshift" or "shift" => "Shift",
            "rshift" => "RShift",
            "lwin" or "win" => "Win",
            "rwin" => "RWin",
            _ => key!,
        };
    }

    /// <summary>
    /// 托盘提示文字。必须带上"双击打开设置" —— 托盘图标没有任何可见提示，
    /// 不写出来没人知道能双击。
    /// </summary>
    internal static string EnabledTooltip(bool enabled)
        => enabled
            ? Loc.F("{0}（双击打开设置）", AppInfo.ProductName)
            : Loc.F("{0} 已暂停（双击打开设置）", AppInfo.ShortName);

    private static Icon? LoadIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Assets/app.ico", UriKind.Absolute);
            using var stream = System.Windows.Application.GetResourceStream(uri)?.Stream;
            return stream == null ? null : new Icon(stream);
        }
        catch (Exception ex)
        {
            CoreLog.Warn("加载托盘图标失败：" + ex.Message);
            return null;
        }
    }

    private void ExitApp()
    {
        _icon.Visible = false;
        _host.ExitApp();
    }

    public void Dispose()
    {
        try
        {
            Theme.ThemeManager.ThemeChanged -= OnThemeChanged;
            _icon.Visible = false;
            _icon.Dispose();
            _customIcon?.Dispose();
        }
        catch { }   /* 预期内：探测/重试型，失败即视为无结果 */
    }

    // ---------------- 菜单主题 ----------------

    /// <summary>当前主题是否深色（跟随系统时按系统设置判）。</summary>
    private static bool CurrentIsDark =>
        Theme.ThemeManager.Current == Theme.ThemeManager.Mode.Dark
        || (Theme.ThemeManager.Current == Theme.ThemeManager.Mode.System && Theme.ThemeManager.IsDarkSystem);

    private void OnThemeChanged(bool dark)
    {
        try
        {
            var app = Application.Current;
            if (app == null) { ApplyMenuTheme(dark); return; }
            app.Dispatcher.BeginInvoke(() => ApplyMenuTheme(dark));
        }
        catch (Exception ex) { CoreLog.Warn("刷新托盘菜单主题失败：" + ex.Message); }
    }

    /// <summary>
    /// 用主题资源给右键菜单上色。
    ///
    /// 以前这里是一张写死的浅色调色板（名字还叫 DarkColorTable）——
    /// 深色主题下弹出的菜单是一片亮白，和旁边的主窗口完全不是一套。
    /// 现在颜色全部从 Brush.* 里取，取不到才退回内置默认值。
    /// </summary>
    private void ApplyMenuTheme(bool dark)
    {
        try
        {
            var p = MenuPalette.Create(dark);
            _menu.BackColor = p.Background;
            _menu.ForeColor = p.Text;
            _menu.Renderer = new ThemedRenderer(p);

            foreach (ToolStripItem item in _menu.Items)
                item.ForeColor = item.Enabled ? p.Text : p.Muted;

            _menu.Invalidate();
        }
        catch (Exception ex)
        {
            CoreLog.Warn("应用托盘菜单配色失败：" + ex.Message);
        }
    }

    private static Color BrushColor(string key, Color fallback)
    {
        try
        {
            if (System.Windows.Application.Current?.TryFindResource(key) is System.Windows.Media.SolidColorBrush b)
            {
                var c = b.Color;
                return Color.FromArgb(c.A, c.R, c.G, c.B);
            }
        }
        catch { }
        return fallback;
    }

    /// <summary>自检用：取托盘菜单在某主题下的底色，确认深色确实比浅色暗。</summary>
    internal static Color MenuBackgroundForTest(bool dark) => MenuPalette.Create(dark).Background;

    /// <summary>自检用：取托盘菜单在某主题下的文字色。</summary>
    internal static Color MenuTextForTest(bool dark) => MenuPalette.Create(dark).Text;

    private sealed class MenuPalette
    {
        public Color Background;
        public Color Border;
        public Color Selected;
        public Color Text;
        public Color Muted;

        public static MenuPalette Create(bool dark)
        {
            /* 只有"请求的主题 == 当前已装载的主题"时，资源字典里的颜色才代表这个主题；
             * 否则（例如自检里刚切过主题再回头问另一个主题的配色）会读到错的字典。
             * 这种时候用内置默认值，两份默认值本身就分浅/深。 */
            bool live = CurrentIsDark == dark;
            Color C(string key, Color fallback) => live ? BrushColor(key, fallback) : fallback;

            if (dark)
            {
                return new MenuPalette
                {
                    Background = C("Brush.Surface", Color.FromArgb(30, 36, 48)),
                    Border = C("Brush.Border", Color.FromArgb(47, 55, 70)),
                    Selected = C("Brush.Selected", Color.FromArgb(42, 51, 65)),
                    Text = C("Brush.TextPrimary", Color.FromArgb(232, 237, 246)),
                    Muted = C("Brush.TextTertiary", Color.FromArgb(154, 167, 188)),
                };
            }

            return new MenuPalette
            {
                Background = C("Brush.Surface", Color.FromArgb(255, 255, 255)),
                Border = C("Brush.Border", Color.FromArgb(227, 231, 239)),
                Selected = C("Brush.Selected", Color.FromArgb(239, 243, 251)),
                Text = C("Brush.TextPrimary", Color.FromArgb(27, 34, 48)),
                Muted = C("Brush.TextTertiary", Color.FromArgb(122, 133, 152)),
            };
        }
    }

    private sealed class ThemedColorTable : ProfessionalColorTable
    {
        private readonly MenuPalette _p;
        public ThemedColorTable(MenuPalette p) => _p = p;

        public override Color ToolStripDropDownBackground => _p.Background;
        public override Color ImageMarginGradientBegin => _p.Background;
        public override Color ImageMarginGradientMiddle => _p.Background;
        public override Color ImageMarginGradientEnd => _p.Background;
        public override Color MenuBorder => _p.Border;
        public override Color MenuItemBorder => _p.Selected;
        public override Color MenuItemSelected => _p.Selected;
        public override Color MenuItemSelectedGradientBegin => _p.Selected;
        public override Color MenuItemSelectedGradientEnd => _p.Selected;
        public override Color MenuItemPressedGradientBegin => _p.Selected;
        public override Color MenuItemPressedGradientEnd => _p.Selected;
        public override Color SeparatorDark => _p.Border;
        public override Color SeparatorLight => _p.Background;
        public override Color ToolStripBorder => _p.Border;
    }

    private sealed class ThemedRenderer : ToolStripProfessionalRenderer
    {
        private readonly MenuPalette _p;
        public ThemedRenderer(MenuPalette p) : base(new ThemedColorTable(p))
        {
            _p = p;
            RoundedEdges = false;
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            if (e.Item is { Enabled: false }) e.TextColor = _p.Muted;
            else if (!e.Item.Selected) e.TextColor = _p.Text;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using var pen = new Pen(_p.Border);
            var r = e.ToolStrip.ClientRectangle;
            r.Width -= 1;
            r.Height -= 1;
            e.Graphics.DrawRectangle(pen, r);
        }
    }
}