using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using CapsLockPlus.Core;
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
        _icon.ShowBalloonTip(1200, AppInfo.ShortName + " 已启动", "按住 CapsLock 试试 A / S / D / F，或按 Caps+Q 打开搜索面板。",
            ToolTipIcon.Info);
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
        catch { }
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
        catch { }
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
