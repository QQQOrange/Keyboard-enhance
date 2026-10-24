"""渲染期间监视：把每个瞬间出现的可见窗口标题与坐标记下来。

用途：确认 `exe --render` 有没有在用户屏幕上弹出窗口。
用法：
    python tools/watch_windows.py out.txt 70        # 监视 70 秒，结果写 out.txt
    # 同时另开一个终端跑：
    #   dist\\KeyboardEnhance.exe --render dist\\shots

判据：所有属于 KeyboardEnhance 的窗口坐标都应该是**负数**（屏幕外）。
只读，不改任何东西。
"""
import ctypes
import ctypes.wintypes as wt
import sys
import time

u = ctypes.windll.user32
EnumProc = ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)

seen = {}
out = sys.argv[1] if len(sys.argv) > 1 else "windows_seen.txt"
duration = float(sys.argv[2]) if len(sys.argv) > 2 else 120.0


def snapshot():
    rows = []

    def cb(h, _):
        if u.IsWindowVisible(h):
            n = u.GetWindowTextLengthW(h)
            if n:
                b = ctypes.create_unicode_buffer(n + 1)
                u.GetWindowTextW(h, b, n + 1)
                pid = wt.DWORD()
                u.GetWindowThreadProcessId(h, ctypes.byref(pid))
                rect = wt.RECT()
                u.GetWindowRect(h, ctypes.byref(rect))
                rows.append((pid.value, b.value, rect.left, rect.top,
                             rect.right - rect.left, rect.bottom - rect.top))
        return True

    u.EnumWindows(EnumProc(cb), 0)
    return rows


end = time.time() + duration
while time.time() < end:
    for pid, title, x, y, w, h in snapshot():
        key = (pid, title)
        if key not in seen:
            seen[key] = (x, y, w, h, time.strftime("%H:%M:%S"))
    time.sleep(0.12)

with open(out, "w", encoding="utf-8") as f:
    for (pid, title), (x, y, w, h, ts) in sorted(seen.items(), key=lambda kv: kv[1][4]):
        f.write(f"{ts}\tpid={pid}\t{x},{y}\t{w}x{h}\t{title}\n")
print("done", len(seen))
