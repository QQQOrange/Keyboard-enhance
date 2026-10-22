"""对比两种发布形态的冷/热启动耗时。

做法：启动进程 → 轮询日志里出现「已启动」那一行 → 记录耗时 → 结束进程。
日志时间戳精度到毫秒，足够看出 JIT 带来的量级差异。

用法：python tools/bench_start.py <exeA> <exeB> [轮数]
"""

import os
import subprocess
import sys
import time
import statistics

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def once(exe, tag, idx):
    data = os.path.join(ROOT, f"out-bench-{tag}-{idx}")
    os.makedirs(data, exist_ok=True)
    env = dict(os.environ)
    env["CAPSLOCKPLUS_DATA"] = data
    env["CAPSLOCKPLUS_MUTEX_SUFFIX"] = f"_bench_{tag}_{idx}"

    log = os.path.join(data, "logs", "app.log")
    t0 = time.perf_counter()
    p = subprocess.Popen([exe, "--tray"], env=env)

    seen = False
    deadline = t0 + 20
    while time.perf_counter() < deadline:
        if os.path.exists(log):
            try:
                text = open(log, encoding="utf-8", errors="replace").read()
            except OSError:
                text = ""
            if "已启动（管理员" in text:
                seen = True
                break
        time.sleep(0.02)

    elapsed = (time.perf_counter() - t0) * 1000
    p.terminate()
    try:
        p.wait(timeout=10)
    except subprocess.TimeoutExpired:
        p.kill()
    time.sleep(0.4)
    return elapsed if seen else None


def main():
    a, b = sys.argv[1], sys.argv[2]
    rounds = int(sys.argv[3]) if len(sys.argv) > 3 else 5

    res = {"A": [], "B": []}
    for i in range(rounds):
        # 交替跑，抵消磁盘缓存与系统负载的先后差异
        for tag, exe in (("A", a), ("B", b)):
            ms = once(exe, tag, i)
            if ms:
                res[tag].append(ms)
            print(f"  round {i+1} {tag}: " + (f"{ms:.0f} ms" if ms else "timeout"))

    for tag, name in (("A", "A (ReadyToRun)"), ("B", "B (无 R2R)")):
        v = res[tag]
        if v:
            print(f"{name}: 中位数 {statistics.median(v):.0f} ms  最小 {min(v):.0f}  最大 {max(v):.0f}  n={len(v)}")


if __name__ == "__main__":
    main()
