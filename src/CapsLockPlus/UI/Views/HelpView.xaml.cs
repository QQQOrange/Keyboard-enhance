using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CapsLockPlus.Core.Input;

namespace CapsLockPlus.UI.Views;

/// <summary>
/// 帮助页：使用指南 + 快捷键速查。
///
/// 速查表完全由**当前配置**生成（遍历所有层的绑定），
/// 所以用户改完按键映射后，这里立刻跟着变 —— 不会出现
/// 「帮助里写着按 Q 打开面板，实际你已经改绑到 W」 这种尴尬。
/// </summary>
public partial class HelpView : UserControl
{
    private AppHost? _host;

    public HelpView()
    {
        InitializeComponent();
        // 离开可视树时退订，避免宿主长期持有旧页面（隐性泄漏）
        Unloaded += OnUnloaded;
    }

    public void Initialize(AppHost host)
    {
        _host = host;

        /* 订阅配置变更：在按键映射页改完绑定，这里立刻跟着更新。
         *
         * 之前只在"切到帮助页"时刷新 —— 若帮助页一直开着（分屏），
         * 另一页改了配置，速查表还是旧内容。 */
        _host.SettingsChanged -= OnSettingsChanged;
        _host.SettingsChanged += OnSettingsChanged;

        Refresh();
    }

    /// <summary>宿主配置变化：重建速查表与指南。</summary>
    private void OnSettingsChanged()
    {
        if (_host == null || !IsVisible) return;
        Refresh();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_host != null) _host.SettingsChanged -= OnSettingsChanged;
    }

    public void Refresh()
    {
        if (_host == null) return;
        UpdateTriggerHint();
        BuildGuide();
        Build(FilterBox.Text?.Trim() ?? "");
    }

    private void OnFilterChanged(object sender, TextChangedEventArgs e) => Build(FilterBox.Text?.Trim() ?? "");

    /// <summary>顶部提示改为**当前实际使用的触发键**，不再写死 CapsLock。</summary>
    private void UpdateTriggerHint()
    {
        if (_host == null) return;

        // 优先用第 1 层的触发键（最常用的那个）作为代表
        var primary = _host.Settings.Triggers
            .Where(t => t.Enabled)
            .OrderBy(t => t.LayerId)
            .FirstOrDefault();

        if (primary == null)
        {
            TriggerKeyText.Text = "（未设置）";
            HintText.Text = "还没有触发键 —— 到「触发键与层」页给某一层分配一个。";
            return;
        }

        TriggerKeyText.Text = DescribeKey(primary.Key);
        HintText.Text = "再看下面的按键 —— 未绑定的键可以到「按键映射」里自己加。"
                      + "每一层可以用不同的触发键，速查表按层分列。";
    }

    private static string DescribeKey(string key)
    {
        // 组合键 ID → 可读文字
        var parsed = KeyName.ParseCombo(key);
        if (parsed != null)
        {
            var (mods, k) = parsed.Value;
            if (mods == KeyModifiers.None) return KeyName.Display(k);
            var parts = new List<string>();
            if (mods.HasFlag(KeyModifiers.Ctrl)) parts.Add("Ctrl");
            if (mods.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
            if (mods.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
            if (mods.HasFlag(KeyModifiers.Win)) parts.Add("Win");
            return string.Join("+", parts) + "+" + KeyName.Display(k);
        }
        return KeyName.Display(key);
    }

    /// <summary>
    /// 生成「使用指南」。
    ///
    /// 内容跟随实际配置（层数、子层、宏数量），而不是写死一段说明 ——
    /// 用户看到的是「我这里现在是怎么配的」。
    /// </summary>
    private void BuildGuide()
    {
        if (_host == null) return;
        GuidePanel.Children.Clear();

        var s = _host.Settings;

        AddSection("① 触发键与图层", new[]
        {
            "按住触发键（默认 CapsLock）就是「功能键」，此时再按其他键会执行你在「按键映射」里配的功能。",
            "一共 10 个图层，每层可以有**自己的触发键**（CapsLock、F13、右 Alt…），互不干扰。",
            "触发键只能是单个按键 —— 组合键留给层内绑定用，避免「同样按 Ctrl+K，到底算触发还是算绑定」的歧义。",
            $"当前配置：{s.Triggers.Count(t => t.Enabled)} 个启用的触发键。",
        });

        AddSection("② 子层（层内修饰键）", new[]
        {
            "进入某一层后，再按住修饰键（Ctrl / Shift / Alt / Win 及其组合），可以用到**另一套独立绑定**。",
            "每层最多 10 个子层，在「按键映射」页点「设置子层…」自由勾选。",
            "例：基础层按 Q 打开面板，按住 Ctrl 再按 Q 则可以是「删除前一个单词」。",
            "每个子层都有独立的按键绑定，互不覆盖。",
        });

        AddSection("③ 按键映射", new[]
        {
            "左半是完整 108 键键盘：点任意键即可编辑它的功能，修改立刻生效。",
            "深蓝色键帽 = 已绑定功能；浅蓝色 = 未绑定。键帽上方的小字是**功能分类**，中间是功能名，下方是键名。",
            "右侧「编辑绑定」顶部的搜索框可以按功能名、分类、宏名、网址快速找到要绑的功能。",
            "「分类」决定功能下拉里显示哪一类；「窗口绑定」「直达链接」「输入文本」这三类会换成专用的编辑界面。",
        });

        AddSection("④ 窗口绑定", new[]
        {
            "分类选「窗口绑定」后，有两个按钮：点选窗口（准星点一下已开的窗口）或选择程序文件。",
            "绑定后按该键：程序没开就自动启动，已开着就切过去，已经在前台再按一次会最小化。",
            "exe 名不一定是软件名（idea64.exe 是 IDEA），可以在「显示名称」里改成你认得的名字。",
        });

        AddSection("⑤ 直达链接", new[]
        {
            "这里有两种用法，别混：一种是「先填好、按键就开」，一种是「选中什么就开什么」。",
            "【先填好】分类选「直达链接」，再选类型：直达网址用默认浏览器打开，直达文件夹在资源管理器里打开，直达文件用系统默认程序打开（和双击一样）。",
            "网址写成 github.com 也行，会自动补 https://；填完会自动抓网页标题当备注。文件夹和文件都能点「浏览…」直接挑。",
            "路径支持 %USERPROFILE%\\Desktop、~\\Downloads、shell:Downloads 这些写法；备注会显示在键帽上，不写备注时显示域名 / 文件夹名 / 文件名。",
            "【选中什么就开什么】分类选「智能直达」（这一类里只有它一个动作）：在任意程序里选中一段文字，按这个键 —— 是网址就开浏览器、是文件夹就开资源管理器、是文件就用默认程序打开。不用预先配置。",
            "智能识别支持带协议的网址、裸域名、盘符路径、UNC 网络路径、环境变量路径，以及 file:/// 开头的地址；选中普通句子时会明确告诉你「这不是链接」，不会乱开浏览器。",
        });

        AddSection("⑤b 自定义按键", new[]
        {
            "分类选「自定义按键」：设一个要发送的键 + 一个应用。按这个键时先看应用在不在运行 —— 在运行就直接把键发过去，没运行就先启动它、等窗口出来再发。",
            "典型用法：把 F13 绑给微信，按一下就切到微信并按 F13；或者把某个键绑给常驻的笔记软件，按下去直接在里面触发它的快捷键。",
            "「以管理员身份启动」只在需要拉起这个程序时生效，会弹一次 UAC；程序本来就在跑时不受影响。",
            "「启动主窗口」开：把应用窗口切到前台再发键，最可靠，但会抢走当前焦点。关：不抢焦点、直接把键投递给那个窗口；个别程序会忽略后台按键，那时请打开这一项。",
        });

        AddSection("⑥ 直接搜索", new[]
        {
            "分类选「直接搜索」，再挑一个搜索引擎（百度、知乎、B站、Google…）。",
            "用法：在任意程序里选中一段文字，按这个键 —— 直接用该引擎在默认浏览器里搜索。",
            "不经过 Qbar 面板，是「看到就查」的最快路径。没选中文字时会提示先选中。",
            "它和「Qbar 搜索」的区别：Qbar 搜索是打开面板再输入；直接搜索是拿选区直接搜。",
        });

        AddSection("⑦ 宏", new[]
        {
            "「宏」页可以录制一串按键操作，然后把整个宏绑到一个键上。",
            "录制中的按键不会被拦截，录完在列表里可以调整每一步的延迟。",
            "绑定时分类选「宏」，再从功能下拉里选具体那个宏。",
            $"当前配置：{s.Macros.Count} 个宏。",
        });

        AddSection("⑧ 剪贴板与同步", new[]
        {
            "内置 10 组独立剪贴板（Caps+C / Caps+V 那一套），与系统剪贴板分开。",
            "「同步与备份」支持 WebDAV：可以把整套配置同步到网盘，也能导入导出明文/加密文件。",
        });

        AddSection("⑨ 常见问题", new[]
        {
            "改了按键没生效？确认当前在正确的层（看键盘上方的层下拉），以及引擎没有处于「暂停」状态。",
            "锁屏（Win+L）后热键失效是系统限制 —— 锁屏界面在独立的安全桌面上，任何程序都无法在上面拦截键盘。",
            "开机自启在「设置」里开关；它写入的是当前用户的注册表，不需要管理员权限。",
        });
    }

    private void AddSection(string title, string[] paragraphs)
    {
        var header = new TextBlock
        {
            Text = title,
            FontSize = 13.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("Brush.TextPrimary"),
            Margin = new Thickness(0, GuidePanel.Children.Count == 0 ? 0 : 14, 0, 6),
        };
        GuidePanel.Children.Add(header);

        foreach (string p in paragraphs)
        {
            GuidePanel.Children.Add(new TextBlock
            {
                Text = "· " + p,
                Style = (Style)FindResource("Text.Secondary"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(2, 0, 0, 4),
            });
        }
    }

    private void Build(string filter)
    {
        if (_host == null) return;
        var groups = new List<HelpGroup>();
        var byCategory = new Dictionary<string, List<HelpItem>>();

        /* 遍历**所有层**的绑定。
         *
         * 每层用各自的触发键做前缀 —— 之前写死 "Caps"，第 2 层
         * 配的是 F13 也显示 "Caps+..."，与实际完全对不上。 */
        foreach (var layer in _host.Settings.Layers.OrderBy(l => l.Id))
        {
            var keys = layer.Keys.Count > 0
                ? layer.Keys
                : (layer.Id == 1 ? _host.Settings.Keys : layer.Keys);

            var trigger = _host.Settings.Triggers.FirstOrDefault(t => t.LayerId == layer.Id);
            string layerPrefix = trigger != null
                ? DescribeKey(trigger.Key)
                : $"第{layer.Id}层";

            foreach (var binding in keys.Where(k => k.IsBound))
            {
                var parsed = KeyName.ParseCombo(binding.Combo);
                if (parsed == null) continue;
                var (mods, key) = parsed.Value;
                if (key == "capslock") continue;

                string modPart = mods switch
                {
                    KeyModifiers.None => "",
                    _ => "+" + string.Join("+", new[]
                    {
                        mods.HasFlag(KeyModifiers.Ctrl) ? "Ctrl" : null,
                        mods.HasFlag(KeyModifiers.Shift) ? "Shift" : null,
                        mods.HasFlag(KeyModifiers.Alt) ? "Alt" : null,
                        mods.HasFlag(KeyModifiers.Win) ? "Win" : null,
                    }.Where(x => x != null)),
                };
                string combo = $"{layerPrefix}{modPart}+{KeyName.Display(key)}";

                var info = _host.Actions.Get(binding.Action);
                if (info == null) continue;

                string desc = DescribeBinding(binding, info);

                if (filter.Length > 0 &&
                    !combo.Contains(filter, StringComparison.OrdinalIgnoreCase) &&
                    !info.Title.Contains(filter, StringComparison.OrdinalIgnoreCase) &&
                    !desc.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!byCategory.TryGetValue(info.Category, out var list))
                {
                    list = new List<HelpItem>();
                    byCategory[info.Category] = list;
                }
                list.Add(new HelpItem(combo, info.Title, desc));
            }
        }

        // 内置的固定说明
        byCategory.TryAdd("面板", new List<HelpItem>());
        var panel = byCategory["面板"];
        if (filter.Length == 0)
        {
            panel.Add(new HelpItem("短按触发键", "切换大小写",
                "不按其他键时松开的默认行为，可在「触发键与层」里改"));
            panel.Add(new HelpItem("双击触发键", "打开 Qbar",
                $"{Core.Input.HotkeyEngine.DoubleTapWindowMs} 毫秒内连按两下"));
            panel.Add(new HelpItem("Qbar 内", "↑ ↓ 选择", "Enter 执行，Esc 关闭，Tab 补全"));
            panel.Add(new HelpItem("Qbar 内", "=1+2*3", "直接计算并把结果复制到剪贴板"));
        }

        string[] order =
        {
            "光标移动", "选择", "编辑", "剪贴板", "面板", "窗口", "窗口绑定",
            "智能直达", "直达链接", "自定义按键", "宏", "鼠标", "系统功能", "基础",
        };
        foreach (var cat in order)
        {
            if (!byCategory.TryGetValue(cat, out var items) || items.Count == 0) continue;
            groups.Add(new HelpGroup(cat, items.Count.ToString(), items));
        }
        foreach (var kv in byCategory)
        {
            if (order.Contains(kv.Key) || kv.Value.Count == 0) continue;
            groups.Add(new HelpGroup(kv.Key, kv.Value.Count.ToString(), kv.Value));
        }

        // 每组内部按"层 → 修饰键 → 键盘顺序"排列，查找更快
        foreach (var g in groups)
        {
            g.Items.Sort((a, b) =>
            {
                int la = LayerRank(a.Combo), lb = LayerRank(b.Combo);
                if (la != lb) return la.CompareTo(lb);
                return KeyRank(a.Combo).CompareTo(KeyRank(b.Combo));
            });
        }

        Groups.ItemsSource = groups;
    }

    /// <summary>
    /// 一条绑定该显示什么说明。
    ///
    /// 窗口绑定显示程序名（或自定义名），直达网页显示备注/网址 ——
    /// 和其他页面保持同一套规则，避免"这里显示覆盖、那里显示 Photoshop"。
    /// </summary>
    private static string DescribeBinding(Core.Settings.KeyBinding binding, Core.Actions.ActionInfo info)
    {
        string id = binding.Action ?? "";

        if (id == "customKey")
        {
            var spec = Core.Actions.CustomKeySpec.FromArg(binding.Arg);
            if (!spec.IsReady) return "自定义按键（还没配好）";
            return $"把 {Core.Actions.CustomKeyRunner.ComboDisplay(spec.Key)} 发给 {spec.ProcessName}"
                 + (spec.RunAsAdmin ? "（需要时以管理员身份启动）" : "")
                 + (spec.BringFront ? "，会先切到前台" : "");
        }

        if (id == "winBind" && !string.IsNullOrWhiteSpace(binding.Arg))
        {
            string name = string.IsNullOrWhiteSpace(binding.Note)
                ? System.IO.Path.GetFileNameWithoutExtension(binding.Arg)
                : binding.Note!.Trim();
            return $"切到 {name}（再按一次最小化）";
        }

        if (id == "webPage")
            return "打开 " + Core.Actions.UrlUtil.LabelOf(binding.Note, binding.Arg);

        // 直达文件夹：说清"打开的是哪个目录"，目录名比整条路径好认
        if (id == "openFolder")
            return "打开文件夹 " + UI.Views.KeysView.FolderLabelOf(binding.Note, binding.Arg);

        // 直达文件：同理，用文件名代替整条路径
        if (id == "openFile")
            return "打开文件 " + UI.Views.KeysView.FileLabelOf(binding.Note, binding.Arg);

        return binding.Arg.Length > 0
            ? $"{info.Description}（参数 {binding.Arg}）"
            : info.Description;
    }

    private static int LayerRank(string combo) =>
        combo.Contains("Shift", StringComparison.Ordinal) ? 1 :
        combo.Contains("Ctrl", StringComparison.Ordinal) ? 2 :
        combo.Contains("Alt", StringComparison.Ordinal) ? 3 :
        combo.Contains("Win", StringComparison.Ordinal) ? 4 : 0;

    private static int KeyRank(string combo)
    {
        int idx = combo.LastIndexOf('+');
        string key = idx >= 0 ? combo[(idx + 1)..] : combo;
        int rank = Array.IndexOf(KeyOrder, key);
        return rank < 0 ? 999 : rank;
    }

    /// <summary>按键盘上从左到右、从上到下的顺序（帮助页读起来更自然）。</summary>
    private static readonly string[] KeyOrder =
    {
        "Q", "W", "E", "R", "T", "Y", "U", "I", "O", "P",
        "A", "S", "D", "F", "G", "H", "J", "K", "L", ";", "'",
        "Z", "X", "C", "V", "B", "N", "M", ",", ".", "/",
        "1", "2", "3", "4", "5", "6", "7", "8", "9", "0",
        "`", "-", "=", "[", "]", "\\",
        "Backspace", "Tab", "Enter", "Space", "RAlt",
        "F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10", "F11", "F12",
        "PgUp", "PgDn", "Home", "End", "Up", "Down", "Left", "Right",
    };

    private sealed record HelpItem(string Combo, string Title, string Desc);

    private sealed class HelpGroup
    {
        public HelpGroup(string title, string count, List<HelpItem> items)
        {
            Title = title;
            Count = count;
            Items = items;
        }

        public string Title { get; }
        public string Count { get; }
        public List<HelpItem> Items { get; }
    }
}
