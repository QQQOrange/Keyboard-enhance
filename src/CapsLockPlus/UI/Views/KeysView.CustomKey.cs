using System.IO;
using System.Threading.Tasks;
using System.Windows.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CapsLockPlus.Core.Actions;
using CapsLockPlus.Core.Input;
using CapsLockPlus.UI.Windows;
using CapsLockPlus.Core.Settings;
using KeyBinding = CapsLockPlus.Core.Settings.KeyBinding;

namespace CapsLockPlus.UI.Views;

public partial class KeysView : UserControl
{
// 自定义按键面板（拼接键 + 目标程序）与「恢复默认」。

    // ---------------- 自定义按键面板 ----------------

    /// <summary>
    /// 选了「自定义按键」分类时，把当前键的动作切成 customKey。
    ///
    /// ⚠ 只在"不是 customKey"时才改，绝不动已有的参数 ——
    /// 用户在这个分类里反复点来点去，不能把他填好的键和应用冲掉。
    /// </summary>
    private void EnsureCustomKeyAction()
    {
        if (_host == null) return;
        var binding = FindInCurrentLayer(CurrentCombo());
        if (binding != null && binding.Action == "customKey") return;
        EnsureActionForCategory("customKey");
    }

    /// <summary>当前键的「自定义按键」参数（没绑这一类就返回空参数）。</summary>
    private CustomKeySpec CurrentCustomSpec()
    {
        var b = FindInCurrentLayer(CurrentCombo());
        return b != null && b.Action == "customKey"
            ? CustomKeySpec.FromArg(b.Arg)
            : new CustomKeySpec();
    }

    /// <summary>
    /// 保存参数到当前键。
    ///
    /// 注意这里**只动这一个绑定**，不碰 _layer / _selectedKey / 其它层 ——
    /// 早先那版捕获完会顺手改掉当前层内修饰键和选中键，
    /// 结果整个视图跳到一个空的修饰子层，看起来像"所有绑定都被清空了"。
    /// </summary>
    private void SaveCustomSpec(CustomKeySpec spec)
    {
        if (_host == null) return;

        string combo = CurrentCombo();
        var binding = FindInCurrentLayer(combo);
        if (binding == null)
        {
            binding = new KeyBinding { Combo = combo };
            CurrentLayerKeys.Add(binding);
        }

        binding.Action = "customKey";
        binding.Arg = spec.ToArg();
        _host.SaveSettings();

        BuildKeyboard();          // 键帽上的名字要跟着变
        RefreshCustomKeyPanel();
    }

    /// <summary>把面板刷成当前绑定的内容。</summary>
    private void RefreshCustomKeyPanel()
    {
        if (_host == null || CustomKeyPicker == null) return;

        var spec = CurrentCustomSpec();

        _suppressEvents = true;
        CustomAppBox.Text = spec.AppPath;
        CustomAdminBox.IsChecked = spec.RunAsAdmin;
        CustomMainWindowBox.IsChecked = spec.BringFront;
        CustomWaitBox.Text = spec.WaitAfterLaunchMs.ToString();
        CustomNoteBox.Text = FindInCurrentLayer(CurrentCombo())?.Note ?? "";
        _suppressEvents = false;

        // 捕获到的键：没设时把按钮文案留着，设了就显示成键帽
        bool hasKey = !string.IsNullOrWhiteSpace(spec.Key);
        CustomKeyBadge.Visibility = hasKey ? Visibility.Visible : Visibility.Collapsed;
        CustomKeyBadgeText.Text = hasKey ? CustomKeyRunner.ComboDisplay(spec.Key) : "";
        CustomKeyCaptureButton.Content = hasKey ? Loc.T("重新设置…") : Loc.T("按下要发送的键…");

        // 应用状态：路径存不存在直接说，别等按下去才报错
        if (string.IsNullOrWhiteSpace(spec.AppPath))
        {
            CustomAppStatus.Text = "";
        }
        else if (File.Exists(spec.AppPath))
        {
            CustomAppStatus.Text = Loc.F("已找到：{0}（进程名 {1}）", Path.GetFileName(spec.AppPath), spec.ProcessName);
        }
        else if (!spec.AppPath.Contains('\\') && !spec.AppPath.Contains('/'))
        {
            // 只填了 exe 名字：靠进程名匹配，也能用
            CustomAppStatus.Text = Loc.F("按进程名「{0}」匹配（没有填完整路径）", spec.ProcessName);
        }
        else
        {
            CustomAppStatus.Text = Loc.T("这个路径现在不存在 —— 按下去会启动失败");
        }

        CustomKeyHint.Text = spec.IsReady
            ? Loc.F("按这个键时：先看「{0}」在不在运行 —— ", spec.ProcessName)
              + Loc.T("在运行就直接把键发过去；没运行就先启动它（")
              + (spec.RunAsAdmin ? Loc.T("以管理员身份") : Loc.T("普通权限"))
              + Loc.T("），等窗口出来再发键。")
            : Loc.T("还差：") + (hasKey ? "" : Loc.T("「要发送的键」"))
              + (!hasKey && string.IsNullOrWhiteSpace(spec.AppPath) ? Loc.T(" 和 ") : "")
              + (string.IsNullOrWhiteSpace(spec.AppPath) ? Loc.T("「绑定应用」") : "");
    }

    /// <summary>捕获"要发送的键"。</summary>
    private void OnCustomKeyCapture(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;

        var spec = CurrentCustomSpec();
        var dlg = new UI.Windows.KeyCaptureWindow
        {
            Owner = Window.GetWindow(this),
            PrefixKeys = TriggerPrefixKeys(),
        };
        if (dlg.ShowDialog() != true || string.IsNullOrEmpty(dlg.CapturedCombo)) return;

        /* 存**完整组合键**（含修饰键），发送时原样重放。
         * 这里刻意不碰 _layer / _selectedKey：捕获的是"要发出去的键"，
         * 跟"当前在编辑键盘上的哪个键"是两件事。 */
        spec.Key = dlg.CapturedCombo;
        SaveCustomSpec(spec);
        ShowBindStatus(Loc.F("已设置要发送的键：{0}", CustomKeyRunner.ComboDisplay(spec.Key)));
    }

    /// <summary>当前配置的触发键（捕获时要告诉捕获窗口，免得把前缀键录成目标键）。</summary>
    private HashSet<int> TriggerPrefixKeys()
    {
        var prefixes = new HashSet<int>();
        if (_host == null) return prefixes;

        foreach (var t in _host.Settings.Triggers)
        {
            if (string.IsNullOrWhiteSpace(t.Key)) continue;
            if (KeyName.VkOf(t.Key) is int vk && vk != 0) prefixes.Add(vk);
        }
        if (KeyName.VkOf("capslock") is int caps && caps != 0) prefixes.Add(caps);
        return prefixes;
    }

    private void OnCustomAppChanged(object sender, TextChangedEventArgs e)
    {
        if (_host == null || _suppressEvents) return;
        var spec = CurrentCustomSpec();
        spec.AppPath = CustomAppBox.Text.Trim();
        SaveCustomSpec(spec);
    }

    private void OnBrowseCustomApp(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = Loc.T("选择要绑定的程序"),
            Filter = Loc.T("程序 (*.exe)|*.exe|所有文件 (*.*)|*.*"),
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;

        var spec = CurrentCustomSpec();
        spec.AppPath = dlg.FileName;
        SaveCustomSpec(spec);
        ShowBindStatus(Loc.F("已绑定应用：{0}", Path.GetFileName(dlg.FileName)));
    }

    private void OnCustomAdminChanged(object sender, RoutedEventArgs e)
    {
        if (_host == null || _suppressEvents) return;
        var spec = CurrentCustomSpec();
        spec.RunAsAdmin = CustomAdminBox.IsChecked == true;
        SaveCustomSpec(spec);
    }

    private void OnCustomMainWindowChanged(object sender, RoutedEventArgs e)
    {
        if (_host == null || _suppressEvents) return;
        var spec = CurrentCustomSpec();
        spec.BringFront = CustomMainWindowBox.IsChecked == true;
        SaveCustomSpec(spec);
    }

    /// <summary>
    /// 「启动后等多久再发键」。
    ///
    /// 边打字边落盘，但**不合法就不落盘** —— 清空输入框时 TextChanged 会先来一个
    /// 空串，这时候把 0 写进去会把用户原来的 900 冲掉；等用户真的输入数字再写。
    /// </summary>
    private void OnCustomWaitChanged(object sender, TextChangedEventArgs e)
    {
        if (_host == null || _suppressEvents) return;
        if (!int.TryParse(CustomWaitBox.Text.Trim(), out int ms)) return;

        var spec = CurrentCustomSpec();
        spec.WaitAfterLaunchMs = Math.Clamp(ms, 0, 10000);
        SaveCustomSpec(spec);
    }

    /// <summary>
    /// 备注。存在 <see cref="KeyBinding.Note"/> 上（不是 spec 里）——
    /// 备注是"这个键叫什么"，属于绑定本身，和"发什么键给哪个程序"无关。
    /// 键帽优先显示它。
    /// </summary>
    private void OnCustomNoteChanged(object sender, TextChangedEventArgs e)
    {
        if (_host == null || _suppressEvents) return;

        string combo = CurrentCombo();
        var binding = FindInCurrentLayer(combo);
        if (binding == null)
        {
            binding = new KeyBinding { Combo = combo, Action = "customKey" };
            CurrentLayerKeys.Add(binding);
        }

        string note = CustomNoteBox.Text.Trim();
        // 空备注存 null，设置文件里干脆不出现这个字段（和直达链接保持一致）
        binding.Note = note.Length == 0 ? null : note;
        _host.SaveSettings();
        BuildKeyboard();          // 键帽上要立刻显示新名字
    }

    /// <summary>键帽上显示什么：备注 → 应用名 → "自定义"。</summary>
    internal static string CustomKeyLabelOf(string? note, CustomKeySpec spec)
    {
        if (!string.IsNullOrWhiteSpace(note)) return note.Trim();
        string proc = spec.ProcessName;
        if (proc.Length > 0) return proc.Length <= 8 ? proc : proc[..8] + "…";
        return Loc.T("自定义");
    }

    /// <summary>
    /// 「恢复默认」——这是整页**唯一会一次性丢掉大量配置**的按钮，必须过两道门。
    ///
    /// 改错一个键还能改回来；恢复默认会一次覆盖基础层的上百个绑定，
    /// 点错就是"刚配好的东西全没了"。所以：
    ///   · 第一道门是按钮本身：它在工具栏最右侧，和"点击某键即可编辑"的说明分开；
    ///   · 第二道门是 <see cref="AppDialog.ConfirmDanger"/>：警告图标 + 红色确定键，
    ///     正文逐条写清"丢什么 / 留什么 / 能不能撤销"；
    ///   · 只有真点了「恢复默认」才执行；点取消、按 Esc、直接关窗口一律不动。
    ///
    /// ⚠ 别改成 <c>MessageBox.Show(...) == OK</c> 之类：全程序都用 AppDialog，
    /// 换回系统对话框会在深色主题下弹一块白底，而且按钮措辞不受控。
    /// </summary>
    private void OnResetClick(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;

        // 不是 const：文本要在**当前语言**下拼，编译期定不下来
        string text =
            Loc.T("把基础层的按键全部恢复成默认值？\n") +
            Loc.T("· 你在这一层改过的绑定会被覆盖（功能、参数、网址、文本、程序、备注）；\n") +
            Loc.T("· 你自己新增的组合键会保留，其它 9 层完全不动；\n") +
            Loc.T("· 这一步不能撤销。");

        if (!AppDialog.ConfirmDanger(Window.GetWindow(this), text,
                Loc.T("恢复默认按键"), okText: Loc.T("恢复默认"))) return;

        _host.ApplyHotkeyScheme(_host.Settings.Global.HotkeyScheme);
        Refresh();
        _host.Notify(Loc.T("按键映射已恢复默认"));
    }
}
