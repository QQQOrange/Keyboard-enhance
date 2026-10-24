"""端到端验证：长文本改走剪贴板粘贴，短文本仍然原地走。

要验的五件事（都是用户能直接感觉到的）：
    ① 短文本不变 —— 打 "hello" 时**不碰剪贴板**（哨兵内容必须原样还在）
    ② 长文本一字不差 —— 220 字整段落进文本框（改之前 86 字就丢字）
    ③ 粘完把剪贴板还回去 —— 哨兵内容原样恢复，用户没丢东西
    ④ 剪贴板里本来是图片时，图片不许丢
    ⑤ 多行文本的换行不能被当成 Enter
    ⑥ 剪贴板本来是空的，粘完还得是空的
    ⑦ 剪贴板里是富文本（HTML Format）时，格式和内容都不能丢

怎么确认"真的走了剪贴板"：看程序自己的日志 `[输入文本] 走剪贴板粘贴 长度=N`。
只看文本对不对不够 —— 逐字注入在老长度上也可能侥幸全对。

用法：
    python tools/e2e_long_text_paste.py              # 用 dist 里刚发布的 exe
    python tools/e2e_long_text_paste.py <exe路径>     # 指定 exe（对照旧版）
"""

import ctypes
import ctypes.wintypes as wt
import json
import os
import subprocess
import sys
import time
from datetime import datetime

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from e2e_ime_typing import (  # noqa: E402
    u32, k32, ROOT, EXE, SENTINEL,
    ensure_focus, clip_get, clip_set,
    read_editor, clear_editor, start_notepad, press_combo,
    hkl_with_lang, use_layout,
)

VK_F9 = 0x78
VK_F10 = 0x79
VK_F11 = 0x7A
CF_DIB = 8


def _open_clipboard(tries=20):
    for _ in range(tries):
        if u32.OpenClipboard(None):
            return True
        time.sleep(0.05)
    return False


class BITMAPINFOHEADER(ctypes.Structure):
    _fields_ = [
        ("biSize", wt.DWORD), ("biWidth", wt.LONG), ("biHeight", wt.LONG),
        ("biPlanes", wt.WORD), ("biBitCount", wt.WORD), ("biCompression", wt.DWORD),
        ("biSizeImage", wt.DWORD), ("biXPelsPerMeter", wt.LONG), ("biYPelsPerMeter", wt.LONG),
        ("biClrUsed", wt.DWORD), ("biClrImportant", wt.DWORD),
    ]


def clip_set_dib(width=32, height=32):
    """往剪贴板放一张 32bpp 位图（CF_DIB），用来验证"图片不会丢"。"""
    if not _open_clipboard():
        return False
    try:
        u32.EmptyClipboard()
        pixels = width * height * 4
        size = ctypes.sizeof(BITMAPINFOHEADER) + pixels
        hmem = k32.GlobalAlloc(0x0002, size)       # GMEM_MOVEABLE
        if not hmem:
            return False
        p = k32.GlobalLock(hmem)
        header = BITMAPINFOHEADER(40, width, height, 1, 32, 0, pixels, 0, 0, 0, 0)
        ctypes.memmove(p, ctypes.byref(header), ctypes.sizeof(header))
        ctypes.memset(p + ctypes.sizeof(header), 0x40, pixels)
        k32.GlobalUnlock(hmem)
        return bool(u32.SetClipboardData(CF_DIB, hmem))
    finally:
        u32.CloseClipboard()


def clip_has_dib():
    if not _open_clipboard():
        return False
    try:
        return bool(u32.GetClipboardData(CF_DIB))
    finally:
        u32.CloseClipboard()


def clip_clear():
    if not _open_clipboard():
        return False
    try:
        return bool(u32.EmptyClipboard())
    finally:
        u32.CloseClipboard()


k32.GlobalSize.argtypes = [wt.HGLOBAL]
k32.GlobalSize.restype = ctypes.c_size_t
u32.RegisterClipboardFormatW.argtypes = [ctypes.c_wchar_p]
u32.RegisterClipboardFormatW.restype = wt.UINT


def clip_set_format(fmt_id, payload):
    """把一段原始字节放进指定剪贴板格式（伪造"浏览器复制的富文本"）。"""
    if not _open_clipboard():
        return False
    try:
        u32.EmptyClipboard()
        hmem = k32.GlobalAlloc(0x0002, len(payload))
        if not hmem:
            return False
        p = k32.GlobalLock(hmem)
        ctypes.memmove(p, payload, len(payload))
        k32.GlobalUnlock(hmem)
        return bool(u32.SetClipboardData(fmt_id, hmem))
    finally:
        u32.CloseClipboard()


def clip_format_size(fmt_id):
    """指定格式的数据长度；0 = 格式不在或内容是空的。"""
    if not _open_clipboard():
        return 0
    try:
        h = u32.GetClipboardData(fmt_id)
        return k32.GlobalSize(h) if h else 0
    finally:
        u32.CloseClipboard()


LONG_TEXT = ("The quick brown fox jumps over 13 lazy dogs! "
             "0123456789 abcdefghijklmnopqrstuvwxyz ABCDEFGHIJKLMNOPQRSTUVWXYZ "
             "plus some symbols: @#$%^&*()_+-=[]{};:'\",.<>/?\\|`~ "
             "and a tail so the length is well past a single batch of 32.")
MULTI_TEXT = ("第一行 0123456789 abcdefghij\n"
              "第二行 9876543210 jihgfedcba\n"
              "第三行 结束")


def clip_get_wait(expect, timeout=2.0):
    """等剪贴板变成期望值（粘贴 + 还原是异步的，按一下就读容易读到中间态）。"""
    end = time.time() + timeout
    got = None
    while time.time() < end:
        got = clip_get()
        if got == expect:
            return got
        time.sleep(0.05)
    return got


def log_lines(data_dir):
    path = os.path.join(data_dir, "logs", "app.log")
    if not os.path.exists(path):
        return []
    return open(path, encoding="utf-8", errors="replace").read().splitlines()


def paste_count(data_dir):
    return sum(1 for ln in log_lines(data_dir) if "走剪贴板粘贴" in ln)


def main():
    stamp = datetime.now().strftime("%H%M%S")
    data = os.path.join(ROOT, f"out-e2e-paste-{stamp}")
    os.makedirs(data, exist_ok=True)
    env = dict(os.environ)
    env["CAPSLOCKPLUS_DATA"] = data
    env["CAPSLOCKPLUS_MUTEX_SUFFIX"] = f"_e2e_paste_{stamp}"

    if not os.path.exists(EXE):
        print(f"找不到 exe：{EXE}")
        return 1
    print(f"被测程序：{EXE}")
    print(f"长文本 {len(LONG_TEXT)} 字，多行文本 {len(MULTI_TEXT)} 字")

    # 1) 起一次，让它写出默认设置
    first = subprocess.Popen([EXE, "--tray"], env=env)
    time.sleep(5.0)
    first.terminate()
    try:
        first.wait(timeout=8)
    except subprocess.TimeoutExpired:
        first.kill()
    time.sleep(0.6)

    settings = os.path.join(data, "settings.json")
    if not os.path.exists(settings):
        print("SETTINGS-MISSING")
        return 1

    cfg = json.load(open(settings, encoding="utf-8"))
    layers = cfg.get("Layers") or []
    if not layers:
        print("NO-LAYERS")
        return 1

    keys = layers[0].setdefault("Keys", [])

    def bind(combo, action, arg):
        for k in keys:
            if k.get("Combo", "").lower() == combo:
                k["Action"] = action
                k["Arg"] = arg
                return
        keys.append({"Combo": combo, "Action": action, "Arg": arg})

    bind("caps_f9", "typeText", "hello")
    bind("caps_f10", "typeText", LONG_TEXT)
    bind("caps_f11", "typeText", MULTI_TEXT)
    json.dump(cfg, open(settings, "w", encoding="utf-8"), ensure_ascii=False, indent=2)
    print("已绑定 caps_f9 → hello、caps_f10 → 200 字英文、caps_f11 → 多行文本")

    proc = subprocess.Popen([EXE, "--tray"], env=env)
    time.sleep(5.0)

    np_proc, np_hwnd = start_notepad()
    if not np_hwnd:
        print("没找到记事本窗口")
        proc.terminate()
        np_proc.kill()
        return 1

    hkl_zh = hkl_with_lang(0x0804)
    old_clip = clip_get()
    results = []

    def check(name, good, detail=""):
        results.append((name, good))
        print(f"{name}：{'OK' if good else 'FAIL'}" + (f"  {detail}" if detail else ""))

    try:
        # ---- ① 短文本：不碰剪贴板 ----
        clip_set(SENTINEL)
        before = paste_count(data)
        ensure_focus(np_hwnd)
        clear_editor(np_hwnd)
        ensure_focus(np_hwnd)
        press_combo(VK_F9)
        untouched = clip_get()          # 读回文本会覆盖剪贴板，先验
        check("① 短文本没碰剪贴板", untouched == SENTINEL, f"剪贴板={untouched!r}")
        got = read_editor(np_hwnd)
        check("① 短文本打对了（hello）", got == "hello", f"读回={got!r}")
        check("① 短文本日志里没有粘贴记录", paste_count(data) == before)

        # ---- ② 长文本：一字不差 + 剪贴板还原 ----
        clip_set(SENTINEL)
        before = paste_count(data)
        ensure_focus(np_hwnd)
        clear_editor(np_hwnd)
        ensure_focus(np_hwnd)
        press_combo(VK_F10, wait=1.5)
        # 先验剪贴板：一旦去读回文本就会覆盖它
        restored = clip_get_wait(SENTINEL)
        check("② 粘完剪贴板已还原", restored == SENTINEL, f"剪贴板={restored!r}")
        check("② 确实走了剪贴板通道", paste_count(data) == before + 1)
        got = read_editor(np_hwnd)
        check("② 长文本一字不差", got == LONG_TEXT,
              "" if got == LONG_TEXT else
              f"长度 {len(got)}/{len(LONG_TEXT)} 读回={got[:80]!r}")

        # ---- ③ 中文输入法下同一段长文本（剪贴板路径不经过输入法）----
        if use_layout(np_hwnd, hkl_zh):
            clip_set(SENTINEL)
            ensure_focus(np_hwnd)
            clear_editor(np_hwnd)
            ensure_focus(np_hwnd)
            press_combo(VK_F10, wait=1.5)
            clip_get_wait(SENTINEL)          # 等粘贴 + 还原结束，别在中间抢剪贴板
            got = read_editor(np_hwnd)
            check("③ 中文输入法下长文本一字不差", got == LONG_TEXT,
                  "" if got == LONG_TEXT else
                  f"长度 {len(got)}/{len(LONG_TEXT)} 读回={got[:80]!r}")
        else:
            check("③ 中文输入法场景", False, "切不到中文输入语言")

        # ---- ④ 剪贴板里是图片时，图片不许丢 ----
        if clip_set_dib():
            before = paste_count(data)
            ensure_focus(np_hwnd)
            clear_editor(np_hwnd)
            ensure_focus(np_hwnd)
            press_combo(VK_F10, wait=1.5)
            check("④ 图片还在剪贴板里", clip_has_dib())
            check("④ 图片场景也走通了长文本", paste_count(data) == before + 1)
        else:
            check("④ 图片场景", False, "没能把位图放进剪贴板")

        # ---- ⑤ 多行长文本：换行不能被当成 Enter ----
        clip_set(SENTINEL)
        ensure_focus(np_hwnd)
        clear_editor(np_hwnd)
        ensure_focus(np_hwnd)
        press_combo(VK_F11, wait=1.5)
        got = read_editor(np_hwnd)
        # 记事本里换行读回来是 \r\n
        expect = MULTI_TEXT.replace("\n", "\r\n")
        check("⑤ 多行文本完整", got == expect, f"读回={got!r}")

        # ---- ⑥ 剪贴板本来是空的：粘完必须还是空的 ----
        # 开机后剪贴板通常就是空的。这条要是错了，用户之后 Ctrl+V 会粘出
        # 一段他根本没有复制过的长文本。
        if clip_clear():
            before = paste_count(data)
            ensure_focus(np_hwnd)
            clear_editor(np_hwnd)
            ensure_focus(np_hwnd)
            press_combo(VK_F10, wait=1.5)
            left = clip_get_wait("")
            check("⑥ 空剪贴板粘完还是空的", left == "", f"剩了 {left!r}")
            check("⑥ 空剪贴板场景确实走了剪贴板", paste_count(data) == before + 1)
        else:
            check("⑥ 空剪贴板场景", False, "没能清空剪贴板")

        # ---- ⑦ 剪贴板里是富文本时，格式和内容都不能丢 ----
        # 这条专门守一个很隐蔽的实现坑：快照里某些格式存的是 MemoryStream，
        # 取出来时读位置在末尾，原样写回去会变成"格式还在、长度 0"。
        html_fmt = u32.RegisterClipboardFormatW("HTML Format")
        payload = b"<html><body><b>rich</b> text copied from a browser</body></html>"
        if html_fmt and clip_set_format(html_fmt, payload):
            before = paste_count(data)
            ensure_focus(np_hwnd)
            clear_editor(np_hwnd)
            ensure_focus(np_hwnd)
            press_combo(VK_F10, wait=1.5)
            size = clip_format_size(html_fmt)
            check("⑦ 富文本格式还在、内容不是空的", size > 0,
                  f"长度 {size}/原 {len(payload)}")
            check("⑦ 富文本场景确实走了剪贴板", paste_count(data) == before + 1)
        else:
            check("⑦ 富文本场景", False, "没能把 HTML 格式放进剪贴板")

    finally:
        if old_clip is not None:
            clip_set(old_clip)
        np_proc.kill()
        proc.terminate()
        try:
            proc.wait(timeout=8)
        except subprocess.TimeoutExpired:
            proc.kill()

    log = os.path.join(data, "logs", "app.log")
    errs = []
    if os.path.exists(log):
        text = open(log, encoding="utf-8", errors="replace").read()
        errs = [ln for ln in text.splitlines() if "[ERROR]" in ln]
        warns = [ln for ln in text.splitlines() if "[输入文本]" in ln]
        print(f"\n日志：ERROR {len(errs)} 条，输入文本提示 {len(warns)} 条")
        for ln in warns[:6]:
            print("   " + ln[len(ln) - min(len(ln), 110):])
    if errs:
        for ln in errs[:5]:
            print("   " + ln)
        results.append(("日志无 ERROR", False))

    passed = sum(1 for _, ok in results if ok)
    print(f"\n结果：{passed}/{len(results)} 项通过")
    return 0 if passed == len(results) else 1


if __name__ == "__main__":
    sys.exit(main())
