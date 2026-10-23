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
// 就地状态提示、直达网页、输入文本、窗口绑定刷新，以及界面事件入口。

    // ---------------- 就地状态提示 ----------------

    /// <summary>
    /// 在编辑面板里显示一行结果（绿色=成功，红色=失败）。
    ///
    /// 为什么不只用 <see cref="AppHost.Notify"/>：那是屏幕右下角的 toast，
    /// 用户正在主窗口里点按钮，视线根本不在那儿 —— 会以为"没反应"。
    /// </summary>
    private void ShowBindStatus(string message, bool ok = true)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            BindStatusBar.Visibility = Visibility.Collapsed;
            return;
        }

        BindStatusText.Text = message;
        BindStatusBar.Background = (Brush)FindResource(ok ? "Brush.AccentSoft" : "Brush.SurfaceAlt");
        BindStatusText.Foreground = (Brush)FindResource(ok ? "Brush.Accent" : "Brush.TextSecondary");
        BindStatusBar.BorderBrush = (Brush)FindResource(ok ? "Brush.AccentSoftBorder" : "Brush.Border");
        BindStatusBar.BorderThickness = new Thickness(1);
        BindStatusBar.Visibility = Visibility.Visible;

        // 3 秒后自动淡出，避免一直占着版面
        _statusTimer?.Stop();
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.5) };
        _statusTimer.Tick += (_, _) =>
        {
            _statusTimer?.Stop();
            _statusTimer = null;
            BindStatusBar.Visibility = Visibility.Collapsed;
        };
        _statusTimer.Start();
    }

    private DispatcherTimer? _statusTimer;

    // ---------------- 直达网页面板 ----------------

    // ---------------- 输入文本 ----------------

    /// <summary>把多行文本框与备注框刷成当前绑定的内容。</summary>
    private void RefreshTypeTextField()
    {
        var b = FindInCurrentLayer(CurrentCombo());
        bool isType = b?.Action == "typeText";

        _suppressEvents = true;
        TypeTextBox.Text = isType ? (b!.Arg ?? "") : "";
        TypeNoteBox.Text = isType ? (b.Note ?? "") : "";
        _suppressEvents = false;
    }

    /// <summary>文本框内容变化：写进当前按键的绑定。</summary>
    private void OnTypeTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_host == null || _suppressEvents) return;

        var b = FindInCurrentLayer(CurrentCombo());
        if (b == null || b.Action != "typeText") return;

        b.Arg = TypeTextBox.Text ?? "";
        _host.SaveSettings();
        BuildKeyboard();

        if (!string.IsNullOrEmpty(b.Arg))
        {
            string preview = b.Arg.Length > 12 ? b.Arg[..12] + "…" : b.Arg;
            ShowBindStatus($"已保存要输入的文字：「{preview.Replace("\r", " ").Replace("\n", " ")}」");
        }
    }

    /// <summary>
    /// 备注变化：写进 <see cref="KeyBinding.Note"/>。
    ///
    /// 备注只影响**显示**（键帽 + 已绑定列表），不参与输入 ——
    /// 用户要把一长串邮箱/地址起个短名字，键帽上才看得清。
    /// 留空即删除备注，键帽回退显示文字开头。
    /// </summary>
    private void OnTypeNoteChanged(object sender, TextChangedEventArgs e)
    {
        if (_host == null || _suppressEvents) return;

        var b = FindInCurrentLayer(CurrentCombo());
        if (b == null || b.Action != "typeText") return;

        string note = (TypeNoteBox.Text ?? "").Trim();
        b.Note = string.IsNullOrEmpty(note) ? null : note;
        _host.SaveSettings();
        BuildKeyboard();

        ShowBindStatus(string.IsNullOrEmpty(note)
            ? "备注已清空，键帽改显示文字开头"
            : $"备注已保存：「{note}」—— 键帽上会显示这个名字");
    }

    /// <summary>
    /// 把「直达链接」面板刷成当前绑定的内容：
    /// 类型按钮的高亮、目标框的标签、浏览/取标题按钮的显隐，全部跟着走。
    /// </summary>
    private void RefreshWebPageFields()
    {
        var b = FindInCurrentLayer(CurrentCombo());
        string kind = IsLinkAction(b?.Action) ? b!.Action! : LinkUrlAction;
        bool isUrl = kind == LinkUrlAction;
        bool isFolder = kind == LinkFolderAction;
        bool isFile = kind == LinkFileAction;

        _suppressEvents = true;
        WebUrlBox.Text = IsLinkAction(b?.Action) ? (b!.Arg ?? "") : "";
        WebNoteBox.Text = b?.Note ?? "";
        _suppressEvents = false;

        // 类型按钮：选中的那个用主按钮样式，另外两个用普通样式
        LinkKindUrlButton.Style = (Style)FindResource(isUrl ? "Button.Primary" : "Button.Base");
        LinkKindFolderButton.Style = (Style)FindResource(isFolder ? "Button.Primary" : "Button.Base");
        LinkKindFileButton.Style = (Style)FindResource(isFile ? "Button.Primary" : "Button.Base");

        LinkTargetLabel.Text = isUrl ? "网址" : isFolder ? "文件夹路径" : "文件路径";
        WebUrlBox.ToolTip = isUrl
            ? "例如 https://github.com；不带 http 也会自动补全"
            : isFolder
                ? @"例如 D:\Work\项目；支持 %USERPROFILE%、~ 和 shell:Downloads"
                : @"例如 D:\文档\方案.docx；支持 %USERPROFILE%、~ 和正斜杠";

        BrowseFolderButton.Visibility = isFolder ? Visibility.Visible : Visibility.Collapsed;
        BrowseFileButton.Visibility = isFile ? Visibility.Visible : Visibility.Collapsed;
        FetchTitleButton.Visibility = isUrl ? Visibility.Visible : Visibility.Collapsed;

        WebFetchStatus.Text = string.IsNullOrWhiteSpace(b?.Arg) || isUrl
            ? ""
            : isFolder
                ? (Directory.Exists(Core.Actions.ActionContext.ExpandPath(b!.Arg))
                    ? "文件夹存在，按这个键就会在资源管理器里打开它。"
                    : "这个路径现在不存在 —— 按下去会提示「文件夹不存在」。")
                : (File.Exists(Core.Actions.ActionContext.ExpandPath(b!.Arg))
                    ? "文件存在，按这个键就会用默认程序打开它。"
                    : "这个路径现在不存在 —— 按下去会提示「文件不存在」。");

        LinkHint.Text = isUrl
            ? "填网址时会自动抓网页标题当备注（不满意可以改）。备注会显示在键帽上。"
            : isFolder
                ? @"不填备注时，键帽上显示文件夹名。路径支持 %USERPROFILE%\Desktop、~\Downloads 和 shell:Downloads 这几种写法。"
                : "不填备注时，键帽上显示文件名。会按系统文件关联打开（和双击一样），不用选程序。";
    }

    private void OnWebUrlChanged(object sender, TextChangedEventArgs e)
    {
        if (_host == null || _suppressEvents) return;
        var b = FindInCurrentLayer(CurrentCombo());
        if (b == null || !IsLinkAction(b.Action)) return;

        b.Arg = WebUrlBox.Text.Trim();
        _host.SaveSettings();
        BuildKeyboard();          // 键帽上要跟着变

        switch (b.Action)
        {
            case LinkUrlAction:
                // 网址填完就去抓标题当备注（用户要求"一定要先拉一下"）
                MaybeFetchTitle();
                break;

            case LinkFolderAction:
                // 文件夹：立刻反馈"这条路径到底存不存在"，别等用户按了才发现
                WebFetchStatus.Text = string.IsNullOrWhiteSpace(b.Arg)
                    ? ""
                    : (Directory.Exists(Core.Actions.ActionContext.ExpandPath(b.Arg))
                        ? "文件夹存在，按这个键就会在资源管理器里打开它。"
                        : "这个路径现在不存在 —— 按下去会提示「文件夹不存在」。");
                break;

            default:
                // 文件：同样立刻告诉用户"这个文件在不在"
                WebFetchStatus.Text = string.IsNullOrWhiteSpace(b.Arg)
                    ? ""
                    : (File.Exists(Core.Actions.ActionContext.ExpandPath(b.Arg))
                        ? "文件存在，按这个键就会用默认程序打开它。"
                        : "这个路径现在不存在 —— 按下去会提示「文件不存在」。");
                break;
        }
    }

    /// <summary>
    /// 抓网页标题填进备注。
    ///
    /// 时机：网址变化后**防抖 700ms** —— 用户还在敲字时不要一路请求。
    /// 只在备注为空时自动填：如果用户已经自己写过备注，绝不覆盖。
    /// </summary>
    private void MaybeFetchTitle(bool force = false)
    {
        var b = FindInCurrentLayer(CurrentCombo());
        if (b == null || b.Action != "webPage") return;
        if (string.IsNullOrWhiteSpace(b.Arg)) return;

        // 已有备注且不是手动点"重新取标题" → 不覆盖用户写的东西
        if (!force && !string.IsNullOrWhiteSpace(b.Note)) return;

        _titleTimer?.Stop();
        _titleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _titleTimer.Tick += (_, _) =>
        {
            _titleTimer?.Stop();
            _titleTimer = null;
            StartTitleFetch(force);
        };
        _titleTimer.Start();
    }

    private DispatcherTimer? _titleTimer;

    /// <summary>真正发起抓取（后台线程，不卡界面）。</summary>
    private void StartTitleFetch(bool overwrite)
    {
        var b = FindInCurrentLayer(CurrentCombo());
        if (b == null || b.Action != "webPage") return;
        string url = b.Arg ?? "";
        if (string.IsNullOrWhiteSpace(url)) return;

        WebFetchStatus.Text = "正在获取网页标题…";
        FetchTitleButton.IsEnabled = false;

        // 用 Task.Run 而不是同步调用：HttpClient 最长等 3 秒，同步会冻结界面
        Task.Run(() => Core.Actions.WebTitleFetcher.Fetch(url))
            .ContinueWith(t =>
            {
                string? title = t.IsCompletedSuccessfully ? t.Result : null;

                Dispatcher.Invoke(() =>
                {
                    FetchTitleButton.IsEnabled = true;

                    // 用户可能在等待期间切走了别的键，重新取一次当前绑定
                    var cur = FindInCurrentLayer(CurrentCombo());
                    if (cur == null || cur.Action != "webPage") return;

                    if (string.IsNullOrWhiteSpace(title))
                    {
                        WebFetchStatus.Text = "没能取到标题（网站在线吗？），可以直接手写备注。";
                        return;
                    }

                    if (!overwrite && !string.IsNullOrWhiteSpace(cur.Note))
                    {
                        WebFetchStatus.Text = $"取到标题「{title}」，但你已有备注，保留你的。";
                        return;
                    }

                    cur.Note = title;
                    _host?.SaveSettings();
                    _suppressEvents = true;
                    WebNoteBox.Text = title;
                    _suppressEvents = false;

                    WebFetchStatus.Text = $"已取到标题：{title}（可以改）";
                    BuildKeyboard();
                });
            });
    }

    private void OnFetchTitleClick(object sender, RoutedEventArgs e)
    {
        var b = FindInCurrentLayer(CurrentCombo());
        if (b == null || b.Action != "webPage" || string.IsNullOrWhiteSpace(b.Arg))
        {
            ShowBindStatus("先填一个网址，再取标题。", ok: false);
            return;
        }
        StartTitleFetch(overwrite: true);
    }

    private void OnWebNoteChanged(object sender, TextChangedEventArgs e)
    {
        if (_host == null || _suppressEvents) return;
        var b = FindInCurrentLayer(CurrentCombo());
        if (b == null || !IsLinkAction(b.Action)) return;

        b.Note = WebNoteBox.Text.Trim();
        _host.SaveSettings();
        BuildKeyboard();

        // 有备注了就给个确认（用户反馈"没有提示"）
        if (!string.IsNullOrWhiteSpace(b.Note))
            ShowBindStatus($"备注已保存：「{b.Note}」—— 键帽上会显示这个名字");
    }

    /// <summary>当前按键已绑定的程序路径（取自绑定的 Arg）。</summary>
    private string CurrentWinBindExe()
    {
        var b = FindInCurrentLayer(CurrentCombo());
        return b?.Action == "winBind" ? (b.Arg ?? "") : "";
    }

    /// <summary>把"已选程序"区域刷新成当前绑定的样子。</summary>
    private void RefreshWinBindPanel()
    {
        string exe = CurrentWinBindExe();
        bool has = !string.IsNullOrWhiteSpace(exe) && File.Exists(exe);

        // 自定义名称（备注）回填到输入框，不覆盖用户已写的内容
        var cur = FindInCurrentLayer(CurrentCombo());
        _suppressEvents = true;
        WinBindNoteBox.Text = cur?.Action == "winBind" ? (cur.Note ?? "") : "";
        _suppressEvents = false;

        if (has)
        {
            // 显示名优先用自定义名，否则用 exe 文件名
            WinBindName.Text = string.IsNullOrWhiteSpace(cur?.Note)
                ? Path.GetFileNameWithoutExtension(exe)
                : cur!.Note!.Trim();
            WinBindPath.Text = exe;
            WinBindClearButton.Visibility = Visibility.Visible;
            WinBindHint.Text = "按这个键：程序没开就自动启动；已开着就切过去；"
                             + "已经在前台时再按一次会最小化。";
        }
        else if (!string.IsNullOrWhiteSpace(exe))
        {
            WinBindName.Text = Path.GetFileNameWithoutExtension(exe);
            WinBindPath.Text = exe + "（文件不存在）";
            WinBindClearButton.Visibility = Visibility.Visible;
            WinBindHint.Text = "这个程序已经不在了，重新选一个吧。";
        }
        else
        {
            WinBindName.Text = "还没选择";
            WinBindPath.Text = "点下面任一按钮选择要绑定的程序";
            WinBindClearButton.Visibility = Visibility.Collapsed;
            WinBindHint.Text = "";
        }

        // 程序图标（取不到就不显示，不留空框）
        WinBindIcon.Source = has ? Core.Icons.IconCache.FromFile(exe) : null;
    }

    /// <summary>窗口绑定的自定义显示名（exe 名不一定是软件名）。</summary>
    private void OnWinBindNoteChanged(object sender, TextChangedEventArgs e)
    {
        if (_host == null || _suppressEvents) return;
        var b = FindInCurrentLayer(CurrentCombo());
        if (b == null || b.Action != "winBind") return;

        string note = WinBindNoteBox.Text.Trim();
        b.Note = string.IsNullOrWhiteSpace(note) ? null : note;
        _host.SaveSettings();
        BuildKeyboard();

        if (!string.IsNullOrWhiteSpace(note))
            ShowBindStatus($"显示名称已保存：「{note}」—— 键帽上会显示这个名字");
    }

    /// <summary>把选好的程序写进当前按键的绑定。</summary>
    private void ApplyWinBindExe(string exePath)
    {
        if (_host == null || string.IsNullOrWhiteSpace(exePath)) return;

        string combo = CurrentCombo();
        var binding = FindInCurrentLayer(combo);
        if (binding == null)
        {
            binding = new KeyBinding { Combo = combo };
            CurrentLayerKeys.Add(binding);
        }
        binding.Action = "winBind";
        binding.Arg = exePath;

        /* 换程序时**保留**用户写的显示名 —— 他可能只是换了个安装路径。
         * 但如果新程序的 exe 名和旧备注配对不上（比如从 notepad 换成 chrome），
         * 保留旧名字反而误导，所以只在完全没备注时不动。 */

        _host.SaveSettings();

        RefreshWinBindPanel();
        BuildKeyboard();

        string name = string.IsNullOrWhiteSpace(binding.Note)
            ? Path.GetFileNameWithoutExtension(exePath)
            : binding.Note!.Trim();
        string keyLabel = KeyName.Display(_selectedKey);
        ShowBindStatus($"{keyLabel} 已绑定「{name}」—— 按一次切过去，再按一次最小化");
        _host.Notify($"{keyLabel} → {name}");
    }

    /// <summary>入口一：选程序文件（适合还没运行的程序）。</summary>
    private void OnPickExeClick(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择要绑定的程序",
            Filter = "程序 (*.exe)|*.exe|所有文件|*.*",
            CheckFileExists = true,
        };
        // 从已选程序所在目录打开，连着改几个程序时更省事
        string cur = CurrentWinBindExe();
        if (File.Exists(cur))
        {
            try { dlg.InitialDirectory = Path.GetDirectoryName(cur); } catch { }   /* 预期内：探测/重试型，失败即视为无结果 */
        }
        if (dlg.ShowDialog() != true) return;
        ApplyWinBindExe(dlg.FileName);
    }

    /// <summary>入口二：十字准星点选已打开的窗口，取其 exe 路径。</summary>
    private void OnPickWindowClick(object sender, RoutedEventArgs e)
    {
        var picker = new UI.Windows.WindowPickerWindow();
        if (Window.GetWindow(this) is { } owner) picker.Owner = owner;
        picker.ShowDialog();

        var h = picker.PickedWindow;
        if (h == IntPtr.Zero) return;

        var info = Core.Windows.WindowService.Describe(h);
        if (info == null || string.IsNullOrEmpty(info.ExePath))
        {
            _host?.Notify("取不到这个窗口的程序路径，改用「选择程序文件」试试");
            return;
        }
        ApplyWinBindExe(info.ExePath);
    }

    /// <summary>清除这个键的程序绑定。</summary>
    private void OnWinBindClear(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        var b = FindInCurrentLayer(CurrentCombo());
        if (b == null) return;
        b.Arg = "";
        b.Action = "doNothing";
        _host.SaveSettings();
        RefreshWinBindPanel();
        BuildKeyboard();
        SelectKey(_selectedKey);      // 让功能下拉也回到"不绑定"
    }

    // ---------------- 事件 ----------------


    /// <summary>层内修饰键切换后要刷新（键盘上的绑定标记会变）。</summary>
    private void SelectModifierLayer(KeyModifiers mods)
    {
        _subLayerKey = LayerKeyOf(mods);
        _layer = mods;
        SelectKey(_selectedKey);
    }



    private void OnBoundSelected(object sender, SelectionChangedEventArgs e)
    {
        // 点列表里的条目要把**修饰键一起**还原，否则点了 Ctrl+Shift+K
        // 只会选中 K，编辑的其实是另一个绑定。
        if (BoundList.SelectedItem is BoundVm vm)
        {
            _layer = vm.Mods;
            SelectKey(vm.Key);
        }
    }


    private void OnActionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_host == null || _suppressEvents) return;
        if (ActionBox.SelectedItem is not ComboBoxItem item || item.Tag is not string actionId) return;
        string combo = CurrentCombo();
        var binding = FindInCurrentLayer(combo);
        if (binding == null)
        {
            // 必须加进**当前层**的列表。写进 Settings.Keys 会让
            // 第 2~10 层新建的绑定跑到第 1 层去（这个 bug 之前就是这样）。
            binding = new KeyBinding { Combo = combo };
            CurrentLayerKeys.Add(binding);
        }
        binding.Action = actionId;
        if (actionId == "doNothing") binding.Arg = "";
        _host.SaveSettings();
        var info = _host.Actions.Get(actionId);
        ActionDesc.Text = info?.Description ?? "";

        /* 从**搜索结果**里选了一个功能 → 要把分类切到它所属的那一类。
         *
         * 否则会出现：搜"删除"选中「删除整行」，但左边的分类还停在
         * 「面板」，功能下拉里显示的又是搜索结果 —— 用户看不出
         * 这个功能到底属于哪个分类。
         *
         * 切分类之前先清空搜索框（否则 BuildActionBox 仍按搜索过滤）。 */
        if (info != null && !string.IsNullOrEmpty(ActionSearchBox?.Text))
        {
            _suppressEvents = true;
            try
            {
                ActionSearchBox.Text = "";

                foreach (ComboBoxItem it in CategoryBox.Items)
                {
                    if (it.Tag as string == info.Category)
                    {
                        CategoryBox.SelectedItem = it;
                        CategoryBoxWinBind.SelectedItem = it;
                        CategoryBoxWeb.SelectedItem = it;
                        CategoryBoxType.SelectedItem = it;
                        break;
                    }
                }
                BuildActionBox();

                // 把刚选的动作重新选上（BuildActionBox 会重置到第一项）
                foreach (ComboBoxItem it in ActionBox.Items)
                {
                    if (it.Tag is string tag && tag.Equals(actionId, StringComparison.OrdinalIgnoreCase))
                    {
                        ActionBox.SelectedItem = it;
                        break;
                    }
                }
            }
            finally
            {
                _suppressEvents = false;
            }

            // 不要走 SelectKey（它会重新按分类定位），这里已经同步好了
            BuildKeyboard();
            return;
        }

        SelectKey(_selectedKey);
    }

    private void OnArgChanged(object sender, TextChangedEventArgs e)
    {
        if (_host == null || _suppressEvents) return;
        var binding = FindInCurrentLayer(CurrentCombo());
        if (binding == null) return;

        string text = ArgBox.Text.Trim();

        /* 步进类参数只收数字。
         *
         * 用户很自然会写「5%」或「5个点」，而 <c>int.TryParse</c> 认不出来 ——
         * 结果就是"我明明填了 5，按一下还是跳 10%"，看着像没保存。
         * 这里直接把非数字滤掉，顺手夹到 1~100，填什么都是有效值。 */
        if (IsStepArgAction(binding.Action) && text.Length > 0)
        {
            string digits = new(text.Where(char.IsDigit).Take(3).ToArray());

            if (digits.Length > 0 && int.TryParse(digits, out int n))
            {
                if (n > 100) digits = "100";
                else if (n == 0) digits = "1";      // 0 等于"按了不动"，没有意义
            }

            if (digits != ArgBox.Text)
            {
                _suppressEvents = true;
                try
                {
                    ArgBox.Text = digits;
                    ArgBox.CaretIndex = digits.Length;
                }
                finally { _suppressEvents = false; }
            }

            text = digits;
            UpdateStepHint(binding.Action, text);
        }

        binding.Arg = text;
        _host.SaveSettings();
    }

    /// <summary>步进框下面那行提示跟着输入变，让用户立刻看到"实际会用多少"。</summary>
    private void UpdateStepHint(string actionId, string text)
    {
        int def = DefaultStepFor(actionId);
        string unit = IsVolumeStepAction(actionId) ? "音量" : "亮度";

        if (string.IsNullOrEmpty(text))
        {
            ArgHint.Text = $"留空 = 用设置里的 {def}%：按一次{unit}变 {def}%";
            return;
        }

        if (!int.TryParse(text, out int v)) return;

        string extra = string.Empty;
        if (IsVolumeStepAction(actionId))
        {
            /* 系统音量是按档走的（通常 2%），设定值不是整数倍时要累计补偿。
             *
             * 这里**不能**去问 VolumeService.StepPercent()：那是 COM 调用，
             * 会在界面线程上把音频端点建起来，而动作队列是另一条 STA 线程 ——
             * 跨单元拿同一个接口指针会出问题。所以只做算术提示。 */
            if (v % 2 != 0)
                extra = $"（系统每档 2%，会按次数平均凑成 {v}%）";
        }

        ArgHint.Text = $"按一次{unit}变 {v}%{extra}；按住不放仍按 1% 细调";
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        var binding = FindInCurrentLayer(CurrentCombo());
        if (binding != null)
        {
            binding.Action = "doNothing";
            binding.Arg = "";
            _host.SaveSettings();
        }
        SelectKey(_selectedKey);
        _host.Notify($"{KeyName.Display(_selectedKey)} 已清除绑定");
    }

    private void OnTestClick(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        string combo = CurrentCombo();
        var binding = FindInCurrentLayer(combo);
        if (binding == null || !binding.IsBound) { _host.Notify("这个按键还没有绑定功能"); return; }
        _host.InvokeAction(binding.Action, binding.Arg);
    }

    /// <summary>
    /// 自定义按键：弹出捕获窗口，用户按下的键即成为新的绑定目标。
    ///
    /// 键盘上画出来的键之外，也支持小键盘、媒体键、浏览器键等
    /// （这些键的 VK 在 WPF 键盘事件里拿不到，必须走低级键盘钩子）。
    /// </summary>
    /// <summary>
    /// 当前编辑目标对应的 combo ID。
    ///
    /// 层内修饰键（<see cref="_layer"/>）就是组合键的修饰部分 ——
    /// 自定义按键捕获到 Ctrl+Shift+K 时，直接把 `_layer` 设成 Ctrl|Shift、
    /// 把 `_selectedKey` 设成 "k"，于是这里一律用同一个公式，
    /// 不存在"捕获到的修饰键被丢掉"的可能。
    ///
    /// **非修饰键子层**（tab / f13 / capslock…）没有修饰位，靠键名前缀区分：
    /// 引擎拼的是 <c>caps_tab_q</c>（见 HotkeyEngine.ComboForSubLayer）。
    /// 以前这里不拼前缀，点了「+ Tab」之后键帽读写的还是 caps_q ——
    /// 等于在改基础层，而引擎查的 caps_tab_q 永远没人写，子层形同虚设。
    /// </summary>
    private string CurrentCombo() => ComboFor(_selectedKey);

    /// <summary>某个物理键在当前层/当前子层下的 combo ID。</summary>
    private string ComboFor(string keyId) => ComboFor(_subLayerKey, _layer, keyId);

    /// <summary>
    /// 拼 combo ID 的**唯一**规则（引擎与界面必须一致）。
    ///
    /// * 直接按：<c>caps_q</c>
    /// * 修饰键子层：<c>caps_ctrl_q</c>（修饰位由 mods 表达）
    /// * 非修饰键子层：<c>caps_tab_q</c>（子层键当前缀）
    ///
    /// 抽成静态是为了让自检能直接断言，不必去启动整个界面。
    /// </summary>
    internal static string ComboFor(string? subLayerKey, KeyModifiers mods, string keyId)
    {
        string prefix = string.IsNullOrEmpty(subLayerKey) || IsModifierOnlySubLayer(subLayerKey)
            ? ""
            : subLayerKey + "_";
        return KeyName.BuildComboEx(mods, prefix + keyId);
    }

    /// <summary>子层键是不是纯修饰键组合（ctrl / ctrl_shift / lalt…）。</summary>
    internal static bool IsModifierOnlySubLayer(string? subKey)
    {
        if (string.IsNullOrWhiteSpace(subKey)) return false;
        foreach (string part in subKey.Split('_', StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "lctrl" or "rctrl":
                case "shift" or "lshift" or "rshift":
                case "alt" or "lalt" or "ralt":
                case "win" or "lwin" or "rwin":
                    continue;
                default:
                    return false;
            }
        }
        return true;
    }

    /// <summary>层内修饰键的中文说明（界面提示用）。</summary>
    private static string ModifierText(KeyModifiers m)
    {
        if (m == KeyModifiers.None) return "直接按";
        var parts = new List<string>();
        if (m.HasFlag(KeyModifiers.Ctrl)) parts.Add("Ctrl");
        if (m.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        if (m.HasFlag(KeyModifiers.Alt)) parts.Add("LAlt");
        if (m.HasFlag(KeyModifiers.Win)) parts.Add("Win");
        return "+ " + string.Join(" + ", parts);
    }
}
