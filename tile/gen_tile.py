#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""一言 Tile 磁贴服务器 · 静态站生成器

从 https://v1.hitokoto.cn/ 取一言，生成 Windows 磁贴 XML：
    _site/tile.xml           随机
    _site/tile-a.xml … l.xml 各个分类（App 会按分类换文件名）

排版：
    每日一言                      ← subheader（34px，大且粗）
    　　正文……                    ← subtitle（20px），首行缩进两个全角空格
    　　　　　　—— 作者《出处》     ← baseSubtle（15px），右对齐
"""
import argparse
import datetime
import html
import json
import os
import pathlib
import sys
import urllib.request
import xml.etree.ElementTree as ET

HERE = pathlib.Path(__file__).resolve().parent
OUT_NAME = "tile"

DEFAULTS = {
    "baseUrl": "",
    "title": "每日一言",
    "quotePrefix": "",
    "showAttribution": True,
    "attributionSep": " —— ",
    "indent": "\u3000\u3000",
    "hitokotoUrl": "https://v1.hitokoto.cn/",
    "types": ["", "a", "b", "c", "d", "e", "f", "g", "h", "i", "j", "k", "l"],
    "clipMedium": 40,
    "clipWide": 80,
    "clipLarge": 200,
    "maxPayloadBytes": 4600,
    "showTitle": True,
    "showAttr": True,
    "titleAlign": "left",
    "attrAlign": "right",
    "titleStyle": "title",
    "quoteStyle": "base",
    "attrStyle": "captionSubtle",
}

PLACEHOLDER = "今日一言暂时取不到，稍后再试。"

TYPE_NAMES = {
    "": "随机", "a": "动画", "b": "漫画", "c": "游戏", "d": "文学", "e": "原创",
    "f": "来自网络", "g": "其他", "h": "影视", "i": "诗词", "j": "网易云",
    "k": "哲学", "l": "抖机灵",
}


# ------------------------------------------------------------------ 工具
def load_config():
    cfg = dict(DEFAULTS)
    p = HERE / "config.json"
    if p.exists():
        try:
            cfg.update(json.loads(p.read_text(encoding="utf-8")))
        except Exception as e:
            print(f"warn: config.json 解析失败，用默认值（{e}）", file=sys.stderr)
    return cfg


def resolve_base_url(cli_value, cfg):
    for candidate in (cli_value, os.environ.get("TILE_BASE_URL"), cfg.get("baseUrl")):
        if candidate and candidate.strip():
            return candidate.strip().rstrip("/")
    repo = os.environ.get("GITHUB_REPOSITORY")
    if repo and "/" in repo:
        owner, name = repo.split("/", 1)
        return f"https://{owner.lower()}.github.io/{name}"
    return ""


def esc(value):
    return (str(value).replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")
            .replace('"', "&quot;").replace("'", "&apos;"))


def clip(text, limit):
    """按句末标点优先截断，避免折行把标点甩到行首。"""
    if not limit or len(text) <= limit:
        return text
    head = text[:limit]
    for stop in ("。", "！", "？", "；", "…", "，", "、"):
        at = head.rfind(stop)
        if at >= limit * 0.5:
            return head[: at + 1]
    return text[: max(1, limit - 1)] + "…"


def fetch_hitokoto(url, timeout=12):
    """取一条一言；失败返回 (None, 原因)。"""
    req = urllib.request.Request(url, headers={
        "User-Agent": "OneSentenceTile/1.0 (+https://github.com/bzhcyy/OneSentenceTile)",
        "Accept": "application/json",
    })
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            data = json.loads(resp.read().decode("utf-8"))
        text = (data.get("hitokoto") or "").strip()
        if not text:
            return None, "接口没有返回正文"
        return {
            "text": text,
            "from": (data.get("from") or "").strip(),
            "from_who": (data.get("from_who") or "").strip(),
            "type": (data.get("type") or "").strip(),
        }, None
    except Exception as e:
        return None, f"{type(e).__name__}: {e}"


def attribution(item, cfg):
    if not cfg.get("showAttribution", True) or not item:
        return ""
    who, src = item.get("from_who", ""), item.get("from", "")
    if who and src:
        body = f"{who}《{src}》"
    elif src:
        body = f"《{src}》"
    elif who:
        body = who
    else:
        return ""
    return (cfg.get("attributionSep") or " —— ") + body


# ------------------------------------------------------------------ 磁贴 XML
def build_xml(base_url, item, cfg, type_code=""):
    title = (cfg.get("title") or "").strip()
    prefix = cfg.get("quotePrefix") or ""
    indent = cfg.get("indent") or ""

    def text(style, body, limit=None, align=None, max_lines=None):
        attrs = f' hint-style="{style}"'
        if align:
            attrs += f' hint-align="{align}"'
        if max_lines:
            attrs += f' hint-maxLines="{max_lines}"'
        value = esc(clip(body, limit) if limit else body)
        return f"<text{attrs} hint-wrap=\"true\">{value}</text>"

    show_title = bool(cfg.get("showTitle", True))
    attr = attribution(item, cfg).strip()
    show_attr = bool(cfg.get("showAttr", True)) and attr != ""
    t_align = cfg.get("titleAlign", "center")
    a_align = cfg.get("attrAlign", "right")

    small = f'<text hint-style="caption">{esc(title)}</text>'
    medium = text("captionSubtle", indent + prefix + item["text"], cfg["clipMedium"])
    wide = (text("base", title, align=t_align) if show_title else "") \
        + text("baseSubtle", indent + prefix + item["text"], cfg["clipWide"]) \
        + (text("captionSubtle", attr, align=a_align) if show_attr else "")
    large = (text(cfg["titleStyle"], title, align=t_align) if show_title else "") \
        + text(cfg["quoteStyle"], indent + prefix + item["text"], cfg["clipLarge"], max_lines=5) \
        + (text(cfg["attrStyle"], attr, align=a_align) if show_attr else "")

    return (
        '<?xml version="1.0" encoding="utf-8"?>\n'
        "<tile>\n  <visual version=\"2\">\n"
        f'    <binding template="TileSmall">{small}</binding>\n'
        f'    <binding template="TileMedium">{medium}</binding>\n'
        f'    <binding template="TileWide">{wide}</binding>\n'
        f'    <binding template="TileLarge">{large}</binding>\n'
        "  </visual>\n</tile>\n"
    )


def validate(xml_text, base_url, cfg):
    problems = []
    if not base_url.startswith("https://"):
        problems.append(f"baseUrl 不是 https：{base_url!r}")
    size = len(xml_text.encode("utf-8"))
    if size > int(cfg["maxPayloadBytes"]):
        problems.append(f"载荷 {size} 字节超限")
    try:
        root = ET.fromstring(xml_text.encode("utf-8"))
    except ET.ParseError as e:
        return [f"XML 不合法：{e}"]
    visual = root.find("visual")
    if visual is None:
        return ["缺少 <visual>"]
    names = [b.get("template") for b in visual.findall("binding")]
    if names != ["TileSmall", "TileMedium", "TileWide", "TileLarge"]:
        problems.append(f"binding 不对：{names}")
    for b in visual.findall("binding"):
        if b.get("fallback"):
            problems.append(f"{b.get('template')}: 不该有 fallback")
        if not b.findall("text"):
            problems.append(f"{b.get('template')}: 没有文字")
        for t in b.findall("text"):
            if not (t.text or "").strip():
                problems.append(f"{b.get('template')}: 空文本")
            if t.get("id") is not None:
                problems.append(f"{b.get('template')}: 不该写 id")
    return problems


# ------------------------------------------------------------------ 首页
INDEX_TEMPLATE = """<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>一言 Tile · 磁贴服务器</title>
<style>
  :root { color-scheme: light dark; --fg:#1b1f27; --dim:#6b7280; --line:#e5e7eb; --bg:#f7f8fa; --card:#fff; --a:#2563eb; }
  @media (prefers-color-scheme: dark) {
    :root { --fg:#e8ecf3; --dim:#98a2b3; --line:#262b34; --bg:#101216; --card:#171a20; --a:#7cb2ff; }
  }
  * { box-sizing: border-box; }
  body { margin:0; background:var(--bg); color:var(--fg);
         font:16px/1.75 -apple-system,"Segoe UI","Microsoft YaHei",sans-serif; }
  main { max-width:760px; margin:0 auto; padding:56px 22px 72px; }
  h1 { font-size:26px; margin:0 0 6px; letter-spacing:.01em; }
  .sub { color:var(--dim); font-size:13px; margin:0 0 30px; }
  .quote { background:var(--card); border:1px solid var(--line); border-radius:14px;
           padding:26px 26px 20px; margin:0 0 12px; }
  .quote p { margin:0; font-size:24px; line-height:1.65; font-weight:600; }
  .quote .from { margin-top:12px; text-align:right; color:var(--dim); font-size:14px; font-weight:400; }
  .hint { color:var(--dim); font-size:12px; margin:0 0 34px; text-align:right; }
  h2 { font-size:15px; margin:34px 0 10px; color:var(--dim); font-weight:600; }
  table { width:100%; border-collapse:collapse; font-size:13px; }
  td { padding:7px 0; border-bottom:1px solid var(--line); vertical-align:top; }
  td:first-child { color:var(--dim); width:96px; white-space:nowrap; }
  code { font-size:12px; word-break:break-all; }
  .chips { display:flex; flex-wrap:wrap; gap:8px; }
  .chips a { background:var(--card); border:1px solid var(--line); border-radius:999px;
             padding:5px 13px; font-size:12px; color:var(--fg); text-decoration:none; }
  .chips a:hover { border-color:var(--a); color:var(--a); }
  ol { margin:0; padding-left:20px; font-size:14px; }
  ol li { margin:6px 0; }
  a { color:var(--a); text-decoration:none; }
  a:hover { text-decoration:underline; }
  footer { margin-top:44px; padding-top:18px; border-top:1px solid var(--line);
           color:var(--dim); font-size:12px; }
</style>
</head>
<body>
<main>
  <h1>一言 Tile · 磁贴服务器</h1>
  <p class="sub">Windows 10 / 11 桌面磁贴 · 每 15 分钟生成 · 数据来自 <a href="https://hitokoto.cn">hitokoto</a></p>

  <div class="quote" id="card">
    <p id="quote">__QUOTE__</p>
    <div class="from" id="from">__FROM__</div>
  </div>
  <p class="hint" id="hint">本页每 60 秒从 v1.hitokoto.cn 现取一条</p>

  <h2>磁贴地址</h2>
  <table>
    <tr><td>随机</td><td><code>__TILE__</code></td></tr>
    <tr><td>生成时间</td><td>__TIME__</td></tr>
    <tr><td>载荷</td><td>__BYTES__ 字节（上限 5 KB）</td></tr>
    <tr><td>自检</td><td><a href="health.json">health.json</a></td></tr>
  </table>

  <h2>分类</h2>
  <div class="chips">__CATS__</div>

  <h2>安装应用</h2>
  <ol>
    <li>下载 <a href="https://github.com/bzhcyy/OneSentenceTile/releases">Release</a> 里的 <code>uwp-package.zip</code></li>
    <li>解压后双击 <code>安装.cmd</code>（自动提权、装证书、装依赖、装主程序）</li>
    <li>打开 App → 设置里开「动态磁贴」</li>
  </ol>

  <footer>
    磁贴 XML 由 GitHub Actions 每 15 分钟生成一次；本页的一言是浏览器实时取的。
    · <a href="https://github.com/bzhcyy/OneSentenceTile">源码</a>
    · <a href="https://developer.hitokoto.cn/">一言 API</a>
  </footer>
</main>

<script>
(function () {
  var q = document.getElementById('quote');
  var f = document.getElementById('from');
  var h = document.getElementById('hint');
  function pull() {
    fetch('https://v1.hitokoto.cn/', { cache: 'no-store' })
      .then(function (r) { return r.json(); })
      .then(function (d) {
        if (!d || !d.hitokoto) return;
        q.textContent = d.hitokoto;
        var who = (d.from_who || '').trim();
        var src = (d.from || '').trim();
        f.textContent = src ? ('—— ' + (who ? who + '《' + src + '》' : '《' + src + '》')) : '';
        h.textContent = '刚刚更新 · ' + new Date().toLocaleTimeString('zh-CN');
      })
      .catch(function () {});
  }
  pull();
  setInterval(pull, 60000);
})();
</script>
</body>
</html>
"""


def build_index(health, variants, cfg):
    cats = []
    for v in variants:
        code = v["type"]
        name = TYPE_NAMES.get(code if code != "random" else "", code)
        cats.append(f'<a href="{html.escape(v["file"])}">{html.escape(name)}</a>')
    return (INDEX_TEMPLATE
            .replace("__QUOTE__", html.escape(health.get("quote") or ""))
            .replace("__FROM__", html.escape(health.get("attribution") or ""))
            .replace("__TIME__", html.escape(health.get("generatedAt") or ""))
            .replace("__TILE__", html.escape(health.get("tileUrl") or ""))
            .replace("__BYTES__", str(health.get("payloadBytes") or 0))
            .replace("__CATS__", "".join(cats)))


# ------------------------------------------------------------------ 主流程
def main():
    ap = argparse.ArgumentParser(description="生成一言 Tile 的磁贴静态站")
    ap.add_argument("--out", default="_site")
    ap.add_argument("--base-url", default=None)
    ap.add_argument("--check", action="store_true", help="只自检，不写文件")
    args = ap.parse_args()

    cfg = load_config()
    base_url = resolve_base_url(args.base_url, cfg)
    if not base_url:
        print("error: 拿不到 baseUrl（--base-url / TILE_BASE_URL / config.json）", file=sys.stderr)
        return 2

    now = datetime.datetime.now(datetime.timezone.utc)
    types = cfg.get("types") or [""]
    out_dir = pathlib.Path(args.out)
    if not args.out.startswith("/") and not pathlib.Path(args.out).is_absolute():
        out_dir = pathlib.Path.cwd() / args.out

    print(f"baseUrl : {base_url}")
    print(f"分类数  : {len(types)}")

    variants, all_ok = [], True
    for code in types:
        url = cfg["hitokotoUrl"]
        if code:
            url += ("&" if "?" in url else "?") + "c=" + code
        item, err = fetch_hitokoto(url)
        source = "hitokoto"
        if not item:
            source = "placeholder"
            item = {"text": PLACEHOLDER, "from": "", "from_who": "", "type": code}
            print(f"warn: 分类 {code or '随机'} 取一言失败（{err}），用占位文案", file=sys.stderr)

        xml_text = build_xml(base_url, item, cfg, code)
        problems = validate(xml_text, base_url, cfg)
        all_ok = all_ok and not problems
        fname = "tile.xml" if not code else f"tile-{code}.xml"
        size = len(xml_text.encode("utf-8"))

        print(f"  {fname:<12} {source:<12} {size:>4}B  {item['text'][:22]}…"
              + ("" if not problems else "  ✗ " + "; ".join(problems)))

        variants.append({
            "file": fname, "type": code or "random", "source": source,
            "quote": item["text"], "from": item.get("from", ""),
            "fromWho": item.get("from_who", ""), "bytes": size,
            "url": f"{base_url}/{fname}",
        })

        if not args.check:
            out_dir.mkdir(parents=True, exist_ok=True)
            (out_dir / fname).write_text(xml_text, encoding="utf-8")

    if not all_ok:
        print("自检失败：上面标 ✗ 的行")
        return 1
    print(f"自检通过：{len(types)} 个文件结构正常")
    if args.check:
        return 0

    (out_dir / ".nojekyll").write_text("", encoding="utf-8")
    first = variants[0]
    health = {
        "ok": True,
        "name": "一言 Tile 磁贴服务器",
        "generatedAt": now.isoformat().replace("+00:00", "Z"),
        "baseUrl": base_url,
        "tileUrl": first["url"],
        "payloadBytes": first["bytes"],
        "quote": first["quote"],
        "attribution": (f"—— {first['fromWho']}《{first['from']}》" if first["from"] else ""),
        "variants": variants,
    }
    (out_dir / "health.json").write_text(json.dumps(health, ensure_ascii=False, indent=2) + "\n",
                                         encoding="utf-8")
    (out_dir / "index.html").write_text(build_index(health, variants, cfg), encoding="utf-8")
    print(f"已写入  : {out_dir}（tile.xml + {len(types) - 1} 个分类 + health.json + index.html）")
    return 0


if __name__ == "__main__":
    sys.exit(main())
