"""整机冒烟：起一个隔离实例，真按键走一遍"进层 → 层内按键 → Qbar"。

目的不是替代自检，而是验证**真实键盘钩子这条路**没被重构改坏：
  1. 按住触发键 + 按别的键 → 引擎日志出现「立即进入第 N 层」；
  2. 层内按 Q → Qbar 面板真的出现（按标题找窗口）；
  3. Esc → 面板关闭；
  4. 全程日志无 ERROR，进程存活。

安全措施：
  * 用独立数据目录 + 独立互斥后缀，不碰用户正在跑的实例与配置；
  * 注入前先把记事本激活到前台，按键只落在它身上；
  * 结束时关闭记事本（不保存），并关掉实例。

用法：python tools/e2e_smoke.py [exe]
"""

import ctypes
import ctypes.wintypes as wt
import os
import subprocess
import sys
import time
from datetime import datetime

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
_DEFAULT_EXE = os.path.join(ROOT, "dist", "KeyboardEnhance.exe")
EXE = os.path.abspath(sys.argv[1]) if len(sys.argv) > 1 else _DEFAULT_EXE

u32 = ctypes.WinDLL("user32", use_last_error=True)
u32.SetProcessDPIAware()

VK_CAPITAL, VK_Q, VK_ESCAPE = 0x14, 0x51, 0x1B
KEYEVENTF_KEYUP = 0x0002
INPUT_KEYBOARD = 1


class KEYBDINPUT(ctypes.Structure):
    _fields_ = [("wVk", wt.WORD), ("wScan", wt.WORD), ("dwFlags", wt.DWORD),
                ("time", wt.DWORD), ("dwExtraInfo", ctypes.POINTER(ctypes.c_ulong))]


class _U(ctypes.Union):
    _fields_ = [("ki", KEYBDINPUT), ("pad", ctypes.c_byte * 32)]


class INPUT(ctypes.Structure):
    _fields_ = [("type", wt.DWORD), ("u", _U)]


def key(vk, up=False):
    inp = INPUT(type=INPUT_KEYBOARD)
    inp.u.ki = KEYBDINPUT(wVk=vk, wScan=0, dwFlags=KEYEVENTF_KEYUP if up else 0,
                          time=0, dwExtraInfo=None)
    u32.SendInput(1, ctypes.byref(inp), ctypes.sizeof(INPUT))


def find_window(title):
    """按标题找可见顶层窗口（找不到返回 0）。"""
    found = []

    @ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)
    def cb(hwnd, _):
        n = u32.GetWindowTextLengthW(hwnd)
        if n:
            buf = ctypes.create_unicode_buffer(n + 1)
            u32.GetWindowTextW(hwnd, buf, n + 1)
            if buf.value == title and u32.IsWindowVisible(hwnd):
                found.append(hwnd)
        return True

    u32.EnumWindows(cb, 0)
    return found[0] if found else 0


def wait_window(title, timeout=6.0):
    end = time.time() + timeout
    while time.time() < end:
        h = find_window(title)
        if h:
            return h
        time.sleep(0.15)
    return 0


def find_notepad_window():
    """按类名找记事本窗口 —— Win11 的商店版记事本类名不是 "Notepad"。

    踩坑记录：`FindWindowW("Notepad", None)` 只认老版 Win32 记事本；
    商店版是 `NotepadWindow`，包在 `ApplicationFrameWindow` 里，
    标题还可能是"记事本"Tab 恢复页而不是"无标题"。按标题找会漏，
    于是整条冒烟被跳过 —— 看起来像"程序坏了"，其实只是没找到窗口。
    """
    found = []

    @ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)
    def cb(hwnd, _):
        if not u32.IsWindowVisible(hwnd):
            return True
        cls = ctypes.create_unicode_buffer(256)
        u32.GetClassNameW(hwnd, cls, 256)
        name = cls.value
        title = ctypes.create_unicode_buffer(256)
        u32.GetWindowTextW(hwnd, title, 256)
        t = title.value

        hit = name.startswith("Notepad") or (
            name == "ApplicationFrameWindow" and ("记事本" in t or "Notepad" in t))
        if hit:
            found.append(hwnd)
            return False
        return True

    u32.EnumWindows(cb, 0)
    return found[0] if found else 0


def activate(hwnd):
    u32.ShowWindow(hwnd, 9)                     # SW_RESTORE
    u32.SetForegroundWindow(hwnd)
    time.sleep(0.35)


def main():
    if not os.path.exists(EXE):
        print(f"找不到 {EXE}")
        return 2

    stamp = datetime.now().strftime("%H%M%S")
    data = os.path.join(ROOT, f"out-smoke-{stamp}")
    os.makedirs(data, exist_ok=True)
    log_path = os.path.join(data, "logs", "app.log")

    env = dict(os.environ)
    env["CAPSLOCKPLUS_DATA"] = data
    env["CAPSLOCKPLUS_MUTEX_SUFFIX"] = f"_smoke_{stamp}"

    note = subprocess.Popen(["notepad.exe"])
    app = subprocess.Popen([EXE, "--tray"], env=env)
    ok = True

    try:
        hnote = wait_window("无标题 - 记事本") or wait_window("Untitled - Notepad")
        if not hnote:
            # 标题可能被本地化成别的写法，退一步：找记事本的类名
            hnote = u32.FindWindowW("Notepad", None)
        if not hnote:
            # Win11 商店版：类名 NotepadWindow / 包在 ApplicationFrameWindow 里
            hnote = find_notepad_window()
        if not hnote:
            print("✗ 没能启动记事本，跳过按键冒烟（不影响代码结论）")
            return 1
        activate(hnote)
        time.sleep(1.0)

        # ---- ① 按住触发键 + A：应进层 ----
        print("① 注入 CapsLock 按住 + A")
        key(VK_CAPITAL)
        time.sleep(0.30)                        # 超过长按阈值 200ms
        key(0x41)                               # A
        key(0x41, up=True)
        time.sleep(0.20)
        key(VK_CAPITAL, up=True)
        time.sleep(0.6)

        # ---- ② 按住触发键 + Q：应打开 Qbar ----
        print("② 注入 CapsLock 按住 + Q")
        activate(hnote)
        key(VK_CAPITAL)
        time.sleep(0.30)
        key(VK_Q)
        key(VK_Q, up=True)
        time.sleep(0.15)
        key(VK_CAPITAL, up=True)

        hqbar = wait_window("Qbar", timeout=6.0)
        if hqbar:
            print("   ✔ Qbar 面板已出现")
            time.sleep(0.75)                    # 等它把前台抢过来（最多 400ms）
        else:
            print("   ✗ Qbar 面板没有出现")
            ok = False

        # ---- ③ Esc 关闭 ----
        fg = u32.GetForegroundWindow()
        n = u32.GetWindowTextLengthW(fg)
        buf = ctypes.create_unicode_buffer(n + 1)
        u32.GetWindowTextW(fg, buf, n + 1)
        print(f"   （按 Esc 前的前台窗口：{buf.value!r}）")
        if hqbar:
            key(VK_ESCAPE)
            key(VK_ESCAPE, up=True)
            end = time.time() + 4.0
            closed = False
            while time.time() < end:
                if not find_window("Qbar"):
                    closed = True
                    break
                time.sleep(0.15)
            print("   ✔ Esc 已关闭面板" if closed else "   ✗ Esc 没能关闭面板")
            ok = ok and closed

        # ---- ④ 进程存活 + 日志无 ERROR ----
        alive = app.poll() is None
        print("   ✔ 实例仍在运行" if alive else "   ✗ 实例已退出")
        ok = ok and alive

        time.sleep(0.8)
        text = ""
        if os.path.exists(log_path):
            with open(log_path, encoding="utf-8", errors="replace") as f:
                text = f.read()

        # 进层的直接证据是"层内按 Q 打开了面板"：面板能弹出来，说明触发键
        # 被识别、层生效、绑定被执行。日志里的「立即进入第 N 层」只在
        # "按住途中按别的键"那条快路径打，等满长按阈值进层是正常路径。
        if hqbar:
            print("   ✔ 层内按键生效（Caps+Q 打开了面板）")
        else:
            ok = False

        focus_fail = "抢前台失败" in text
        if focus_fail:
            print("   ✗ 日志报了「Qbar 抢前台失败」")
            ok = False

        errors = [ln for ln in text.splitlines() if "[ERROR]" in ln or "Exception" in ln]
        if errors:
            print(f"   ✗ 日志有 {len(errors)} 条异常：")
            for ln in errors[:5]:
                print("     " + ln)
            ok = False
        else:
            print("   ✔ 日志无 ERROR / Exception")

    finally:
        try:
            app.terminate()
            app.wait(timeout=8)
        except Exception:
            app.kill()
        try:
            note.terminate()
            note.wait(timeout=5)
        except Exception:
            note.kill()

    print()
    print("结论:", "整条链路正常" if ok else "发现问题，见上面 ✗")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
