#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""检查线上磁贴服务器的新鲜度。

GitHub Actions 的 schedule 是「尽力而为」：忙的时候会推迟，甚至一整天都不跑。
Pages 又是纯静态托管，所以 tile.xml 可能几个小时都是同一份。这个脚本读
health.json 的 generatedAt，算出「这份快照已经放了多久」，给 CI 和手动排查用。

用法：
    python tile/check_freshness.py                          # 默认查 Pages，超过 45 分钟告警
    python tile/check_freshness.py --max-age 10 --strict     # CI 部署后自检：超过 10 分钟就失败
    python tile/check_freshness.py --base-url http://127.0.0.1:8000

退出码：0 表示正常（或只是非严格模式下的告警）；--strict 且过期时为 1。
"""
import argparse
import datetime
import json
import os
import sys
import urllib.request

DEFAULT_BASE = "https://bzhcyy.github.io/OneSentenceTile"
USER_AGENT = "OneSentenceTile-freshness/1.0 (+https://github.com/bzhcyy/OneSentenceTile)"


def fetch_json(url, timeout=20):
    # 加 ?v= 绕过 Pages 的 10 分钟 CDN 缓存，否则读到的是更旧的副本
    sep = "&" if "?" in url else "?"
    req = urllib.request.Request(
        f"{url}{sep}v={int(datetime.datetime.now().timestamp())}",
        headers={"User-Agent": USER_AGENT, "Cache-Control": "no-cache"},
    )
    with urllib.request.urlopen(req, timeout=timeout) as resp:
        return json.loads(resp.read().decode("utf-8"))


def parse_time(value):
    if not value:
        raise ValueError("health.json 里没有 generatedAt")
    return datetime.datetime.fromisoformat(str(value).replace("Z", "+00:00"))


def main():
    ap = argparse.ArgumentParser(description="检查磁贴快照的新鲜度")
    ap.add_argument("--base-url", default=os.environ.get("TILE_BASE_URL") or DEFAULT_BASE)
    ap.add_argument("--max-age", type=int, default=45, help="多少分钟后算过期（默认 45）")
    ap.add_argument("--strict", action="store_true", help="过期时返回退出码 1")
    args = ap.parse_args()

    base = args.base_url.rstrip("/")
    in_actions = os.environ.get("GITHUB_ACTIONS") == "true"

    try:
        health = fetch_json(f"{base}/health.json")
    except Exception as e:
        msg = f"读不到 {base}/health.json：{type(e).__name__}: {e}"
        print(("::warning::" if in_actions else "warn: ") + msg, file=sys.stderr)
        return 1 if args.strict else 0

    now = datetime.datetime.now(datetime.timezone.utc)
    try:
        generated = parse_time(health.get("generatedAt"))
    except Exception as e:
        msg = f"health.json 的 generatedAt 解析失败：{e}"
        print(("::warning::" if in_actions else "warn: ") + msg, file=sys.stderr)
        return 1 if args.strict else 0

    age = max(0.0, (now - generated).total_seconds() / 60.0)
    expected = int(health.get("refreshMinutes") or 15)
    limit = args.max_age or int(health.get("staleAfterMinutes") or 45)

    print(f"磁贴快照：{base}")
    print(f"  生成时间：{health.get('generatedAt')}（{generated.isoformat()}）")
    print(f"  已放置  ：{age:.0f} 分钟（期望每 {expected} 分钟刷新一次，上限 {limit} 分钟）")
    print(f"  载荷    ：{health.get('payloadBytes', '?')} 字节")
    print(f"  当前一言：{health.get('quote', '')}")

    if age > limit:
        reason = (
            f"Pages 快照已经 {age:.0f} 分钟没更新（期望 {expected} 分钟）。"
            "GitHub 的 schedule 是尽力而为，Pages 自己不会更新——"
            "App 会改用一言官方 API，官方也取不到再退回 Worker。"
        )
        print(("::warning::" if in_actions else "warn: ") + reason, file=sys.stderr)
        return 1 if args.strict else 0

    print(f"OK：快照在 {limit} 分钟内，新鲜。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
