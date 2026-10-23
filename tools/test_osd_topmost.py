"""验证亮度横条"永远在最上层"（用户报：按亮度键，横条被任务栏挡住）。

根因：SetWindowPos(hwnd, HWND_TOPMOST) 只保证"在所有非 topmost 窗口之上"，
在**同为 topmost** 的窗口之间不改次序 —— 任务栏（或播放器悬浮条）顶上来之后，
反复发 HWND_TOPMOST 是抢不回来的。只有 HWND_TOP 能顶到 Z 序最前。

本脚本用真任务栏复现：
  1. 起 `--osd 50 --osd-hold 12000`，等横条出现；
  2. 记基线：横条是否在任务栏之上；
  3. 把任务栏用 HWND_TOP 顶到最前（等价于"任务栏弹出来/被点了一下"），
     立刻再查一次，确认横条确实被压下去了（否则这次测试没有意义，会如实报出来）；
  4. 等 800ms（重提升周期 200ms，给 4 次机会），再查横条是否回到任务栏之上。

修好之前第 4 步是 False（连发一秒都没变化），修好之后是 True。

用法：python tools/test_osd_topmost.py <KeyboardEnhance.exe>
任务栏的"自动隐藏"要**关掉**（始终显示），否则它本来就不在屏幕上，测不出东西。
"""
import ctypes
import ctypes.wintypes as wt
import os
import subprocess
import sys
import tempfile
import time

EXE = sys.argv[1]
TITLE = "按键增强 横条"

u = ctypes.windll.user32
u.SetProcessDPIAware()

HWND_TOP = 0
FLAGS = 0x0002 | 0x0001 | 0x0010 | 0x0400     # NOMOVE | NOSIZE | NOACTIVATE | NOSENDCHANGING


def all_hwnds():
    out = []

    @ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)
    def cb(h, _):
        out.append(h)
        return True

    u.EnumWindows(cb, 0)
    return out


def title(h):
    n = u.GetWindowTextLengthW(h)
    if not n:
        return ""
    b = ctypes.create_unicode_buffer(n + 1)
    u.GetWindowTextW(h, b, n + 1)
    return b.value


def find_osd():
    for h in all_hwnds():
        if title(h) == TITLE:
            return h
    return None


def above(a, b):
    """a 是否压在 b 之上（用 EnumWindows 的相对位次：谁的数字小谁在上）。"""
    order = all_hwnds()
    if a not in order or b not in order:
        return None
    return order.index(a) < order.index(b)


tb = u.FindWindowW("Shell_TrayWnd", None)
if not tb:
    sys.exit("找不到任务栏，测不了")

class APPBARDATA(ctypes.Structure):
    _fields_ = [("cbSize", wt.DWORD), ("hWnd", wt.HWND), ("uCallbackMessage", wt.UINT),
                ("uEdge", wt.UINT), ("rc", wt.RECT), ("lParam", wt.LPARAM)]


ab = APPBARDATA()
ab.cbSize = ctypes.sizeof(APPBARDATA)
state = ctypes.windll.shell32.SHAppBarMessage(4, ctypes.byref(ab))   # ABM_GETSTATE
if state & 1:
    print("提示：任务栏是自动隐藏，测不出遮挡（本脚本建议任务栏始终显示）")

env = os.environ.copy()
env["CAPSLOCKPLUS_DATA"] = tempfile.mkdtemp(prefix="osdtop_")
env["CAPSLOCKPLUS_MUTEX_SUFFIX"] = "_top%d" % int(time.time())

proc = subprocess.Popen([EXE, "--osd", "50", "--osd-hold", "12000"], env=env)
try:
    osd = None
    for _ in range(140):
        time.sleep(0.05)
        osd = find_osd()
        if osd:
            break
    if not osd:
        sys.exit("横条没出现，测试无法进行")

    time.sleep(0.9)                    # 等淡入 + 定位稳定
    base = above(osd, tb)
    print(f"[1] 基线：横条={hex(osd)} 任务栏={hex(tb)} 横条在任务栏之上={base}")

    u.SetWindowPos(tb, HWND_TOP, 0, 0, 0, 0, FLAGS)
    covered = above(osd, tb)           # 立刻查，不给重提升插队的机会
    print(f"[2] 把任务栏顶到最前：横条还在任务栏之上={covered}")

    # 采样 1.2 秒：外壳有时会跟我们的重提升抢 Z 序，一次采样看不出真相
    samples = []
    for _ in range(12):
        time.sleep(0.10)
        samples.append(above(osd, tb))
    after = samples[-1]
    print(f"[3] 之后 1.2s 的采样：{''.join('Y' if v else 'n' for v in samples)}（Y=在任务栏之上）")

    if covered is True:
        print("RESULT: SKIP（任务栏没压上去，这次结果不算数）")
        sys.exit(2)

    ok = after is True
    print("RESULT:", "PASS" if ok else "FAIL")
    sys.exit(0 if ok else 1)
finally:
    proc.kill()
