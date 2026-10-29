"""端到端验证：「输入文本」发出的每个按键都必须带**扫描码**。

用户报的现象：RustDesk 的固定密码框里，14 位密码只落 11 位（Tab+W 触发）。

根因（本脚本就是给它上锁的）：`SendInput` 只填了虚拟键，`wScan` 留 0。
靠扫描码认键的程序 —— 远程桌面客户端是最典型的一类，RustDesk 会把本地按键的
扫描码原样转给被控端 —— 拿到 wScan=0 的按键会漏掉一部分：实测丢的正好是
"要 Shift 的那几个"（大写字母和符号），14 位密码只剩 7 位。

这个脚本不依赖任何特定目标程序：它自己装一个 WH_KEYBOARD_LL 钩子，
直接看程序注入出来的按键**长什么样**（虚拟键 + 扫描码），
所以它同时是"回归测试"和"注入质量检查"。

用法：
    python tools/e2e_typing_scancode.py             # 用 dist 里的 exe
    python tools/e2e_typing_scancode.py <exe路径>    # 指定 exe（对照旧版）

退出码 0 = 每个按键都带扫描码。
"""

import ctypes
import ctypes.wintypes as wt
import json
import os
import subprocess
import sys
import threading
import time
from datetime import datetime

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from e2e_ime_typing import ROOT, EXE, tap, key  # noqa: E402

u32 = ctypes.WinDLL("user32", use_last_error=True)
k32 = ctypes.WinDLL("kernel32", use_last_error=True)

VK_CAPITAL = 0x14
VK_W = 0x57
WH_KEYBOARD_LL = 13
INJECT_TAG = 0x434C5021          # InputSender.InjectionTag，用来只挑出程序自己发的键

TEXT = "Ab3!kL9@xY2#zQ"          # 14 位：8 个要 Shift + 6 个不要
SHIFTED = set("!@#$%^&*()_+{}|:\"<>?~")
EXPECT_KEYS = sum(4 if (ch.isupper() or ch in SHIFTED) else 2 for ch in TEXT)


class KBDLLHOOKSTRUCT(ctypes.Structure):
    _fields_ = [("vkCode", wt.DWORD), ("scanCode", wt.DWORD), ("flags", wt.DWORD),
                ("time", wt.DWORD), ("dwExtraInfo", ctypes.c_size_t)]


HOOKPROC = ctypes.WINFUNCTYPE(ctypes.c_ssize_t, ctypes.c_int, wt.WPARAM, wt.LPARAM)
u32.SetWindowsHookExW.restype = ctypes.c_void_p
u32.SetWindowsHookExW.argtypes = [ctypes.c_int, HOOKPROC, ctypes.c_void_p, wt.DWORD]
u32.CallNextHookEx.argtypes = [ctypes.c_void_p, ctypes.c_int, wt.WPARAM, wt.LPARAM]
u32.CallNextHookEx.restype = ctypes.c_ssize_t

events = []            # (vk, scan, down)
_ready = threading.Event()
_hook_handle = ctypes.c_void_p(0)


def hook_thread():
    global _hook_handle

    def proc(n_code, w_param, l_param):
        if n_code >= 0:
            data = ctypes.cast(l_param, ctypes.POINTER(KBDLLHOOKSTRUCT)).contents
            if data.dwExtraInfo == INJECT_TAG:
                down = int(w_param) in (0x0100, 0x0104)      # WM_KEYDOWN / WM_SYSKEYDOWN
                events.append((data.vkCode, data.scanCode, down))
        return u32.CallNextHookEx(_hook_handle, n_code, w_param, l_param)

    proc_ref = HOOKPROC(proc)
    _hook_handle = u32.SetWindowsHookExW(WH_KEYBOARD_LL, proc_ref, None, 0)
    _ready.set()
    msg = wt.MSG()
    while u32.GetMessageW(ctypes.byref(msg), None, 0, 0) > 0:
        u32.TranslateMessage(ctypes.byref(msg))
        u32.DispatchMessageW(ctypes.byref(msg))


def bind(keys, combo, action, arg):
    for k in keys:
        if k.get("Combo", "").lower() == combo:
            k["Action"] = action
            k["Arg"] = arg
            return
    keys.append({"Combo": combo, "Action": action, "Arg": arg})


def main():
    exe = sys.argv[1] if len(sys.argv) > 1 else EXE
    if not os.path.exists(exe):
        print(f"找不到 exe：{exe}")
        return 1

    stamp = datetime.now().strftime("%H%M%S")
    data = os.path.join(ROOT, f"out-e2e-scan-{stamp}")
    os.makedirs(data, exist_ok=True)
    env = dict(os.environ)
    env["CAPSLOCKPLUS_DATA"] = data
    env["CAPSLOCKPLUS_MUTEX_SUFFIX"] = f"_e2e_scan_{stamp}"

    print(f"被测程序：{exe}")
    first = subprocess.Popen([exe, "--tray"], env=env)
    time.sleep(5.5)
    first.terminate()
    try:
        first.wait(timeout=8)
    except subprocess.TimeoutExpired:
        first.kill()
    time.sleep(0.8)

    settings = os.path.join(data, "settings.json")
    cfg = json.load(open(settings, encoding="utf-8"))
    layers = cfg.get("Layers") or []
    if not layers:
        print("NO-LAYERS")
        return 1
    bind(layers[0].setdefault("Keys", []), "caps_w", "typeText", TEXT)
    json.dump(cfg, open(settings, "w", encoding="utf-8"), ensure_ascii=False, indent=2)
    print(f"已把 caps_w → 输入文本 [{TEXT}]（{len(TEXT)} 字）")

    # 先装钩子：这样程序注入的每个键都会被记下来
    t = threading.Thread(target=hook_thread, daemon=True)
    t.start()
    _ready.wait(3.0)
    if not _hook_handle:
        print("钩子安装失败")
        return 1

    proc = subprocess.Popen([exe, "--tray"], env=env)
    time.sleep(5.5)

    # Caps 按住 → 敲 W → 松手（和用户按组合键的动作一致）
    key(VK_CAPITAL)
    time.sleep(0.4)
    tap(VK_W)
    time.sleep(0.06)
    key(VK_CAPITAL, up=True)
    time.sleep(2.5)

    proc.terminate()
    time.sleep(0.4)

    zero = [e for e in events if e[1] == 0]
    print(f"捕获到程序注入的按键 {len(events)} 个（期望 {EXPECT_KEYS}）")
    print(f"  扫描码为 0 的：{len(zero)} 个")
    if zero:
        print("  前几个缺扫描码的键：" + ", ".join(f"vk=0x{vk:02X}" for vk, _, _ in zero[:8]))

    ok = len(events) > 0 and not zero
    print(f"[{'PASS' if ok else 'FAIL'}] 每个注入按键都带扫描码")
    if not ok and zero:
        print("  → 这就是密码框少字的原因：目标程序认不出这些按键")
    print("==== %s ====" % ("通过" if ok else "有按键缺扫描码"))
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
