/**
 * +1s 磁贴服务器（Cloudflare Worker 版）
 *
 * 直接输出 Windows 磁贴 XML，不需要 Pages / 定时任务：
 *   https://onesentencetile.onesentencetile.workers.dev/tile.xml  → 磁贴 XML（默认每小时固定一条「每日一言：」）
 *   https://onesentencetile.onesentencetile.workers.dev/health    → 自检 JSON
 *
 * 免费额度：10 万请求/天。磁贴每 30 分钟拉一次 ≈ 48 次/天，完全够用。
 *
 * 刷新策略（CONFIG.refreshMode）：
 *   hourly  = 每小时固定一条，同一小时内所有请求返回同一条（用 Cache API 存 1 小时）
 *   request = 每次请求都换新的
 *   想改成「每半小时」：把 hourKey() 里的 slice(0,13) 换成按半小时取整即可。
 *
 * 排版（和 GitHub Pages 版一致）：
 *   每日一言：
 *   　　正文……                      （开头两个全角空格）
 *   　　　　　　—— 作者《出处》      （右对齐，字号更小）
 */

// ---------------- 可调配置 ----------------
const CONFIG = {
  title: '每日一言：',
  showTitle: true,            // 第一行标题（已带全角冒号）
  quotePrefix: '',            // 正文前缀，标题已带冒号，一般留空
  indent: '\u3000\u3000',     // 正文缩进：两个全角空格
  showAttribution: true,      // 是否显示「—— 作者《出处》」
  attributionSep: ' —— ',
  smallText: '每日一言：',     // 71x71 小磁贴显示的字
  clipSmall: 0,
  clipMedium: 40,
  clipWide: 80,
  clipLarge: 200,
  // 分类由 App 通过 ?type=x 传入（a 动画 b 漫画 c 游戏 d 文学 e 原创 f 来自网络
  // g 其他 h 影视 i 诗词 j 网易云 k 哲学 l 抖机灵），不传 = 随机
  hitokotoUrl: 'https://v1.hitokoto.cn/',
  timeoutMs: 3000,
  // 刷新方式：hourly = 每小时固定一条（同一个小时里所有请求看到同一条）
  //           request = 每次请求都换（磁贴每 30 分钟拉一次就是一条新的）
  refreshMode: 'request',
  utcOffset: 8,               // 小时以北京时间为准
  cacheSeconds: 60
};

// ---------------- 接口挂了的兜底语录 ----------------
const FALLBACK_TEXT = "今日一言暂时取不到，稍后再试。";

// ---------------- 工具 ----------------
const esc = (s) => String(s)
  .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
  .replace(/"/g, '&quot;').replace(/'/g, '&apos;');

function clip(text, max) {
  if (!max || text.length <= max) return text;
  var slice = text.slice(0, max);
  // 优先在句末标点处断开，其次逗号，最后才硬切
  var stops = ["。", "！", "？", "；", "…", "，", "、"];
  for (var i = 0; i < stops.length; i++) {
    var at = slice.lastIndexOf(stops[i]);
    if (at >= max * 0.5) {
      return slice.slice(0, at + 1);
    }
  }
  return slice.slice(0, max - 1) + "…";
}

const STYLES = ["caption", "captionSubtle", "body", "bodySubtle", "base", "baseSubtle",
  "subtitle", "subtitleSubtle", "title", "titleSubtle", "subheader", "subheaderSubtle",
  "header", "headerSubtle"];

function pickStyle(value, fallback) {
  var v = String(value || "").trim();
  return STYLES.indexOf(v) >= 0 ? v : fallback;
}

function hourKey(type, styles) {
  // 用「北京时间的小时 + 分类」做缓存键，同一小时同一分类复用同一条
  const now = new Date(Date.now() + CONFIG.utcOffset * 3600 * 1000);
  var tag = [styles && styles.titleStyle, styles && styles.quoteStyle, styles && styles.attrStyle,
             (styles && styles.showTitle) ? "T1" : "T0", (styles && styles.showAttr) ? "A1" : "A0",
             styles && styles.titleAlign, styles && styles.attrAlign].join("-");
  return 'https://tile-cache.internal/' + (type || 'random') + '/' + tag + '/' + now.toISOString().slice(0, 13);
}

function fallbackItem(err) {
  return { text: FALLBACK_TEXT, from: '', who: '', source: 'placeholder', error: err ? String(err.message || err) : null };
}

async function fetchHitokoto(type) {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), CONFIG.timeoutMs);
  const target = type
    ? CONFIG.hitokotoUrl + (CONFIG.hitokotoUrl.includes('?') ? '&' : '?') + 'c=' + encodeURIComponent(type)
    : CONFIG.hitokotoUrl;
  try {
    const resp = await fetch(target, {
      signal: controller.signal,
      headers: { accept: 'application/json' },
      cf: { cacheTtl: 0 }
    });
    if (!resp.ok) throw new Error('HTTP ' + resp.status);
    const data = await resp.json();
    const text = (data.hitokoto || '').trim();
    if (!text) throw new Error('empty hitokoto');
    return { text, from: (data.from || '').trim(), who: (data.from_who || '').trim(), source: 'hitokoto' };
  } catch (e) {
    return fallbackItem(e);
  } finally {
    clearTimeout(timer);
  }
}

async function getHitokoto(type, styles) {
  // request 模式：每次请求都现取
  if (CONFIG.refreshMode !== 'hourly') return fetchHitokoto(type);

  const cache = (typeof caches !== 'undefined' && caches.default) ? caches.default : null;
  const key = new Request(hourKey(type, styles));

  if (cache) {
    const hit = await cache.match(key);
    if (hit) {
      const item = await hit.json();
      item.cached = true;
      return item;
    }
  }

  const item = await fetchHitokoto(type);
  item.cached = false;
  item.hour = hourKey(type, styles).split('/').pop();
  item.type = type || 'random';

  // 只缓存成功取到的结果（兜底的那条不缓存，下一分钟还可能取到真的）
  if (cache && item.source === 'hitokoto') {
    const body = JSON.stringify(item);
    await cache.put(key, new Response(body, {
      headers: { 'content-type': 'application/json', 'cache-control': 'max-age=' + CONFIG.cacheSeconds }
    }));
  }
  return item;
}

function attribution(item) {
  if (!CONFIG.showAttribution) return '';
  const who = item.who || '';
  const from = item.from || '';
  if (who && from) return CONFIG.attributionSep + who + '《' + from + '》';
  if (from) return CONFIG.attributionSep + '《' + from + '》';
  if (who) return CONFIG.attributionSep + who;
  return '';
}

function alignOf(value, fallback) {
  var v = String(value || "").trim().toLowerCase();
  return (v === "left" || v === "center" || v === "right") ? v : fallback;
}

function buildTileXml(item, opt) {
  const body = CONFIG.indent + CONFIG.quotePrefix + clip(item.text, CONFIG.clipLarge);
  const attr = attribution(item).trim();

  const sTitle = pickStyle(opt.titleStyle, "title");
  const sQuote = pickStyle(opt.quoteStyle, "base");
  const sAttr = pickStyle(opt.attrStyle, "captionSubtle");
  const showTitle = opt.showTitle;
  const showAttr = opt.showAttr && attr.length > 0;
  const titleAlign = alignOf(opt.titleAlign, "left");
  const attrAlign = alignOf(opt.attrAlign, "right");

  const small = '<text hint-style="caption">' + esc(CONFIG.smallText) + '</text>';
  const medium = '<text hint-style="' + sQuote + '" hint-wrap="true">'
    + esc(CONFIG.indent + CONFIG.quotePrefix + clip(item.text, CONFIG.clipMedium)) + '</text>';
  const wide = (showTitle ? '<text hint-style="' + sTitle + '" hint-align="' + titleAlign + '">' + esc(CONFIG.title) + '</text>' : '')
    + '<text hint-style="' + sQuote + '" hint-wrap="true">'
    + esc(CONFIG.indent + CONFIG.quotePrefix + clip(item.text, CONFIG.clipWide)) + '</text>'
    + (showAttr ? '<text hint-style="' + sAttr + '" hint-align="' + attrAlign + '" hint-wrap="true">' + esc(attr) + '</text>' : '');
  const large = (showTitle ? '<text hint-style="' + sTitle + '" hint-align="' + titleAlign + '">' + esc(CONFIG.title) + '</text>' : '')
    + '<text hint-style="' + sQuote + '" hint-wrap="true" hint-maxLines="5">' + esc(body) + '</text>'
    + (showAttr ? '<text hint-style="' + sAttr + '" hint-align="' + attrAlign + '" hint-wrap="true">' + esc(attr) + '</text>' : '');

  return '<?xml version="1.0" encoding="utf-8"?>\n'
    + '<tile>\n  <visual version="2">\n'
    + '    <binding template="TileSmall">' + small + '</binding>\n'
    + '    <binding template="TileMedium">' + medium + '</binding>\n'
    + '    <binding template="TileWide">' + wide + '</binding>\n'
    + '    <binding template="TileLarge">' + large + '</binding>\n'
    + '  </visual>\n</tile>\n';
}

export default {
  async fetch(request) {
    const url = new URL(request.url);
    // 分类：单个字母 a~l，非法值按随机处理
    let type = (url.searchParams.get('type') || '').trim().toLowerCase();
    if (!/^[a-l]$/.test(type)) type = '';
    const opt = {
      titleStyle: pickStyle(url.searchParams.get('titleStyle'), ''),
      quoteStyle: pickStyle(url.searchParams.get('quoteStyle'), ''),
      attrStyle: pickStyle(url.searchParams.get('attrStyle'), ''),
      showTitle: url.searchParams.get('showTitle') !== '0',
      showAttr: url.searchParams.get('showAttr') !== '0',
      titleAlign: alignOf(url.searchParams.get('titleAlign'), 'left'),
      attrAlign: alignOf(url.searchParams.get('attrAlign'), 'right')
    };

    if (url.pathname === '/health') {
      const item = await getHitokoto(type, opt);
      return new Response(JSON.stringify({
        ok: true,
        time: new Date().toISOString(),
        source: item.source,
        type: type || 'random',
        refreshMode: CONFIG.refreshMode,
        hour: item.hour || null,
        cached: !!item.cached,
        error: item.error || null,
        quote: item.text,
        from: item.from,
        fromWho: item.who,
        quoteUrl: url.origin + '/tile.xml'
      }, null, 2), {
        headers: { 'content-type': 'application/json; charset=utf-8', 'cache-control': 'no-store' }
      });
    }

    const item = await getHitokoto(type, opt);
    return new Response(buildTileXml(item, opt), {
      status: 200,
      headers: {
        'content-type': 'application/xml; charset=utf-8',
        // 磁贴必须每次拿新的，别让边缘缓存住
        'cache-control': 'no-store, no-cache, must-revalidate',
        'access-control-allow-origin': '*'
      }
    });
  }
};
