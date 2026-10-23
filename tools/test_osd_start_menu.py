"""端到端验证：开始菜单打开时，亮度横条是否仍然可见。

复现用户的场景：
  1. 起 `--osd 50 --osd-hold 20000`，横条出现在屏幕底部居中；
  2. 先抓一次横条卡片内部的颜色（基线）；
  3. 按 Win 键打开开始菜单（它会覆盖屏幕底部中央）；
  4. 再抓同一块区域：颜色**没变**=横条仍在最上；变成开始菜单的底色=被盖住。

为什么用"前后对比"而不是认某个固定颜色：开始菜单是 acrylic 材质，
颜色随壁纸变，没有稳定值可认。横条自己在这 1 秒内不变，所以
"像素不变"就是最干净的判据。

用法：
    python tools/test_osd_start_menu.py <KeyboardEnhance.exe>

退出码 0 = 通过，1 = 失败。脚本结束会把开始菜单收起来。
"""

import ctypes
import ctypes.wintypes as wt
import os
import subprocess
import sys
import time

EXE = sys.argv[1] if len(sys.argv) > 1 else os.path.join("dist", "KeyboardEnhance.exe")
TITLE = "按键增强 横条"

u = ctypes.WinDLL("user32", use_last_error=True)
g = ctypes.WinDLL("gdi32", use_last_error=True)
u.SetProcessDPIAware()

VK_LWIN, KEYEVENTF_KEYUP = 0x5B, 0x0002


class BITMAPINFOHEADER(ctypes.Structure):
    _fields_ = [
        ("biSize", wt.DWORD), ("biWidth", ctypes.c_long), ("biHeight", ctypes.c_long),
        ("biPlanes", wt.WORD), ("biBitCount", wt.WORD), ("biCompression", wt.DWORD),
        ("biSizeImage", wt.DWORD), ("biXPelsPerMeter", ctypes.c_long),
        ("biYPelsPerMeter", ctypes.c_long), ("biClrUsed", wt.DWORD),
        ("biClrImportant", wt.DWORD),
    ]


class BITMAPINFO(ctypes.Structure):
    _fields_ = [("bmiHeader", BITMAPINFOHEADER), ("bmiColors", wt.DWORD * 3)]


g.CreateCompatibleDC.argtypes = [wt.HDC]
g.CreateCompatibleDC.restype = wt.HDC
g.CreateCompatibleBitmap.argtypes = [wt.HDC, ctypes.c_int, ctypes.c_int]
g.CreateCompatibleBitmap.restype = wt.HBITMAP
g.SelectObject.argtypes = [wt.HDC, wt.HGDIOBJ]
g.BitBlt.argtypes = [wt.HDC, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int,
                     wt.HDC, ctypes.c_int, ctypes.c_int, wt.DWORD]
g.GetDIBits.argtypes = [wt.HDC, wt.HBITMAP, wt.UINT, wt.UINT, wt.LPVOID,
                        ctypes.POINTER(BITMAPINFO), wt.UINT]
g.DeleteObject.argtypes = [wt.HGDIOBJ]
g.DeleteDC.argtypes = [wt.HDC]
u.GetDC.argtypes = [wt.HWND]
u.GetDC.restype = wt.HDC
u.ReleaseDC.argtypes = [wt.HWND, wt.HDC]


def grab(x, y, w, h):
    """抓一块屏幕像素，返回 (平均 RGB, 像素列表)。"""
    hdc = u.GetDC(None)
    mem = g.CreateCompatibleDC(hdc)
    bmp = g.CreateCompatibleBitmap(hdc, w, h)
    g.SelectObject(mem, bmp)
    g.BitBlt(mem, 0, 0, w, h, hdc, x, y, 0x00CC0020)

    bi = BITMAPINFO()
    bi.bmiHeader.biSize = ctypes.sizeof(BITMAPINFOHEADER)
    bi.bmiHeader.biWidth = w
    bi.bmiHeader.biHeight = -h
    bi.bmiHeader.biPlanes = 1
    bi.bmiHeader.biBitCount = 32

    buf = (ctypes.c_ubyte * (w * h * 4))()
    g.GetDIBits(mem, bmp, 0, h, buf, ctypes.byref(bi), 0)
    g.DeleteObject(bmp)
    g.DeleteDC(mem)
    u.ReleaseDC(None, hdc)

    px = [(buf[i + 2], buf[i + 1], buf[i]) for i in range(0, len(buf), 4)]
    if not px:
        return (0, 0, 0), []
    avg = tuple(sum(c[i] for c in px) // len(px) for i in range(3))
    return avg, px


def diff(a, b):
    return max(abs(a[i] - b[i]) for i in range(3))


def find_osd():
    found = []

    @ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)
    def cb(h, _):
        n = u.GetWindowTextLengthW(h)
        if n:
            b = ctypes.create_unicode_buffer(n + 1)
            u.GetWindowTextW(h, b, n + 1)
            if b.value == TITLE and u.IsWindowVisible(h):
                found.append(h)
        return True

    u.EnumWindows(cb, 0)
    return found


def band_of(h):
    b = wt.DWORD(0)
    if u.GetWindowBand(h, ctypes.byref(b)):
        return b.value
    return 0


def tap_win():
    u.keybd_event(VK_LWIN, 0, 0, None)
    time.sleep(0.04)
    u.keybd_event(VK_LWIN, 0, KEYEVENTF_KEYUP, None)


def start_menu_open():
    """开始菜单/搜索是否在前台。它们都在 band 6。"""
    return band_of(u.GetForegroundWindow()) == 6


def open_start_menu() -> bool:
    """把开始菜单打开，最多试三次。

    为什么要重试：`tap_win()` 偶尔会被系统的"Win 键已经按下"状态吃掉，
    只发一次时前台还是原来那个窗口 —— 那样后面的"像素没变"就是假通过。
    """
    for _ in range(3):
        if start_menu_open():
            return True
        tap_win()
        time.sleep(1.4)
        if start_menu_open():
            return True
        time.sleep(0.4)
    return False


def close_start_menu() -> None:
    for _ in range(3):
        if not start_menu_open():
            return
        tap_win()
        time.sleep(0.6)


def main():
    env = dict(os.environ)
    env["CAPSLOCKPLUS_DATA"] = os.path.join(os.getcwd(), ".e2e-start-menu")
    env["CAPSLOCKPLUS_MUTEX_SUFFIX"] = "-e2e-start-menu"

    print(f"启动 {EXE}")
    p = subprocess.Popen([EXE, "--osd", "100", "--osd-hold", "20000"], env=env)
    ok = False
    try:
        hwnd = None
        for _ in range(120):
            hs = find_osd()
            if hs:
                hwnd = hs[-1]
                break
            time.sleep(0.1)

        if not hwnd:
            print("✘ 横条没出现")
            return 1

        # 淡入 150ms，窗口一出现就抓会抓到半透明的中间态
        time.sleep(0.9)

        r = wt.RECT()
        u.GetWindowRect(hwnd, ctypes.byref(r))
        w, h = r.right - r.left, r.bottom - r.top
        print(f"横条 hwnd={hwnd} band={band_of(hwnd)} rect={r.left},{r.top} {w}x{h}")
        if w < 100 or h < 40:
            print("✘ 横条尺寸异常")
            return 1

        # 采样点：**进度条填充区**（--osd 100 时整条都是强调色）。
        #
        # 为什么认颜色而不是"前后不变"：卡片本身是 84% 不透明的材质，
        # 开始菜单会把桌面压暗，透过卡片看颜色会跟着变 —— 用"像素不变"
        # 当判据会把"正常显示"误判成"被盖住"（第一版就踩了）。
        # 强调蓝（日间 #0067C0 / 夜间 #4CC2FF）是开始菜单里不会出现的颜色，
        # 只要它还在，就说明横条画在上面。
        sx, sy = r.left + 130, r.top + int(h * 0.50) - 7
        sw, sh = 40, 14

        base_avg, _ = grab(sx, sy, sw, sh)
        # 阴性对照：横条左侧、同样被开始菜单覆盖的位置
        cx2 = r.left - sw * 3
        ctrl_base, _ = grab(cx2, sy, sw, sh)

        def blueness(c):
            return c[2] - c[0]

        print(f"开始菜单未打开：进度条={base_avg}（蓝={blueness(base_avg)}）"
              f" 对照点={ctrl_base}（蓝={blueness(ctrl_base)}）")

        if blueness(base_avg) < 40:
            print("✘ 横条本身就没画在任务栏/开始菜单之上（采样点看不到强调蓝）")
            print("  常见原因：窗口落回 band 1，被任务栏压住 —— 用 CAPSLOCKPLUS_OSD_BAND=0 可复现")
            return 1

        close_start_menu()
        if not open_start_menu():
            print("✘ 没能打开开始菜单，测试无效（不算通过）")
            return 1

        fg = u.GetForegroundWindow()
        print(f"开始菜单前台 hwnd={fg} band={band_of(fg)}")

        during_avg, _ = grab(sx, sy, sw, sh)
        ctrl_now, _ = grab(cx2, sy, sw, sh)
        print(f"开始菜单已打开：进度条={during_avg}（蓝={blueness(during_avg)}）"
              f" 对照点={ctrl_now}（蓝={blueness(ctrl_now)}）")

        ok = blueness(during_avg) >= 40
        print("✔ 横条仍在最上层（强调蓝还在），开始菜单只能盖住它旁边"
              if ok else "✘ 横条被开始菜单盖住了（强调蓝消失）")

        tap_win()
        time.sleep(0.6)

        # 第二个场景，更贴近用户的操作顺序：先开着开始菜单，再按亮度键。
        # band 的排序和两者出现的先后无关，但"新窗口会不会把开始菜单顶掉"
        # 是另一回事，值得单独确认一次。
        print("\n--- 场景二：开始菜单开着时再唤出横条 ---")
        close_start_menu()
        if not open_start_menu():
            print("⚠ 没能重新打开开始菜单，场景二跳过")
        else:
            print(f"开始菜单前台 band={band_of(u.GetForegroundWindow())}")

            env2 = dict(env)
            env2["CAPSLOCKPLUS_MUTEX_SUFFIX"] = "-e2e-start-menu-2"
            p2 = subprocess.Popen([EXE, "--osd", "100", "--osd-hold", "8000"], env=env2)
            try:
                hwnd2 = None
                for _ in range(80):
                    hs2 = [x for x in find_osd() if x != hwnd]
                    if hs2:
                        hwnd2 = hs2[-1]
                        break
                    time.sleep(0.1)

                if not hwnd2:
                    print("✘ 开始菜单开着时横条没出现")
                    ok = False
                else:
                    time.sleep(0.9)
                    r2 = wt.RECT()
                    u.GetWindowRect(hwnd2, ctypes.byref(r2))
                    sx2 = r2.left + 130
                    sy2 = r2.top + (r2.bottom - r2.top) // 2 - 7
                    c2, _ = grab(sx2, sy2, sw, sh)
                    still_open = band_of(u.GetForegroundWindow()) == 6
                    print(f"横条 band={band_of(hwnd2)} 进度条={c2}（蓝={blueness(c2)}）"
                          f" 开始菜单仍在前台={still_open}")

                    ok = blueness(c2) >= 40 and still_open
                    print("✔ 开始菜单没被顶掉，横条同时可见"
                          if ok else "✘ 场景二失败")
            finally:
                p2.terminate()
                try:
                    p2.wait(timeout=5)
                except Exception:
                    p2.kill()

        close_start_menu()
    finally:
        p.terminate()
        try:
            p.wait(timeout=5)
        except Exception:
            p.kill()

    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
