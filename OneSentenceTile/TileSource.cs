using System;
using System.Globalization;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Storage;

namespace OneSentenceTile
{
    /// <summary>
    /// 磁贴内容来源：Worker 和 Pages 两个，按用户设置决定优先用哪个，
    /// 优先的那个连不上、或者内容已经过期，就自动换另一个。
    ///
    /// Worker：https://onesentencetile.onesentencetile.workers.dev/tile.xml
    ///         每次请求现取「每日一言」，永远新鲜；支持 ?type=x 选分类。
    /// Pages ：https://bzhcyy.github.io/OneSentenceTile/tile.xml
    ///         由 GitHub Actions 生成，是「静态快照」。GitHub 的 schedule 只是尽力而为，
    ///         实测会被推迟到 4～9 小时才跑一次，所以这份快照经常是几小时前的。
    ///
    /// 判定方式：
    ///   1. 真实 GET 一次，4 秒超时，内容里出现 &lt;tile 才算「连得上」；
    ///   2. 选中 Pages 时，再读一次 health.json 的 generatedAt：超过 StaleAfterMinutes
    ///      就标记 PickedIsStale —— 由界面先改走一言官方 API，取不到再用 Worker 兜底，
    ///      别让磁贴停在几小时前那条。
    /// </summary>
    static class TileSource
    {
        public const string Worker = "https://onesentencetile.onesentencetile.workers.dev/tile.xml";
        public const string Pages = "https://bzhcyy.github.io/OneSentenceTile/tile.xml";
        public const string PagesBase = "https://bzhcyy.github.io/OneSentenceTile/";
        public const string PagesHealth = "https://bzhcyy.github.io/OneSentenceTile/health.json";

        /// <summary>Pages 快照放了多久就算「不可信」。工作流是 15 分钟一次，45 分钟足够容忍偶尔的延迟。</summary>
        private const int DefaultStaleMinutes = 45;

        private static string _picked;
        private static string _lastUri;
        private static bool _pickedStale;
        private static int _pagesAgeMinutes = -1;
        private static DateTime _pagesAgeCheckedUtc = DateTime.MinValue;

        /// <summary>true = Worker 优先；false = Pages 优先。存在 RoamingSettings 里。</summary>
        public static bool PreferWorker
        {
            get
            {
                var v = ApplicationData.Current.RoamingSettings.Values["preferWorker"];
                return v == null ? true : (bool)v;
            }
            set { ApplicationData.Current.RoamingSettings.Values["preferWorker"] = value; }
        }

        /// <summary>Pages 快照超过多少分钟算过期（默认 45）。</summary>
        public static int StaleAfterMinutes
        {
            get
            {
                var v = ApplicationData.Current.RoamingSettings.Values["tileStaleMinutes"];
                return v == null ? DefaultStaleMinutes : Convert.ToInt32(v);
            }
            set { ApplicationData.Current.RoamingSettings.Values["tileStaleMinutes"] = value; }
        }

        /// <summary>这次解析出来的 Pages 快照是否已经过期（过期时界面会直连一言兜底）。</summary>
        public static bool PickedIsStale
        {
            get { return _pickedStale; }
        }

        /// <summary>Pages 快照的年龄（分钟），-1 表示没读过/读不到 health.json。</summary>
        public static int PagesAgeMinutes
        {
            get { return _pagesAgeMinutes; }
        }

        /// <summary>当前选中的源，给界面显示用。</summary>
        public static string SourceLabel
        {
            get
            {
                if (_picked == null) return "未探测";
                return _picked == Pages ? "Pages" : "Worker";
            }
        }

        /// <summary>
        /// 一言分类：空 = 随机，否则是 a~l 里的一个字母
        /// （a 动画 b 漫画 c 游戏 d 文学 e 原创 f 来自网络 g 其他 h 影视 i 诗词 k 哲学 l 抖机灵）。
        /// </summary>
        public static string HitokotoType
        {
            get
            {
                var v = ApplicationData.Current.RoamingSettings.Values["hitokotoType"];
                return (v as string) ?? "";
            }
            set { ApplicationData.Current.RoamingSettings.Values["hitokotoType"] = value ?? ""; }
        }

        /// <summary>
        /// Worker 用 ?type=x；Pages 是静态托管，每个分类一个文件，换成 tile-x.xml。
        /// 随机（分类为空）时原样返回。
        /// </summary>
        public static string WithType(string url)
        {
            var t = HitokotoType;
            if (string.IsNullOrEmpty(t)) return url;

            if (url.StartsWith(Pages, StringComparison.OrdinalIgnoreCase))
            {
                // https://xxx/tile.xml -> https://xxx/tile-d.xml
                return url.Substring(0, url.Length - "tile.xml".Length) + "tile-" + t + ".xml";
            }

            return url + (url.Contains("?") ? "&" : "?") + "type=" + Uri.EscapeDataString(t);
        }

        /// <summary>
        /// 按设置的优先级挑一个能连上的地址；两个都不通时返回优先的那个。
        /// 挑中的是 Pages 时顺带判断快照是否过期（结果放在 PickedIsStale 里，
        /// 由界面决定是否走「官方 API → Worker」的兜底）。
        /// </summary>
        public static async Task<Uri> ResolveAsync()
        {
            string first = PreferWorker ? Worker : Pages;
            string second = PreferWorker ? Pages : Worker;

            _pickedStale = false;

            if (await ProbeAsync(WithType(first)))
            {
                _picked = first;
            }
            else if (await ProbeAsync(WithType(second)))
            {
                _picked = second;
            }
            else
            {
                _picked = first;
            }

            // 选中 Pages 时看一眼快照年龄：静态托管自己不会刷新，
            // GitHub 的 cron 又经常被推迟，所以「连得上」不等于「内容是新的」。
            // 过期后不在这里换源，交给界面按「一言官方 API → Worker」的顺序兜底；
            // Worker 保持原样，仍然是每次请求现取的实时源。
            if (_picked == Pages)
            {
                int age = await PagesAgeMinutesAsync();
                _pickedStale = age > StaleAfterMinutes;
            }

            _lastUri = WithStyle(WithCacheBust(WithType(_picked)));
            return new Uri(_lastUri);
        }

        /// <summary>上一次 ResolveAsync 选出来的地址，避免同一次刷新里重复探测。</summary>
        public static Uri LastResolvedUri
        {
            get { return _lastUri == null ? null : new Uri(_lastUri); }
        }

        /// <summary>当前选中的地址（没解析过就是 null），便于日志/界面显示。</summary>
        public static string PickedUrl
        {
            get { return _picked == null ? null : WithStyle(WithCacheBust(WithType(_picked))); }
        }

        private static async Task<bool> ProbeAsync(string url)
        {
            try
            {
                using (var http = new HttpClient())
                {
                    http.Timeout = TimeSpan.FromSeconds(2.5);
                    var text = await http.GetStringAsync(url);
                    return text != null && text.IndexOf("<tile", StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 读 Pages 的 health.json，算「快照放了多久」。30 秒内复用上一次结果，别重复请求。
        /// 返回 -1 表示读不到（没网 / health.json 不存在 / 格式不认识）。
        /// </summary>
        private static async Task<int> PagesAgeMinutesAsync()
        {
            if ((DateTime.UtcNow - _pagesAgeCheckedUtc).TotalSeconds < 30)
            {
                return _pagesAgeMinutes;
            }

            try
            {
                using (var http = new HttpClient())
                {
                    // 带 ?v= 绕开 Pages 的 10 分钟 CDN 缓存，否则读到的可能是更旧的一份
                    http.Timeout = TimeSpan.FromSeconds(3);
                    var json = await http.GetStringAsync(PagesHealth + "?v=" + EpochSeconds());

                    var match = Regex.Match(json, "\"generatedAt\"\\s*:\\s*\"([^\"]+)\"");
                    if (!match.Success)
                    {
                        _pagesAgeCheckedUtc = DateTime.UtcNow;
                        _pagesAgeMinutes = -1;
                        return -1;
                    }

                    DateTimeOffset generated;
                    if (!DateTimeOffset.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out generated))
                    {
                        _pagesAgeCheckedUtc = DateTime.UtcNow;
                        _pagesAgeMinutes = -1;
                        return -1;
                    }

                    var age = (int)Math.Max(0, (DateTimeOffset.UtcNow - generated).TotalMinutes);
                    _pagesAgeMinutes = age;
                    _pagesAgeCheckedUtc = DateTime.UtcNow;
                    return age;
                }
            }
            catch
            {
                _pagesAgeCheckedUtc = DateTime.UtcNow;
                _pagesAgeMinutes = -1;
                return -1;
            }
        }

        /// <summary>
        /// 静态文件走 CDN（GitHub Pages 是 10 分钟），磁贴又被系统按 URL 缓存，
        /// 所以给 Pages 地址挂一个时间戳：每轮刷新都是新的 URL，不会拿到旧副本。
        /// </summary>
        private static string WithCacheBust(string url)
        {
            // 注意用 PagesBase 判断：选了分类时地址是 tile-d.xml，不以 tile.xml 结尾
            if (!url.StartsWith(PagesBase, StringComparison.OrdinalIgnoreCase)) return url;
            return url + (url.Contains("?") ? "&" : "?") + "v=" + EpochSeconds();
        }

        private static long EpochSeconds()
        {
            return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        }

        // ---------------- 直连一言：两个服务器都不可信时的最后兜底 ----------------

        /// <summary>一条「一言」，字段和两个服务器返回的一致。</summary>
        public sealed class HitokotoQuote
        {
            public string Text = "";
            public string From = "";
            public string Who = "";
        }

        /// <summary>
        /// 直接问 v1.hitokoto.cn 要一条。App 有 internetClient 权限，国内一般直连得到，
        /// 所以就算 Worker 被墙、Pages 快照过期，磁贴也能拿到新句子（排版仍由 App 本地重建）。
        /// </summary>
        public static async Task<HitokotoQuote> FetchHitokotoAsync()
        {
            string url = "https://v1.hitokoto.cn/?encode=json";
            var type = HitokotoType;
            if (!string.IsNullOrEmpty(type)) url += "&c=" + Uri.EscapeDataString(type);
            url += "&v=" + EpochSeconds();

            try
            {
                using (var http = new HttpClient())
                {
                    http.Timeout = TimeSpan.FromSeconds(6);
                    var body = await http.GetStringAsync(url);
                    var obj = JsonObject.Parse(body);
                    var quote = new HitokotoQuote
                    {
                        Text = JsonString(obj, "hitokoto"),
                        From = JsonString(obj, "from"),
                        Who = JsonString(obj, "from_who")
                    };
                    return quote.Text.Length == 0 ? null : quote;
                }
            }
            catch
            {
                return null;
            }
        }

        private static string JsonString(JsonObject obj, string key)
        {
            if (obj == null || !obj.ContainsKey(key)) return "";
            try
            {
                // from_who 可能是 null：这种情况按空字符串处理
                return obj.GetNamedString(key, "");
            }
            catch
            {
                return "";
            }
        }

        /// <summary>「—— 作者《出处》」，和两个服务器生成的格式保持一致。</summary>
        public static string FormatAttribution(string who, string from)
        {
            who = (who ?? "").Trim();
            from = (from ?? "").Trim();
            if (who.Length > 0 && from.Length > 0) return "—— " + who + "《" + from + "》";
            if (from.Length > 0) return "—— 《" + from + "》";
            if (who.Length > 0) return "—— " + who;
            return "";
        }

        // ---------------- 磁贴文字样式（Windows 只支持这几档 hint-style） ----------------
        public static string StyleTitle
        {
            get { return GetSetting("tileTitleStyle", "title"); }
            set { PutSetting("tileTitleStyle", value); }
        }

        public static string StyleQuote
        {
            get { return GetSetting("tileQuoteStyle", "base"); }
            set { PutSetting("tileQuoteStyle", value); }
        }

        public static string StyleAttr
        {
            get { return GetSetting("tileAttrStyle", "captionSubtle"); }
            set { PutSetting("tileAttrStyle", value); }
        }

        public static bool ShowTitle
        {
            get { return ApplicationData.Current.RoamingSettings.Values["tileShowTitle"] == null ? true : (bool)ApplicationData.Current.RoamingSettings.Values["tileShowTitle"]; }
            set { ApplicationData.Current.RoamingSettings.Values["tileShowTitle"] = value; }
        }

        public static bool ShowAttr
        {
            get { return ApplicationData.Current.RoamingSettings.Values["tileShowAttr"] == null ? true : (bool)ApplicationData.Current.RoamingSettings.Values["tileShowAttr"]; }
            set { ApplicationData.Current.RoamingSettings.Values["tileShowAttr"] = value; }
        }

        public static string TitleAlign
        {
            get { return GetSetting("tileTitleAlign", "left"); }
            set { PutSetting("tileTitleAlign", value); }
        }

        public static string AttrAlign
        {
            get { return GetSetting("tileAttrAlign", "right"); }
            set { PutSetting("tileAttrAlign", value); }
        }

        /// <summary>拼上样式参数：Worker 会据此改 XML，Pages 静态文件忽略，App 本地重建时也用不到。</summary>
        public static string WithStyle(string url)
        {
            return url + (url.Contains("?") ? "&" : "?")
                + "titleStyle=" + Uri.EscapeDataString(StyleTitle)
                + "&quoteStyle=" + Uri.EscapeDataString(StyleQuote)
                + "&attrStyle=" + Uri.EscapeDataString(StyleAttr)
                + "&showTitle=" + (ShowTitle ? "1" : "0")
                + "&showAttr=" + (ShowAttr ? "1" : "0")
                + "&titleAlign=" + Uri.EscapeDataString(TitleAlign)
                + "&attrAlign=" + Uri.EscapeDataString(AttrAlign);
        }

        /// <summary>拉取内容用的地址：强制带标题和出处，方便 App 本地重建 XML。</summary>
        public static async Task<Uri> ResolveContentAsync()
        {
            var uri = await ResolveAsync();
            var text = uri.AbsoluteUri;
            return new Uri(text + (text.Contains("?") ? "&" : "?") + "showTitle=1&showAttr=1");
        }

        /// <summary>
        /// Worker 的内容地址。Worker 是实时源（每次请求现取），只在 Pages 过期、
        /// 官方 API 也取不到时兜底用，App 对它不做任何改动。
        /// </summary>
        public static Uri WorkerContentUri()
        {
            var text = WithStyle(WithType(Worker));
            return new Uri(text + (text.Contains("?") ? "&" : "?") + "showTitle=1&showAttr=1");
        }

        private static string GetSetting(string key, string fallback)
        {
            var v = ApplicationData.Current.RoamingSettings.Values[key];
            return (v as string) ?? fallback;
        }

        private static void PutSetting(string key, string value)
        {
            ApplicationData.Current.RoamingSettings.Values[key] = value ?? "";
        }
    }
}
