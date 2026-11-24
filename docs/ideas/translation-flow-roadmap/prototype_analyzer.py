#!/usr/bin/env python3
"""
EraTrans 汉化路线图原型分析器（静态 Emuera 生命周期 + 调用图）

用法：
    python3 prototype_analyzer.py <游戏根目录> [--json]

说明：
    只读扫描游戏目录下的 ERB / CSV 文件，按 Emuera 引擎内置生命周期入口做 BFS，
    输出玩家开机后最先遇到的核心文件与各阶段覆盖统计。
"""

import argparse
import collections
import json
import os
import re
import sys
import time

HOOKS = {
    "Stage 1: 启动与开局": ["SYSTEM_TITLE", "EVENTFIRST", "EVENTLOAD"],
    "Stage 2: 主界面/日常": ["EVENTSHOP", "SHOW_SHOP", "USERSHOP", "EVENTBUY"],
    "Stage 3: 核心调教/回合": [
        "EVENTTRAIN",
        "SHOW_STATUS",
        "SHOW_USERCOM",
        "USERCOM",
        "EVENTCOM",
        "EVENTCOMEND",
        "EVENTEND",
        "EVENTTURNEND",
    ],
}

BEGIN_MAP = {
    "TITLE": ["SYSTEM_TITLE"],
    "FIRST": ["EVENTFIRST"],
    "SHOP": ["EVENTSHOP", "SHOW_SHOP", "USERSHOP"],
    "TRAIN": ["EVENTTRAIN", "SHOW_STATUS", "SHOW_USERCOM", "USERCOM"],
    "AFTERTRAIN": ["EVENTEND"],
    "TURNEND": ["EVENTTURNEND"],
    "ABLUP": ["SHOW_JUEL", "SHOW_ABLUP_SELECT", "USERABLUP"],
}

FUNC_DEF_RE = re.compile(r"^\s*@([^\s,;(]+)")
STATIC_CALL_RE = re.compile(r"^\s*(?:TRYC?CALL|TRY?JUMP|CALL|JUMP)\s+([^\s,;(]+)", re.IGNORECASE)
FORM_CALL_RE = re.compile(r"^\s*(?:TRYC?CALLFORM|TRY?JUMPFORM|CALLFORM|JUMPFORM)\s+([^\s,;(]+)", re.IGNORECASE)
BEGIN_RE = re.compile(r"^\s*BEGIN\s+([A-Za-z]+)", re.IGNORECASE)


def read_text(path: str) -> str:
    raw = open(path, "rb").read()
    if raw.startswith(b"\xef\xbb\xbf"):
        return raw[3:].decode("utf-8", errors="replace")
    try:
        return raw.decode("utf-8")
    except UnicodeDecodeError:
        return raw.decode("cp932", errors="replace")


def analyze_game(root: str) -> dict:
    t0 = time.time()
    entries = os.listdir(root)
    erb_name = next((d for d in entries if d.upper() == "ERB" and os.path.isdir(os.path.join(root, d))), None)
    csv_name = next((d for d in entries if d.upper() == "CSV" and os.path.isdir(os.path.join(root, d))), None)
    if erb_name is None:
        raise FileNotFoundError(f"未在目录下找到 ERB 文件夹：{root}")

    erb_dir = os.path.join(root, erb_name)
    csv_dir = os.path.join(root, csv_name) if csv_name else None

    erb_files = []
    for r, _, fs in os.walk(erb_dir):
        for f in fs:
            if f.lower().endswith(".erb"):
                erb_files.append(os.path.join(r, f))

    csv_files = []
    if csv_dir:
        for r, _, fs in os.walk(csv_dir):
            for f in fs:
                if f.lower().endswith(".csv"):
                    csv_files.append(os.path.join(r, f))

    func_to_files = collections.defaultdict(list)
    func_calls_static = collections.defaultdict(set)
    func_calls_form_prefix = collections.defaultdict(set)
    func_begins = collections.defaultdict(set)
    file_print_lines = collections.Counter()

    for fp in sorted(erb_files):
        rel = os.path.relpath(fp, root).replace("\\", "/")
        text = read_text(fp)
        cur_func = None
        for line in text.splitlines():
            s = line.strip()
            if not s or s.startswith(";"):
                continue
            m = FUNC_DEF_RE.match(line)
            if m:
                cur_func = m.group(1).upper()
                func_to_files[cur_func].append(rel)
                continue
            if cur_func is None:
                continue
            up = s.upper()
            if up.startswith(("PRINT", "HTML_PRINT", "DATA", "PRINTBUTTON")):
                file_print_lines[rel] += 1
            mb = BEGIN_RE.match(line)
            if mb:
                func_begins[cur_func].add(mb.group(1).upper())
                continue
            mf = FORM_CALL_RE.match(line)
            if mf:
                prefix = re.split(r"[{%\\]", mf.group(1))[0].upper()
                if len(prefix) >= 3:
                    func_calls_form_prefix[cur_func].add(prefix)
                continue
            mc = STATIC_CALL_RE.match(line)
            if mc:
                func_calls_static[cur_func].add(mc.group(1).upper())

    all_funcs = list(func_to_files.keys())

    def run_bfs(seeds, include_form_prefix=False):
        dist = {}
        entry_source = {}
        q = collections.deque()
        for s in seeds:
            if s in func_to_files and s not in dist:
                dist[s] = 0
                entry_source[s] = f"@{s}"
                q.append(s)
        while q:
            u = q.popleft()
            d = dist[u]
            neighbors = set(func_calls_static.get(u, ()))
            for b in func_begins.get(u, ()):
                neighbors.update(BEGIN_MAP.get(b, ()))
            if include_form_prefix:
                for pfx in func_calls_form_prefix.get(u, ()):
                    for fn in all_funcs:
                        if fn.startswith(pfx):
                            neighbors.add(fn)
            for v in neighbors:
                if v in func_to_files and v not in dist:
                    dist[v] = d + 1
                    entry_source[v] = entry_source[u]
                    q.append(v)

        file_info = {}
        for fn, d in dist.items():
            for rel in func_to_files[fn]:
                if rel not in file_info or d < file_info[rel]["depth"]:
                    file_info[rel] = {
                        "file": rel,
                        "depth": d,
                        "entry": entry_source[fn],
                        "printLines": file_print_lines[rel],
                    }
        return file_info

    all_hook_seeds = [h for hs in HOOKS.values() for h in hs]
    static_files = run_bfs(all_hook_seeds, include_form_prefix=False)
    hybrid_files = run_bfs(all_hook_seeds, include_form_prefix=True)
    opening_files = run_bfs(["SYSTEM_TITLE", "EVENTFIRST", "EVENTLOAD"], include_form_prefix=False)

    hooks_found = {}
    for stage, hooks in HOOKS.items():
        hooks_found[stage] = {h: func_to_files[h] for h in hooks if h in func_to_files}

    elapsed_ms = round((time.time() - t0) * 1000, 1)
    return {
        "gameRoot": os.path.basename(os.path.abspath(root)),
        "erbFileCount": len(erb_files),
        "csvFileCount": len(csv_files),
        "functionCount": len(func_to_files),
        "elapsedMs": elapsed_ms,
        "staticReachableCount": len(static_files),
        "hybridReachableCount": len(hybrid_files),
        "hooks": hooks_found,
        "openingTopFiles": sorted(
            opening_files.values(),
            key=lambda x: (x["depth"], -x["printLines"], x["file"]),
        )[:20],
    }


def main():
    parser = argparse.ArgumentParser(description="Analyze ERA game execution flow for translation roadmap.")
    parser.add_argument("game_root", help="Path to the ERA game root directory (containing ERB/ and CSV/).")
    parser.add_argument("--json", action="store_true", help="Output raw JSON.")
    args = parser.parse_args()

    result = analyze_game(args.game_root)
    if args.json:
        print(json.dumps(result, ensure_ascii=False, indent=2))
        return

    total = max(1, result["erbFileCount"])
    print(f"Game: {result['gameRoot']}")
    print(
        f"ERB: {result['erbFileCount']} | CSV: {result['csvFileCount']} | "
        f"Functions: {result['functionCount']} | Elapsed: {result['elapsedMs']} ms"
    )
    print(
        f"Reachable (static): {result['staticReachableCount']}/{result['erbFileCount']} "
        f"({result['staticReachableCount'] * 100 / total:.1f}%)"
    )
    print(
        f"Reachable (+ CALLFORM prefix): {result['hybridReachableCount']}/{result['erbFileCount']} "
        f"({result['hybridReachableCount'] * 100 / total:.1f}%)"
    )
    print("\nStartup Top Files (filtered to printLines > 0):")
    for item in result["openingTopFiles"]:
        if item["printLines"] > 0:
            print(f"  depth={item['depth']} | prints={item['printLines']:4d} | {item['entry']:<14s} | {item['file']}")


if __name__ == "__main__":
    main()
