using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using CapsLockPlus.Core;
using CapsLockPlus.Core.Input;
using CapsLockPlus.UI.Theme;
using Application = System.Windows.Application;
using MenuItem = System.Windows.Forms.ToolStripMenuItem;

namespace CapsLockPlus.UI;

/// <summary>系统托盘图标与右键菜单（用 WinForms NotifyIcon，稳定且支持 Win11 通知区域）。</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly AppHost _host;
    private readonly ToolStripMenuItem _enabledItem;
    private readonly ToolStripMenuItem _capsStateItem;
    private Icon? _customIcon;

    public TrayIcon(AppHost host)
    {
        _host = host;
        _customIcon = LoadIcon();

        var menu = new ToolStripMenuItem(AppInfo.ProductName) { Enabled = false };

        _enabledItem = new ToolStripMenuItem("暂停 " + AppInfo.ShortName, null, (_, _) => host.ToggleEnabled())
        {
            CheckOnClick = false,
        };
        _capsStateItem = new ToolStripMenuItem("", null, (_, _) => ToggleCaps()) { Enabled = false };

        var open = new ToolStripMenuItem("打开设置…", null, (_, _) => host.ShowMainWindow());
        var qbar = new ToolStripMenuItem("打开 Qbar  (Caps+Q)", null, (_, _) => host.ShowQbar());
        var math = new ToolStripMenuItem("计算器  (Caps+F2)", null, (_, _) => host.ShowMathBoard());
        var bindings = new ToolStripMenuItem("窗口绑定（按键映射页）", null, (_, _) => host.ShowMainWindow("keys"));
        var help = new ToolStripMenuItem("使用帮助", null, (_, _) => host.ShowMainWindow("help"));
        var reload = new ToolStripMenuItem("重新加载配置  (Caps+F5)", null, (_, _) => host.ReloadAll());
        var exit = new ToolStripMenuItem("退出", null, (_, _) => ExitApp());

        var context = new ContextMenuStrip
        {
            Renderer = new ToolStripProfessionalRenderer(new DarkColorTable()),
            ShowImageMargin = false,
            Font = new Font("Microsoft YaHei UI", 9f),
        };
        context.Items.AddRange(new ToolStripItem[]
        {
            menu,
            new ToolStripSeparator(),
            _enabledItem,
            _capsStateItem,
            new ToolStripSeparator(),
            open,
            qbar,
            math,
            bindings,
            help,
            new ToolStripSeparator(),
            reload,
            exit,
        });
        context.Opening += (_, _) => UpdateCapsState();

        _icon = new NotifyIcon
        {
            Icon = _customIcon ?? SystemIcons.Application,
            Text = AppInfo.ProductName,
            Visible = true,
            ContextMenuStrip = context,
        };
        _icon.DoubleClick += (_, _) => host.ShowMainWindow();
        _icon.BalloonTipClicked += (_, _) => host.ShowMainWindow();

        /* 启动气泡的正文要跟着**实际配置**走。
         *
         * 之前写死"按住 CapsLock 试试 A/S/D/F，或按 Caps+Q 打开搜索面板"——
         * 一是还在用旧名 CapsLock，二是用户可能已经把触发键改成 F13、
         * 把打开面板的键从 Q 换掉了，那这句提示就是错的。 */
        _icon.ShowBalloonTip(1800, AppInfo.ShortName + " 已启动", StartupTip(host),
            ToolTipIcon.Info);
    }

    /// <summary>根据当前配置生成一句真实的启动提示。</summary>
    private static string StartupTip(AppHost host)
    {
        try
        {
            // 第一个启用的触发键
            var trigger = host.Settings.Triggers
                .Where(t => t.Enabled)
                .OrderBy(t => t.LayerId)
                .FirstOrDefault();

            string keyName = trigger != null ? KeyName.Display(trigger.Key) : "触发键";

            /* 找一个"打开面板"类的按键作为示例。
             * 去第 1 层里找动作是 qbar 的绑定，没找到就退回通用说法。 */
            string example = "";
            var layer1 = host.Settings.Layers.FirstOrDefault(l => l.Id == 1);
            var qbarBinding = layer1?.Keys.FirstOrDefault(k =>
                k.Action == "qbar" && k.IsBound);
            if (qbarBinding != null)
            {
                var parsed = KeyName.ParseCombo(qbarBinding.Combo);
                if (parsed is { } p && p.Mods == KeyModifiers.None)
                    example = $"，或按 {keyName}+{KeyName.Display(p.Key)} 打开搜索面板";
            }

            return $"按住 {keyName} 试试 A / S / D / F{example}。";
        }
        catch
        {
            return "按住触发键即可使用全部功能。";
        }
    }

    private static void ToggleCaps()
    {
        Core.Input.HotkeyEngine.ToggleCapsLock();
    }

    private void UpdateCapsState()
    {
        _enabledItem.Text = _host.Engine.Enabled ? "暂停 " + AppInfo.ShortName : "恢复 " + AppInfo.ShortName;
        _capsStateItem.Text = Core.Input.HotkeyEngine.CapsLockOn ? "大写锁定：开" : "大写锁定：关";
        _icon.Icon = _customIcon ?? SystemIcons.Application;
    }

    public void SetEnabled(bool enabled)
    {
        try
        {
            _enabledItem.Text = enabled ? "暂停 " + AppInfo.ShortName : "恢复 " + AppInfo.ShortName;
            _icon.Text = enabled ? AppInfo.ProductName : AppInfo.ShortName + " 已暂停";
        }
        catch { }   /* 预期内：探测/重试型，失败即视为无结果 */
    }

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
