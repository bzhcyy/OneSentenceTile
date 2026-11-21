using System;
using System.Net.Http;
using System.Threading.Tasks;
using Windows.Storage;

namespace OneSentenceTile
{
    /// <summary>
    /// 磁贴内容来源：Worker 和 Pages 两个，按用户设置决定优先用哪个，
    /// 优先的那个连不上就自动换另一个。
    ///
    /// Worker：https://onesentencetile.onesentencetile.workers.dev/tile.xml
    ///         每次请求现取「每日一言」，同一小时固定一条；支持 ?type=x 选分类。
    /// Pages ：https://bzhcyy.github.io/OneSentenceTile/tile.xml
    ///         每小时生成一次，github.io 国内基本可直连（不支持分类，永远随机）。
    ///
    /// 判定方式：真实 GET 一次，4 秒超时，内容里出现 &lt;tile 才算可用。
    /// </summary>
    static class TileSource
    {
        public const string Worker = "https://onesentencetile.onesentencetile.workers.dev/tile.xml";
        public const string Pages = "https://bzhcyy.github.io/OneSentenceTile/tile.xml";

        private static string _picked;

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

        /// <summary>按设置的优先级挑一个能用的地址；两个都不通时返回优先的那个。</summary>
        public static async Task<Uri> ResolveAsync()
        {
            bool preferWorker = PreferWorker;
            string first = preferWorker ? Worker : Pages;
            string second = preferWorker ? Pages : Worker;

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
            return new Uri(WithType(_picked));
        }

        /// <summary>当前选中的地址（没解析过就是 null），便于日志/界面显示。</summary>
        public static string PickedUrl
        {
            get { return _picked == null ? null : WithType(_picked); }
        }

        private static async Task<bool> ProbeAsync(string url)
        {
            try
            {
                using (var http = new HttpClient())
                {
                    http.Timeout = TimeSpan.FromSeconds(4);
                    var text = await http.GetStringAsync(url);
                    return text != null && text.IndexOf("<tile", StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch
            {
                return false;
            }
        }
    }
}
