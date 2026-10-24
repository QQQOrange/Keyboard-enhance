"""复现并对比：中文输入法开着时，「输入文本」为什么会在输入法候选框里拼拼音。

四种注入策略在同一个真实目标（记事本，Win11 版，TSF 输入法架构）上做 A/B：

  A  imm+keys      现在的实现：ImmAssociateContext(hwnd, NULL) 摘输入法 + 真实虚拟键
  B  keys          只发真实虚拟键（对照，确认"摘输入法"这一步有没有用）
  C  unicode       只发 KEYEVENTF_UNICODE（不经过布局与输入法）
  D  layout+keys   先把窗口输入语言临时切到 en-US，再发真实虚拟键，事后还原

判定：跑完按 Esc（取消可能存在的组合）→ Ctrl+A → Ctrl+C 读回**已提交**的文本。
  · 读回 "hello"  → 文字直接落进文本框（正确）
  · 读回空字符串  → 被输入法当成拼音吃了（就是这个 bug）

用法：python tools/repro_ime_typing.py
"""

import ctypes
import ctypes.wintypes as wt
import subprocess
import sys
import time

u32 = ctypes.WinDLL("user32", use_last_error=True)
k32 = ctypes.WinDLL("kernel32", use_last_error=True)
imm = ctypes.WinDLL("imm32", use_last_error=True)

u32.SetProcessDPIAware()

# ---- 原型：全部显式声明，避免 64 位下被截断成 32 位 int ----
u32.GetForegroundWindow.restype = wt.HWND
u32.GetWindowThreadProcessId.argtypes = [wt.HWND, ctypes.POINTER(wt.DWORD)]
u32.GetWindowThreadProcessId.restype = wt.DWORD
u32.GetKeyboardLayout.argtypes = [wt.DWORD]
u32.GetKeyboardLayout.restype = ctypes.c_void_p
u32.GetKeyboardLayoutList.argtypes = [ctypes.c_int, ctypes.POINTER(ctypes.c_void_p)]
u32.GetKeyboardLayoutList.restype = ctypes.c_int
u32.LoadKeyboardLayoutW.argtypes = [ctypes.c_wchar_p, wt.UINT]
u32.LoadKeyboardLayoutW.restype = ctypes.c_void_p
u32.PostMessageW.argtypes = [wt.HWND, wt.UINT, wt.WPARAM, wt.LPARAM]
u32.PostMessageW.restype = wt.BOOL
u32.GetClassNameW.argtypes = [wt.HWND, ctypes.c_wchar_p, ctypes.c_int]

imm.ImmIsIME.argtypes = [ctypes.c_void_p]
imm.ImmIsIME.restype = wt.BOOL
imm.ImmAssociateContext.argtypes = [wt.HWND, ctypes.c_void_p]
imm.ImmAssociateContext.restype = ctypes.c_void_p

WNDENUMPROC = ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)
u32.EnumWindows.argtypes = [WNDENUMPROC, wt.LPARAM]

WM_INPUTLANGCHANGEREQUEST = 0x0050
KLF_ACTIVATE = 0x0001
INPUT_KEYBOARD = 1
KEYEVENTF_KEYUP = 0x0002
KEYEVENTF_UNICODE = 0x0004

VK_CAPITAL = 0x14
VK_ESCAPE = 0x1B
VK_A = 0x41
VK_C = 0x43
VK_CONTROL = 0x11


class KEYBDINPUT(ctypes.Structure):
    _fields_ = [("wVk", wt.WORD), ("wScan", wt.WORD), ("dwFlags", wt.DWORD),
                ("time", wt.DWORD), ("dwExtraInfo", ctypes.c_size_t)]


class _U(ctypes.Union):
    _fields_ = [("ki", KEYBDINPUT), ("pad", ctypes.c_byte * 32)]


class INPUT(ctypes.Structure):
    _fields_ = [("type", wt.DWORD), ("u", _U)]


def key(vk, up=False, scan=0, flags=0, tag=0x494D4554):
    inp = INPUT(type=INPUT_KEYBOARD)
    inp.u.ki = KEYBDINPUT(wVk=0 if flags & KEYEVENTF_UNICODE else vk,
                          wScan=scan if flags & KEYEVENTF_UNICODE else 0,
                          dwFlags=flags | (KEYEVENTF_KEYUP if up else 0),
                          time=0, dwExtraInfo=tag)
    u32.SendInput(1, ctypes.byref(inp), ctypes.sizeof(INPUT))


def tap(vk, gap=0.012):
    key(vk)
    time.sleep(gap)
    key(vk, up=True)
    time.sleep(gap)


def ctrl(vk):
    key(VK_CONTROL)
    time.sleep(0.02)
    tap(vk)
    key(VK_CONTROL, up=True)


# ---------------- 窗口 ----------------

def visible_windows(pid=None):
    out = []

    def cb(hwnd, _):
        p = wt.DWORD()
        tid = u32.GetWindowThreadProcessId(hwnd, ctypes.byref(p))
        if (pid is None or p.value == pid) and u32.IsWindowVisible(hwnd):
            cls = ctypes.create_unicode_buffer(256)
            u32.GetClassNameW(hwnd, cls, 256)
            out.append((hwnd, tid, p.value, cls.value))
        return True

    u32.EnumWindows(WNDENUMPROC(cb), 0)
    return out


def focus(hwnd):
    u32.ShowWindow(hwnd, 9)
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


def layout_of(hwnd):
    tid = u32.GetWindowThreadProcessId(hwnd, None)
    return u32.GetKeyboardLayout(tid)


def hkl_with_lang(lang):
    buf = (ctypes.c_void_p * 64)()
    n = u32.GetKeyboardLayoutList(64, buf)
    for i in range(n):
        h = buf[i] or 0
        if (h & 0xFFFF) == lang:
            return h
    return 0


def use_layout(hwnd, hkl, timeout=0.6):
    """给目标窗口所在线程切输入语言，等到真正生效（WM_INPUTLANGCHANGEREQUEST 是异步的）。"""
    if not hkl:
        return False
    if layout_of(hwnd) == hkl:
        return True
    u32.PostMessageW(hwnd, WM_INPUTLANGCHANGEREQUEST, 0, hkl)
    end = time.time() + timeout
    while time.time() < end:
        if layout_of(hwnd) == hkl:
            return True
        time.sleep(0.01)
    return False


# ---------------- 剪贴板 ----------------

CF_UNICODETEXT = 13
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
k32.GlobalFree.argtypes = [wt.HGLOBAL]


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
        h = k32.GlobalAlloc(0x0042, size)          # GMEM_MOVEABLE|GMEM_ZEROINIT
        p = k32.GlobalLock(h)
        ctypes.memmove(p, ctypes.create_unicode_buffer(text), size)
        k32.GlobalUnlock(h)
        u32.SetClipboardData(CF_UNICODETEXT, h)
        return True
    finally:
        u32.CloseClipboard()


# ---------------- 用例 ----------------

def type_keys(text, gap=0.012):
    for ch in text:
        vk = u32.VkKeyScanW(ord(ch)) & 0xFF
        key(vk)
        time.sleep(gap)
        key(vk, up=True)
        time.sleep(gap)


def type_unicode(text, gap=0.008):
    for ch in text:
        key(0, scan=ord(ch), flags=KEYEVENTF_UNICODE)
        time.sleep(gap)
        key(0, up=True, scan=ord(ch), flags=KEYEVENTF_UNICODE)
        time.sleep(gap)


SENTINEL = "\u0001__NOTHING__"


def read_committed(hwnd, timeout=0.8):
    focus(hwnd)
    time.sleep(0.15)
    tap(VK_ESCAPE)                 # 取消可能存在的输入法组合
    time.sleep(0.12)
    clip_set(SENTINEL)
    ctrl(VK_A)
    time.sleep(0.12)
    ctrl(VK_C)
    end = time.time() + timeout
    while time.time() < end:
        got = clip_get()
        if got != SENTINEL:
            return got
        time.sleep(0.05)
    return clip_get()


def main():
    before = {h for h, *_ in visible_windows()}
    np = subprocess.Popen(["notepad.exe"])
    # 不能按 pid 找：Win11 的 notepad.exe 只是外壳，真正的窗口可能挂在另一个进程上
    hwnd = 0
    for _ in range(40):
        time.sleep(0.25)
        new = [w for w in visible_windows()
               if w[0] not in before and w[3] == "Notepad"]
        if new:
            hwnd = new[0][0]
            break
    if not hwnd:
        print("没找到记事本窗口")
        np.kill()
        return 1
    print(f"目标：记事本 hwnd={hwnd} pid={u32.GetWindowThreadProcessId(hwnd, None)}")

    hkl_zh = hkl_with_lang(0x0804)
    hkl_en = hkl_with_lang(0x0409) or u32.LoadKeyboardLayoutW("00000409", 0)
    print(f"zh HKL=0x{hkl_zh or 0:08X}  en HKL=0x{hkl_en or 0:08X}")

    focus(hwnd)
    time.sleep(0.3)
    ok = use_layout(hwnd, hkl_zh)
    print(f"把记事本输入语言切到中文：{'成功' if ok else '失败'}"
          f"（当前 HKL=0x{layout_of(hwnd) or 0:08X}）")

    old_clip = clip_get()
    results = {}
    try:
        for name, fn in [
            ("A 摘输入法+真实按键", "imm"),
            ("B 只发真实按键", "keys"),
            ("C 只发 Unicode", "unicode"),
            ("D 切 en-US+真实按键", "layout"),
        ]:
            focus(hwnd)
            use_layout(hwnd, hkl_zh)
            time.sleep(0.2)
            ctrl(VK_A)
            tap(0x2E)                        # Delete 清空
            time.sleep(0.2)

            if fn == "imm":
                old = imm.ImmAssociateContext(hwnd, None)
                try:
                    type_keys("hello")
                finally:
                    if old:
                        imm.ImmAssociateContext(hwnd, old)
            elif fn == "keys":
                type_keys("hello")
            elif fn == "unicode":
                type_unicode("hello")
            else:
                use_layout(hwnd, hkl_en)
                time.sleep(0.25)
                type_keys("hello")
                time.sleep(0.25)
                use_layout(hwnd, hkl_zh)
                time.sleep(0.2)

            time.sleep(0.45)
            got = read_committed(hwnd)
            results[name] = got
            flag = "OK  直接落字" if got == "hello" else "FAIL 被输入法吃了"
            print(f"  {name:<22} 读回={got!r:<12} {flag}")

        print("\n当前输入语言（应已回到中文）：0x%08X" % (layout_of(hwnd) or 0))
    finally:
        if old_clip is not None:
            clip_set(old_clip)
        np.kill()
        print("已关闭记事本、还原剪贴板")

    return 0 if sum(1 for v in results.values() if v == "hello") else 1


if __name__ == "__main__":
    sys.exit(main())
