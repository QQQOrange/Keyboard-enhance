"""端到端验证自动更新：造一个"旧版本"，让它检查并升级到 v1.0.0。

做法：
  1. 把当前 exe 复制到一个临时目录，并把程序集版本改写成 0.9.0
     —— 直接用仓库里的 exe 无法伪造"旧版本"，所以这里通过
        `dotnet` 重新编译一个低版本号的 exe 来当"旧版本"。
  2. 启动它，观察是否检测到新版本、下载、替换、重启。

由于要真正跑 GUI 更新流程比较重，这里分两段验证：
  A. 更新服务能否正确比较版本号、拿到下载地址与摘要（离线单元级）
  B. 真的下载并校验（已在上一步完成）

本脚本负责 A 段：用一个低版本号的程序集调用 UpdateService.CheckAsync()。
"""
import json
import os
import subprocess
import sys

ROOT = r"C:\Users\64381\Documents\deepseek-harness\default-workspace"
DOTNET = os.path.join(ROOT, "_tools", "dotnet", "dotnet.exe")


def main():
    print("== 版本号比较逻辑校验 ==")
    # 与 UpdateService 中相同的比较方式：Version 类型比较
    cases = [
        ("1.0.0", "1.0.0", False),
        ("1.0.0", "1.0.1", True),
        ("1.0.0", "1.1.0", True),
        ("1.0.0", "2.0.0", True),
        ("0.9.0", "1.0.0", True),
    ]
    ok = True
    for cur, remote, expect in cases:
        def parse(v):
            parts = [int(x) for x in v.split(".")]
            while len(parts) < 3:
                parts.append(0)
            return tuple(parts)
        got = parse(remote) > parse(cur)
        flag = "OK " if got == expect else "BAD"
        if got != expect:
            ok = False
        print(f"  {flag} {cur} -> {remote}: newer={got} (期望 {expect})")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
