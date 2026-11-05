#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""通过 GitHub REST API 推送本地提交（git over HTTPS 被墙时的兜底）。

用法：
    python tools/push-via-api.py [远端分支名] [本地起始提交]

原理：
  1. 读出 GitHub token（与 publish-release.py 同一个凭据）；
  2. 对比远端分支 HEAD 与本地 HEAD，取出差集里的提交（按时间正序）；
  3. 逐个提交：为改动文件上传 blob → 建 tree（base_tree 指向父提交）→ 建 commit；
  4. 最后把 refs/heads/<branch> 指向新 commit。

只处理「新增/修改/删除」三种情况；重命名会被 diff 拆成删除 + 新增，结果一致。
"""
import base64
import ctypes
import json
import os
import subprocess
import sys
import urllib.error
import urllib.request

REPO = "QQQOrange/Keyboard-enhance"
API = "https://api.github.com"


def read_token():
    class CREDENTIAL(ctypes.Structure):
        _fields_ = [
            ("Flags", ctypes.c_uint32),
            ("Type", ctypes.c_uint32),
            ("TargetName", ctypes.c_wchar_p),
            ("Comment", ctypes.c_wchar_p),
            ("LastWritten", ctypes.c_uint64),
            ("CredentialBlobSize", ctypes.c_uint32),
            ("CredentialBlob", ctypes.POINTER(ctypes.c_byte)),
            ("Persist", ctypes.c_uint32),
            ("AttributeCount", ctypes.c_uint32),
            ("Attributes", ctypes.c_void_p),
            ("TargetAlias", ctypes.c_wchar_p),
            ("UserName", ctypes.c_wchar_p),
        ]

    advapi = ctypes.windll.advapi32
    cred = ctypes.POINTER(CREDENTIAL)()
    for name in ("git:https://github.com", "gh:github.com", "GitHub - https://api.github.com"):
        if advapi.CredReadW(name, 1, 0, ctypes.byref(cred)):
            c = cred.contents
            data = ctypes.string_at(c.CredentialBlob, c.CredentialBlobSize)
            advapi.CredFree(cred)
            try:
                return data.decode("utf-16-le")
            except UnicodeDecodeError:
                return data.decode("utf-8", "ignore")
    raise SystemExit("找不到 GitHub 凭据")


def call(method, path, token, payload=None):
    data = json.dumps(payload).encode("utf-8") if payload is not None else None
    req = urllib.request.Request(API + path, data=data, method=method)
    req.add_header("Authorization", "Bearer " + token)
    req.add_header("Accept", "application/vnd.github+json")
    if data:
        req.add_header("Content-Type", "application/json")
    try:
        with urllib.request.urlopen(req, timeout=60) as resp:
            return json.loads(resp.read().decode("utf-8"))
    except urllib.error.HTTPError as e:
        body = e.read().decode("utf-8", "replace")
        raise SystemExit(f"API {method} {path} 失败（{e.code}）：{body[:600]}")


def git(*args):
    """跑 git 并**按 UTF-8 解码**。

    Windows 上默认用 GBK 解码子进程输出，遇到 UTF-8 的提交信息会直接抛
    UnicodeDecodeError，整个推送就断在"取提交说明"这一步。
    """
    out = subprocess.run(["git"] + list(args), capture_output=True)
    if out.returncode != 0:
        raise SystemExit("git " + " ".join(args) + " 失败：" + out.stderr.decode("utf-8", "replace").strip())
    return out.stdout.decode("utf-8", "replace")


def main():
    branch = sys.argv[1] if len(sys.argv) > 1 else "main"
    base = sys.argv[2] if len(sys.argv) > 2 else None

    token = read_token()
    head = git("rev-parse", "HEAD").strip()

    info = call("GET", f"/repos/{REPO}/git/ref/heads/{branch}", token)
    remote_head = info["object"]["sha"]

    if remote_head == head:
        print("远端已是最新：" + head[:7])
        return

    base = base or remote_head
    commits = git("rev-list", "--reverse", f"{base}..{head}").split()
    if not commits:
        print("没有需要推送的提交")
        return

    print(f"待推送 {len(commits)} 个提交：{base[:7]} → {head[:7]}")

    for sha in commits:
        status_lines = git("diff-tree", "--no-commit-id", "--name-status", "-r", "-M", sha).splitlines()
        parent = git("rev-parse", sha + "^").strip()
        parent_tree = git("rev-parse", parent + "^{tree}").strip()

        entries, deleted = [], []
        for line in status_lines:
            parts = line.split("\t")
            status = parts[0]
            path = parts[-1]  # 重命名取新路径

            if status.startswith("D"):
                # GitHub 的 tree API 用 sha=None 表示删除这个路径
                entries.append({"path": path, "mode": "100644", "type": "blob", "sha": None})
                continue

            full = os.path.join(os.getcwd(), path.replace("/", os.sep))
            with open(full, "rb") as fh:
                blob = call("POST", f"/repos/{REPO}/git/blobs", token, {
                    "content": base64.b64encode(fh.read()).decode("ascii"),
                    "encoding": "base64",
                })
            entries.append({"path": path, "mode": "100644", "type": "blob", "sha": blob["sha"]})

        tree_payload = {"base_tree": parent_tree, "tree": entries}
        new_tree = call("POST", f"/repos/{REPO}/git/trees", token, tree_payload)["sha"]

        message = git("log", "-1", "--format=%B", sha)
        author_raw = git("log", "-1", "--format=%an%x00%ae%x00%aI", sha).strip().split("\x00")
        commit_payload = {
            "message": message,
            "tree": new_tree,
            "parents": [parent],
            "author": {"name": author_raw[0], "email": author_raw[1], "date": author_raw[2]},
            "committer": {"name": author_raw[0], "email": author_raw[1], "date": author_raw[2]},
        }
        new_commit = call("POST", f"/repos/{REPO}/git/commits", token, commit_payload)
        print(f"  已建提交 {new_commit['sha'][:7]}（本地 {sha[:7]}）：{message.splitlines()[0][:60]}")

        call("PATCH", f"/repos/{REPO}/git/refs/heads/{branch}", token, {
            "sha": new_commit["sha"],
            "force": False,
        })
        print(f"  refs/heads/{branch} → {new_commit['sha'][:7]}")

    print("推送完成（经 API）")


if __name__ == "__main__":
    main()
