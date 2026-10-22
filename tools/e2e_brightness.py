"""端到端验证：真的按亮度键，看**显示器硬件**的亮度有没有变。

关键教训：一开始我用"截全屏算平均亮度"来判断，结果明明写进去了却显示"没变化" ——
因为截图捕获的是 GPU 渲染结果，**背光亮度根本不进画面**。
要判断，只能读 DDC/CI 的原始值（tools/ddc.py）。

流程：
    1. 起一个隔离实例，让它写出默认设置
    2. 把 caps_f9 / caps_f10 绑成 亮度- / 亮度+
    3. 启动 → 读硬件亮度 → 按 5 次亮度- → 再读 → 按 5 次亮度+ → 再读
"""

import ctypes
import ctypes.wintypes as wt
import json
import os
import subprocess
import sys
import time
from datetime import datetime

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import ddc

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
_DEFAULT_EXE = os.path.join(ROOT, "src", "CapsLockPlus", "bin", "Debug",
                            "net10.0-windows", "win-x64", "KeyboardEnhance.exe")
# 可以传一个 exe 路径做旧版本对照（python tools/e2e_brightness.py <exe>）
EXE = os.path.abspath(sys.argv[1]) if len(sys.argv) > 1 else _DEFAULT_EXE

u32 = ctypes.WinDLL("user32", use_last_error=True)
u32.SetProcessDPIAware()

VK_CAPITAL, VK_F9, VK_F10 = 0x14, 0x78, 0x79
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


def brightness():
    """读第一台支持 DDC 的屏；读不到返回 None。"""
    res = ddc.read()
    return res[0][1] if res else None


def start(env):
    return subprocess.Popen([EXE, "--tray"], env=env)


def stop(p):
    p.terminate()
    try:
        p.wait(timeout=8)
    except subprocess.TimeoutExpired:
        p.kill()
    time.sleep(0.6)


def main():
    stamp = datetime.now().strftime("%H%M%S")
    data = os.path.join(ROOT, f"out-e2e-{stamp}")
    os.makedirs(data, exist_ok=True)
    env = dict(os.environ)
    env["CAPSLOCKPLUS_DATA"] = data
    env["CAPSLOCKPLUS_MUTEX_SUFFIX"] = f"_e2e_{stamp}"

    baseline = brightness()
    if baseline is None:
        print("读不到 DDC 亮度，这台机器没法做这个验证")
        return 1
    print(f"初始硬件亮度：{baseline}%")

    # 1) 起一次生成默认设置
    first = start(env)
    time.sleep(4.0)
    stop(first)

    settings = os.path.join(data, "settings.json")
    if not os.path.exists(settings):
        print("SETTINGS-MISSING"); return 1

    cfg = json.load(open(settings, encoding="utf-8"))
    layers = cfg.get("Layers") or []
    if not layers:
        print("NO-LAYERS"); return 1

    keys = layers[0].setdefault("Keys", [])

    def bind(combo, action):
        """绑定用 Combo 标识（触发键_键名）；写错字段会被静默忽略。"""
        for k in keys:
            if k.get("Combo", "").lower() == combo:
                k["Action"] = action
                k["Arg"] = "10"
                return
        keys.append({"Combo": combo, "Action": action, "Arg": "10"})

    bind("caps_f9", "brightnessDown")
    bind("caps_f10", "brightnessUp")
    json.dump(cfg, open(settings, "w", encoding="utf-8"), ensure_ascii=False, indent=2)
    print("已把 caps_f9 / caps_f10 绑成 亮度- / 亮度+（步进 10）")

    p = start(env)
    time.sleep(4.0)

    def press(vk, times):
        key(VK_CAPITAL)
        time.sleep(0.35)            # 给层切换留出 HoldMs
        for _ in range(times):
            key(vk)
            time.sleep(0.05)
            key(vk, up=True)
            time.sleep(0.4)
        key(VK_CAPITAL, up=True)
        time.sleep(2.5)             # 等平滑推进走完

    ok = True
    try:
        before = brightness()
        press(VK_F9, 5)
        after_down = brightness()
        print(f"按 5 次「亮度-」：{before}% → {after_down}%（变化 {after_down - before:+d}）")
        if after_down > before - 10:
            print("  x 亮度没有明显下降")
            ok = False

        press(VK_F10, 5)
        after_up = brightness()
        print(f"再按 5 次「亮度+」：{after_down}% → {after_up}%（变化 {after_up - after_down:+d}）")
        if after_up < after_down + 10:
            print("  x 亮度没有明显回升")
            ok = False
    finally:
        # 一定要还原：这个测试会把屏幕调到很暗，不能留给你
        time.sleep(0.5)
        ddc.set_percent(baseline)
        time.sleep(0.8)
        now = brightness()
        print(f"已还原亮度：{now}%（原值 {baseline}%）")
        stop(p)

    log = os.path.join(data, "logs", "app.log")
    errs = 0
    if os.path.exists(log):
        text = open(log, encoding="utf-8", errors="replace").read()
        errs = text.count("[ERROR]")
        pump = "推进线程" in text
        print(f"日志：ERROR {errs} 条，推进线程{'已' if pump else '未'}启动")

    if errs:
        ok = False

    print()
    print("结论:", "硬件亮度随按键变化，链路通了" if ok else "还有问题，需要继续查")
    return 0 if ok else 2


if __name__ == "__main__":
    sys.exit(main())
