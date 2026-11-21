using System;
using System.Net.Http;
using System.Threading.Tasks;

namespace addOneSecond
{
    /// <summary>
    /// 磁贴内容来源：优先 Cloudflare Worker，拉不到就自动退回 GitHub Pages。
    ///
    /// Worker：https://add-one-second.addonesecond.workers.dev/tile.xml
    ///         每次请求现取「每日一言」，同一小时固定一条；国内可能被 DNS 污染。
    /// Pages ：https://bzhcyy.github.io/add-one-second-UWP/tile.xml
    ///         每小时生成一次，github.io 国内基本可直连，当兜底。
    ///
    /// 判定方式：真实 GET 一次，4 秒超时，内容里出现 &lt;tile 才算可用。
    /// </summary>
    static class TileSource
    {
        public const string Worker = "https://add-one-second.addonesecond.workers.dev/tile.xml";
        public const string Pages = "https://bzhcyy.github.io/add-one-second-UWP/tile.xml";

        private static string _picked;

        /// <summary>返回本次运行要用的磁贴地址（进程内只探一次）。</summary>
        public static async Task<Uri> ResolveAsync()
        {
            if (!string.IsNullOrEmpty(_picked))
            {
                return new Uri(_picked);
            }

            _picked = await ProbeAsync(Worker) ? Worker : Pages;
            return new Uri(_picked);
        }

        /// <summary>当前选中的地址，便于界面/日志显示。</summary>
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
