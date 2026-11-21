#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""生成 Windows 磁贴 XML（GitHub Pages 静态托管版）

排版对应「图 2」那种三段式：
    一路走好！            ← title（仅大磁贴）
    6362天1小时33分钟      ← 计数行
    长者语录：……          ← 语录正文（自动换行）

要点：
  * AddOneSecond/Assets/pic/*.png 其实是「语录海报」（白字透明底），
    当背景铺上去会和磁贴文字叠成两层字，所以中/宽/大磁贴改用生成的深色底图 bg.png，
    海报只留给最小的 71x71 磁贴。
  * 只用 Win10 自适应模板（TileSmall/TileMedium/TileWide/TileLarge），不混旧版模板。
  * 载荷必须 < 5KB（Windows 硬限制），每次生成都会自检。

用法:
    python tile/gen_tile.py                  # 输出到 _site/
    python tile/gen_tile.py --check          # 只自检
    python tile/gen_tile.py --base-url https://user.github.io/repo
"""
import argparse
import datetime
import json
import os
import pathlib
import random
import struct
import sys
import urllib.request
import xml.etree.ElementTree as ET
import zlib

HERE = pathlib.Path(__file__).resolve().parent
REPO_ROOT = HERE.parent

DEFAULTS = {
    "baseUrl": "",
    # 第二行/标题：现在默认都关掉，磁贴只留「每日一言：……」
    "title": "时间众筹总计",        # 大磁贴标题；留空则不显示
    "showTitle": False,
    "showCounter": False,           # 是否显示计数/日期那一行
    "counterLabel": "",
    # 第二行显示什么：counter = 时间众筹总计（7 倍速）；beijing = 总天数+北京时分；clock = 北京时间
    "counterMode": "clock",
    "clockFormat": "%Y年%m月%d日",
    "utcOffset": 8,
    # 语录来源：hitokoto = 一言接口（随机）；local = 仓库里的 quotes.json
    "source": "hitokoto",
    "hitokotoUrl": "https://v1.hitokoto.cn/",
    # 要生成哪些分类的文件："" = 随机（tile.xml），其余生成 tile-<c>.xml
    "types": ["", "a", "b", "c", "d", "e", "f", "g", "h", "i", "j", "k", "l"],
    "quotePrefix": "每日一言：",     # 语录前缀
    "showAttribution": True,        # 正文后面是否带「 —— 作者《出处》」
    "attributionSep": " —— ",       # 正文与出处之间的分隔（空格 + 破折号）
    # 背景：留空 = 完全透明（Windows 会透出壁纸并做磨砂虚化）
    #       "bg.png" = 用生成器画的深色渐变底图；也可以直接填一个 https 图片地址
    "backgroundImage": "",
    "branding": "",                 # 磁贴右下角应用小 logo：logo / 留空=不显示
    "smallText": "每日一言",         # 71x71 小磁贴显示的字
    "clipMedium": 30,               # 中磁贴最多字数
    "clipWide": 60,                 # 宽磁贴
    "clipLarge": 160,               # 大磁贴
    "maxPayloadBytes": 4600,
}

BASE_SECONDS = 105695630   # 众筹基准秒数
BASE_EPOCH = 1606379872    # 对应 Unix 时间戳
INDENT = "\u3000\u3000"    # 正文缩进：两个全角空格
BIRTHDAY_POEM = "你们啊，要考虑历史的进程！今天可是我的生日！（祝长者生日快乐！越活越年轻！）"
BG_NAME = "bg.png"


# ---------------------------------------------------------------- 基础工具
def load_config():
    cfg = dict(DEFAULTS)
    p = HERE / "config.json"
    if p.exists():
        try:
            cfg.update(json.loads(p.read_text(encoding="utf-8")))
        except Exception as e:
            print(f"warn: tile/config.json 解析失败，用默认配置（{e}）", file=sys.stderr)
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


def escape_xml(value):
    return (str(value).replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")
            .replace('"', "&quot;").replace("'", "&apos;"))


def clip(text, limit):
    return text if len(text) <= limit else text[: max(1, limit - 1)] + "…"


def stats_for(now):
    epoch = int(now.timestamp())
    seconds = BASE_SECONDS + (epoch - BASE_EPOCH) * 7
    dd = (seconds // 60 // 60 // 24) % 9999999
    hh = (seconds // 60 // 60) % 24
    mm = (seconds // 60) % 60
    return {"seconds": seconds, "dd": dd, "hh": hh, "mm": mm,
            "total": f"{dd}天{hh}小时{mm}分钟"}


def counter_line(cfg, now):
    """磁贴第二行的文字。

    clock   = 北京时间（11月21日 16:08）
    beijing = 总天数（时间众筹 7 倍速） + 北京现在的小时/分钟（16525天16小时8分钟）
    counter = 时间众筹总计（7 倍速的完整 dd/hh/mm）
    """
    mode = (cfg.get("counterMode") or "counter").lower()
    if mode == "clock":
        offset = float(cfg.get("utcOffset", 8))
        tz = datetime.timezone(datetime.timedelta(hours=offset))
        return now.astimezone(tz).strftime(cfg.get("clockFormat") or "%m月%d日 %H:%M")
    if mode == "beijing":
        offset = float(cfg.get("utcOffset", 8))
        tz = datetime.timezone(datetime.timedelta(hours=offset))
        local = now.astimezone(tz)
        dd = stats_for(now)["dd"]
        return f"{dd}天{local.hour}小时{local.minute}分钟"
    return (cfg.get("counterLabel", "") or "") + stats_for(now)["total"]


def parse_hitokoto(payload):
    """从一言接口的 JSON 里取出正文（保留出处，去掉多余空白）。"""
    text = (payload.get("hitokoto") or "").strip()
    if not text:
        return None
    who = (payload.get("from_who") or "").strip()
    src = (payload.get("from") or "").strip()
    return {"text": text, "from": src, "from_who": who, "type": payload.get("type")}


def fetch_hitokoto(url):
    """拉一条一言；失败返回 (None, 错误信息)。"""
    try:
        req = urllib.request.Request(url, headers={
            "User-Agent": "add-one-second-tile/1.0 (+https://github.com/bzhcyy/add-one-second-UWP)",
            "Accept": "application/json",
        })
        with urllib.request.urlopen(req, timeout=12) as r:
            payload = json.loads(r.read().decode("utf-8"))
        item = parse_hitokoto(payload)
        if not item:
            return None, "接口返回的 hitokoto 为空"
        return item, None
    except Exception as e:
        return None, f"{type(e).__name__}: {e}"


def write_gradient_png(path, size=400, top=(44, 44, 46), bottom=(12, 12, 14)):
    """不依赖 Pillow：手写一张竖直渐变的深色底图（保证文字始终可读）。"""
    rows = []
    for y in range(size):
        t = y / (size - 1)
        r = int(top[0] + (bottom[0] - top[0]) * t)
        g = int(top[1] + (bottom[1] - top[1]) * t)
        b = int(top[2] + (bottom[2] - top[2]) * t)
        rows.append(b"\x00" + bytes((r, g, b)) * size)
    raw = b"".join(rows)

    def chunk(tag, data):
        return (struct.pack(">I", len(data)) + tag + data
                + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF))

    ihdr = struct.pack(">IIBBBBB", size, size, 8, 2, 0, 0, 0)
    png = (b"\x89PNG\r\n\x1a\n"
           + chunk(b"IHDR", ihdr)
           + chunk(b"IDAT", zlib.compress(raw, 9))
           + chunk(b"IEND", b""))
    path.write_bytes(png)
    return len(png)


def format_attribution(meta, cfg):
    """把 {from_who, from} 拼成「 —— 闻一多《最后一次讲演》」这样的后缀。"""
    if not cfg.get("showAttribution", True) or not meta:
        return ""
    who = (meta.get("from_who") or "").strip()
    src = (meta.get("from") or "").strip()
    if who and src:
        body = f"{who}《{src}》"
    elif src:
        body = f"《{src}》"
    elif who:
        body = who
    else:
        return ""
    return (cfg.get("attributionSep") or " —— ") + body


# ---------------------------------------------------------------- 磁贴 XML
def build_xml(base_url, poem, cfg, now, attribution=""):
    stats = stats_for(now)
    bg_setting = (cfg.get("backgroundImage") or "").strip()
    if not bg_setting:
        bg_src = ""                                  # 透明：透出壁纸 + 系统磨砂
    elif bg_setting.startswith(("http://", "https://")):
        bg_src = bg_setting
    else:
        bg_src = f"{base_url}/{bg_setting.lstrip('/')}"
    bg = escape_xml(bg_src) if bg_src else ""

    prefix = cfg.get("quotePrefix", "") or ""
    title = (cfg.get("title") or "").strip()
    show_title = bool(cfg.get("showTitle")) and bool(title)
    show_counter = bool(cfg.get("showCounter"))
    counter_text = counter_line(cfg, now)

    def counter(style="base"):
        return (f'<text hint-style="{style}">{escape_xml(counter_text)}</text>'
                if show_counter else "")

    def quote(style, limit):
        # 正文开头空两格（两个全角空格），中文排版习惯
        text = INDENT + prefix + clip(poem, limit)
        return f'<text hint-style="{style}" hint-wrap="true">{escape_xml(text)}</text>'

    def attr_text(style):
        """出处单独占一段，字号更小、更淡，右对齐。"""
        if not attribution:
            return ""
        return (f'<text hint-style="{style}" hint-align="right" hint-wrap="true">'
                f'{escape_xml(attribution.strip())}</text>')

    def bg_img():
        return f'<image placement="background" src="{bg}"/>' if bg else ""

    # 71x71：放不下一整段话，只写一行小字
    small_text = escape_xml(cfg.get("smallText") or "每日一言")
    small = bg_img() + f'<text hint-style="caption">{small_text}</text>'

    title_line = (f'<text hint-style="title">{escape_xml(title)}</text>'
                  if show_title else "")
    # 宽磁贴只有 310x150，标题用更小的 base
    title_line_small = (f'<text hint-style="base">{escape_xml(title)}</text>'
                        if show_title else "")
    branding = (cfg.get("branding") or "").strip()
    visual_attr = f' version="2" branding="{escape_xml(branding)}"' if branding else ' version="2"'

    # 正文固定用 subtitle（20px）；只有同时显示计数行时才降到 base，给上面留空间
    large_style = "base" if show_counter else "subtitle"

    return (
        '<?xml version="1.0" encoding="utf-8"?>\n'
        "<tile>\n"
        f"  <visual{visual_attr}>\n"
        f'    <binding template="TileSmall">{small}</binding>\n'
        f'    <binding template="TileMedium">{bg_img()}'
        f'{counter()}{quote("captionSubtle", int(cfg["clipMedium"]))}</binding>\n'
        f'    <binding template="TileWide">{bg_img()}'
        f'{title_line_small}{counter()}{quote("baseSubtle", int(cfg["clipWide"]))}'
        f'{attr_text("captionSubtle")}</binding>\n'
        f'    <binding template="TileLarge">{bg_img()}'
        f'{title_line}{counter("subtitle")}'
        f'{quote(large_style, int(cfg["clipLarge"]))}'
        f'{attr_text("baseSubtle")}</binding>\n'
        "  </visual>\n"
        "</tile>\n"
    )


INDEX_TEMPLATE = """<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<meta http-equiv="refresh" content="60">
<title>+1s 磁贴服务器</title>
<style>
  :root { color-scheme: dark; }
  body { margin:0; background:#141414; color:#e8e8e8;
         font:16px/1.7 -apple-system,"Segoe UI","Microsoft YaHei",sans-serif; }
  main { max-width:760px; margin:0 auto; padding:40px 20px 64px; }
  h1 { font-size:26px; margin:0 0 6px; }
  .ok { color:#3ddc84; margin:0 0 28px; font-size:14px; letter-spacing:.04em; }
  dl { display:grid; grid-template-columns:110px 1fr; gap:8px 16px; margin:0 0 28px;
       font-size:14px; }
  dt { color:#8a8a8a; }
  dd { margin:0; word-break:break-all; }
  a { color:#7cc4ff; text-decoration:none; }
  a:hover { text-decoration:underline; }
  blockquote { margin:0 0 24px; padding:16px 20px; background:#1e1e1e;
               border-left:3px solid #3ddc84; border-radius:6px; font-size:15px; }
  code { background:#232323; padding:2px 6px; border-radius:4px; font-size:13px; }
  .hint { color:#9a9a9a; font-size:13px; margin-top:28px; }
</style>
</head>
<body>
<main>
  <h1>+1s 磁贴服务器</h1>
  <p class="ok">● 运行中 · 每 30 分钟自动更新一次</p>
  <dl>
    <dt>生成时间</dt><dd>__TIME__</dd>
    <dt>磁贴 XML</dt><dd><a href="tile.xml">tile.xml</a> · __BYTES__ 字节（Windows 上限 5 KB）</dd>
    <dt>自检信息</dt><dd><a href="health.json">health.json</a></dd>
    <dt>背景</dt><dd>__PIC__</dd>
    <dt>磁贴地址</dt><dd><code>__TILE__</code></dd>
  </dl>
  <blockquote>__QUOTE__</blockquote>
  <p class="hint">这一页只是给人看的；Windows 只认 <code>__TILE__</code>。
     本页每 60 秒自动刷新。</p>
</main>
</body>
</html>
"""


def build_index(health):
    import html
    page = INDEX_TEMPLATE
    for token, value in (
        ("__TIME__", html.escape(str(health["generatedAt"]))),
        ("__BYTES__", str(health["payloadBytes"])),
        ("__PIC__", html.escape(health.get("background") or "（无图，透明磁贴）")),
        ("__TILE__", html.escape(health["tileUrl"])),
        ("__QUOTE__", html.escape(str(health.get("quote", "")))),
    ):
        page = page.replace(token, value)
    return page


# ---------------------------------------------------------------- 自检
def validate(xml_text, base_url, cfg):
    problems = []
    if not base_url.startswith("https://"):
        problems.append(f"baseUrl 不是 https: {base_url!r}")
    size = len(xml_text.encode("utf-8"))
    limit = int(cfg.get("maxPayloadBytes", 4600))
    if size > limit:
        problems.append(f"载荷 {size} 字节，超过上限 {limit}")

    try:
        root = ET.fromstring(xml_text.encode("utf-8"))
    except ET.ParseError as e:
        return [f"XML 不合法: {e}"]
    if root.tag != "tile":
        problems.append(f"根节点应为 <tile>，实际 {root.tag}")
    visual = root.find("visual")
    if visual is None or len(root.findall("visual")) != 1:
        problems.append("必须且只能有一个 <visual>")

    bindings = visual.findall("binding") if visual is not None else []
    names = [b.get("template") for b in bindings]
    want = ["TileSmall", "TileMedium", "TileWide", "TileLarge"]
    if names != want:
        problems.append(f"binding 顺序/命名不对: {names}")

    hint_ok = {"caption", "captionSubtle", "body", "bodySubtle", "base", "baseSubtle",
               "subtitle", "subtitleSubtle", "title", "titleSubtle", "titleNumeral",
               "subheader", "subheaderSubtle", "subheaderNumeral",
               "header", "headerSubtle", "headerNumeral"}
    for b in bindings:
        name = b.get("template")
        if b.get("fallback"):
            problems.append(f"{name}: 不该有 fallback（新旧模板混用会被系统丢弃）")
        imgs = b.findall("image")
        if len(imgs) > 1:
            problems.append(f"{name}: 图片最多 1 张，实际 {len(imgs)}")
        for img in imgs:
            src = img.get("src") or ""
            if not src.startswith(("https://", "http://")):
                problems.append(f"{name}: 图片必须是 http(s) 绝对地址 -> {src}")
            if img.get("placement") != "background":
                problems.append(f"{name}: placement 应为 background")
        texts = b.findall("text")
        if name == "TileSmall" and len(texts) > 1:
            problems.append("TileSmall 最多放一行字")
        if name != "TileSmall" and not texts:
            problems.append(f"{name}: 没有文字")
        for t in texts:
            if t.get("id") is not None:
                problems.append(f"{name}: 自适应模板不该写 id 属性")
            if not (t.text or "").strip():
                problems.append(f"{name}: 文本为空")
            if t.get("hint-style") not in hint_ok:
                problems.append(f"{name}: hint-style 非法 -> {t.get('hint-style')}")
    return problems


def main():
    ap = argparse.ArgumentParser(description="生成 GitHub Pages 版磁贴 XML")
    ap.add_argument("--out", default="_site", help="输出目录（默认 _site）")
    ap.add_argument("--base-url", default=None, help="Pages 根地址，如 https://user.github.io/repo")
    ap.add_argument("--quote-index", type=int, default=None, help="固定用第几条语录（调试用）")
    ap.add_argument("--check", action="store_true", help="只自检，不写文件")
    args = ap.parse_args()

    cfg = load_config()
    base_url = resolve_base_url(args.base_url, cfg)
    if not base_url:
        print("error: 拿不到 baseUrl。请传 --base-url，或设置 TILE_BASE_URL，"
              "或在 tile/config.json 里填 baseUrl。", file=sys.stderr)
        return 2

    quotes = json.loads((HERE / "quotes.json").read_text(encoding="utf-8"))
    if not quotes:
        print("error: tile/quotes.json 为空", file=sys.stderr)
        return 2

    now = datetime.datetime.now(datetime.timezone.utc)
    birthday = (now.month == 8 and now.day == 17)
    source = (cfg.get("source") or "local").lower()

    def fetch_one(tcode):
        """按分类取一条；取不到就退回本地 quotes.json。"""
        if source == "hitokoto" and not birthday:
            url = cfg.get("hitokotoUrl") or "https://v1.hitokoto.cn/"
            if tcode:
                url += ("&" if "?" in url else "?") + "c=" + tcode
            item, warn = fetch_hitokoto(url)
            if item:
                return {"text": item["text"],
                        "meta": {"from": item["from"], "from_who": item["from_who"], "type": item["type"]},
                        "origin": "hitokoto"}
            print(f"warn: 分类 {tcode or '随机'} 取一言失败（{warn}），退回本地语录", file=sys.stderr)
        if birthday:
            return {"text": BIRTHDAY_POEM, "meta": {}, "origin": "birthday"}
        idx = args.quote_index if args.quote_index is not None else random.randrange(len(quotes))
        return {"text": quotes[idx], "meta": {}, "origin": "local", "index": idx}

    types = cfg.get("types") or [""]
    out_dir = pathlib.Path(args.out)
    if not out_dir.is_absolute():
        out_dir = pathlib.Path.cwd() / out_dir
    out_dir.mkdir(parents=True, exist_ok=True)

    bg_setting = (cfg.get("backgroundImage") or "").strip()
    if bg_setting == BG_NAME and not args.check:
        write_gradient_png(out_dir / BG_NAME)

    print(f"baseUrl   : {base_url}")
    print(f"分类数    : {len(types)}（随机 + {len(types) - 1} 个分类）")
    all_ok = True
    default_health = None
    variants = []

    for tcode in types:
        item = fetch_one(tcode)
        poem, meta, origin = item["text"], item["meta"], item["origin"]
        attribution = format_attribution(meta, cfg)
        xml_text = build_xml(base_url, poem, cfg, now, attribution)
        problems = validate(xml_text, base_url, cfg)
        size = len(xml_text.encode("utf-8"))
        fname = "tile.xml" if not tcode else f"tile-{tcode}.xml"
        ok = not problems
        all_ok = all_ok and ok
        print(f"  {fname:<12} {origin:<8} {size:>4}B  {poem[:24]}…" + ("" if ok else "  ✗ " + "; ".join(problems)))

        variants.append({"file": fname, "type": tcode or "random", "source": origin,
                         "quote": poem, "from": meta.get("from", ""), "fromWho": meta.get("from_who", ""),
                         "bytes": size, "url": f"{base_url}/{fname}"})

        if not args.check:
            (out_dir / fname).write_text(xml_text, encoding="utf-8")

        if not tcode:
            default_health = {
                "ok": ok,
                "generatedAt": now.isoformat().replace("+00:00", "Z"),
                "baseUrl": base_url,
                "tileUrl": f"{base_url}/tile.xml",
                "background": (f"{base_url}/{bg_setting}" if bg_setting and not bg_setting.startswith("http")
                               else (bg_setting or None)),
                "payloadBytes": size,
                "title": cfg.get("title", ""),
                "counter": counter_line(cfg, now) if cfg.get("showCounter") else None,
                "counterMode": (cfg.get("counterMode") or "counter"),
                "stats": stats_for(now)["total"],
                "quote": poem,
                "source": origin,
                "quoteMeta": meta,
                "variants": [],
            }

    if not all_ok:
        print("自检失败：上面标 ✗ 的那几行")
        return 1
    print(f"自检通过：{len(types)} 个文件，均为 4 个自适应 binding、载荷未超限")

    if args.check:
        return 0

    (out_dir / ".nojekyll").write_text("", encoding="utf-8")
    if default_health is not None:
        default_health["variants"] = variants
        (out_dir / "health.json").write_text(
            json.dumps(default_health, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        (out_dir / "index.html").write_text(build_index(default_health), encoding="utf-8")
    print(f"已写入    : {out_dir}（tile.xml + {len(types) - 1} 个分类文件）")
    return 0


if __name__ == "__main__":
    sys.exit(main())
