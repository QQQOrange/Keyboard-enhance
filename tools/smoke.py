"""冒烟测试：隔离实例 + 真实按键注入 + 截图 + 日志检查。

为什么用 Python 而不是 PowerShell：
  · PowerShell 注释里的中文会被按 GBK 解析（BOM-less 文件），把语法搞坏；
  · 会话内 Add-Type 的类型只注册一次，改了定义也还是用旧版（踩过两次）。
Python 每次都是新进程，没有这两个坑。

用法：python tools/smoke.py
"""

import ctypes
import ctypes.wintypes as wt
import json
import os
import re
import shutil
import subprocess
import sys
import time
from datetime import datetime

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
EXE = os.path.join(ROOT, "src", "CapsLockPlus", "bin", "Debug",
                   "net10.0-windows", "win-x64", "KeyboardEnhance.exe")

u32 = ctypes.WinDLL("user32", use_last_error=True)
gdi32 = ctypes.WinDLL("gdi32", use_last_error=True)
u32.SetProcessDPIAware()

VK_CAPITAL, VK_Q, VK_ESC = 0x14, 0x51, 0x1B
KEYEVENTF_KEYUP = 0x0002
INPUT_KEYBOARD = 1


class KEYBDINPUT(ctypes.Structure):
    _fields_ = [("wVk", wt.WORD), ("wScan", wt.WORD), ("dwFlags", wt.DWORD),
                ("time", wt.DWORD), ("dwExtraInfo", ctypes.POINTER(ctypes.c_ulong))]


class _INPUTunion(ctypes.Union):
    _fields_ = [("ki", KEYBDINPUT), ("padding", ctypes.c_byte * 32)]


class INPUT(ctypes.Structure):
    _fields_ = [("type", wt.DWORD), ("u", _INPUTunion)]


def key(vk, up=False):
    inp = INPUT(type=INPUT_KEYBOARD)
    inp.u.ki = KEYBDINPUT(wVk=vk, wScan=0,
                          dwFlags=KEYEVENTF_KEYUP if up else 0,
                          time=0, dwExtraInfo=None)
    u32.SendInput(1, ctypes.byref(inp), ctypes.sizeof(INPUT))


def tap(vk, hold_ms=40):
    key(vk)
    time.sleep(hold_ms / 1000)
    key(vk, up=True)


def enum_overlays():
    """列出浮层类窗口（Qbar / 提示 / 选窗口 / 录制条）。"""
    out = []

    @ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)
    def cb(hwnd, _):
        n = u32.GetWindowTextLengthW(hwnd)
        if n:
            buf = ctypes.create_unicode_buffer(n + 1)
            u32.GetWindowTextW(hwnd, buf, n + 1)
            t = buf.value
            if t in ("Qbar", "提示", "选择窗口", "正在录制宏"):
                r = wt.RECT()
                u32.GetWindowRect(hwnd, ctypes.byref(r))
                out.append((t, bool(u32.IsWindowVisible(hwnd)),
                            r.left, r.top, r.right - r.left, r.bottom - r.top))
        return True

    u32.EnumWindows(cb, 0)
    return out


def screenshot(path):
    """GDI 截全屏并存成 BMP（不依赖第三方库）。"""
    w = u32.GetSystemMetrics(0)
    h = u32.GetSystemMetrics(1)
    hdc = u32.GetDC(0)
    mem = gdi32.CreateCompatibleDC(hdc)
    bmp = gdi32.CreateCompatibleBitmap(hdc, w, h)
    gdi32.SelectObject(mem, bmp)
    gdi32.BitBlt(mem, 0, 0, w, h, hdc, 0, 0, 0x00CC0020)

    class BITMAPINFOHEADER(ctypes.Structure):
        _fields_ = [("biSize", wt.DWORD), ("biWidth", wt.LONG), ("biHeight", wt.LONG),
                    ("biPlanes", wt.WORD), ("biBitCount", wt.WORD),
                    ("biCompression", wt.DWORD), ("biSizeImage", wt.DWORD),
                    ("biXPelsPerMeter", wt.LONG), ("biYPelsPerMeter", wt.LONG),
                    ("biClrUsed", wt.DWORD), ("biClrImportant", wt.DWORD)]

    bi = BITMAPINFOHEADER()
    bi.biSize = ctypes.sizeof(BITMAPINFOHEADER)
    bi.biWidth, bi.biHeight = w, -h
    bi.biPlanes, bi.biBitCount, bi.biCompression = 1, 32, 0

    buf = ctypes.create_string_buffer(w * h * 4)
    gdi32.GetDIBits(mem, bmp, 0, h, buf, ctypes.byref(bi), 0)

    import struct
    header = b"BM" + struct.pack("<IHHI", 14 + 40 + len(buf), 0, 0, 14 + 40)
    dib = struct.pack("<IiiHHIIiiII", 40, w, -h, 1, 32, 0, len(buf), 0, 0, 0, 0)
    with open(path, "wb") as f:
        f.write(header + dib + buf.raw)

    gdi32.DeleteObject(bmp)
    gdi32.DeleteDC(mem)
    u32.ReleaseDC(0, hdc)
    return w, h


def sample(bmp_path, xs, ys):
    """从 32 位 BMP 里取几个点（top-down）。"""
    data = open(bmp_path, "rb").read()
    off = int.from_bytes(data[10:14], "little")
    w = int.from_bytes(data[18:22], "little", signed=True)
    res = {}
    for name, (x, y) in zip(xs, ys):
        i = off + (y * w + x) * 4
        b, g, r, _a = data[i:i + 4]
        res[name] = (r, g, b)
    return res


def main():
    stamp = datetime.now().strftime("%H%M%S")
    data = os.path.join(ROOT, f"out-smoke-{stamp}")
    os.makedirs(data, exist_ok=True)

    env = dict(os.environ)
    env["CAPSLOCKPLUS_DATA"] = data
    env["CAPSLOCKPLUS_MUTEX_SUFFIX"] = "_smoke"

    def start():
        return subprocess.Popen([EXE, "--tray"], env=env)

    # ---- 第一趟：生成默认设置 ----
    p = start()
    time.sleep(3.5)
    p.terminate()
    p.wait(timeout=10)
    time.sleep(1.0)

    settings = os.path.join(data, "settings.json")
    if not os.path.exists(settings):
        print("SETTINGS-MISSING", data)
        return 1

    raw = open(settings, encoding="utf-8").read()
    light = re.sub(r'"Theme"\s*:\s*"[^"]*"', '"Theme": "light"', raw)
    with open(settings, "w", encoding="utf-8", newline="") as f:
        f.write(light)
    json.loads(light)          # 写回后必须是合法 JSON
    print(f"settings ok ({len(light)} chars), theme forced to light")

    # ---- 第二趟：按住触发键 + Q 打开 Qbar ----
    p = start()
    time.sleep(4.0)

    key(VK_CAPITAL)
    time.sleep(0.12)
    tap(VK_Q)
    time.sleep(1.6)

    wins = enum_overlays()
    print("overlay windows:", wins if wins else "none")

    shot = os.path.join(ROOT, f"out-smoke-qbar-{stamp}.bmp")
    w, h = screenshot(shot)
    print(f"screenshot {w}x{h} -> {shot}")

    # Qbar 位于水平居中、工作区偏上。取它的面板中心采样底色。
    # 注意：窗口四周有 18 DIP 的透明边距（ShadowPad），
    # 取窗口边缘会采到桌面壁纸，必须取正中间。
    qbar = next((x for x in wins if x[0] == "Qbar"), None)
    if qbar:
        _, vis, left, top, ww, hh = qbar
        cx, cy = left + ww // 2, top + hh // 2
        px = sample(shot, ["center", "above", "below"],
                    [(cx, cy), (cx, cy - hh // 3), (cx, cy + hh // 3)])
        print("qbar rect:", left, top, ww, hh, "visible:", vis)
        print("qbar pixels:", px)

    key(VK_CAPITAL, up=True)
    tap(VK_ESC)
    time.sleep(0.6)
    p.terminate()
    try:
        p.wait(timeout=10)
    except subprocess.TimeoutExpired:
        p.kill()

    # ---- 日志 ----
    log = os.path.join(data, "logs", "app.log")
    if os.path.exists(log):
        text = open(log, encoding="utf-8", errors="replace").read().splitlines()
        errs = [l for l in text if "[ERROR]" in l]
        warns = [l for l in text if "[WARN]" in l]
        print(f"log lines={len(text)} errors={len(errs)} warnings={len(warns)}")
        for l in errs[:5]:
            print("  ", l)
    else:
        print("LOG-MISSING")

    return 0


if __name__ == "__main__":
    sys.exit(main())
