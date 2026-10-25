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
LANG_DIR = SRC / "Assets" / "Lang"
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
    r"(CoreLog\.|Log\.|Debug\.|Trace\.|case\s|==\s*\"|!=\s*\"|\.Contains\(|"
    r"StartsWith\(|EndsWith\(|Equals\(|nameof\(|Regex|Path\.|\.id\b)"
)


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


def cs_strings() -> "OrderedDict[str, list[str]]":
    """指定 C# 文件里的中文字面量（普通字面量，不含插值）。"""
    out: "OrderedDict[str, list[str]]" = OrderedDict()
    for rel in CS_TARGETS:
        p = SRC / rel
        if not p.exists():
            continue
        for i, line in enumerate(p.read_text(encoding="utf-8-sig").splitlines(), 1):
            st = line.strip()
            if st.startswith("//") or st.startswith("///"):
                continue
            if CS_SKIP_LINE.search(line):
                continue
            if 'Loc.T(' in line or 'Loc.F(' in line:
                continue
            for m in re.finditer(r'(?<![$\w])"((?:[^"\\]|\\.)*)"', line):
                v = m.group(1)
                if CJK.search(v):
                    out.setdefault(v, []).append(f"{rel}:{i}")
    return out


def cmd_extract(args):
    x, c = xaml_strings(), cs_strings()
    merged: "OrderedDict[str, list[str]]" = OrderedDict()
    for k, v in list(c.items()) + list(x.items()):
        merged.setdefault(k, []).extend(v)
    print(f"XAML 唯一原文 {len(x)} 条，C# 唯一原文 {len(c)} 条，合计 {len(merged)} 条\n")
    for k, where in merged.items():
        print(f"{k}\t{len(where)}\t{where[0]}")
    print(f"\n合计 {len(merged)} 条", file=sys.stderr)


def load_pack(path: Path) -> dict:
    if not path.exists():
        return {}
    return json.loads(path.read_text(encoding="utf-8"))


def cmd_check(args):
    x, c = xaml_strings(), cs_strings()
    used = list(x) + [k for k in c if k not in x]
    en = load_pack(EN_PACK)
    missing = [k for k in used if not en.get(k)]
    extra = [k for k in en if k not in used and not k.startswith("__")]

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


ENTITY = re.compile(r"&(?:[a-zA-Z][a-zA-Z0-9]*|#[0-9]+|#[xX][0-9a-fA-F]+);")


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
        buf.append(s[last:m.start()].replace("&", "&amp;").replace("'", "&apos;").replace('"', "&quot;"))
        buf.append(m.group(0))          # 实体原样保留，交给 XML 解析器解码
        last = m.end()
    buf.append(s[last:].replace("&", "&amp;").replace("'", "&apos;").replace('"', "&quot;"))
    return "".join(buf)


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


def lang_tsv(code: str) -> Path:
    return LANG_SRC / f"{code}.tsv"


def cmd_dump(args):
    code = args.code
    x, c = xaml_strings(), cs_strings()
    merged: "OrderedDict[str, str]" = OrderedDict()
    for k in list(x) + list(c):
        merged.setdefault(k, "")
    pack = load_pack(LANG_DIR / f"{code}.json")

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
            f.write(f"{k}\t{v}\n")
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
        k, v = k.strip("\r"), v.strip("\r")
        if not k:
            continue
        if k.startswith("__"):
            meta[k] = v
            continue
        # 占位符必须一一对应，否则运行期 Loc.F 会抛 FormatException
        a = len(re.findall(r"\{\d+\}", k))
        b = len(re.findall(r"\{\d+\}", v))
        if a != b:
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
    args = ap.parse_args()
    args.fn(args)


if __name__ == "__main__":
    main()
