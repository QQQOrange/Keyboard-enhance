"""探测窗口所属的「Z 序带」（window band）。

Windows 8 之后，外壳（任务栏、开始菜单、音量条）把自己的窗口放进
私有的 band 里；普通进程的窗口即使设了 WS_EX_TOPMOST / HWND_TOP，
也永远排在 band 比它高的窗口下面。这个脚本把每个顶层窗口的 band
打出来，用来解释「为什么开始菜单能盖住自绘横条」。

用法：
    python tools/probe_band.py              # 只列 band != 0 的窗口
    python tools/probe_band.py --all        # 全部（很长）
    python tools/probe_band.py --find Start # 按标题/类名过滤
"""

import ctypes
import ctypes.wintypes as w
import subprocess
import sys

user32 = ctypes.WinDLL("user32", use_last_error=True)
kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)

ZBID = {
    0: "0  <none>",
    1: "1  DESKTOP",
    2: "2  IMMERSIVE_BACKGROUND",
    3: "3  IMMERSIVE_APPCHROME",
    4: "4  IMMERSIVE_MOGO",
    5: "5  IMMERSIVE_EDGY",
    6: "6  IMMERSIVE_INACTIVEMOBODY",
    7: "7  IMMERSIVE_INACTIVEDOCK",
    8: "8  IMMERSIVE_ACTIVEMOBODY",
    9: "9  IMMERSIVE_ACTIVEDOCK",
    10: "10 IMMERSIVE_BELOW",
    11: "11 UIACCESS",
    12: "12 IMMERSIVE_RESTRICTED",
    13: "13 SYSTEM_TOOLS",
}

try:
    GetWindowBand = user32.GetWindowBand
    GetWindowBand.argtypes = [w.HWND, ctypes.POINTER(w.DWORD)]
    GetWindowBand.restype = w.BOOL
    HAVE_GET = True
except AttributeError:
    HAVE_GET = False

try:
    SetWindowBand = user32.SetWindowBand
    SetWindowBand.argtypes = [w.HWND, w.HWND, w.DWORD]
    SetWindowBand.restype = w.BOOL
    HAVE_SET = True
except AttributeError:
    HAVE_SET = False

GetWindowLongW = user32.GetWindowLongW
GetWindowLongW.argtypes = [w.HWND, ctypes.c_int]
GetWindowLongW.restype = ctypes.c_long

GetWindowTextW = user32.GetWindowTextW
GetClassNameW = user32.GetClassNameW
IsWindowVisible = user32.IsWindowVisible
GetWindowThreadProcessId = user32.GetWindowThreadProcessId

GWL_EXSTYLE = -20
WS_EX_TOPMOST = 0x8

EnumProc = ctypes.WINFUNCTYPE(w.BOOL, w.HWND, w.LPARAM)


def proc_name(pid):
    try:
        out = subprocess.check_output(
            ["tasklist", "/FI", f"PID eq {pid}", "/FO", "CSV", "/NH"],
            encoding="mbcs", errors="replace")
        line = out.strip().strip('"')
        return line.split('","')[0]
    except Exception:
        return "?"


def band_of(hwnd):
    if not HAVE_GET:
        return -1
    b = w.DWORD(0)
    if GetWindowBand(hwnd, ctypes.byref(b)):
        return b.value
    return -1


def text_of(hwnd):
    n = GetWindowTextW(hwnd, None, 0)
    buf = ctypes.create_unicode_buffer(n + 2)
    GetWindowTextW(hwnd, buf, n + 2)
    return buf.value


def class_of(hwnd):
    buf = ctypes.create_unicode_buffer(256)
    GetClassNameW(hwnd, buf, 256)
    return buf.value


def collect():
    rows = []
    pids = {}

    def cb(hwnd, _):
        if not IsWindowVisible(hwnd):
            return True
        pid = w.DWORD(0)
        GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
        if pid.value not in pids:
            pids[pid.value] = proc_name(pid.value)
        ex = GetWindowLongW(hwnd, GWL_EXSTYLE) & 0xFFFFFFFF
        rows.append({
            "hwnd": hwnd,
            "title": text_of(hwnd),
            "cls": class_of(hwnd),
            "pid": pid.value,
            "proc": pids[pid.value],
            "band": band_of(hwnd),
            "topmost": bool(ex & WS_EX_TOPMOST),
        })
        return True

    user32.EnumWindows(EnumProc(cb), 0)
    return rows


def dump(rows, only_banded=True, needle=None):
    shown = []
    for r in rows:
        if needle:
            hay = (r["title"] + " " + r["cls"] + " " + r["proc"]).lower()
            if needle.lower() not in hay:
                continue
        if only_banded and r["band"] <= 0:
            continue
        shown.append(r)

    shown.sort(key=lambda r: (-r["band"], r["proc"]))
    print(f"{'hwnd':>10}  {'band':<28} {'top':<4} {'pid':>6}  {'proc':<28} class / title")
    print("-" * 150)
    for r in shown:
        print(f"{r['hwnd']:>10}  {ZBID.get(r['band'], str(r['band'])):<28} "
              f"{'Y' if r['topmost'] else '-':<4} {r['pid']:>6}  {r['proc'][:27]:<28} "
              f"{r['cls'][:34]} | {r['title'][:48]}")
    print(f"\n合计 {len(shown)} 个窗口")
    return shown


if __name__ == "__main__":
    args = sys.argv[1:]
    only = "--all" not in args
    needle = None
    if "--find" in args:
        needle = args[args.index("--find") + 1]

    if not HAVE_GET:
        print("user32!GetWindowBand 不存在（Windows 7？）")
    print(f"GetWindowBand={'OK' if HAVE_GET else 'NO'}  SetWindowBand={'OK' if HAVE_SET else 'NO'}\n")
    dump(collect(), only_banded=only, needle=needle)
