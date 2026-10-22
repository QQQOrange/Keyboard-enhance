using CapsLockPlus.Core.Input;
using CapsLockPlus.Core.Text;
using CapsLockPlus.Core.Windows;

namespace CapsLockPlus.Core.Actions;

/// <summary>动作的展示信息（用于按键映射界面）。</summary>
public sealed record ActionInfo(
    string Id, string Category, string Title, string Description,
    bool NeedsArg = false, string ArgHint = "", string ShortTitle = "",
    bool Hidden = false)
{
    /// <summary>
    /// 键帽上实际显示的文字。
    ///
    /// 有短名用短名；没有就自动截到 5 字 —— 键帽只有约 26px 宽，
    /// 再长的文字必然被裁，不如主动截断保证"能认出前几个字"。
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string KeycapTitle
    {
        get
        {
            string t = string.IsNullOrWhiteSpace(ShortTitle) ? Title : ShortTitle.Trim();
            return t.Length <= 5 ? t : t[..5];
        }
    }
}

/// <summary>
/// 动作目录：动作 ID -> 显示名 + 执行逻辑。
/// 界面的“按键映射”页完全由这个目录生成，所以新增动作只需要改这里一处。
/// </summary>
public sealed class ActionCatalog
{
    private readonly Dictionary<string, Func<string, bool>> _handlers = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ActionInfo> _items = new();

    public IReadOnlyList<ActionInfo> Items => _items;

    /// <summary>
    /// 动作 ID → 中文标题的全静态表。
    ///
    /// 为什么需要一张静态表：触发键的「短按动作」要在列表里显示成人话
    /// （"保留按键原功能"而不是 `keepKey`），而那个显示属性在
    /// `TriggerKey`（Core.Settings）上，拿不到某次运行时构造的
    /// <see cref="ActionCatalog"/> 实例。
    ///
    /// 早先 `TriggerKey.Detail` 里写死了一个 switch，只覆盖
    /// toggleCapsLock / esc / qbar / doNothing 四个 —— 用户把短按改成
    /// 「保留按键原功能」后，列表上显示的却是 "无"，
    /// 让人以为设置没保存成功。现在改成查这张表，新增动作自动就有名字。
    /// </summary>
    private static readonly Dictionary<string, string> StaticTitles =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>按 ID 取动作的中文标题（不存在时返回 null）。</summary>
    public static string? TitleOf(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        return StaticTitles.TryGetValue(id, out var title) ? title : null;
    }

    public ActionInfo? Get(string id) => _items.FirstOrDefault(i => i.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public bool Invoke(string id, string arg)
    {
        if (_handlers.TryGetValue(id, out var handler)) return handler(arg);
        CoreLog.Warn($"未知动作：{id}");
        return false;
    }

    private void Add(string id, string category, string title, string desc, Func<string, bool> handler,
                     bool needsArg = false, string argHint = "", string shortTitle = "",
                     bool hidden = false)
    {
        _items.Add(new ActionInfo(id, category, title, desc, needsArg, argHint, shortTitle, hidden));
        _handlers[id] = handler;
        StaticTitles[id] = title;      // 供 TriggerKey.Detail 等"离线显示"用
    }

    public static ActionCatalog Create(ActionContext ctx)
    {
        var c = new ActionCatalog();

        // ---------------- 基础 ----------------
        c.Add("doNothing", "基础", "不绑定", "该组合键不执行任何动作", _ => true);
        c.Add("toggleCapsLock", "基础", "切换大小写", "等价于按一下 CapsLock", _ => { HotkeyEngineToggle(); return true; }, shortTitle: "大小写");
        c.Add("keepKey", "基础", "保留按键原功能",
            "短按触发键 = 这个键本来的意思。把 F5 设为触发键时，短按就是刷新", _ => true);
        c.Add("esc", "基础", "Esc", "发送 Esc", _ => Send(VK_Esc));
        c.Add("enter", "基础", "回车", "发送回车", _ => Send(VK_Enter));
        c.Add("tab", "基础", "Tab", "发送 Tab", _ => Send(VK_Tab));
        c.Add("space", "基础", "空格", "发送空格", _ => Send(VK_Space));
        c.Add("backspace", "基础", "退格", "发送退格", _ => Send(VK_Back));
        c.Add("delete", "基础", "删除", "发送 Delete", _ => Send(VK_Delete));
        c.Add("openSettings", "基础", "打开设置", "打开 按键增强 设置窗口", _ => { ctx.RequestOpenSettings(); return true; }, shortTitle: "设置");
        c.Add("toggleEnabled", "基础", "启用/暂停", "临时暂停或恢复所有按键增强热键", _ => { ctx.RequestToggleEnabled(); return true; }, shortTitle: "启暂停");

        // ---------------- 光标 ----------------
        c.Add("moveLeft", "光标移动", "左移", "光标左移一个字符（可长按连续）", a => SendRepeat(0x25, a));
        c.Add("moveRight", "光标移动", "右移", "光标右移一个字符", a => SendRepeat(0x27, a));
        c.Add("moveUp", "光标移动", "上移", "光标上移一行", a => SendRepeat(0x26, a));
        c.Add("moveDown", "光标移动", "下移", "光标下移一行", a => SendRepeat(0x28, a));
        c.Add("moveWordLeft", "光标移动", "左移一个单词", "Ctrl+←", a => SendCtrlRepeat(0x25, a), shortTitle: "词←");
        c.Add("moveWordRight", "光标移动", "右移一个单词", "Ctrl+→", a => SendCtrlRepeat(0x27, a), shortTitle: "词→");
        c.Add("home", "光标移动", "行首", "Home", _ => Send(VK_Home));
        c.Add("end", "光标移动", "行尾", "End", _ => Send(VK_End));
        c.Add("moveToPageBeginning", "光标移动", "文档开头", "Ctrl+Home", _ => SendCtrl(0x24));
        c.Add("moveToPageEnd", "光标移动", "文档结尾", "Ctrl+End", _ => SendCtrl(0x23));
        c.Add("pageUp", "光标移动", "上一页", "PgUp", _ => Send(0x21));
        c.Add("pageDown", "光标移动", "下一页", "PgDn", _ => Send(0x22));
        c.Add("pageMoveLineUp", "光标移动", "页面上移一行", "Ctrl+↑，光标不动", a => SendCtrlRepeat(0x26, a), shortTitle: "页↑");
        c.Add("pageMoveLineDown", "光标移动", "页面下移一行", "Ctrl+↓，光标不动", a => SendCtrlRepeat(0x28, a), shortTitle: "页↓");
        c.Add("enterWherever", "光标移动", "在行尾换行", "先 End 再回车（不管光标在哪）", _ => { Send(VK_End); Send(VK_Enter); return true; });

        // ---------------- 选择 ----------------
        c.Add("selectLeft", "选择", "向左选中", "Shift+←", a => SendShiftRepeat(0x25, a), shortTitle: "选←");
        c.Add("selectRight", "选择", "向右选中", "Shift+→", a => SendShiftRepeat(0x27, a), shortTitle: "选→");
        c.Add("selectUp", "选择", "向上选中", "Shift+↑", a => SendShiftRepeat(0x26, a), shortTitle: "选↑");
        c.Add("selectDown", "选择", "向下选中", "Shift+↓", a => SendShiftRepeat(0x28, a), shortTitle: "选↓");
        c.Add("selectWordLeft", "选择", "向左选中单词", "Ctrl+Shift+←", a => SendCtrlShiftRepeat(0x25, a), shortTitle: "选词←");
        c.Add("selectWordRight", "选择", "向右选中单词", "Ctrl+Shift+→", a => SendCtrlShiftRepeat(0x27, a), shortTitle: "选词→");
        c.Add("selectHome", "选择", "选中到行首", "Shift+Home", _ => SendShift(0x24), shortTitle: "选行首");
        c.Add("selectEnd", "选择", "选中到行尾", "Shift+End", _ => SendShift(0x23), shortTitle: "选行尾");
        c.Add("selectToPageBeginning", "选择", "选中到文档开头", "Ctrl+Shift+Home", _ => SendCtrlShift(0x24), shortTitle: "选到首");
        c.Add("selectToPageEnd", "选择", "选中到文档结尾", "Ctrl+Shift+End", _ => SendCtrlShift(0x23), shortTitle: "选到尾");
        c.Add("selectCurrentWord", "选择", "选中当前单词", "Ctrl+← 再 Ctrl+Shift+→", _ => { SendCtrl(0x25); SendCtrlShift(0x27); return true; }, shortTitle: "选中词");
        c.Add("selectCurrentLine", "选择", "选中当前行", "Home 再 Shift+End", _ => { TextActions.SelectLine(); return true; }, shortTitle: "选行");
        c.Add("selectAll", "选择", "全选", "Ctrl+A", _ => SendCtrl(0x41));

        // ---------------- 编辑 ----------------
        c.Add("deleteLine", "编辑", "删除整行", "End + Shift+Home + 退格", _ => { Send(VK_End); SendShift(0x24); Send(VK_Back); return true; }, shortTitle: "删行");
        c.Add("deleteToLineBeginning", "编辑", "删除到行首", "Shift+Home + 退格", _ => { SendShift(0x24); Send(VK_Back); return true; }, shortTitle: "删到首");
        c.Add("deleteToLineEnd", "编辑", "删除到行尾", "Shift+End + 退格", _ => { SendShift(0x23); Send(VK_Back); return true; }, shortTitle: "删到尾");
        c.Add("deleteToPageBeginning", "编辑", "删除到文档开头", "Ctrl+Shift+Home + 退格", _ => { SendCtrlShift(0x24); Send(VK_Back); return true; }, shortTitle: "删到顶");
        c.Add("deleteToPageEnd", "编辑", "删除到文档结尾", "Ctrl+Shift+End + 退格", _ => { SendCtrlShift(0x23); Send(VK_Back); return true; }, shortTitle: "删到底");
        c.Add("deleteWord", "编辑", "删除前一个单词", "Ctrl+Shift+← 再删除", _ => { SendCtrlShift(0x25); Send(VK_Delete); return true; }, shortTitle: "删词");
        c.Add("forwardDeleteWord", "编辑", "删除后一个单词", "Ctrl+Shift+→ 再删除", _ => { SendCtrlShift(0x27); Send(VK_Delete); return true; }, shortTitle: "删词后");
        c.Add("wrapSelection", "编辑", "用符号包裹选区", "例如把选中的文字变成 (文字)，参数为起始符号", a =>
        {
            string open = string.IsNullOrEmpty(a) ? "(" : a;
            string close = open switch { "(" => ")", "[" => "]", "{" => "}", "<" => ">", "\"" => "\"", "'" => "'", _ => open };
            TextActions.WrapSelection(open, close);
            return true;
        }, true, "例如 ( 或 [ 或 \"");

        c.Add("deleteAll", "编辑", "清空内容", "Ctrl+A 再删除", _ => { SendCtrl(0x41); Send(VK_Delete); return true; });
        c.Add("undoRedo", "编辑", "撤销 / 重做", "交替发送 Ctrl+Z 与 Ctrl+Y", _ => ctx.UndoRedo(), shortTitle: "撤销");
        c.Add("upperCase", "编辑", "转大写", "把选中文本转成大写", _ => { TextActions.ChangeCase(TextActions.CaseMode.Upper); return true; });
        c.Add("lowerCase", "编辑", "转小写", "把选中文本转成小写", _ => { TextActions.ChangeCase(TextActions.CaseMode.Lower); return true; });
        c.Add("titleCase", "编辑", "首字母大写", "把选中文本转成词首大写", _ => { TextActions.ChangeCase(TextActions.CaseMode.Title); return true; });

        // ---------------- 剪贴板 ----------------
        c.Add("copy1", "剪贴板", "复制到剪贴板 1", "按下触发键 + C：复制到独立剪贴板 1", _ => ctx.Clipboard.Copy(1), shortTitle: "复制1");
        c.Add("cut1", "剪贴板", "剪切到剪贴板 1", "按下触发键 + X", _ => ctx.Clipboard.Cut(1), shortTitle: "剪到1");
        c.Add("paste1", "剪贴板", "粘贴剪贴板 1", "按下触发键 + V", _ => ctx.Clipboard.Paste(1), shortTitle: "粘1");
        c.Add("copy2", "剪贴板", "复制到剪贴板 2", "按下触发键 + LAlt + C：复制到独立剪贴板 2", _ => ctx.Clipboard.Copy(2), shortTitle: "复制2");
        c.Add("cut2", "剪贴板", "剪切到剪贴板 2", "按下触发键 + LAlt + X", _ => ctx.Clipboard.Cut(2), shortTitle: "剪到2");
        c.Add("paste2", "剪贴板", "粘贴剪贴板 2", "按下触发键 + LAlt + V", _ => ctx.Clipboard.Paste(2), shortTitle: "粘2");
        c.Add("pasteSystem", "剪贴板", "粘贴系统剪贴板", "恢复系统剪贴板后粘贴", _ => ctx.Clipboard.PasteSystem(), shortTitle: "粘系统");
        c.Add("toggleClipboard", "剪贴板", "开关独立剪贴板", "开/关 按键增强剪贴板功能", _ => ctx.Clipboard.ToggleEnabled(), shortTitle: "剪贴板");
        c.Add("clipboardHistory", "剪贴板", "剪贴板历史", "查看并粘贴最近的复制内容", _ => { ctx.RequestOpenClipboardHistory(); return true; });

        // ---------------- 面板 / 工具 ----------------
        c.Add("qbar", "面板", "Qbar 快速启动", "打开 Qbar：搜索、启动程序、打开网址、计算", _ => { ctx.RequestQbar(); return true; }, shortTitle: "Qbar");

        // ---- 直接用指定搜索引擎开 Qbar ----
        //
        // 每个引擎一个动作（qbar:<关键字>），在「按键映射」页像普通动作一样
        // 绑到任意层的任意按键。按下后弹出的 Qbar **默认就用这个引擎搜索**，
        // 省得每次再在面板里选一次。
        //
        // ⚠ 只注册**常用引擎**而不是全部 300 个：塞进功能下拉会把列表撑爆，
        // 正常键盘也按不出 300 个组合键。别的引擎仍可在面板里手动输入关键字用。
        string[] quickEngines =
        {
            "bd", "zh", "bili", "go", "bi", "tb", "jd", "gh",
            "wx", "db", "xhs",
        };
        foreach (var e in ctx.Settings().Search.Where(x =>
                     !x.Key.Equals("default", StringComparison.OrdinalIgnoreCase) &&
                     quickEngines.Contains(x.Key, StringComparer.OrdinalIgnoreCase)))
        {
            string key = e.Key;
            string name = string.IsNullOrWhiteSpace(e.Comment) ? key : e.Comment;
            c.Add("qbar:" + key, "Qbar 搜索",
                $"Qbar：{name}",
                $"打开 Qbar 并直接用「{name}」搜索",
                _ => { ctx.RequestQbar(key); return true; });
        }
        c.Add("mathBoard", "面板", "计算器", "打开带历史记录的计算器", _ => { ctx.RequestMathBoard(); return true; });
        c.Add("tabScript", "面板", "Tab 计算 / 热字串", "把光标前的算式直接算出结果，或展开热字串", _ => ctx.RunTabScript(), shortTitle: "计算板");
        c.Add("translate", "面板", "翻译选中文本", "调用翻译服务并显示结果", _ => { ctx.RequestTranslate(); return true; }, shortTitle: "翻译");
        c.Add("openDocs", "面板", "使用帮助", "打开内置帮助", _ => { ctx.RequestHelp(); return true; });
        c.Add("reload", "面板", "重新加载配置", "重新读取设置并重装钩子", _ => { ctx.RequestReload(); return true; }, shortTitle: "重载");

        // ---------------- 直接搜索 ----------------
        //
        // 用途：在任意程序里选中一段文字，按这个键 → 直接用指定引擎
        // 在**默认浏览器**里搜索那段文字，不经过 Qbar 面板。
        //
        // 和「Qbar 搜索」分类的区别：
        //   · Qbar 搜索 = 打开面板，预先选好引擎，用户再输入关键词
        //   · 直接搜索 = 拿**当前选中的文本**直接去搜索，不弹面板
        /* 注册**全部**搜索引擎（不只是常用那几个）。
         *
         * 「直接搜索」下拉里有搜索框可以过滤，所以即使有 300 多项也能快速找到 ——
         * 不像「Qbar 搜索」那样受下拉长度限制。用户要求"把 Qbar 面板里
         * 所有引擎都搬过来"。 */
        foreach (var e in ctx.Settings().Search.Where(x =>
                     !x.Key.Equals("default", StringComparison.OrdinalIgnoreCase) &&
                     !string.IsNullOrWhiteSpace(x.Value)))
        {
            string key = e.Key;
            string name = string.IsNullOrWhiteSpace(e.Comment) ? key : e.Comment;
            c.Add("search:" + key, "直接搜索",
                $"搜索：{name}",
                $"在默认浏览器里用「{name}」搜索当前选中的文字",
                a => ctx.SearchSelection(key, a),
                needsArg: false, argHint: "",
                shortTitle: name.Length <= 4 ? name : name[..4]);
        }

        // ---------------- 窗口 ----------------
        c.Add("winPin", "窗口", "窗口置顶 / 取消", "让当前窗口永远在最前面", _ => ctx.Windows.ToggleTopmost(), shortTitle: "置顶");
        c.Add("winTransparent", "窗口", "窗口透明度切换", "在几档透明度之间循环", _ => ctx.Windows.CycleOpacity(), shortTitle: "透明");
        c.Add("winMinimize", "窗口", "最小化窗口", "最小化当前窗口", _ => ctx.Windows.Minimize());
        c.Add("winMaximize", "窗口", "最大化 / 还原", "切换最大化状态", _ => ctx.Windows.ToggleMaximize(), shortTitle: "最大化");
        c.Add("winClose", "窗口", "关闭窗口", "关闭当前窗口", _ => ctx.Windows.Close());
        c.Add("winCenter", "窗口", "窗口居中", "把窗口移到屏幕中央", _ => ctx.Windows.Center());
        c.Add("winSnapLeft", "窗口", "贴左半屏", "Win11 风格贴边", _ => ctx.Windows.Snap(SnapSide.Left));
        c.Add("winSnapRight", "窗口", "贴右半屏", "Win11 风格贴边", _ => ctx.Windows.Snap(SnapSide.Right));
        c.Add("winSnapTop", "窗口", "贴上半屏", "顶部半屏", _ => ctx.Windows.Snap(SnapSide.Top));
        c.Add("winSnapBottom", "窗口", "贴下半屏", "底部半屏", _ => ctx.Windows.Snap(SnapSide.Bottom));
        c.Add("winSnapTopLeft", "窗口", "贴左上角", "四分之一屏", _ => ctx.Windows.Snap(SnapSide.TopLeft));
        c.Add("winSnapTopRight", "窗口", "贴右上角", "四分之一屏", _ => ctx.Windows.Snap(SnapSide.TopRight));
        c.Add("winSnapBottomLeft", "窗口", "贴左下角", "四分之一屏", _ => ctx.Windows.Snap(SnapSide.BottomLeft));
        c.Add("winSnapBottomRight", "窗口", "贴右下角", "四分之一屏", _ => ctx.Windows.Snap(SnapSide.BottomRight));
        c.Add("winSendToBottom", "窗口", "窗口置底", "把窗口压到最下面", _ => ctx.Windows.SendToBottom());
        c.Add("winNextApp", "窗口", "同程序下一个窗口", "在当前程序的窗口间循环", _ => ctx.Windows.CycleSameApp(false), shortTitle: "下窗口");
        c.Add("winPrevApp", "窗口", "同程序上一个窗口", "反向循环", _ => ctx.Windows.CycleSameApp(true), shortTitle: "上窗口");
        c.Add("winLeft", "窗口", "激活左边的窗口", "按屏幕位置找左边最近的窗口", _ => ctx.Windows.ActivateDirectional("l"), shortTitle: "左窗");
        c.Add("winRight", "窗口", "激活右边的窗口", "按屏幕位置找右边最近的窗口", _ => ctx.Windows.ActivateDirectional("r"), shortTitle: "右窗");
        c.Add("winUp", "窗口", "激活上边的窗口", "按屏幕位置找上边最近的窗口", _ => ctx.Windows.ActivateDirectional("u"), shortTitle: "上窗");
        c.Add("winDown", "窗口", "激活下边的窗口", "按屏幕位置找下边最近的窗口", _ => ctx.Windows.ActivateDirectional("d"), shortTitle: "下窗");

        // ---------------- 鼠标 ----------------
        c.Add("mouseLeft", "鼠标", "鼠标左移", "可长按加速", _ => ctx.Mouse.Move(-1, 0));
        c.Add("mouseRight", "鼠标", "鼠标右移", "可长按加速", _ => ctx.Mouse.Move(1, 0));
        c.Add("mouseUp", "鼠标", "鼠标上移", "可长按加速", _ => ctx.Mouse.Move(0, -1));
        c.Add("mouseDown", "鼠标", "鼠标下移", "可长按加速", _ => ctx.Mouse.Move(0, 1));
        c.Add("clickLeft", "鼠标", "鼠标左键", "点击左键", _ => { InputSender.Click(false); return true; });
        c.Add("clickRight", "鼠标", "鼠标右键", "点击右键", _ => { InputSender.Click(true); return true; });
        c.Add("wheelUp", "鼠标", "滚轮上滚", "向上滚动", a => { InputSender.Wheel(ParseInt(a, 3)); return true; });
        c.Add("wheelDown", "鼠标", "滚轮下滚", "向下滚动", a => { InputSender.Wheel(-ParseInt(a, 3)); return true; });
        c.Add("mouseSpeedUp", "鼠标", "鼠标速度 +", "进入层并按住 LAlt 时的鼠标速度加一", _ => ctx.ChangeMouseSpeed(1), shortTitle: "鼠速+");
        c.Add("mouseSpeedDown", "鼠标", "鼠标速度 -", "鼠标速度减一", _ => ctx.ChangeMouseSpeed(-1), shortTitle: "鼠速-");

        // ---------------- 系统功能 ----------------
        //
        // 一整组"按一下就生效"的系统级操作：媒体控制、音量、亮度、
        // 常用系统工具。集中在一个分类里，用户不必在「系统」里翻。
        c.Add("mediaPrev", "系统功能", "上一首", "媒体键：跳到上一首", _ => Send(0xB1));
        c.Add("mediaNext", "系统功能", "下一首", "媒体键：跳到下一首", _ => Send(0xB0));
        c.Add("mediaPlayPause", "系统功能", "播放 / 暂停", "媒体键：播放或暂停", _ => Send(0xB3), shortTitle: "播放");
        c.Add("mediaStop", "系统功能", "停止播放", "媒体键：停止", _ => Send(0xB2));
        /* 音量：直接调 Core Audio（IAudioEndpointVolume），不再模拟媒体键。
         *
         * 旧的 `SendRepeat(0xAF, …)` 依赖系统 shell 分发 VK_VOLUME_UP，
         * 在键盘过滤驱动 / 远程会话 / 前台程序抢媒体键的机器上会静默失效，
         * 用户看到的现象就是"按了完全没反应"。现在走音频引擎，成功与否当场可知，
         * 还能回读真实音量显示百分比。参数可改步进（默认 5%）。 */
        c.Add("volumeUp", "系统功能", "音量 +",
            "把系统音量提高 5%（参数可改步进，例如填 10）",
            a => ctx.ChangeVolume(VolumeStep(a)), shortTitle: "音量+");
        c.Add("volumeDown", "系统功能", "音量 -",
            "把系统音量降低 5%（参数可改步进）",
            a => ctx.ChangeVolume(-VolumeStep(a)), shortTitle: "音量-");
        c.Add("volumeMute", "系统功能", "静音", "静音 / 取消静音（切换）",
            _ => ctx.ToggleMute(), shortTitle: "静音");

        c.Add("openCalculator", "系统功能", "打开计算器",
            "打开 Windows 自带的计算器",
            _ => { ctx.RunProgram("calc.exe"); return true; }, shortTitle: "计算器");

        /* 亮度：WMI（内置屏）→ DDC/CI（外接屏）。两条都不通时给明确提示 ——
         * 用户按了没反应会以为按键坏了。 */
        c.Add("brightnessUp", "系统功能", "亮度 +",
            "屏幕亮度提高 10%（参数可改步进；外接屏需开启 DDC/CI）",
            a => ctx.ChangeBrightness(10 * ParseInt(a, 1)), shortTitle: "亮+");
        c.Add("brightnessDown", "系统功能", "亮度 -",
            "屏幕亮度降低 10%（参数可改步进；外接屏需开启 DDC/CI）",
            a => ctx.ChangeBrightness(-10 * ParseInt(a, 1)), shortTitle: "亮-");

        c.Add("openExplorer", "系统功能", "打开文件资源管理器",
            "打开「此电脑」", _ => { ctx.RunProgram("explorer.exe"); return true; }, shortTitle: "资源");
        c.Add("openTaskManager", "系统功能", "打开任务管理器",
            "打开任务管理器", _ => { ctx.RunProgram("taskmgr.exe"); return true; }, shortTitle: "任务");
        c.Add("lockScreen", "系统功能", "锁定屏幕", "Win+L", _ => { InputSender.Chord(0x5B, 0x4C); return true; });
        c.Add("screenshot", "系统功能", "截图", "调用系统截图工具", _ => ctx.Screenshot());

        // ---------------- 旧动作（不再显示，仅保留执行兼容） ----------------
        //
        // 「打开网址」「运行程序」从界面上撤掉了：它们的用途已经被
        // 「直达链接」分类（预填网址/文件夹/文件）和「智能直达」分类
        // （选中什么就开什么）完整覆盖 —— 三个入口指向同一件事，
        // 用户只会纠结"我该在哪个分类里填地址"。
        //
        // 但**不能直接删掉**：老配置里可能已经绑了这两个动作，
        // 删掉后按键会变成"未知动作"，按下去毫无反应。
        // 所以保留执行能力、标记 Hidden，界面上不再出现。
        c.Add("openUrl", "系统", "打开网址", "打开参数里的网址",
            a => { if (!string.IsNullOrWhiteSpace(a)) ctx.OpenUrl(a); return true; },
            hidden: true);
        c.Add("runProgram", "系统", "运行程序", "运行参数里的程序或命令",
            a => { if (!string.IsNullOrWhiteSpace(a)) ctx.RunProgram(a); return true; },
            hidden: true);

        // ---------------- 智能直达 ----------------
        //
        // 这一类只有**一个**动作：目标来自当前选中的文字，按下才知道。
        //
        // 早先这里叫「系统」，下面并列着「打开网址」「运行程序」「直达链接」三项。
        // 但前两项都要预先填好固定地址/程序，和「直达链接」分类里
        // 预填的直达网址 / 直达文件夹 / 直达文件**语义完全重复** ——
        // 用户看到两个地方都能填网址，不知道该用哪个。
        //
        // 现在是：预填的归「直达链接」分类（那里有专用面板、能写备注），
        // 选中的归「智能直达」分类（这里一个动作就够）。
        c.Add("smartOpen", "智能直达", "智能直达",
            "先选中一段文字再按这个键：是网址就用浏览器打开，是文件夹就用资源管理器打开，是文件就用默认程序打开",
            _ => ctx.SmartOpen(), shortTitle: "智能直达");

        // ---------------- 直达链接 ----------------
        //
        // 这一类下面有三个动作：直达网址 / 直达文件夹 / 直达文件。
        // 共同点：目标**预先填好**，按键时照单打开，不需要选区。
        // 选了「直达链接」分类后，界面会换成"类型 + 目标 + 备注"的专用面板
        // （见 KeysView），备注还能显示在键帽上。
        //
        // 与「系统 → 直达链接」的区别：
        //   预填 = 常用地址一键直达（每天点的后台、常开的工作目录）
        //   智能 = 眼前临时出现的东西，选中即开，不用配置
        //
        // 为什么单独建动作而不是复用「系统 → 打开网址」：
        // 界面要根据分类切换到专用面板，复用的话用户在「系统」分类里
        // 选「打开网址」就看不到备注框，也不会出现文件夹/文件那一套。
        c.Add("webPage", "直达链接", "直达网址",
            "用默认浏览器打开这个网址；可以加备注，备注会显示在键帽上",
            a => { if (!string.IsNullOrWhiteSpace(a)) ctx.OpenUrl(a); return true; },
            needsArg: true);

        c.Add("openFolder", "直达链接", "直达文件夹",
            "在文件资源管理器里打开这个文件夹；可以加备注，备注会显示在键帽上",
            a => { if (!string.IsNullOrWhiteSpace(a)) ctx.OpenFolder(a); return true; },
            needsArg: true);

        c.Add("openFile", "直达链接", "直达文件",
            "用系统默认程序打开这个文件（.docx 进 Word、.mp4 进播放器，跟双击一样）；可以加备注，备注会显示在键帽上",
            a => { if (!string.IsNullOrWhiteSpace(a)) ctx.OpenFile(a); return true; },
            needsArg: true);

        // ---------------- 自定义按键 ----------------
        //
        // 这一类只有一个动作，参数全在 Arg 里的 JSON（见 CustomKeySpec）。
        //
        // 它解决的是"把某个键转发给某个常驻程序"这件事：
        // 例如把 F13 绑给微信 —— 按下时先确认微信在跑（没跑就先拉起来），
        // 再把 F13 发过去。以前这个能力散在「窗口绑定」里，
        // 而窗口绑定只管"切过去"，管不了"切过去再按一下某个键"。
        //
        // 为什么不做成参数框里的纯文本：一个动作要记住"键 + 应用 + 两个开关"，
        // 纯文本既不好解析也不好给用户编辑，所以给这一类配了专用面板（见 KeysView），
        // Arg 只是它的序列化形式。
        c.Add("customKey", "自定义按键", "发送按键到应用",
            "先确认绑定的应用在运行（没运行就先启动），再把设定的键发给它",
            a =>
            {
                var r = CustomKeyRunner.Run(CustomKeySpec.FromArg(a));
                if (!string.IsNullOrWhiteSpace(r.Message)) ctx.Notify(r.Message);
                return r.Ok;
            },
            needsArg: false, shortTitle: "自定义");

        // ---------------- 输入文本 ----------------
        //
        // 把一段预设文字直接打到**当前焦点所在的输入框**里。
        // 适合邮箱、地址、常用签名这类反复要敲的内容。
        //
        // 逐字符用 Unicode 方式发送（不碰剪贴板），所以：
        //   · 中文、emoji 都能发
        //   · 不会覆盖用户剪贴板里的内容
        //   · 走 IME 路径，输入法正常
        c.Add("typeText", "输入文本", "输入一段文字",
            "把这段文字直接输入到当前光标处（支持中文与 emoji）",
            a => { if (!string.IsNullOrEmpty(a)) ctx.TypeText(a); return true; },
            needsArg: true);

        // ---------------- 窗口绑定 ----------------
        //
        // 只有一个动作。用户在「分类」里选「窗口绑定」后，界面**不再显示功能下拉**，
        // 而是直接给出两个按钮（点选窗口 / 选择程序文件）—— 因为"绑哪个窗口"
        // 是去抓来的，不是从列表里选出来的。
        //
        // 旧的 10 个固定槽位动作已彻底移除：它们只会让下拉里多出 20 个
        // 「激活窗口槽位 N / 绑定到窗口槽位 N」的噪声，与现在的用法重复。
        c.Add("winBind", "窗口绑定", "切换窗口",
            "选定一个程序后：按一下切过去，再按一次最小化；没开就自动启动",
            a => ctx.WindowBindings.ActivateExe(a, "winBind:" + a),
            needsArg: true);

        // ---------------- 宏 ----------------
        // 宏动作 ID 形如 macro:<宏ID>，参数可覆盖重复次数。
        // 每个宏一个动作，这样就能像普通动作一样绑到任意层的任意按键上。
        foreach (var macro in ctx.Settings().Macros)
        {
            string id = macro.Id;
            string name = macro.DisplayName;
            int estMs = macro.EstimatedMs;
            c.Add("macro:" + id, "宏", $"播放宏：{name}",
                $"共 {macro.Steps.Count} 步，约 {estMs}ms", a =>
                {
                    ctx.PlayMacro(id, a);
                    return true;
                });
        }
        c.Add("macroManager", "宏", "打开宏管理", "录制 / 编辑 / 绑定宏", _ => { ctx.RequestOpenMacros(); return true; });

        return c;
    }

    // ---------------- 小工具 ----------------
    private const int VK_Back = 0x08, VK_Tab = 0x09, VK_Enter = 0x0D, VK_Esc = 0x1B, VK_Space = 0x20,
                      VK_Home = 0x24, VK_End = 0x23, VK_Delete = 0x2E;

    private static bool Send(int vk) { InputSender.Tap(vk); return true; }
    private static bool SendCtrl(int vk) { InputSender.Ctrl(vk); return true; }
    private static bool SendShift(int vk) { InputSender.Shift(vk); return true; }
    private static bool SendCtrlShift(int vk) { InputSender.CtrlShift(vk); return true; }
    private static bool SendRepeat(int vk, string arg) { InputSender.Tap(vk, ParseInt(arg, 1)); return true; }
    private static bool SendCtrlRepeat(int vk, string arg) { for (int i = 0; i < ParseInt(arg, 1); i++) InputSender.Ctrl(vk); return true; }
    private static bool SendShiftRepeat(int vk, string arg) { for (int i = 0; i < ParseInt(arg, 1); i++) InputSender.Shift(vk); return true; }
    private static bool SendCtrlShiftRepeat(int vk, string arg) { for (int i = 0; i < ParseInt(arg, 1); i++) InputSender.CtrlShift(vk); return true; }

    /// <summary>音量步进：默认 5%，参数可以覆盖（允许 1~100）。</summary>
    private static int VolumeStep(string? arg)
        => int.TryParse(arg, out var v) ? Math.Clamp(v, 1, 100) : Core.Windows.VolumeService.DefaultStep;

    internal static int ParseInt(string? s, int fallback)
        => int.TryParse(s, out var v) ? Math.Clamp(v, 1, 200) : fallback;

    private static void HotkeyEngineToggle() => HotkeyEngine.ToggleCapsLock();
}
