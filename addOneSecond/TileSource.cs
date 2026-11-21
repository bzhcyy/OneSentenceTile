using System;
using System.Net.Http;
using System.Threading.Tasks;
using Windows.Storage;

namespace addOneSecond
{
    /// <summary>
    /// 磁贴内容来源：Worker 和 Pages 两个，按用户设置决定优先用哪个，
    /// 优先的那个连不上就自动换另一个。
    ///
    /// Worker：https://add-one-second.addonesecond.workers.dev/tile.xml
    ///         每次请求现取「每日一言」，同一小时固定一条；国内可能被 DNS 污染。
    /// Pages ：https://bzhcyy.github.io/add-one-second-UWP/tile.xml
    ///         每小时生成一次，github.io 国内基本可直连。
    ///
    /// 判定方式：真实 GET 一次，4 秒超时，内容里出现 &lt;tile 才算可用。
    /// </summary>
    static class TileSource
    {
        public const string Worker = "https://add-one-second.addonesecond.workers.dev/tile.xml";
        public const string Pages = "https://bzhcyy.github.io/add-one-second-UWP/tile.xml";

        private static string _picked;

        /// <summary>true = Worker 优先；false = Pages 优先。存在 RoamingSettings 里。</summary>
        public static bool PreferWorker
        {
            get
            {
                var v = ApplicationData.Current.RoamingSettings.Values["preferWorker"];
                return v == null ? true : (bool)v;
            }
            set
            {
                ApplicationData.Current.RoamingSettings.Values["preferWorker"] = value;
            }
        }

        /// <summary>按设置的优先级挑一个能用的地址；两个都不通时返回优先的那个。</summary>
        public static async Task<Uri> ResolveAsync()
        {
            bool preferWorker = PreferWorker;
            string first = preferWorker ? Worker : Pages;
            string second = preferWorker ? Pages : Worker;

            if (await ProbeAsync(first))
            {
                _picked = first;
            }
            else if (await ProbeAsync(second))
            {
                _picked = second;
            }
            else
            {
                _picked = first;
            }
            return new Uri(_picked);
        }

        /// <summary>当前选中的地址（没解析过就是 null），便于日志/界面显示。</summary>
        public static string PickedUrl
        {
            get { return _picked; }
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
