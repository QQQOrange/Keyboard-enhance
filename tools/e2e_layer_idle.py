"""端到端：按住触发键 → 触发一个功能 → 停顿 → 再按另一个键，仍然要生效。

这是用户报的第二个问题（v4.5.4 修）：停顿后层被误判"触发键已松开"而退出，
之后的按键全部变回原生功能。

做法：隔离实例 + 真实注入按键 + 看日志里是否**两次**都触发了动作。
  Caps↓ → A(caps_a=moveWordLeft) → 等 3.5 秒 → S(caps_s=moveLeft) → Caps↑
"""

import ctypes
import ctypes.wintypes as wt
import os
import subprocess
import sys
import time
from datetime import datetime

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
EXE = os.path.join(ROOT, "src", "CapsLockPlus", "bin", "Debug",
                   "net10.0-windows", "win-x64", "KeyboardEnhance.exe")

u32 = ctypes.WinDLL("user32", use_last_error=True)
u32.SetProcessDPIAware()

VK_CAPITAL, VK_A, VK_S = 0x14, 0x41, 0x53
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


def main():
    stamp = datetime.now().strftime("%H%M%S")
    data = os.path.join(ROOT, f"out-idle-{stamp}")
    os.makedirs(data, exist_ok=True)
    env = dict(os.environ)
    env["CAPSLOCKPLUS_DATA"] = data
    env["CAPSLOCKPLUS_MUTEX_SUFFIX"] = f"_idle_{stamp}"

    p = subprocess.Popen([EXE, "--tray"], env=env)
    try:
        time.sleep(4.0)

        # 按住触发键进层（HoldMs 默认 200ms，先给它 600ms）
        key(VK_CAPITAL)
        time.sleep(0.6)

        # 第一个功能：caps_a = moveWordLeft
        key(VK_A); time.sleep(0.05); key(VK_A, up=True)
        time.sleep(0.4)

        # 用户停下来看了 3.5 秒（触发键仍然按着）
        print("停顿 3.5 秒（模拟看结果）……")
        time.sleep(3.5)

        # 第二个功能：caps_s = moveLeft
        key(VK_S); time.sleep(0.05); key(VK_S, up=True)
        time.sleep(0.5)

        key(VK_CAPITAL, up=True)
        time.sleep(0.6)
    finally:
        p.terminate()
        try:
            p.wait(timeout=8)
        except subprocess.TimeoutExpired:
            p.kill()

    log = os.path.join(data, "logs", "app.log")
    text = open(log, encoding="utf-8", errors="replace").read() if os.path.exists(log) else ""
    lines = [l for l in text.splitlines() if "动作：" in l]
    for l in lines:
        print("  ", l)

    a_ok = "moveWordLeft" in text
    s_ok = "moveLeft" in text
    errs = text.count("[ERROR]")

    print()
    print("第一次按键（停顿前）触发了 moveWordLeft:", a_ok)
    print("第二次按键（停顿后）触发了 moveLeft:", s_ok)
    print("日志 ERROR 条数:", errs)
    ok = a_ok and s_ok and errs == 0
    print("结论:", "停顿后层仍然有效 ✓" if ok else "停顿后层失效 ✗")
    return 0 if ok else 2


if __name__ == "__main__":
    sys.exit(main())
