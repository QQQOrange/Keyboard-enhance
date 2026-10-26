#!/usr/bin/env python3
"""界面文本本地化工具链（CapsLockPlus / 按键增强）。

用法
----
    python tools/i18n.py extract            # 扫描源码，列出所有待翻译原文
    python tools/i18n.py check              # 查语言包缺哪些、多了哪些、占位符对不对
    python tools/i18n.py apply-xaml         # 把 XAML 里的中文属性值换成 {loc:T '原文'}
    python tools/i18n.py apply-cs           # 把指定 C# 文件里的中文字面量包成 Loc.T("…")
    python tools/i18n.py dump en-US         # 导出译稿 tools/lang/en-US.tsv（已有译文自动带上）
    python tools/i18n.py build en-US        # 译稿 → Assets/Lang/en-US.json（并校验占位符）
    python tools/i18n.py merge BATCH.json   # 把一批译文并进 en-US.json

设计要点
--------
· **原文即键**：语言包是 {"中文原文": "译文"}，查不到就显示原文，永远不会露出 key。
· XAML 侧用 {loc:T '原文'}，由 TExtension 返回绑定到 LocToken 的 Binding，切语言实时刷新。
· C# 侧用 Loc.T("原文")；带参数用 Loc.F("… {0} …", arg)。
· apply-* 都是幂等的：已经包过的不会再包一次，可以反复跑。
· 译稿（tools/lang/*.tsv）是**给人改的**，json 是**给程序读的**。
  dump → 改 tsv → build，三行命令就能加一门新语言，不用碰代码。
"""

from __future__ import annotations

import argparse
import html
import json
import re
import sys
from collections import OrderedDict
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "src" / "CapsLockPlus"
# 本地化内核已经拆成独立程序集，它自己也有 Loc.T("…") 的键（如列表连接符「、」），
# 扫描时必须一起看 —— 否则 check 会把它们当成"语言包里多出来的条目"。
LOC_SRC = ROOT / "src" / "CapsLockPlus.Localization"
LANG_DIR = ROOT / "src" / "CapsLockPlus.Localization" / "Assets" / "Lang"

# 故意不译的原文：自检用来验证"中英混排/查不到就回退原文"的样本，
# 译文一填这些断言就没有意义了；「简体中文」是语言自身的名字，永远显示原文。
INTENTIONAL_SAME = {"汉字Agqy", "Ag中qg", "简体中文"}

# 语言包里合法存在、但静态扫描看不见的键：
#   「、」—— Loc.Join 的列表连接符。它是 U+3001（顿号），不在 CJK 的 4E00-9FFF 里，
#            扫描器抓不到，于是会被误报成"语言包里多出来的条目"。
SCAN_BLIND = {"、"}
EN_PACK = LANG_DIR / "en-US.json"
ZH_PACK = LANG_DIR / "zh-CN.json"

CJK = re.compile(r"[\u4e00-\u9fff]")

# 需要本地化的 XAML 属性（含无障碍读屏文案 —— 它们同样会被人"看到/听到"）
XAML_ATTRS = (
    "Text", "Content", "Header", "ToolTip", "Title",
    "Watermark", "PlaceholderText", "Description", "Tag",
    "AutomationProperties.Name", "AutomationProperties.HelpText",
)

# 参与 C# 自动替换的文件（相对 src/CapsLockPlus）。
#
# 只放"中文只用于**界面文案**"的文件 —— 自检、日志、协议常量一律不碰。
#
# 为什么 Core/ 一个都不在里面：
#   · ActionCatalog   —— 300 多条标题/说明，已经在 Add() 这个显示出口统一翻译，
#                        逐条包 Loc.T 只会把那份清单变成噪音；
#   · AppHost.Notify / AppDialog / Toast / OSD —— 同理，在出口翻；
#   · 其余 Core 里的中文大多是日志与异常消息（CoreLog.* 被 CS_SKIP_LINE 跳过），
#     真正给用户看的已经通过上面几个出口覆盖。
CS_TARGETS = [
    "UI/Views/SettingsView.xaml.cs",
    "UI/Views/BackupView.xaml.cs",
    "UI/Views/MacrosView.xaml.cs",
    "UI/Views/AboutView.xaml.cs",
    "UI/Views/WindowsView.xaml.cs",
    "UI/Views/GuideView.xaml.cs",
    "UI/Views/HelpView.xaml.cs",
    "UI/Views/KeyTestView.xaml.cs",
    "UI/Views/ClipboardView.xaml.cs",
    "UI/Views/LayersView.xaml.cs",
    "UI/Views/QbarSettingsView.xaml.cs",
    "UI/Views/KeysView.xaml.cs",
    "UI/Views/KeysView.Inputs.cs",
    "UI/Views/KeysView.Layers.cs",
    "UI/Views/KeysView.Keyboard.cs",
    "UI/Views/KeysView.CustomKey.cs",
    "UI/Views/KeysView.Panels.cs",
    "UI/Windows/AboutWindow.xaml.cs",
    "UI/Windows/ClipboardHistoryWindow.xaml.cs",
    "UI/Windows/KeyCaptureWindow.xaml.cs",
    "UI/Windows/MainWindow.xaml.cs",
    "UI/Windows/MathBoardWindow.xaml.cs",
    "UI/Windows/NewPasswordWindow.xaml.cs",
    "UI/Windows/OnboardingWindow.xaml.cs",
    "UI/Windows/OsdWindow.xaml.cs",
    "UI/Windows/PasswordPromptWindow.xaml.cs",
    "UI/Windows/QbarWindow.xaml.cs",
    "UI/Windows/RecordingHudWindow.xaml.cs",
    "UI/Windows/RestoreWindow.xaml.cs",
    "UI/Windows/StartupPasswordWindow.xaml.cs",
    "UI/Windows/SubLayerEditorWindow.xaml.cs",
    "UI/Windows/UpdatePromptWindow.xaml.cs",
    "UI/Windows/WindowPickerWindow.xaml.cs",
]

# 这些行上的中文字面量不自动包（语义是"值"而不是"文案"）
CS_SKIP_LINE = re.compile(
    # 注意：**不要**在这里写 `Path\.` —— 它会把整行都跳过，
    # 连 `Loc.F("已找到：{0}（进程名 {1}）", Path.GetFileName(...))` 这种
    # 真界面文案一起漏掉（这条就是这么漏的：查漏说"一条不缺"，界面却是中文）。
    # 同理日志只认**方法调用**（`CoreLog.Warn(`），不能写裸 `Log\.` ——
    # `CoreLog.LogPath` 会把整行吞掉（`设置文件：… 日志：…` 就是这么漏的）。
    r"(CoreLog\.(?:Warn|Info|Error|Swallow|Write)\(|\bLog\.(?:Warn|Info|Error|Debug|Trace)\("
    r"|Debug\.|Trace\.|ErrorSink\?*\.|case\s|==\s*\"|!=\s*\"|\.Contains\(|"
    r"StartsWith\(|EndsWith\(|Equals\(|nameof\(|Regex|\.id\b)"
)


# 诊断/日志专用文件：里面的中文只进日志、诊断报告和 issue 附件，
# 不进语言包 —— 否则查漏报告里永远挂着几百条"其实不该翻"的条目。
DIAG_FILES = {
    "RenderTools.cs",          # 离屏渲染 + 文案审计工具本身
    "KeyDiagnostics.cs",       # 测试页「复制诊断信息」的正文
    "HardwareProbe.cs",        # 音量/亮度命令行探针
    "OsdBandSurface.cs",       # band 回退原因（只进日志）
    "OsdWindow.Render.cs",     # 渲染几何调试输出
    "OsdWindow.Presentation.cs",
    "OsdWindow.Band.cs",
}


def walk_source():
    for p in sorted(SRC.rglob("*")):
        s = str(p).replace("\\", "/")
        if "/obj/" in s or "/bin/" in s:
            continue
        yield p


def strip_xaml_comments(s: str) -> str:
    return re.sub(r"<!--.*?-->", "", s, flags=re.S)


def xaml_strings() -> "OrderedDict[str, list[str]]":
    """XAML 里需要本地化的属性值 → 出现位置列表。

    返回的**键是解码后的真实文本**（&amp;#x0a; → 换行、&amp;amp; → &）——
    运行期 Loc.T 拿到的是 XML 解码之后的字符串，键必须跟它一模一样，
    否则语言包里永远查不到、界面永远显示中文。
    """
    out: "OrderedDict[str, list[str]]" = OrderedDict()
    for p in walk_source():
        if p.suffix.lower() != ".xaml":
            continue
        text = strip_xaml_comments(p.read_text(encoding="utf-8-sig"))

        # 已经改成 {loc:T '原文'} 的（迁移之后就是这一种，占绝大多数）
        for m in re.finditer(r"\{loc:T '((?:[^']|&apos;)*)'\}", text):
            out.setdefault(xaml_markup_unescape(html.unescape(m.group(1))), []).append(
                f"{p.relative_to(ROOT)}:{attr_hint(text, m.start())}")

        # 还没迁移的裸中文属性
        for m in re.finditer(r'([\w.:]+)="([^"]*)"', text):
            attr, val = m.group(1), m.group(2)
            if attr not in XAML_ATTRS:
                continue
            if not CJK.search(val):
                continue
            if val.lstrip().startswith("{"):        # 已经是绑定/标记扩展
                continue
            out.setdefault(html.unescape(val), []).append(f"{p.relative_to(ROOT)}:{attr}")
    return out


def attr_hint(text: str, pos: int) -> str:
    """从标记扩展的位置回溯出它属于哪个属性名（只用于报告里的定位提示）。"""
    line_start = text.rfind("\n", 0, pos) + 1
    m = re.findall(r"([\w.:]+)=", text[line_start:pos])
    return m[-1] if m else "xaml"


def code_unescape(s: str) -> str:
    """把 C# 字面量里的转义还原成**运行期真实字符串**。

    为什么必须做：源码里写的是 `"第一行\\n第二行"`（反斜杠 + n 两个字符），
    但运行期 Loc.T 收到的是**真实换行**。键要是按源码文本算的，
    语言包里就永远查不到这条，界面照旧显示中文 —— 这类错误不会报错，只会静默失效。

    只处理最常见的几种转义；顺序有讲究：先把 `\\\\` 换成占位符，
    否则后面的 `\\n` 替换会把 `\\\\n`（反斜杠 + n）误当成换行。
    """
    if "\\" not in s:
        return s
    out = (s.replace("\\\\", "\x00")
            .replace("\\n", "\n")
            .replace("\\r", "\r")
            .replace("\\t", "\t")
            .replace('\\"', '"')
            .replace("\\'", "'")
            .replace("\\0", "\x00"))
    return out.replace("\x00", "\\")


def scan_literals(line: str):
    """逐个字面量扫一行，返回 [(是否插值, 原始内容)]。

    **必须逐字扫描**，不能用 `"[^"]*"` 那种正则：一行里同时出现
    `$"…{a}…"` 和 `Loc.F("…")` 时，正则会把 `$"` 后面那个引号当普通开头，
    再把中间那段 ` : Loc.F(` 当成一个"字面量"，于是紧随其后的真字面量
    被整段跳过 —— 表现为"英文包里永远差一条，但 check 说一条不缺"。
    """
    out = []
    i, n = 0, len(line)
    while i < n:
        c = line[i]
        if c in "$@":
            j = i
            prefix = ""
            while j < n and line[j] in "$@":
                prefix += line[j]
                j += 1
            if j < n and line[j] == '"':
                body, end = read_literal(line, j, verbatim="@" in prefix, interp="$" in prefix)
                if body is None:
                    break
                out.append(("$" in prefix, body))
                i = end + 1
                continue
            i = j
            continue
        if c == '"':
            body, end = read_literal(line, i, verbatim=False)
            if body is None:
                break
            out.append((False, body))
            i = end + 1
            continue
        i += 1
    return out


def read_literal(line: str, quote_at: int, verbatim: bool, interp: bool = False):
    """从引号位置读到一个字符串的结尾，返回 (内容, 结束引号下标)。

    插值串（interp=True）要**跳过 `{…}` 里的东西**：`$"{(a ? "甲" : b)} 已在前台"`
    这种写法里，花括号内的引号是内层字符串的引号，不是本字符串的结尾。
    不认这一点就会把后半截 `" 已在前台"` 当成一条独立原文，语言包里多出一条垃圾。
    """
    j, n = quote_at + 1, len(line)
    body = []
    depth = 0
    while j < n:
        c = line[j]
        if interp:
            if depth == 0 and c == "{":
                if line[j:j + 2] == "{{":
                    body.append("{{")
                    j += 2
                    continue
                depth = 1
                body.append(c)
                j += 1
                continue
            if depth > 0:
                if c == "{":
                    depth += 1
                elif c == "}":
                    depth -= 1
                body.append(c)
                j += 1
                continue
        if verbatim:
            if c == '"':
                if j + 1 < n and line[j + 1] == '"':
                    body.append('"')
                    j += 2
                    continue
                return "".join(body), j
            body.append(c)
            j += 1
            continue
        if c == "\\":
            body.append(line[j:j + 2])
            j += 2
            continue
        if c == '"':
            return "".join(body), j
        body.append(c)
        j += 1
    return None, -1


def strip_comments(src: str) -> str:
    """把 C# 注释替换成空格（**保留换行**，行号不变）。

    为什么必须去注释：本项目的中文注释里到处是带引号的词（"干跑"、"欠账"），
    扫描器会把它们当成"界面原文"，于是查漏报告里全是根本不存在的条目 ——
    报告一旦不可信，就等于没有查漏。

    URL 里的 `//`、字符串里的 `/*` 都不受影响：这里区分了
    代码 / 行注释 / 块注释 / 普通字符串 / 逐字字符串 五种状态。
    """
    out = []
    i, n = 0, len(src)
    state = "code"
    while i < n:
        c = src[i]
        two = src[i:i + 2]

        if state == "code":
            if two == "//":
                state = "line"
                out.append("  ")
                i += 2
                continue
            if two == "/*":
                state = "block"
                out.append("  ")
                i += 2
                continue
            if c == "@" and src[i + 1:i + 2] == '"':
                state = "vstr"
                out.append('@"')
                i += 2
                continue
            if src[i:i + 3] == '"""':
                # C# 11 原始字符串：可以跨行、里面还有什么引号都不算结尾
                state = "raw"
                out.append('"""')
                i += 3
                continue
            if c == '"':
                state = "str"
                out.append('"')
                i += 1
                continue
            if c == "'":
                # 字符字面量：`'"'` 这种最容易把状态机带歪（当成字符串开头），
                # 结果后面几行注释全被当成代码，注释里的词就混进待译清单。
                state = "chr"
                out.append("'")
                i += 1
                continue
            out.append(c)
            i += 1
            continue

        if state == "line":
            if c == "\n":
                state = "code"
                out.append("\n")
                i += 1
                continue
            out.append(" ")
            i += 1
            continue

        if state == "block":
            if two == "*/":
                state = "code"
                out.append("  ")
                i += 2
                continue
            out.append("\n" if c == "\n" else " ")
            i += 1
            continue

        if state == "raw":
            if src[i:i + 3] == '"""':
                state = "code"
                out.append('"""')
                i += 3
                continue
            out.append(c)
            i += 1
            continue

        if state == "chr":
            if c == "\\":
                out.append(src[i:i + 2])
                i += 2
                continue
            if c == "'":
                state = "code"
            out.append(c)
            i += 1
            continue

        if state == "str":
            if c == "\\":
                out.append(src[i:i + 2])
                i += 2
                continue
            if c == '"':
                state = "code"
                out.append('"')
                i += 1
                continue
            if c == "\n":
                # 普通字符串不能跨行：走到行尾说明前面有引号没配对（比如 `'"'`），
                # 必须收状态，否则后面几行的注释会被当成代码
                state = "code"
                out.append("\n")
                i += 1
                continue
            out.append(c)
            i += 1
            continue

        # vstr：逐字字符串，"" 表示一个引号，可以跨行
        if two == '""':
            out.append('""')
            i += 2
            continue
        if c == '"':
            state = "code"
            out.append('"')
            i += 1
            continue
        out.append(c)
        i += 1
    return "".join(out)


def cs_strings(all_files: bool = True) -> "OrderedDict[str, list[str]]":
    """C# 里的中文字面量（普通字面量，不含插值）。

    默认扫**全仓**（apply-cs 才只看白名单）——
    因为翻译键的来源不止被包裹的那几处：ActionCatalog 的标题、对话框调用点、
    Notify("…") 的参数都还是裸中文，它们同样是语言包里的键。
    """
    out: "OrderedDict[str, list[str]]" = OrderedDict()
    if all_files:
        files = []
        for base in (SRC, LOC_SRC):
            for p in sorted(base.rglob("*.cs")):
                s = str(p).replace("\\", "/")
                if "/obj/" in s or "/bin/" in s or p.name.startswith("SelfTest"):
                    continue
                files.append(p)
        pairs = [(str(p.relative_to(ROOT)).replace("\\", "/"), p) for p in files]
    else:
        pairs = [(rel, SRC / rel) for rel in CS_TARGETS if (SRC / rel).exists()]

    for rel, p in pairs:
        if p.name in DIAG_FILES:
            continue
        text = strip_comments(p.read_text(encoding="utf-8-sig"))
        for i, line in enumerate(text.splitlines(), 1):
            if not line.strip():
                continue
            if CS_SKIP_LINE.search(line):
                continue
            for interp, raw in scan_literals(line):
                if interp:
                    continue          # 插值串是另一回事（见 cs_interpolated）
                v = code_unescape(raw)
                if CJK.search(v) and not v.startswith("Loc."):
                    out.setdefault(v, []).append(f"{rel}:{i}")
    return out


def cs_interpolated() -> "OrderedDict[str, list[str]]":
    """插值串（$"…{x}…"）里的中文 —— 报告用，提醒这些还没接多语言。

    插值串的占位符是 {x} 而不是 {0}，没法直接当语言包的键，
    要手工改成 Loc.F("… {0} …", x)。
    """
    out: "OrderedDict[str, list[str]]" = OrderedDict()
    for p in walk_source():
        if p.suffix.lower() != ".cs" or p.name.startswith("SelfTest"):
            continue
        rel = str(p.relative_to(SRC)).replace("\\", "/")
        text = strip_comments(p.read_text(encoding="utf-8-sig"))
        for i, line in enumerate(text.splitlines(), 1):
            if not line.strip():
                continue
            if CS_SKIP_LINE.search(line):
                continue
            for interp, raw in scan_literals(line):
                if not interp or not CJK.search(raw):
                    continue
                out.setdefault(raw, []).append(f"{rel}:{i}")
    return out


def cmd_extract(args):
    x, c = xaml_strings(), cs_strings()
    merged: "OrderedDict[str, list[str]]" = OrderedDict()
    for k, v in list(c.items()) + list(x.items()):
        merged.setdefault(k, []).extend(v)
    print(f"XAML 唯一原文 {len(x)} 条，C# 唯一原文 {len(c)} 条，合计 {len(merged)} 条\n")
    for k, where in merged.items():
        print(f"{k}\t{len(where)}\t{where[0]}")

    interp = cs_interpolated()
    if interp:
        print(f"\n⚠ 还有 {len(interp)} 条插值串里的中文没接多语言"
              '（形如 $"已复制 {n} 项"，要手工改成 Loc.F("… {0} …", n)）：')
        for k, where in list(interp.items())[:30]:
            print(f"    {k}\t{where[0]}")
        if len(interp) > 30:
            print(f"    … 还有 {len(interp) - 30} 条")

    print(f"\n合计 {len(merged)} 条", file=sys.stderr)

def load_pack(path: Path) -> dict:
    if not path.exists():
        return {}
    return json.loads(path.read_text(encoding="utf-8"))


def cmd_check(args):
    x, c = xaml_strings(), cs_strings()
    used = list(x) + [k for k in c if k not in x]
    en = load_pack(EN_PACK)
    missing = [k for k in used if not en.get(k) and k not in INTENTIONAL_SAME]
    extra = [k for k in en if k not in used and k not in SCAN_BLIND and not k.startswith("__")]

    print(f"源码用到 {len(used)} 条原文；en-US 已译 {len(en) - 1 if '__name' in en else len(en)} 条")
    print(f"缺译文 {len(missing)} 条" + ("" if not missing else "："))
    for k in missing[:80]:
        print("  ", k)
    if len(missing) > 80:
        print(f"  … 还有 {len(missing) - 80} 条")
    if extra:
        print(f"\n语言包里多出 {len(extra)} 条（源码里已不存在，可删）：")
        for k in extra[:20]:
            print("  ", k)

    # 占位符一致性：{0} 数量必须一致，否则运行时会 FormatException
    bad = []
    for k, v in en.items():
        if k.startswith("__") or not v:
            continue
        a = len(re.findall(r"\{\d+\}", k))
        b = len(re.findall(r"\{\d+\}", v))
        if a != b:
            bad.append((k, v, a, b))
    if bad:
        print(f"\n占位符不匹配 {len(bad)} 条：")
        for k, v, a, b in bad:
            print(f"   原文 {k!r} ({a} 个) → 译文 {v!r} ({b} 个)")

    # 插值串（$"…{x}…"）里的中文不会被上面两条查漏覆盖：它们进不了语言包
    # （占位符是 {x} 不是 {0}），但**照样会显示在界面上**。
    # 这里只报数量与位置，人工判断该改成 Loc.F 还是留在日志里。
    interp = cs_interpolated()
    if interp:
        print(f"\n插值串里还有 {len(interp)} 条中文（多数是日志/诊断，界面文案要改成 Loc.F）：")
        for k, where in list(interp.items())[:15]:
            print(f"    {where[0]}  {k}")
        if len(interp) > 15:
            print(f"    … 还有 {len(interp) - 15} 条（python tools/i18n.py extract 看全部）")


ENTITY = re.compile(r"&(?:[a-zA-Z][a-zA-Z0-9]*|#[0-9]+|#[xX][0-9a-fA-F]+);")


def xaml_markup_unescape(s: str) -> str:
    """还原标记扩展里的反斜杠转义。

    XAML 的标记扩展参数是"转义字符串"：`\\` 才是一个反斜杠，`\\,` 是逗号本身。
    所以 `{loc:T 'C:\\Windows'}` 在运行期拿到的是 `C:\\Windows`，而
    `{loc:T 'C:\\Windows'}`（只写一个反斜杠）拿到的是 `C:Windows` ——
    **反斜杠会被吃掉**，键对不上，界面就一直显示中文。
    """
    if "\\" not in s:
        return s
    out, i, n = [], 0, len(s)
    while i < n:
        c = s[i]
        if c == "\\" and i + 1 < n:
            out.append(s[i + 1])
            i += 2
            continue
        out.append(c)
        i += 1
    return "".join(out)


def xaml_escape(s: str) -> str:
    """把原文塞进 {loc:T '…'} 里，同时保证 XML 解码后**还是原来那段文本**。

    两个坑：
      · 单引号会截断标记扩展的字符串参数 → &apos;；
      · 原文里的 &amp;#x0a; 这类实体必须原样留着，否则 XML 解码后得到的是
        字面量 "&#x0a;" 而不是换行（第一版就踩了这个，界面直接显示六个字符）。
        所以先把实体整段保护起来，只转义实体以外的 &。
    """
    buf: list[str] = []
    last = 0
    for m in ENTITY.finditer(s):
        buf.append(_esc_xaml_part(s[last:m.start()]))
        buf.append(m.group(0))          # 实体原样保留，交给 XML 解析器解码
        last = m.end()
    buf.append(_esc_xaml_part(s[last:]))
    return "".join(buf)


def _esc_xaml_part(s: str) -> str:
    # 反斜杠必须在标记扩展里写成两个，否则 XAML 解析时会被当成转义前缀吃掉
    return (s.replace("\\", "\\\\")
             .replace("&", "&amp;")
             .replace("'", "&apos;")
             .replace('"', "&quot;"))


def cmd_apply_xaml(args):
    changed_files = 0
    changed_attrs = 0
    for p in walk_source():
        if p.suffix.lower() != ".xaml":
            continue
        raw = p.read_text(encoding="utf-8-sig")
        text = raw

        def repl(m):
            nonlocal changed_attrs
            attr, val = m.group(1), m.group(2)
            if attr not in XAML_ATTRS or not CJK.search(val) or val.lstrip().startswith("{"):
                return m.group(0)
            changed_attrs += 1
            return f'{attr}="{{loc:T \'{xaml_escape(val)}\'}}"'

        new = re.sub(r'([\w.:]+)="([^"]*)"', repl, text)
        # 加 xmlns:loc（幂等）
        if new != raw and "xmlns:loc=" not in new:
            new = re.sub(
                r'(xmlns:x="http://schemas\.microsoft\.com/winfx/2006/xaml")',
                r'\1\n             xmlns:loc="clr-namespace:CapsLockPlus.UI.Markup"',
                new, count=1,
            )
        if new != raw:
            p.write_text(new, encoding="utf-8-sig")
            changed_files += 1
    print(f"已处理 {changed_files} 个 XAML，改写 {changed_attrs} 个属性")


def cmd_apply_cs(args):
    total = 0
    for rel in CS_TARGETS:
        p = SRC / rel
        if not p.exists():
            continue
        lines = p.read_text(encoding="utf-8-sig").splitlines(keepends=True)
        out = []
        n = 0
        for line in lines:
            st = line.strip()
            if (st.startswith("//") or st.startswith("///")
                    or CS_SKIP_LINE.search(line) or "Loc.T(" in line or "Loc.F(" in line):
                out.append(line)
                continue

            def repl(m):
                nonlocal n
                inner = m.group(1)
                if not CJK.search(inner):
                    return m.group(0)
                n += 1
                return f'Loc.T("{inner}")'

            new = re.sub(r'(?<![$\w])"((?:[^"\\]|\\.)*)"', repl, line)
            out.append(new)
        if n:
            p.write_text("".join(out), encoding="utf-8-sig")
            total += n
            print(f"  {rel}: {n} 处")
    print(f"共包了 {total} 处中文字面量")


def cmd_merge(args):
    batch = json.loads(Path(args.batch).read_text(encoding="utf-8"))
    en = load_pack(EN_PACK)
    added = updated = 0
    for k, v in batch.items():
        if k in en and en[k] != v:
            updated += 1
        elif k not in en:
            added += 1
        en[k] = v
    en.setdefault("__name", "English")
    en.setdefault("__culture", "en-US")
    ordered = OrderedDict()
    ordered["__name"] = en.pop("__name")
    ordered["__culture"] = en.pop("__culture", "en-US")
    for k in sorted(en):
        ordered[k] = en[k]
    EN_PACK.parent.mkdir(parents=True, exist_ok=True)
    EN_PACK.write_text(json.dumps(ordered, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"合并 {added} 条新增、{updated} 条更新；en-US 现有 {len(ordered) - 2} 条译文")


# ---------------- 译稿（tsv）↔ 语言包（json） ----------------
#
# 为什么中间隔一层 tsv：
#   · 1000 条译文的 json 手写容易漏逗号、引号转义；
#   · tsv 一行一条，"原文<TAB>译文"，翻译的人（或我）改起来一目了然；
#   · 同一份 tsv 换个人改，diff 只看改动那几行；
#   · 新增语言 = dump 一个空译稿 → 填 → build，全程不碰代码。

LANG_SRC = ROOT / "tools" / "lang"


def esc_cell(s: str) -> str:
    """译稿是"一行一条"，所以文本里的换行要转义成 \\n。

    只有 7 条原文带换行（说明性段落），但不处理就会把一行拆成两行，
    dump 出来的译稿直接读不回来。
    """
    return s.replace("\\", "\\\\").replace("\n", "\\n").replace("\r", "")


def unesc_cell(s: str) -> str:
    """把译稿里的转义还原（`esc_cell` 的逆运算）。

    **必须成对逐个扫描**，不能像第一版那样先 `.replace("\\n", "\\n")` 再
    `.replace("\\\\", "\\\\")`：`\\\\notepad`（路径里的两个反斜杠）里的
    `\\n` 会被当成换行，于是 `C:\\\\Windows\\\\notepad.exe` 变成
    `C:\\Windows` + 换行 + `otepad.exe` —— 键被改掉，语言包里就永远查不到这条，
    界面静默退回中文。这类错不会报错，只能靠这条注释和单元自检挡住。
    """
    out: list[str] = []
    i = 0
    n = len(s)
    while i < n:
        c = s[i]
        if c == "\\" and i + 1 < n:
            nxt = s[i + 1]
            if nxt == "n":
                out.append("\n")
                i += 2
                continue
            if nxt == "t":
                out.append("\t")
                i += 2
                continue
            if nxt == "r":          # \r 在译稿里没有意义，直接丢弃
                i += 2
                continue
            if nxt == "\\":
                out.append("\\")
                i += 2
                continue
        out.append(c)
        i += 1
    return "".join(out)


def lang_tsv(code: str) -> Path:
    return LANG_SRC / f"{code}.tsv"


def cmd_fill(args):
    code = args.code
    src = lang_tsv(code)
    if not src.exists():
        sys.exit(f"找不到译稿 {src}，先跑 dump {code}")

    patch: dict[str, str] = {}
    for line in Path(args.patch).read_text(encoding="utf-8-sig").splitlines():
        if not line or line.startswith("#") or "\t" not in line:
            continue
        k, v = line.split("\t", 1)
        if k.strip():
            patch[unesc_cell(k.rstrip("\r"))] = unesc_cell(v.rstrip("\r"))

    lines = src.read_text(encoding="utf-8").splitlines()
    out: list[str] = []
    hit = 0
    known = {unesc_cell(l.split("\t", 1)[0]) for l in lines if "\t" in l}
    for line in lines:
        if "\t" not in line:
            out.append(line)
            continue
        k = line.split("\t", 1)[0]
        if k in patch:
            # 写回时必须重新转义：patch 里的值已经是解码后的真实文本
            # （可能含换行和反斜杠），直接写会让一条变成多行、或把 `\n`
            # 当成字面量写进译稿。
            out.append(f"{k}\t{esc_cell(patch[k])}")
            hit += 1
        else:
            out.append(line)

    unknown = [k for k in patch if k not in known]
    src.write_text("\n".join(out) + "\n", encoding="utf-8")
    print(f"补入 {hit} 条；漏掉的原文 {len(unknown)} 条")
    for k in unknown[:15]:
        print("   ⚠", k)


def cmd_dump(args):
    code = args.code
    x, c = xaml_strings(), cs_strings()
    merged: "OrderedDict[str, str]" = OrderedDict()
    for k in list(x) + list(c):
        merged.setdefault(k, "")
    pack = load_pack(LANG_DIR / f"{code}.json")
    # 语言包里可能有**源码里扫不到**的键：`Loc.Join` 的连接符「、」就是这么一条
    # （它是运行期传进 `Loc.T` 的参数，源码里只出现在 `T("、")` 之外的地方）。
    # 不把它们带过来，`dump` 一次就把译文丢了 —— 界面表现为「A、B、C」，自检报错。
    for k in pack:
        if not k.startswith("__"):
            merged.setdefault(k, "")

    LANG_SRC.mkdir(parents=True, exist_ok=True)
    out = lang_tsv(code)
    fresh = 0
    with out.open("w", encoding="utf-8", newline="\n") as f:
        f.write(f"__name\t{pack.get('__name', code)}\n")
        f.write(f"__culture\t{pack.get('__culture', code)}\n")
        for k in merged:
            v = pack.get(k, "")
            if not v:
                fresh += 1
            f.write(f"{esc_cell(k)}\t{esc_cell(v)}\n")
    print(f"已导出 {out.relative_to(ROOT)}：{len(merged)} 条，其中 {fresh} 条待译")
    print("只写原文不写译文 = 沿用原文（产品名、快捷键这类不该翻的留空即可）。")


def cmd_build(args):
    code = args.code
    src = lang_tsv(code)
    if not src.exists():
        sys.exit(f"找不到译稿 {src}，先跑 dump {code}")

    pack: "OrderedDict[str, str]" = OrderedDict()
    meta: dict[str, str] = {}
    bad: list[str] = []
    for lineno, line in enumerate(src.read_text(encoding="utf-8").splitlines(), 1):
        if not line or line.startswith("#"):
            continue
        if "\t" not in line:
            bad.append(f"第 {lineno} 行没有 TAB：{line[:60]}")
            continue
        k, v = line.split("\t", 1)
        k, v = unesc_cell(k.strip("\r")), unesc_cell(v.strip("\r"))
        if not k:
            continue
        if k.startswith("__"):
            meta[k] = v
            continue
        # 占位符必须一一对应，否则运行期 Loc.F 会抛 FormatException。
        # 空译文（= 还没翻）直接跳过：它不会写进语言包。
        a = len(re.findall(r"\{\d+\}", k))
        b = len(re.findall(r"\{\d+\}", v))
        if v and a != b:
            bad.append(f"占位符不匹配（原文 {a} 个 / 译文 {b} 个）：{k} → {v}")
        pack[k] = v

    if bad:
        print(f"译稿有 {len(bad)} 处问题，未生成语言包：")
        for b in bad[:40]:
            print("  ", b)
        sys.exit(1)

    ordered: "OrderedDict[str, str]" = OrderedDict()
    ordered["__name"] = meta.get("__name") or code
    ordered["__culture"] = meta.get("__culture") or code
    for k in sorted(pack):
        # 空译文不写进语言包：Loc.T 查不到就返回原文，
        # 留一堆空串只会让包变大、也让"缺译"统计失真
        if pack[k]:
            ordered[k] = pack[k]

    dest = LANG_DIR / f"{code}.json"
    dest.parent.mkdir(parents=True, exist_ok=True)
    dest.write_text(json.dumps(ordered, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

    filled = sum(1 for k, v in pack.items() if v)
    print(f"已生成 {dest.relative_to(ROOT)}：{len(pack)} 条，{filled} 条有译文、"
          f"{len(pack) - filled} 条沿用原文")


def main():
    ap = argparse.ArgumentParser()
    sub = ap.add_subparsers(dest="cmd", required=True)
    sub.add_parser("extract").set_defaults(fn=cmd_extract)
    sub.add_parser("check").set_defaults(fn=cmd_check)
    sub.add_parser("apply-xaml").set_defaults(fn=cmd_apply_xaml)
    sub.add_parser("apply-cs").set_defaults(fn=cmd_apply_cs)
    p = sub.add_parser("merge")
    p.add_argument("batch")
    p.set_defaults(fn=cmd_merge)
    for name, fn, helptext in (
        ("dump", cmd_dump, "导出译稿 tsv"),
        ("build", cmd_build, "译稿 tsv → 语言包 json"),
    ):
        q = sub.add_parser(name, help=helptext)
        q.add_argument("code", help="语言代码，如 en-US / ja-JP")
        q.set_defaults(fn=fn)
    q = sub.add_parser("fill", help="把一批译文补进译稿")
    q.add_argument("code")
    q.add_argument("patch", help="patch tsv：原文<TAB>译文")
    q.set_defaults(fn=cmd_fill)
    args = ap.parse_args()
    args.fn(args)


if __name__ == "__main__":
    main()
