"""打开开始菜单，对比「开始菜单 / 任务栏 / 音量条 / 自绘横条」各自的 band。

用法：
    python tools/probe_start_band.py

会真的按一次 Win 键（0.8 秒后自动再按一次收起），仅用于取现场证据。
"""

import ctypes
import ctypes.wintypes as w
import sys
import time

sys.path.insert(0, ".")
from probe_band import (  # noqa: E402
    ZBID, band_of, class_of, collect, proc_name, text_of, user32,
)

user32.keybd_event.argtypes = [w.BYTE, w.BYTE, w.DWORD, ctypes.POINTER(w.ULONG)]

VK_LWIN = 0x5B
KEYEVENTF_KEYUP = 0x0002


def tap_win():
    user32.keybd_event(VK_LWIN, 0, 0, None)
    time.sleep(0.03)
    user32.keybd_event(VK_LWIN, 0, KEYEVENTF_KEYUP, None)


def main():
    print("=== 打开前 ===")
    before = {r["hwnd"]: r for r in collect()}

    tap_win()
    time.sleep(1.0)

    print("\n=== 开始菜单打开后（只列 band / 新出现 / 尺寸显著变化的窗口）===\n")
    after = collect()

    rows = []
    for r in after:
        b = r["band"]
        is_new = r["hwnd"] not in before
        hay = (r["proc"] + " " + r["cls"] + " " + r["title"]).lower()
        interesting = (
            is_new
            or b > 1
            or "start" in hay
            or "explorer" in hay
            or "shellexperience" in hay
            or "corewindow" in hay
        )
        if interesting:
            rows.append(r)

    rows.sort(key=lambda r: (-r["band"], r["proc"]))
    print(f"{'hwnd':>10}  {'band':<28} {'top':<4} {'new':<4} {'proc':<26} class | title")
    print("-" * 150)
    for r in rows:
        print(f"{r['hwnd']:>10}  {ZBID.get(r['band'], str(r['band'])):<28} "
              f"{'Y' if r['topmost'] else '-':<4} {'NEW' if r['hwnd'] not in before else '':<4} "
              f"{r['proc'][:25]:<26} {r['cls'][:30]} | {r['title'][:40]}")

    # 焦点窗口
    fg = user32.GetForegroundWindow()
    print(f"\n前台窗口 hwnd={fg}  class={class_of(fg)!r}  title={text_of(fg)!r}  "
          f"band={band_of(fg)}  proc={proc_name_quick(fg)}")

    tap_win()
    time.sleep(0.4)
    print("\n（已再次按 Win 收起开始菜单）")


def proc_name_quick(hwnd):
    import ctypes.wintypes as wt
    pid = wt.DWORD(0)
    user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
    return proc_name(pid.value)


if __name__ == "__main__":
    main()
