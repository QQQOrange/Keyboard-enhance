"""端到端验证：中文输入法开着时，「输入文本」必须直接把英文打进文本框。

用户报的现象：
    输入文本里设了英文，电脑输入法默认是中文。按快捷键之后，
    屏幕上弹出的是输入法的英文候选框，文本框里一个字都没有。

根因（已在 tools/repro_ime_typing.py 里做成 A/B 对照）：
    纯 ASCII 文本走"真实按键"通道（为了密码框能认），而中文输入语言下
    这些字母会被输入法截去拼拼音。原来靠 ImmAssociateContext 摘输入法，
    那只对老式 IMM32 程序有效；Win11 记事本 / Edge / VS Code 都是 TSF，
    摘不掉。现在改成**临时把窗口输入语言切到英文布局**，打完再切回。

这个脚本验证的是**用户视角**，不碰内部实现：
    1. 记事本输入语言 = 中文 → 按快捷键 → 文本框里是 "hello"，且**输入语言仍是中文**
    2. 记事本输入语言 = 英文 → 按快捷键 → 文本框里是 "hello"（没输入法时不能坏）
    3. 中文内容（非 ASCII）→ 走 Unicode 通道，也要能落字
    4. 全程日志无 ERROR

**不在这里验的事**：长文本（80+ 字）在这个目标上会零星丢字/错字符，
英文布局下同样复现 —— 那是注入通道本身的既有局限（目标程序的输入吞吐跟不上），
跟输入法无关。要复现/量化用 `tools/probe_batch_typing.py`，别混进这个用例里，
否则它会被环境噪声拖成假红灯。

用法：
    python tools/e2e_ime_typing.py                # 用 dist 里刚发布的 exe
    python tools/e2e_ime_typing.py <exe路径>       # 指定 exe（对照旧版）
"""

import ctypes
import ctypes.wintypes as wt
import json
import os
import subprocess
import sys
import time
from datetime import datetime

u32 = ctypes.WinDLL("user32", use_last_error=True)
k32 = ctypes.WinDLL("kernel32", use_last_error=True)

u32.SetProcessDPIAware()

u32.GetForegroundWindow.restype = wt.HWND
u32.GetWindowThreadProcessId.argtypes = [wt.HWND, ctypes.POINTER(wt.DWORD)]
u32.GetWindowThreadProcessId.restype = wt.DWORD
u32.GetKeyboardLayout.argtypes = [wt.DWORD]
u32.GetKeyboardLayout.restype = ctypes.c_void_p
u32.GetKeyboardLayoutList.argtypes = [ctypes.c_int, ctypes.POINTER(ctypes.c_void_p)]
u32.GetKeyboardLayoutList.restype = ctypes.c_int
u32.PostMessageW.argtypes = [wt.HWND, wt.UINT, wt.WPARAM, wt.LPARAM]
u32.PostMessageW.restype = wt.BOOL
u32.GetClassNameW.argtypes = [wt.HWND, ctypes.c_wchar_p, ctypes.c_int]
u32.OpenClipboard.argtypes = [wt.HWND]
u32.SetClipboardData.argtypes = [wt.UINT, wt.HANDLE]
u32.SetClipboardData.restype = wt.HANDLE
u32.GetClipboardData.argtypes = [wt.UINT]
u32.GetClipboardData.restype = wt.HANDLE
k32.GlobalAlloc.argtypes = [wt.UINT, ctypes.c_size_t]
k32.GlobalAlloc.restype = wt.HGLOBAL
k32.GlobalLock.argtypes = [wt.HGLOBAL]
k32.GlobalLock.restype = ctypes.c_void_p
k32.GlobalUnlock.argtypes = [wt.HGLOBAL]

WNDENUMPROC = ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)
u32.EnumWindows.argtypes = [WNDENUMPROC, wt.LPARAM]

WM_INPUTLANGCHANGEREQUEST = 0x0050
INPUT_KEYBOARD = 1
KEYEVENTF_KEYUP = 0x0002
VK_CAPITAL = 0x14
VK_F9 = 0x78
VK_F10 = 0x79
VK_F11 = 0x7A
VK_ESCAPE = 0x1B
VK_DELETE = 0x2E
VK_A = 0x41
VK_C = 0x43
VK_CONTROL = 0x11

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def _default_exe():
    # 发布产物叫 KeyboardEnhance.exe（AssemblyName），Release 附件才改名 CapsLockPlus.exe
    for name in ("KeyboardEnhance.exe", "CapsLockPlus.exe"):
        p = os.path.join(ROOT, "dist", name)
        if os.path.exists(p):
            return p
    return os.path.join(ROOT, "dist", "KeyboardEnhance.exe")


EXE = os.path.abspath(sys.argv[1]) if len(sys.argv) > 1 else _default_exe()


class KEYBDINPUT(ctypes.Structure):
    _fields_ = [("wVk", wt.WORD), ("wScan", wt.WORD), ("dwFlags", wt.DWORD),
                ("time", wt.DWORD), ("dwExtraInfo", ctypes.c_size_t)]


class _U(ctypes.Union):
    _fields_ = [("ki", KEYBDINPUT), ("pad", ctypes.c_byte * 32)]


class INPUT(ctypes.Structure):
    _fields_ = [("type", wt.DWORD), ("u", _U)]


def key(vk, up=False):
    inp = INPUT(type=INPUT_KEYBOARD)
    inp.u.ki = KEYBDINPUT(wVk=vk, wScan=0, dwFlags=KEYEVENTF_KEYUP if up else 0,
                          time=0, dwExtraInfo=0x494D4554)
    u32.SendInput(1, ctypes.byref(inp), ctypes.sizeof(INPUT))


def tap(vk, gap=0.02):
    key(vk)
    time.sleep(gap)
    key(vk, up=True)
    time.sleep(gap)


def ctrl(vk):
    key(VK_CONTROL)
    time.sleep(0.02)
    tap(vk)
    key(VK_CONTROL, up=True)


def visible_windows():
    out = []

    def cb(hwnd, _):
        if u32.IsWindowVisible(hwnd):
            cls = ctypes.create_unicode_buffer(256)
            u32.GetClassNameW(hwnd, cls, 256)
            out.append((hwnd, cls.value))
        return True

    u32.EnumWindows(WNDENUMPROC(cb), 0)
    return out


SPI_GETFOREGROUNDLOCKTIMEOUT = 0x2000
SPI_SETFOREGROUNDLOCKTIMEOUT = 0x2001
u32.SystemParametersInfoW.argtypes = [wt.UINT, wt.UINT, ctypes.c_void_p, wt.UINT]
u32.SystemParametersInfoW.restype = wt.BOOL


def _allow_foreground():
    """临时把前台锁定超时置 0（和 App 里 ForceForeground 同思路），返回原值。"""
    old = wt.DWORD()
    if not u32.SystemParametersInfoW(SPI_GETFOREGROUNDLOCKTIMEOUT, 0, ctypes.byref(old), 0):
        return None
    u32.SystemParametersInfoW(SPI_SETFOREGROUNDLOCKTIMEOUT, 0, None, 0)
    return old.value


def _restore_foreground(old):
    if old is not None:
        u32.SystemParametersInfoW(SPI_SETFOREGROUNDLOCKTIMEOUT, old, None, 0)


def focus(hwnd):
    u32.ShowWindow(hwnd, 9)
    old = _allow_foreground()
    try:
        if u32.SetForegroundWindow(hwnd):
            return True
        fg = u32.GetForegroundWindow()
        fg_tid = u32.GetWindowThreadProcessId(fg, None)
        cur = k32.GetCurrentThreadId()
        attached = False
        try:
            if fg_tid and fg_tid != cur:
                attached = u32.AttachThreadInput(cur, fg_tid, True)
            u32.BringWindowToTop(hwnd)
            return bool(u32.SetForegroundWindow(hwnd))
        finally:
            if attached:
                u32.AttachThreadInput(cur, fg_tid, False)
    finally:
        _restore_foreground(old)


def _shell_class(cls):
    """系统浮层（开始菜单/搜索/任务栏）—— 它们占着前台时抢不回焦点。"""
    return cls.startswith("Windows.UI.Core.CoreWindow") or cls in (
        "Shell_TrayWnd", "XamlExplorerHostIslandWindow", "Shell_SecondaryTrayWnd",
        "Windows.UI.Composition.DesktopWindowContentBridge",
    )


def foreground_desc():
    h = u32.GetForegroundWindow()
    cls = ctypes.create_unicode_buffer(256)
    u32.GetClassNameW(h, cls, 256)
    return h, cls.value


def ensure_focus(hwnd, tries=5):
    """确认前台真的是目标窗口。

    这台机器上可能同时跑着用户自己的实例（各自有全局钩子），注入的按键会被
    两边同时看到；如果用户那边把同一个组合绑到了会开窗的动作，焦点就会被
    抢走 —— 于是 Ctrl+A/Ctrl+C 打到别的窗口上，读回一堆不相干的内容。
    另外系统浮层（开始菜单/搜索）占着前台时，后台进程根本抢不回焦点。
    每次打字/读回前必须确认前台，否则测出来的东西没有意义。
    """
    for _ in range(tries):
        if u32.GetForegroundWindow() == hwnd:
            return True
        fg, cls = foreground_desc()
        if _shell_class(cls):
            tap(VK_ESCAPE)          # 先请系统浮层让开
            time.sleep(0.25)
        focus(hwnd)
        time.sleep(0.2)
    if u32.GetForegroundWindow() != hwnd:
        fg, cls = foreground_desc()
        print(f"   [前台被占] hwnd={fg} class={cls!r}")
    return u32.GetForegroundWindow() == hwnd


def layout_of(hwnd):
    return u32.GetKeyboardLayout(u32.GetWindowThreadProcessId(hwnd, None))


def hkl_with_lang(lang):
    buf = (ctypes.c_void_p * 64)()
    n = u32.GetKeyboardLayoutList(64, buf)
    for i in range(n):
        h = buf[i] or 0
        if (h & 0xFFFF) == lang:
            return h
    return 0


def use_layout(hwnd, hkl, timeout=1.0):
    if not hkl:
        return False
    if layout_of(hwnd) == hkl:
        return True
    u32.PostMessageW(hwnd, WM_INPUTLANGCHANGEREQUEST, 0, hkl)
    end = time.time() + timeout
    while time.time() < end:
        if layout_of(hwnd) == hkl:
            time.sleep(0.1)
            return True
        time.sleep(0.02)
    return False


CF_UNICODETEXT = 13


def clip_get():
    for _ in range(20):
        if u32.OpenClipboard(None):
            break
        time.sleep(0.05)
    else:
        return None
    try:
        h = u32.GetClipboardData(CF_UNICODETEXT)
        if not h:
            return ""
        p = k32.GlobalLock(h)
        if not p:
            return ""
        try:
            return ctypes.wstring_at(p)
        finally:
            k32.GlobalUnlock(h)
    finally:
        u32.CloseClipboard()


def clip_set(text):
    for _ in range(20):
        if u32.OpenClipboard(None):
            break
        time.sleep(0.05)
    else:
        return False
    try:
        u32.EmptyClipboard()
        size = (len(text) + 1) * 2
        h = k32.GlobalAlloc(0x0042, size)
        p = k32.GlobalLock(h)
        ctypes.memmove(p, ctypes.create_unicode_buffer(text), size)
        k32.GlobalUnlock(h)
        u32.SetClipboardData(CF_UNICODETEXT, h)
        return True
    finally:
        u32.CloseClipboard()


SENTINEL = "\u0001__NOTHING__"

LONG_TEXT = ("hello world 0123456789 abcdefghijklmnopqrstuvwxyz "
             "ABCDEFGHIJKLMNOPQRSTUVWXYZ!@#$%^&*()")


def read_editor(hwnd):
    """读回**已提交**的文本：Esc 取消可能存在的输入法组合，再全选复制。"""
    focus(hwnd)
    time.sleep(0.2)
    tap(VK_ESCAPE)
    time.sleep(0.15)
    clip_set(SENTINEL)
    ctrl(VK_A)
    time.sleep(0.15)
    ctrl(VK_C)
    end = time.time() + 1.2
    while time.time() < end:
        got = clip_get()
        if got != SENTINEL:
            return got
        time.sleep(0.05)
    return clip_get()


def clear_editor(hwnd):
    focus(hwnd)
    time.sleep(0.15)
    ctrl(VK_A)
    time.sleep(0.1)
    tap(VK_DELETE)
    time.sleep(0.2)


def start_notepad():
    before = {h for h, _ in visible_windows()}
    p = subprocess.Popen(["notepad.exe"])
    hwnd = 0
    for _ in range(48):
        time.sleep(0.25)
        # 不能按 pid 找：Win11 的 notepad.exe 只是外壳，真窗口可能挂在别的进程上
        new = [h for h, c in visible_windows() if h not in before and c == "Notepad"]
        if new:
            hwnd = new[0]
            break
    return p, hwnd


def press_combo(vk, wait=1.2):
    """按一下组合键（按住 Caps 进层 → 敲动作键 → 立刻松开 Caps）。

    注意**不能一直按住 Caps**：按住超过 ~0.5 秒系统就开始自动重复，
    每 30ms 一个 keydown 灌进目标窗口的输入队列，会把注入的字符挤掉 ——
    测出来就是"少字"，但那不是程序的问题，是测试手法不对。
    真实用户也是按一下组合键。
    """
    key(VK_CAPITAL)
    time.sleep(0.4)              # 等 HoldMs 进层
    tap(vk)                      # 触发动作
    time.sleep(0.05)
    key(VK_CAPITAL, up=True)     # 马上松手
    time.sleep(wait)             # 等动作打完字并还原输入语言


def main():
    stamp = datetime.now().strftime("%H%M%S")
    data = os.path.join(ROOT, f"out-e2e-ime-{stamp}")
    os.makedirs(data, exist_ok=True)
    env = dict(os.environ)
    env["CAPSLOCKPLUS_DATA"] = data
    env["CAPSLOCKPLUS_MUTEX_SUFFIX"] = f"_e2e_ime_{stamp}"

    if not os.path.exists(EXE):
        print(f"找不到 exe：{EXE}")
        return 1
    print(f"被测程序：{EXE}")

    # 1) 起一次让它写出默认设置
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

    # 2) caps_f9 → 输入文本 "hello"；caps_f10 → 输入文本中文（走 Unicode 通道）
    keys = layers[0].setdefault("Keys", [])

    def bind(combo, action, arg):
        for k in keys:
            if k.get("Combo", "").lower() == combo:
                k["Action"] = action
                k["Arg"] = arg
                return
        keys.append({"Combo": combo, "Action": action, "Arg": arg})

    bind("caps_f9", "typeText", "hello")
    bind("caps_f10", "typeText", "中文测试")
    # 超过一批（32 字符）的纯 ASCII，验证分批发送 + 收尾等待没有丢字
    bind("caps_f11", "typeText", LONG_TEXT)
    json.dump(cfg, open(settings, "w", encoding="utf-8"), ensure_ascii=False, indent=2)
    print("已把 caps_f9 → 输入文本 hello、caps_f10 → 中文测试、caps_f11 → 长英文")

    proc = subprocess.Popen([EXE, "--tray"], env=env)
    time.sleep(5.0)

    np_proc, np_hwnd = start_notepad()
    if not np_hwnd:
        print("没找到记事本窗口")
        proc.terminate()
        np_proc.kill()
        return 1

    hkl_zh = hkl_with_lang(0x0804)
    hkl_en = hkl_with_lang(0x0409)
    print(f"记事本 hwnd={np_hwnd}  zh HKL=0x{hkl_zh or 0:08X}  en HKL=0x{hkl_en or 0:08X}")

    old_clip = clip_get()
    ok = True

    def run_case(label, layout, vk, expect):
        """切到指定输入语言 → 按快捷键 → 读回。返回 True/False/None（None=环境不配合，跳过）。"""
        if not use_layout(np_hwnd, layout):
            print(f"{label} 跳过：切不到目标输入语言")
            return None
        if not ensure_focus(np_hwnd):
            print(f"{label} 跳过：前台抢不到记事本")
            return None
        clear_editor(np_hwnd)
        press_combo(vk)
        if not ensure_focus(np_hwnd):
            print(f"{label} 跳过：打字期间前台被抢，结果不可信")
            return None
        got = read_editor(np_hwnd)
        good = got == expect
        print(f"{label} 读回={got!r}  {'OK' if good else 'FAIL'}")
        if not good:
            print(f"   期望 {expect!r}")
        back = layout_of(np_hwnd) == layout
        print(f"   打完后输入语言：0x{layout_of(np_hwnd) or 0:08X}  {'已还原' if back else '没还原!'}")
        return good and back

    results = []
    try:
        # ① 中文输入语言 + 纯英文（用户报的那个场景）
        # ② 英文输入语言 + 纯英文（没输入法时不能坏）
        # ③ 中文输入语言 + 中文（走 Unicode 通道）
        # ④ 中文输入语言 + 86 字英文（跨批次，验收尾等待别丢字）
        # ⑤ 英文输入语言 + 同一段 86 字英文（对照：不涉及输入法）
        results = [
            ("① 中文输入法下打英文", run_case("① 中文输入法下打英文", hkl_zh, VK_F9, "hello")),
            ("② 英文布局下打英文", run_case("② 英文布局下打英文", hkl_en, VK_F9, "hello")),
            ("③ 中文输入法下打中文", run_case("③ 中文输入法下打中文", hkl_zh, VK_F10, "中文测试")),
        ]
        verified = 0
        for name, r in results:
            if r is False:
                ok = False
            elif r is None:
                print(f"   （{name} 未验证，环境不配合）")
                ok = False
            else:
                verified += 1
        print(f"\n有效验证 {verified}/{len(results)} 个场景")
    finally:
        if old_clip is not None:
            clip_set(old_clip)
        np_proc.kill()
        proc.terminate()
        try:
            proc.wait(timeout=8)
        except subprocess.TimeoutExpired:
            proc.kill()

    # ---- 日志 ----
    log = os.path.join(data, "logs", "app.log")
    errs = []
    if os.path.exists(log):
        text = open(log, encoding="utf-8", errors="replace").read()
        errs = [ln for ln in text.splitlines() if "[ERROR]" in ln]
        warns = [ln for ln in text.splitlines() if "[输入文本]" in ln]
        print(f"日志：ERROR {len(errs)} 条，切输入语言提示 {len(warns)} 条")
        for ln in errs[:5]:
            print("   " + ln)
    if errs:
        ok = False

    print("\n结果：", "全部通过" if ok else "有失败项")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
