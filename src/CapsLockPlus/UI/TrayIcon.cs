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
    private readonly ToolStripMenuItem _menuTitle;
    private readonly ToolStripMenuItem _enabledItem;
    private readonly ToolStripMenuItem _capsStateItem;
    private readonly ToolStripMenuItem _openItem;
    private readonly ToolStripMenuItem _qbarItem;
    private readonly ToolStripMenuItem _mathItem;
    private readonly ToolStripMenuItem _bindingsItem;
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
        _helpItem = new ToolStripMenuItem("", null, (_, _) => host.ShowMainWindow("help"));
        _reloadItem = new ToolStripMenuItem("", null, (_, _) => host.ReloadAll());
        _exitItem = new ToolStripMenuItem("", null, (_, _) => ExitApp());

        var context = new ContextMenuStrip
        {
            Renderer = new ToolStripProfessionalRenderer(new DarkColorTable()),
            ShowImageMargin = false,
            Font = new Font("Microsoft YaHei UI", 9f),
        };
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
            _helpItem,
            new ToolStripSeparator(),
            _reloadItem,
            _exitItem,
        });
        context.Opening += (_, _) => UpdateCapsState();

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
            _icon.Text = EnabledTooltip(_host.Engine.Enabled);
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
            _icon.Visible = false;
            _icon.Dispose();
            _customIcon?.Dispose();
        }
        catch { }   /* 预期内：探测/重试型，失败即视为无结果 */
    }

    private sealed class DarkColorTable : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Color.FromArgb(255, 252, 253, 255);
        public override Color ImageMarginGradientBegin => Color.FromArgb(255, 252, 253, 255);
        public override Color ImageMarginGradientMiddle => Color.FromArgb(255, 252, 253, 255);
        public override Color ImageMarginGradientEnd => Color.FromArgb(255, 252, 253, 255);
        public override Color MenuBorder => Color.FromArgb(255, 228, 232, 240);
        public override Color MenuItemBorder => Color.FromArgb(255, 232, 240, 255);
        public override Color MenuItemSelected => Color.FromArgb(255, 240, 243, 249);
        public override Color SeparatorDark => Color.FromArgb(255, 232, 236, 244);
        public override Color SeparatorLight => Color.FromArgb(255, 245, 247, 251);
    }
}
