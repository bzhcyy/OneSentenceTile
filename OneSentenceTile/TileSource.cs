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
    /// 磁贴内容来源：官方一言 / Worker / Pages 三个，用户在设置里三选一，
    /// 选中的那个取不到就按固定顺序换下一个。
    ///
    /// 官方一言：https://v1.hitokoto.cn  直连 JSON，App 自己拼磁贴；国内一般可达，
    ///          但系统「周期更新」只认 XML 地址，所以 App 关掉后由 Worker 顶替。
    ///
    /// Worker：https://onesentencetile.onesentencetile.workers.dev/tile.xml
    ///         每次请求现取「每日一言」，永远新鲜；支持 ?type=x 选分类。
    /// Pages ：https://bzhcyy.github.io/OneSentenceTile/tile.xml
    ///         由 GitHub Actions 生成，是「静态快照」。GitHub 的 schedule 只是尽力而为，
    ///         实测会被推迟到 4～9 小时才跑一次，所以这份快照经常是几小时前的。
    ///
    /// 判定方式：
    ///   1. 真实 GET 一次，超时就算「连不上」，换下一个源；
    ///   2. 选中 Pages 时，再读一次 health.json 的 generatedAt：超过 StaleAfterMinutes
    ///      就算快照停更，界面直接跳过它，别让磁贴停在几小时前那条。
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
        private static string _lastUriMode;
        private static int _pagesAgeMinutes = -1;
        private static DateTime _pagesAgeCheckedUtc = DateTime.MinValue;

        /// <summary>磁贴源三选一，存在 RoamingSettings 的 tileSource 里。</summary>
        public static class Mode
        {
            public const string Hitokoto = "hitokoto";
            public const string Worker = "worker";
            public const string Pages = "pages";
        }

        /// <summary>当前选中的磁贴源：hitokoto（官方一言，默认）/ worker / pages。</summary>
        public static string SelectedMode
        {
            get
            {
                var v = ApplicationData.Current.RoamingSettings.Values["tileSource"] as string;
                if (v == Mode.Worker) return Mode.Worker;
                if (v == Mode.Pages) return Mode.Pages;
                return Mode.Hitokoto;
            }
            set
            {
                ApplicationData.Current.RoamingSettings.Values["tileSource"] =
                    (value == Mode.Worker || value == Mode.Pages) ? value : Mode.Hitokoto;
            }
        }

        /// <summary>界面上显示的名称，和设置里下拉框的文字对应。</summary>
        public static string ModeName(string mode)
        {
            if (mode == Mode.Worker) return "Worker";
            if (mode == Mode.Pages) return "Pages";
            return "官方一言";
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
        /// 给 Windows 系统「周期更新」选择 XML 地址。官方 API 返回 JSON，
        /// 系统无法直接解析，因此应用关闭时按 Worker → Pages 的顺序回退。
        /// 官方 API 仍是应用运行时的第一优先级。
        /// </summary>
        public static async Task<Uri> ResolveAsync()
        {
            string first = Worker;
            string second = Pages;

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
                _picked = second; // 两个 XML 源都探测失败时保留 Pages 作为系统重试地址
            }

            // 静态托管走 CDN（10 分钟）、磁贴又被系统按 URL 缓存，所以挂个时间戳；
            // Pages 快照是否过期由界面用 PagesFreshAsync 另行判断，这里只管地址。
            _lastUri = WithStyle(WithCacheBust(WithType(_picked)));
            _lastUriMode = SelectedMode;
            return new Uri(_lastUri);
        }

        /// <summary>
        /// Pages 快照还算不算新鲜：health.json 读不到时按「新鲜」处理，
        /// 别因为读不到健康信息就把好好的备用源也跳过了。
        /// </summary>
        public static async Task<bool> PagesFreshAsync()
        {
            int age = await PagesAgeMinutesAsync();
            return age < 0 || age <= StaleAfterMinutes;
        }

        /// <summary>上一次 ResolveAsync 选出来的地址；换了磁贴源就当没解析过，重探一次。</summary>
        public static Uri LastResolvedUri
        {
            get
            {
                if (_lastUri == null || _lastUriMode != SelectedMode) return null;
                return new Uri(_lastUri);
            }
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

        /// <summary>
        /// Worker 的内容地址。Worker 是实时源（每次请求现取），
        /// 官方一言取不到、或者 Pages 过期时兜底用，App 对它不做任何改动。
        /// </summary>
        public static Uri WorkerContentUri()
        {
            var text = WithStyle(WithType(Worker));
            return new Uri(text + (text.Contains("?") ? "&" : "?") + "showTitle=1&showAttr=1");
        }

        /// <summary>Pages 的内容地址（带时间戳绕开 CDN 缓存）。</summary>
        public static Uri PagesContentUri()
        {
            var text = WithStyle(WithCacheBust(WithType(Pages)));
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
