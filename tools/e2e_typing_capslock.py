"""端到端验证：「输入文本」在**大写锁定开着**时也要按用户要的大小写打。

真机链路，不绕任何一层：钩子收到 Caps 组合 → 进层 → 触发动作 →
`InputSender.Text` → `SendInput` → 记事本收到字符。

验三件事：
    ① Caps 开着时打 `Passw0rd!` —— 字母的大小写必须原样，不能翻面
       （修之前是 `pASSW0RD!`，密码框立刻报"大小写错误"）；
    ② Caps 关着时同一串文字仍然正确（回归）；
    ③ 打完字**大写锁定状态没被改掉** —— 输入文本不该动用户的键盘状态。

用法：
    python tools/e2e_typing_capslock.py              # 用 dist 里刚发布的 exe
    python tools/e2e_typing_capslock.py <exe路径>     # 指定 exe（用来对照旧版）
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
from e2e_ime_typing import (  # noqa: E402
    u32, k32, ROOT, EXE,
    ensure_focus, read_editor, clear_editor, start_notepad, press_combo,
    tap,
)

VK_CAPITAL = 0x14
VK_F9 = 0x78

# 大小写混排 + 数字符号，长度在"真实按键"通道的范围内（< 40 字，不走剪贴板）
TEXT = "Passw0rd!AbC"


def caps_on():
    """大写锁定开关状态。

    ⚠ 只能看 GetKeyState 的最低位：GetAsyncKeyState 的最低位是
    "上次调用之后按过没有"，连读两次第二次就变 False。
    """
    return bool(u32.GetKeyState(VK_CAPITAL) & 1)


def set_caps(want, tries=4):
    """把大写锁定切到 want。靠"短按一次 CapsLock"（产品的默认短按动作就是切换）。"""
    for _ in range(tries):
        if caps_on() == want:
            return True
        tap(VK_CAPITAL)
        time.sleep(0.9)
    return caps_on() == want


def bind(keys, combo, action, arg):
    for k in keys:
        if k.get("Combo", "").lower() == combo:
            k["Action"] = action
            k["Arg"] = arg
            return
    keys.append({"Combo": combo, "Action": action, "Arg": arg})


def run_case(name, want_caps, editor, tally):
    """一个用例：切好 Caps → 按组合键 → 读回文字 → 比对。"""
    if not set_caps(want_caps):
        print(f"[FAIL] {name}：大写锁定切不到 {'开' if want_caps else '关'}")
        tally["bad"] += 1
        return False
    state_before = caps_on()

    ensure_focus(editor)
    clear_editor(editor)

    press_combo(VK_F9, wait=1.6)
    got = read_editor(editor)
    state_after = caps_on()

    same = got == TEXT
    print(f"[{'PASS' if same else 'FAIL'}] {name}（Caps={'开' if want_caps else '关'}）"
          f" 期望=[{TEXT}] 实际=[{got}]")
    if not same:
        tally["bad"] += 1
    if state_after != state_before:
        print(f"[FAIL] {name}：打完字大写锁定被改掉了（{state_before} → {state_after}）")
        tally["bad"] += 1
    return same


def main():
    stamp = datetime.now().strftime("%H%M%S")
    data = os.path.join(ROOT, f"out-e2e-caps-{stamp}")
    os.makedirs(data, exist_ok=True)
    env = dict(os.environ)
    env["CAPSLOCKPLUS_DATA"] = data
    env["CAPSLOCKPLUS_MUTEX_SUFFIX"] = f"_e2e_caps_{stamp}"

    if not os.path.exists(EXE):
        print(f"找不到 exe：{EXE}")
        return 1
    print(f"被测程序：{EXE}")

    original_caps = caps_on()

    # 1) 起一次让它写出默认设置，再把 caps_f9 绑成「输入文本」
    first = subprocess.Popen([EXE, "--tray"], env=env)
    time.sleep(5.0)
    first.terminate()
    try:
        first.wait(timeout=8)
    except subprocess.TimeoutExpired:
        first.kill()
    time.sleep(0.6)

    settings = os.path.join(data, "settings.json")
    if not os.path.exists(settings):
        print("SETTINGS-MISSING")
        return 1

    cfg = json.load(open(settings, encoding="utf-8"))
    layers = cfg.get("Layers") or []
    if not layers:
        print("NO-LAYERS")
        return 1

    keys = layers[0].setdefault("Keys", [])
    bind(keys, "caps_f9", "typeText", TEXT)
    json.dump(cfg, open(settings, "w", encoding="utf-8"), ensure_ascii=False, indent=2)
    print(f"已把 caps_f9 → 输入文本 {TEXT}")

    proc = subprocess.Popen([EXE, "--tray"], env=env)
    time.sleep(5.0)

    np_proc, np_hwnd = start_notepad()
    if not np_hwnd:
        print("没找到记事本窗口")
        proc.terminate()
        return 1

    tally = {"bad": 0}
    try:
        run_case("Caps 开着打字", True, np_hwnd, tally)
        run_case("Caps 关着打字", False, np_hwnd, tally)
    finally:
        set_caps(original_caps)          # 用户的键盘状态要还原
        try:
            np_proc.terminate()
        except Exception:
            pass
        proc.terminate()
        time.sleep(0.5)

    print("==== %s（失败 %d 处）====" % ("全部通过" if tally["bad"] == 0 else "有失败", tally["bad"]))
    return 0 if tally["bad"] == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
