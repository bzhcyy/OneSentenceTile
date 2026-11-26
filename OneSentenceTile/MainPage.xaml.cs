using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.AccessCache;
using Windows.Storage.Pickers;
using Windows.UI;
using Windows.UI.Text;
using Windows.UI.Notifications;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;

namespace OneSentenceTile
{
    /// <summary>一言 Tile 主页面：展示每日一言 + 磁贴/壁纸设置。</summary>
    public sealed partial class MainPage : Page
    {
        private bool _loading;                                   //正在套用设置，别触发保存
        private DispatcherTimer _wallTimer;                      //壁纸轮播定时器
        private DispatcherTimer _tileTimer;                      //运行期间按间隔刷新磁贴（保证自定义排版）
        private readonly List<StorageFile> _wallFiles = new List<StorageFile>();
        private int _wallIndex;

        private const string DailyTitle = "每日一言";            //标题文字（界面和磁贴共用）
        private const string DailyTitleWithColon = DailyTitle + "："; //标题统一带全角冒号

        public MainPage()
        {
            this.InitializeComponent();
        }

        // ------------------------------------------------------------ 启动
        private async void Page_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                var dict = await StorageFile.GetFileFromApplicationUriAsync(new Uri("ms-appx:///VoiceCommandDictionary.xml"));
                await Windows.ApplicationModel.VoiceCommands.VoiceCommandDefinitionManager
                    .InstallCommandDefinitionsFromStorageFileAsync(dict);
            }
            catch { }

            try
            {
                Windows.ApplicationModel.Core.CoreApplication.GetCurrentView().TitleBar.ExtendViewIntoTitleBar = true;
            }
            catch { }

            // 一次性迁移：按 settingsRev 逐版本补齐默认值，已经迁过的用户不会再被覆盖
            var rev = 0;
            int.TryParse(GetString("settingsRev", "0"), out rev);

            if (rev < 5)
            {
                // 默认：标题大24、正文标准15、出处极小淡12；标题靠左、出处靠右
                SetString("tileTitleAlign", "left");
                SetString("tileAttrAlign", "right");
                SetString("tileTitleStyle", "title");
                SetString("tileQuoteStyle", "base");
                SetString("tileAttrStyle", "captionSubtle");
                SetBool("tileOn", true);
                SetBool("tileShowTitle", true);
                SetBool("tileShowAttr", true);
                rev = 5;
            }

            if (rev < 6)
            {
                // 磁贴源从「Worker / Pages 两档开关」改成官方一言 / Worker / Pages 三选一，
                // 默认走官方一言（v1.hitokoto.cn）；旧的 preferWorker 不再使用
                SetString("tileSource", TileSource.Mode.Hitokoto);
                try
                {
                    var values = ApplicationData.Current.RoamingSettings.Values;
                    if (values.ContainsKey("preferWorker")) values.Remove("preferWorker");
                }
                catch { }
                rev = 6;
            }

            if (rev < 7)
            {
                // 修正旧版默认正文样式：body（小15）改为 base（标准15）。
                // 只迁移旧默认值，不覆盖用户已选择的其他正文样式。
                if (GetString("tileQuoteStyle", "base") == "body")
                    SetString("tileQuoteStyle", "base");
                rev = 7;
            }

            SetString("settingsRev", rev.ToString());

            FillTileStyleCombos();
            FillUiSizeCombos();

            _loading = true;
            isTileFresh.IsOn = GetBool("tileOn", true);
            SelectComboByTag(tileSourceCombo, TileSource.SelectedMode);
            isDisplayRequest.IsOn = GetBool("keepOn", false);
            isfullScreen.IsOn = GetBool("fullscreen", false);
            BackGroundColorRedSlider.Value = GetDouble("bgR", 255);
            BackGroundColorGreenSlider.Value = GetDouble("bgG", 255);
            BackGroundColorBlueSlider.Value = GetDouble("bgB", 255);
            BackGroundAcrylicBlueSlider.Value = GetDouble("bgA", 10);
            FontColorRedSlider.Value = GetDouble("fgR", 0);
            FontColorGreenSlider.Value = GetDouble("fgG", 0);
            FontColorBlueSlider.Value = GetDouble("fgB", 0);
            SelectComboByTag(hitokotoTypeCombo, TileSource.HitokotoType);
            SelectComboByTag(wallpaperModeCombo, WallpaperMode);
            SelectComboByTag(wallpaperIntervalCombo, WallpaperIntervalMin.ToString());
            SelectComboByTag(tileTitleStyleCombo, TileSource.StyleTitle);
            SelectComboByTag(tileQuoteStyleCombo, TileSource.StyleQuote);
            SelectComboByTag(tileAttrStyleCombo, TileSource.StyleAttr);
            tileShowTitleSwitch.IsOn = TileSource.ShowTitle;
            tileShowAttrSwitch.IsOn = TileSource.ShowAttr;
            SelectComboByTag(tileTitleAlignCombo, TileSource.TitleAlign);
            SelectComboByTag(tileAttrAlignCombo, TileSource.AttrAlign);
            SelectComboByTag(tileIntervalCombo, TileIntervalTag);
            SelectComboByTag(uiFontCombo, GetString("uiFont", ""));
            customFontBox.Text = GetString("uiFontCustom", "");
            SelectComboByTag(uiTitleSizeCombo, GetString("uiTitleSize", "60"));
            SelectComboByTag(uiQuoteSizeCombo, GetString("uiQuoteSize", "40"));
            SelectComboByTag(uiFromSizeCombo, GetString("uiFromSize", "20"));
            uiTitleBold.IsOn = GetBool("uiTitleBold", true);
            uiQuoteBold.IsOn = GetBool("uiQuoteBold", true);
            uiFromBold.IsOn = GetBool("uiFromBold", false);
            wallpaperUrlBox.Text = GetString("wallUrl", "");
            _loading = false;

            SetBcakGroundColor();
            SetForeColor();
            ApplyUiFont();
            ApplyUiTextStyles();
            ApplyTitleBarTheme();
            await ApplyWallpaperAsync();
            await LoadDailyQuoteAsync();
            await ApplyTileAsync();
        }

        // ------------------------------------------------------------ 设置存取
        private static string GetString(string key, string fallback)
        {
            var v = ApplicationData.Current.RoamingSettings.Values[key];
            return (v as string) ?? fallback;
        }

        private static void SetString(string key, string value)
        {
            ApplicationData.Current.RoamingSettings.Values[key] = value ?? "";
        }

        private static bool GetBool(string key, bool fallback)
        {
            var v = ApplicationData.Current.RoamingSettings.Values[key];
            return v == null ? fallback : (bool)v;
        }

        private static void SetBool(string key, bool value)
        {
            ApplicationData.Current.RoamingSettings.Values[key] = value;
        }

        private static double GetDouble(string key, double fallback)
        {
            var v = ApplicationData.Current.RoamingSettings.Values[key];
            return v == null ? fallback : Convert.ToDouble(v);
        }

        private static void SetDouble(string key, double value)
        {
            ApplicationData.Current.RoamingSettings.Values[key] = value;
        }

        private static void SelectComboByTag(ComboBox combo, string tag)
        {
            foreach (ComboBoxItem item in combo.Items)
            {
                if (((string)item.Tag) == (tag ?? ""))
                {
                    combo.SelectedItem = item;
                    return;
                }
            }
            if (combo.Items.Count > 0) combo.SelectedIndex = 0;
        }

        // ------------------------------------------------------------ 磁贴


        /// <summary>刷新间隔。实时模式在应用内立即刷新，并使用系统允许的最短 30 分钟周期作为关闭应用后的后台兜底。</summary>
        private string TileIntervalTag
        {
            get
            {
                var value = GetString("tileInterval", "Realtime");
                // 兼容旧版本保存的 15 分钟设置：迁移为系统支持的最短周期 30 分钟。
                if (value == "QuarterHour")
                {
                    SetString("tileInterval", "HalfHour");
                    return "HalfHour";
                }
                return value;
            }
            set { SetString("tileInterval", value); }
        }

        private static readonly Dictionary<string, PeriodicUpdateRecurrence> IntervalMap =
            new Dictionary<string, PeriodicUpdateRecurrence>
            {
                { "HalfHour", PeriodicUpdateRecurrence.HalfHour },
                { "Hour", PeriodicUpdateRecurrence.Hour },
                { "SixHours", PeriodicUpdateRecurrence.SixHours },
                { "TwelveHours", PeriodicUpdateRecurrence.TwelveHours },
                { "Daily", PeriodicUpdateRecurrence.Daily }
            };

        private static int IntervalMinutes(string tag)
        {
            switch (tag)
            {
                case "HalfHour": return 30;
                case "Hour": return 60;
                case "SixHours": return 360;
                case "TwelveHours": return 720;
                case "Daily": return 1440;
                default: return 0;
            }
        }

        /// <summary>App 运行期间按间隔刷新磁贴；关掉 App 后由系统的周期更新兜底。</summary>
        private void StartTileTimer(int minutes)
        {
            StopTileTimer();
            if (minutes <= 0) return;

            _tileTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(minutes) };
            _tileTimer.Tick += async (s, e) =>
            {
                try
                {
                    await RefreshTileOnceAsync();
                }
                catch { }
            };
            _tileTimer.Start();
        }

        private void StopTileTimer()
        {
            if (_tileTimer != null)
            {
                _tileTimer.Stop();
                _tileTimer = null;
            }
        }

        private async Task ApplyTileAsync()
        {
            var updater = TileUpdateManager.CreateTileUpdaterForApplication();
            if (!isTileFresh.IsOn)
            {
                try { updater.StopPeriodicUpdate(); } catch { }
                StopTileTimer();
                UpdateTileStatus("");
                return;
            }

            try { updater.StopPeriodicUpdate(); } catch { }

            // 不论选择「实时」还是固定间隔，都先立即刷新一次。
            await RefreshTileOnceAsync();

            // Windows 的系统磁贴后台更新只接受 XML URL，不能直接请求官方一言 JSON。
            // 「实时」也必须注册系统周期更新，否则关闭 App / 重启后完全没有后台刷新。
            // Windows 最短周期为 30 分钟，因此实时模式的后台兜底按 30 分钟执行。
            var intervalTag = TileIntervalTag;
            var periodicTag = intervalTag == "Realtime" ? "HalfHour" : intervalTag;
            var uri = TileSource.LastResolvedUri;
            if (uri == null) uri = await TileSource.ResolveAsync();

            PeriodicUpdateRecurrence recurrence;
            if (!IntervalMap.TryGetValue(periodicTag, out recurrence))
                recurrence = PeriodicUpdateRecurrence.HalfHour;

            try
            {
                updater.StartPeriodicUpdate(uri, recurrence);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("注册磁贴后台周期更新失败：" + ex);
            }

            // App 开着时也按有效周期刷新；实时模式采用系统支持的最短 30 分钟。
            StartTileTimer(IntervalMinutes(periodicTag));
        }

        /// <summary>
        /// 先尝试用户在设置中明确选择的源，再按该源的回退顺序尝试其他源。
        /// 官方一言：API → Worker → Pages；Worker：Worker → Pages → API；
        /// Pages：Pages → API → Worker（过期 Pages 会在调用处跳过）。
        /// </summary>
        private async Task<bool> RefreshTileOnceAsync()
        {
            var mode = TileSource.SelectedMode;
            var ok = false;
            var usedLabel = "";

            foreach (var step in SourceOrder(mode))
            {
                if (step == TileSource.Mode.Pages)
                {
                    // 用户明确选择 Pages 时尊重选择；只有 Pages 作为自动回退源时才跳过过期快照。
                    if (mode != TileSource.Mode.Pages && !await TileSource.PagesFreshAsync()) continue;
                    if (await PushTileNowAsync(TileSource.PagesContentUri()))
                    {
                        ok = true;
                        usedLabel = TileSource.ModeName(TileSource.Mode.Pages);
                        break;
                    }
                }
                else if (step == TileSource.Mode.Worker)
                {
                    if (await PushTileNowAsync(TileSource.WorkerContentUri()))
                    {
                        ok = true;
                        usedLabel = TileSource.ModeName(TileSource.Mode.Worker);
                        break;
                    }
                }
                else
                {
                    // 官方一言只给文字，排版由 App 本地重建
                    var quote = await TileSource.FetchHitokotoAsync();
                    if (quote != null && PushTileXml(BuildTileXml(
                            DailyTitleWithColon, quote.Text,
                            TileSource.FormatAttribution(quote.Who, quote.From))))
                    {
                        ok = true;
                        usedLabel = TileSource.ModeName(TileSource.Mode.Hitokoto);
                        break;
                    }
                }
            }

            UpdateAlignAvailability();
            UpdateTileStatus(usedLabel);
            return ok;
        }

        private static IEnumerable<string> SourceOrder(string mode)
        {
            if (mode == TileSource.Mode.Worker)
                return new[] { TileSource.Mode.Worker, TileSource.Mode.Pages, TileSource.Mode.Hitokoto };
            if (mode == TileSource.Mode.Pages)
                return new[] { TileSource.Mode.Pages, TileSource.Mode.Hitokoto, TileSource.Mode.Worker };
            return new[] { TileSource.Mode.Hitokoto, TileSource.Mode.Worker, TileSource.Mode.Pages };
        }

        /// <summary>
        /// 对齐选项始终可用：磁贴 XML 由 App 本地重建（RebuildTileXml），
        /// 标题/出处的对齐对 Worker 和 Pages 源都生效，不存在“静态文件改不了”的问题。
        /// </summary>
        private void UpdateAlignAvailability()
        {
            tileTitleAlignCombo.IsEnabled = true;
            tileAttrAlignCombo.IsEnabled = true;
        }

        /// <summary>状态栏只说明实际用的是哪个源，不提示兜底过程。</summary>
        private void UpdateTileStatus(string usedLabel)
        {
            if (tileStatusText == null) return;
            if (!isTileFresh.IsOn)
            {
                tileStatusText.Text = "动态磁贴已关闭";
                return;
            }

            var label = usedLabel.Length > 0 ? usedLabel : TileSource.ModeName(TileSource.SelectedMode);
            tileStatusText.Text = "磁贴源：" + label;
        }

        private async Task<bool> PushTileNowAsync(Uri uri)
        {
            try
            {
                string remote;
                using (var http = new System.Net.Http.HttpClient())
                {
                    http.Timeout = TimeSpan.FromSeconds(8);
                    remote = await http.GetStringAsync(uri);
                }

                // 服务器只负责提供文字，排版（字号/对齐/开关）由 App 按用户设置重建，
                // 这样 Pages 静态源也能用同一套样式。
                return PushTileXml(RebuildTileXml(remote));
            }
            catch
            {
                return false;
            }
        }

        private static bool PushTileXml(string localXml)
        {
            try
            {
                var doc = new Windows.Data.Xml.Dom.XmlDocument();
                doc.LoadXml(localXml);
                TileUpdateManager.CreateTileUpdaterForApplication().Update(new TileNotification(doc));
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string XmlEscape(string value)
        {
            return (value ?? "")
                .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
                .Replace("\"", "&quot;").Replace("'", "&apos;");
        }


        /// <summary>判断一段文字是不是标题（兼容带冒号和不带冒号两种）。</summary>
        private static bool IsTitleText(string text)
        {
            var t = (text ?? "").Trim();
            return t == DailyTitle || t == DailyTitleWithColon;
        }

        /// <summary>解析服务器给的磁贴 XML，取出标题/正文/出处，再按设置重新排版。</summary>
        private string RebuildTileXml(string remoteXml)
        {
            string title = DailyTitleWithColon;
            string quote = "";
            string attr = "";

            try
            {
                var doc = System.Xml.Linq.XDocument.Parse(remoteXml);
                var large = doc.Descendants("binding")
                    .FirstOrDefault(b => (string)b.Attribute("template") == "TileLarge")
                    ?? doc.Descendants("binding").LastOrDefault();
                var texts = large != null
                    ? large.Elements("text").Select(x => (x.Value ?? "").Trim()).Where(x => x.Length > 0).ToList()
                    : new List<string>();

                if (texts.Count >= 3)
                {
                    title = texts[0];
                    quote = texts[1];
                    attr = texts[2];
                }
                else if (texts.Count == 2)
                {
                    // 没出处：判断第一段是不是标题（Worker / Pages 已直接返回「每日一言：」）
                    if (IsTitleText(texts[0])) { title = texts[0]; quote = texts[1]; }
                    else { quote = texts[0]; attr = texts[1]; }
                }
                else if (texts.Count == 1)
                {
                    quote = texts[0];
                }
            }
            catch
            {
                quote = "（磁贴内容解析失败）";
            }

            return BuildTileXml(title, quote, attr);
        }

        /// <summary>
        /// 把「标题 / 正文 / 出处」排成磁贴 XML（字号/对齐/开关全部按设置来）。
        /// 服务器返回的 XML 和直连一言拿到的文字都走这里，保证两条路排版一致。
        /// </summary>
        private string BuildTileXml(string title, string quote, string attr)
        {
            title = string.IsNullOrWhiteSpace(title) ? DailyTitleWithColon : title.Trim();
            quote = (quote ?? "").Trim();
            attr = (attr ?? "").Trim();
            if (quote.Length == 0) quote = "（今日一言暂时取不到）";

            var indent = "\u3000\u3000";
            var tStyle = TileSource.StyleTitle;
            var qStyle = TileSource.StyleQuote;
            var aStyle = TileSource.StyleAttr;
            var tAlign = TileSource.TitleAlign;
            var aAlign = TileSource.AttrAlign;

            string Text(string style, string body, string align, int maxLines)
            {
                var extra = string.IsNullOrEmpty(align) ? "" : " hint-align=\"" + align + "\"";
                if (maxLines > 0) extra += " hint-maxLines=\"" + maxLines + "\"";
                return "<text hint-style=\"" + style + "\"" + extra + " hint-wrap=\"true\">"
                    + XmlEscape(body) + "</text>";
            }

            var sb = new System.Text.StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?><tile><visual version=\"2\">");
            sb.Append("<binding template=\"TileSmall\">").Append(Text("caption", DailyTitleWithColon, "", 0)).Append("</binding>");
            sb.Append("<binding template=\"TileMedium\">").Append(Text(qStyle, indent + quote, "", 0)).Append("</binding>");
            sb.Append("<binding template=\"TileWide\">");
            if (TileSource.ShowTitle) sb.Append(Text(tStyle, title, tAlign, 0));
            sb.Append(Text(qStyle, indent + quote, "", 0));
            if (TileSource.ShowAttr && attr.Length > 0) sb.Append(Text(aStyle, attr, aAlign, 2));
            sb.Append("</binding>");
            sb.Append("<binding template=\"TileLarge\">");
            if (TileSource.ShowTitle) sb.Append(Text(tStyle, title, tAlign, 0));
            sb.Append(Text(qStyle, indent + quote, "", 5));
            if (TileSource.ShowAttr && attr.Length > 0) sb.Append(Text(aStyle, attr, aAlign, 2));
            sb.Append("</binding>");
            sb.Append("</visual></tile>");
            return sb.ToString();
        }

        private async void refreshTileButton_Click(object sender, RoutedEventArgs e)
        {
            await ApplyTileAsync();
        }

        private async void tileIntervalCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var item = tileIntervalCombo.SelectedItem as ComboBoxItem;
            if (item == null) return;
            TileIntervalTag = (string)item.Tag;
            if (_loading) return;
            await ApplyTileAsync();
        }

        private async void isTileFresh_Toggled(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            SetBool("tileOn", isTileFresh.IsOn);
            await ApplyTileAsync();
        }


        private async void tileSourceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var item = tileSourceCombo.SelectedItem as ComboBoxItem;
            if (item == null) return;
            TileSource.SelectedMode = (string)item.Tag;
            if (_loading) return;
            await ApplyTileAsync();       //换源立刻重推一次磁贴
            await LoadDailyQuoteAsync();  //界面上的句子也跟着换
        }

        private async void hitokotoTypeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var item = hitokotoTypeCombo.SelectedItem as ComboBoxItem;
            if (item == null) return;
            TileSource.HitokotoType = (string)item.Tag;
            if (_loading) return;
            await ApplyTileAsync();       //分类一变立刻刷新磁贴
            await LoadDailyQuoteAsync();  //界面上也立刻换成新分类的句子
        }

        // ------------------------------------------------------------ 每日一言
        private async Task LoadDailyQuoteAsync()
        {
            // 和磁贴同一套固定优先级：官方 API → Worker → Pages；Pages 停更时跳过
            foreach (var step in SourceOrder(TileSource.SelectedMode))
            {
                if (step == TileSource.Mode.Pages)
                {
                    if (!await TileSource.PagesFreshAsync()) continue;
                    if (await ShowQuoteFromUriAsync(TileSource.PagesContentUri())) return;
                }
                else if (step == TileSource.Mode.Worker)
                {
                    if (await ShowQuoteFromUriAsync(TileSource.WorkerContentUri())) return;
                }
                else
                {
                    var quote = await TileSource.FetchHitokotoAsync();
                    if (quote != null)
                    {
                        dailyQuoteText.Text = quote.Text;
                        dailyQuoteFrom.Text = TileSource.FormatAttribution(quote.Who, quote.From);
                        return;
                    }
                }
            }

            dailyQuoteText.Text = "（暂时取不到每日一言，请检查网络）";
            dailyQuoteFrom.Text = "";
        }

        /// <summary>从服务器给的磁贴 XML 里取正文/出处填到界面上，成功返回 true。</summary>
        private async Task<bool> ShowQuoteFromUriAsync(Uri uri)
        {
            try
            {
                using (var http = new System.Net.Http.HttpClient())
                {
                    http.Timeout = TimeSpan.FromSeconds(8);
                    var xml = await http.GetStringAsync(uri);
                    var doc = System.Xml.Linq.XDocument.Parse(xml);
                    var large = doc.Descendants("binding")
                                    .Where(b => (string)b.Attribute("template") == "TileLarge")
                                    .FirstOrDefault()
                                ?? doc.Descendants("binding").LastOrDefault();
                    var texts = large == null
                        ? new List<string>()
                        : large.Elements("text").Select(x => x.Value).ToList();

                    var body = texts.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x) && !IsTitleText(x));
                    var from = texts.LastOrDefault();

                    dailyQuoteText.Text = string.IsNullOrWhiteSpace(body) ? "（暂时取不到每日一言）" : body.Trim();
                    dailyQuoteFrom.Text = (from != null && from != body && from.Trim().StartsWith("—"))
                        ? from.Trim()
                        : "";
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        // ------------------------------------------------------------ 磁贴文字样式 / 界面字体
        private async void tileTitleStyleCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var item = tileTitleStyleCombo.SelectedItem as ComboBoxItem;
            if (item == null) return;
            TileSource.StyleTitle = (string)item.Tag;
            if (_loading) return;
            await ApplyTileAsync();
        }

        private async void tileQuoteStyleCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var item = tileQuoteStyleCombo.SelectedItem as ComboBoxItem;
            if (item == null) return;
            TileSource.StyleQuote = (string)item.Tag;
            if (_loading) return;
            await ApplyTileAsync();
        }

        private async void tileAttrStyleCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var item = tileAttrStyleCombo.SelectedItem as ComboBoxItem;
            if (item == null) return;
            TileSource.StyleAttr = (string)item.Tag;
            if (_loading) return;
            await ApplyTileAsync();
        }

        private void uiFontCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var item = uiFontCombo.SelectedItem as ComboBoxItem;
            if (item == null) return;
            SetString("uiFont", (string)item.Tag);
            if (_loading) return;
            ApplyUiFont();
        }

        /// <summary>界面字体（磁贴字体由 Windows 决定，不支持自定义）。</summary>
        private void ApplyUiFont()
        {
            var tag = GetString("uiFont", "");
            string name = tag;
            if (tag == "custom") name = GetString("uiFontCustom", "");
            if (string.IsNullOrWhiteSpace(name))
            {
                headerText.ClearValue(TextBlock.FontFamilyProperty);
                dailyQuoteText.ClearValue(TextBlock.FontFamilyProperty);
                dailyQuoteFrom.ClearValue(TextBlock.FontFamilyProperty);
                return;
            }
            var chain = name;
            foreach (var fallback in new[] { "Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI" })
            {
                if (chain.IndexOf(fallback, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    chain += ", " + fallback;
                }
            }
            var family = new FontFamily(chain);
            headerText.FontFamily = family;
            dailyQuoteText.FontFamily = family;
            dailyQuoteFrom.FontFamily = family;
        }

        /// <summary>界面三段文字的字号 / 粗细。</summary>
        private void ApplyUiTextStyles()
        {
            // 标题 / 出处的显示与对齐（与磁贴共用同一组开关）
            headerText.Visibility = TileSource.ShowTitle ? Visibility.Visible : Visibility.Collapsed;
            dailyQuoteFrom.Visibility = TileSource.ShowAttr ? Visibility.Visible : Visibility.Collapsed;
            ApplyAlign(headerText, TileSource.TitleAlign);
            ApplyAlign(dailyQuoteFrom, TileSource.AttrAlign);

            headerText.FontSize = ParseDouble(GetString("uiTitleSize", "60"), 30);
            dailyQuoteText.FontSize = ParseDouble(GetString("uiQuoteSize", "40"), 36);
            dailyQuoteFrom.FontSize = ParseDouble(GetString("uiFromSize", "20"), 18);

            headerText.FontWeight = GetBool("uiTitleBold", true) ? FontWeights.Bold : FontWeights.Normal;
            dailyQuoteText.FontWeight = GetBool("uiQuoteBold", true) ? FontWeights.Bold : FontWeights.Normal;
            dailyQuoteFrom.FontWeight = GetBool("uiFromBold", false) ? FontWeights.Bold : FontWeights.Normal;
        }

        private static double ParseDouble(string text, double fallback)
        {
            double v;
            return double.TryParse(text, out v) && v > 0 ? v : fallback;
        }

        private void uiTitleSizeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var item = uiTitleSizeCombo.SelectedItem as ComboBoxItem;
            if (item == null) return;
            SetString("uiTitleSize", (string)item.Tag);
            if (!_loading) ApplyUiTextStyles();
        }

        private void uiQuoteSizeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var item = uiQuoteSizeCombo.SelectedItem as ComboBoxItem;
            if (item == null) return;
            SetString("uiQuoteSize", (string)item.Tag);
            if (!_loading) ApplyUiTextStyles();
        }

        private void uiFromSizeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var item = uiFromSizeCombo.SelectedItem as ComboBoxItem;
            if (item == null) return;
            SetString("uiFromSize", (string)item.Tag);
            if (!_loading) ApplyUiTextStyles();
        }

        private void uiTitleBold_Toggled(object sender, RoutedEventArgs e)
        {
            SetBool("uiTitleBold", uiTitleBold.IsOn);
            if (!_loading) ApplyUiTextStyles();
        }

        private void uiQuoteBold_Toggled(object sender, RoutedEventArgs e)
        {
            SetBool("uiQuoteBold", uiQuoteBold.IsOn);
            if (!_loading) ApplyUiTextStyles();
        }

        private void uiFromBold_Toggled(object sender, RoutedEventArgs e)
        {
            SetBool("uiFromBold", uiFromBold.IsOn);
            if (!_loading) ApplyUiTextStyles();
        }

        private static void ApplyAlign(TextBlock block, string align)
        {
            switch ((align ?? "").ToLowerInvariant())
            {
                case "left":
                    block.TextAlignment = TextAlignment.Left;
                    block.HorizontalAlignment = HorizontalAlignment.Left;
                    break;
                case "right":
                    block.TextAlignment = TextAlignment.Right;
                    block.HorizontalAlignment = HorizontalAlignment.Right;
                    break;
                default:
                    block.TextAlignment = TextAlignment.Center;
                    block.HorizontalAlignment = HorizontalAlignment.Center;
                    break;
            }
        }

        private async void tileShowTitleSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            TileSource.ShowTitle = tileShowTitleSwitch.IsOn;
            if (_loading) return;
            ApplyUiTextStyles();
            await ApplyTileAsync();
        }

        private async void tileShowAttrSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            TileSource.ShowAttr = tileShowAttrSwitch.IsOn;
            if (_loading) return;
            ApplyUiTextStyles();
            await ApplyTileAsync();
        }

        private async void tileTitleAlignCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var item = tileTitleAlignCombo.SelectedItem as ComboBoxItem;
            if (item == null) return;
            TileSource.TitleAlign = (string)item.Tag;
            if (_loading) return;
            ApplyUiTextStyles();
            await ApplyTileAsync();
        }

        private async void tileAttrAlignCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var item = tileAttrAlignCombo.SelectedItem as ComboBoxItem;
            if (item == null) return;
            TileSource.AttrAlign = (string)item.Tag;
            if (_loading) return;
            ApplyUiTextStyles();
            await ApplyTileAsync();
        }

        private void applyCustomFontButton_Click(object sender, RoutedEventArgs e)
        {
            SetString("uiFontCustom", (customFontBox.Text ?? "").Trim());
            SetString("uiFont", "custom");
            SelectComboByTag(uiFontCombo, "custom");
            ApplyUiFont();
        }

        /// <summary>右上角标题栏按钮跟随主题：有壁纸时用白字，否则黑字。</summary>
        private void ApplyTitleBarTheme()
        {
            try
            {
                var bar = Windows.UI.ViewManagement.ApplicationView.GetForCurrentView().TitleBar;
                var dark = WallpaperMode != "none";
                bar.ButtonBackgroundColor = Colors.Transparent;
                bar.ButtonInactiveBackgroundColor = Colors.Transparent;
                bar.ButtonForegroundColor = dark ? Colors.White : Colors.Black;
                bar.ButtonInactiveForegroundColor = dark ? Color.FromArgb(160, 255, 255, 255) : Color.FromArgb(160, 0, 0, 0);
                bar.ButtonHoverBackgroundColor = dark ? Color.FromArgb(40, 255, 255, 255) : Color.FromArgb(30, 0, 0, 0);
                bar.ButtonPressedBackgroundColor = dark ? Color.FromArgb(60, 255, 255, 255) : Color.FromArgb(50, 0, 0, 0);
            }
            catch { }
        }

        // ------------------------------------------------------------ 壁纸
        private string WallpaperMode
        {
            get { return GetString("wallMode", "none"); }
            set { SetString("wallMode", value); }
        }

        private int WallpaperIntervalMin
        {
            get
            {
                int m;
                return int.TryParse(GetString("wallInterval", "10"), out m) && m > 0 ? m : 10;
            }
            set { SetString("wallInterval", value.ToString()); }
        }

        private static bool IsImageName(string name)
        {
            var n = name.ToLowerInvariant();
            return n.EndsWith(".jpg") || n.EndsWith(".jpeg") || n.EndsWith(".png")
                || n.EndsWith(".bmp") || n.EndsWith(".gif");
        }

        private async Task ApplyWallpaperAsync()
        {
            StopWallTimer();
            _wallFiles.Clear();
            _wallIndex = 0;

            var mode = WallpaperMode;
            if (mode == "none")
            {
                wallImage.Source = null;
                wallImage.Visibility = Visibility.Collapsed;
                return;
            }

            wallImage.Visibility = Visibility.Visible;

            if (mode == "url")
            {
                var url = GetString("wallUrl", "").Trim();
                if (url.Length > 0)
                {
                    try { wallImage.Source = new BitmapImage(new Uri(url)); } catch { }
                }
                return;
            }

            if (mode == "file")
            {
                var token = GetString("wallFileToken", "");
                if (token.Length == 0) return;
                try
                {
                    var file = await StorageApplicationPermissions.FutureAccessList.GetFileAsync(token);
                    await ShowWallFileAsync(file);
                }
                catch { }
                return;
            }

            if (mode == "folder")
            {
                var token = GetString("wallFolderToken", "");
                if (token.Length == 0) return;
                try
                {
                    var folder = await StorageApplicationPermissions.FutureAccessList.GetFolderAsync(token);
                    var files = await folder.GetFilesAsync();
                    foreach (var f in files.Where(f => IsImageName(f.Name)).OrderBy(f => f.Name))
                    {
                        _wallFiles.Add(f);
                    }
                    if (_wallFiles.Count == 0) return;
                    await ShowWallFileAsync(_wallFiles[0]);
                    if (_wallFiles.Count > 1) StartWallTimer();
                }
                catch { }
            }
        }

        private async Task ShowWallFileAsync(StorageFile file)
        {
            try
            {
                using (var stream = await file.OpenAsync(FileAccessMode.Read))
                {
                    var bmp = new BitmapImage();
                    await bmp.SetSourceAsync(stream);
                    wallImage.Source = bmp;
                }
            }
            catch { }
        }

        private void StartWallTimer()
        {
            StopWallTimer();
            _wallTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(WallpaperIntervalMin) };
            _wallTimer.Tick += async (s, e) =>
            {
                if (_wallFiles.Count == 0) return;
                _wallIndex = (_wallIndex + 1) % _wallFiles.Count;
                await ShowWallFileAsync(_wallFiles[_wallIndex]);
            };
            _wallTimer.Start();
        }

        private void StopWallTimer()
        {
            if (_wallTimer != null)
            {
                _wallTimer.Stop();
                _wallTimer = null;
            }
        }

        private async void wallpaperModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var item = wallpaperModeCombo.SelectedItem as ComboBoxItem;
            if (item == null) return;
            WallpaperMode = (string)item.Tag;
            if (_loading) return;
            await ApplyWallpaperAsync();
            ApplyTitleBarTheme();
        }

        private async void wallpaperIntervalCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var item = wallpaperIntervalCombo.SelectedItem as ComboBoxItem;
            if (item == null) return;
            int m;
            if (int.TryParse((string)item.Tag, out m)) WallpaperIntervalMin = m;
            if (_loading) return;
            if (WallpaperMode == "folder") await ApplyWallpaperAsync();
        }

        private async void pickImageButton_Click(object sender, RoutedEventArgs e)
        {
            var picker = new FileOpenPicker();
            picker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;
            foreach (var ext in new[] { ".jpg", ".jpeg", ".png", ".bmp", ".gif" })
            {
                picker.FileTypeFilter.Add(ext);
            }
            var file = await picker.PickSingleFileAsync();
            if (file == null) return;
            SetString("wallFileToken", StorageApplicationPermissions.FutureAccessList.Add(file));
            WallpaperMode = "file";
            SelectComboByTag(wallpaperModeCombo, "file");
            await ApplyWallpaperAsync();
        }

        private async void pickFolderButton_Click(object sender, RoutedEventArgs e)
        {
            var picker = new FolderPicker();
            picker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;
            picker.FileTypeFilter.Add("*");
            var folder = await picker.PickSingleFolderAsync();
            if (folder == null) return;
            SetString("wallFolderToken", StorageApplicationPermissions.FutureAccessList.Add(folder));
            WallpaperMode = "folder";
            SelectComboByTag(wallpaperModeCombo, "folder");
            await ApplyWallpaperAsync();
        }

        private async void applyWallpaperUrlButton_Click(object sender, RoutedEventArgs e)
        {
            SetString("wallUrl", (wallpaperUrlBox.Text ?? "").Trim());
            WallpaperMode = "url";
            SelectComboByTag(wallpaperModeCombo, "url");
            await ApplyWallpaperAsync();
        }

        // ------------------------------------------------------------ 窗口 / 颜色
        private void settings_Click(object sender, RoutedEventArgs e)
        {
            mainSplitView.IsPaneOpen = !mainSplitView.IsPaneOpen;
        }

        private void isfullScreen_Toggled(object sender, RoutedEventArgs e)
        {
            try
            {
                var view = Windows.UI.ViewManagement.ApplicationView.GetForCurrentView();
                if (isfullScreen.IsOn)
                {
                    if (!view.TryEnterFullScreenMode())
                    {
                        // 窗口刚激活时可能失败，稍后再试一次
                        _ = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Low, () =>
                        {
                            try { view.TryEnterFullScreenMode(); } catch { }
                            SetBcakGroundColor();   // 真正进全屏后重刷背景（全屏不用亚克力）
                        });
                    }
                }
                else
                {
                    view.ExitFullScreenMode();
                }
            }
            catch { }

            SetBcakGroundColor();   // 全屏 / 非全屏的亚克力表现不同，切换时重刷
            if (!_loading) SetBool("fullscreen", isfullScreen.IsOn);
        }

        private Windows.System.Display.DisplayRequest _displayRequest;

        private void isDisplayRequest_Toggled(object sender, RoutedEventArgs e)
        {
            try
            {
                if (isDisplayRequest.IsOn)
                {
                    if (_displayRequest == null) _displayRequest = new Windows.System.Display.DisplayRequest();
                    _displayRequest.RequestActive();
                }
                else if (_displayRequest != null)
                {
                    _displayRequest.RequestRelease();
                }
            }
            catch { }
            if (!_loading) SetBool("keepOn", isDisplayRequest.IsOn);
        }

        private void BackGroundColorRedSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            SetBcakGroundColor();
            if (!_loading) SetDouble("bgR", e.NewValue);
        }

        private void BackGroundColorGreenSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            SetBcakGroundColor();
            if (!_loading) SetDouble("bgG", e.NewValue);
        }

        private void BackGroundColorBlueSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            SetBcakGroundColor();
            if (!_loading) SetDouble("bgB", e.NewValue);
        }

        private void BackGroundAcrylicBlueSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            SetBcakGroundColor();
            if (!_loading) SetDouble("bgA", e.NewValue);
        }

        private void FontColorRedSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            SetForeColor();
            if (!_loading) SetDouble("fgR", e.NewValue);
        }

        private void FontColorGreenSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            SetForeColor();
            if (!_loading) SetDouble("fgG", e.NewValue);
        }

        private void FontColorBlueSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            SetForeColor();
            if (!_loading) SetDouble("fgB", e.NewValue);
        }

        private void SetForeColor()
        {
            var color = Color.FromArgb(255,
                (byte)FontColorRedSlider.Value,
                (byte)FontColorGreenSlider.Value,
                (byte)FontColorBlueSlider.Value);
            var brush = new SolidColorBrush(color);
            dailyQuoteText.Foreground = brush;
            dailyQuoteFrom.Foreground = brush;
            headerText.Foreground = brush;
            settings.Foreground = brush;
        }

        /// <summary>
        /// 重画背景。最底下是亚克力（窗口模式下把桌面采样进来）或纯背景色，
        /// 壁纸浮在上面，并按「透」滑块变透明：窗口模式透出桌面磨砂，
        /// 全屏透出背景色。所以 0 是原图、100 是纯背景色，壁纸和全屏下都有效果。
        /// </summary>
        private void SetBcakGroundColor()
        {
            try
            {
                var r = (byte)BackGroundColorRedSlider.Value;
                var g = (byte)BackGroundColorGreenSlider.Value;
                var b = (byte)BackGroundColorBlueSlider.Value;
                var density = Math.Max(0, Math.Min(1, BackGroundAcrylicBlueSlider.Value / 100.0));

                // 全屏时系统会禁用 HostBackdrop 亚克力，AcrylicBrush 只会退成 FallbackColor；
                // 半透明色叠在纯黑窗口上会整屏发暗，背景色滑块也跟着失效。
                // 所以全屏一律用不透明背景色打底，窗口模式才用亚克力。
                var canAcrylic = !isfullScreen.IsOn
                    && Windows.Foundation.Metadata.ApiInformation
                        .IsTypePresent("Windows.UI.Xaml.Media.XamlCompositionBrushBase");

                if (canAcrylic)
                {
                    mainGrid.Background = new AcrylicBrush
                    {
                        BackgroundSource = AcrylicBackgroundSource.HostBackdrop,
                        TintColor = Color.FromArgb(255, r, g, b),
                        TintOpacity = density,
                        // 省电模式 / 窗口失焦时同样会退成这个颜色
                        FallbackColor = Color.FromArgb(255, r, g, b)
                    };
                }
                else
                {
                    mainGrid.Background = new SolidColorBrush(Color.FromArgb(255, r, g, b));
                }

                // 壁纸按同一个浓度变透明。以前「透」只作用在壁纸下面的亚克力上，
                // 被不透明的壁纸整张挡住，所以有壁纸 / 全屏时滑块看不出效果。
                wallImage.Opacity = 1 - density;
            }
            catch { }
        }

        /// <summary>语音指令入口（App.OnActivated 调用）。</summary>
        public void openAuto()
        {
            mainSplitView.IsPaneOpen = true;
        }

        // ------------------------------------------------------------ 下拉填充 / 字体对话框
        private static readonly string[,] TileStyleTable = new string[,]
        {
            { "caption", "极小 12" }, { "captionSubtle", "极小·淡 12" },
            { "body", "小 15" }, { "bodySubtle", "小·淡 15" },
            { "base", "标准 15" }, { "baseSubtle", "标准·淡 15" },
            { "subtitle", "中 20" }, { "subtitleSubtle", "中·淡 20" },
            { "title", "大 24" }, { "titleSubtle", "大·淡 24" },
            { "subheader", "特大 34" }, { "subheaderSubtle", "特大·淡 34" },
            { "header", "巨大 46" }, { "headerSubtle", "巨大·淡 46" }
        };

        private void FillTileStyleCombos()
        {
            foreach (var combo in new[] { tileTitleStyleCombo, tileQuoteStyleCombo, tileAttrStyleCombo })
            {
                if (combo.Items.Count > 0) continue;
                for (int i = 0; i < TileStyleTable.GetLength(0); i++)
                {
                    combo.Items.Add(new ComboBoxItem { Content = TileStyleTable[i, 1], Tag = TileStyleTable[i, 0] });
                }
            }
        }

        private void FillUiSizeCombos()
        {
            FillSizes(uiTitleSizeCombo, new[] { 16.0, 18, 20, 22, 24, 26, 28, 30, 32, 34, 38, 42, 46, 52, 60 });
            FillSizes(uiQuoteSizeCombo, new[] { 16.0, 18, 20, 22, 24, 26, 28, 30, 32, 34, 36, 40, 44, 48, 56, 64, 72 });
            FillSizes(uiFromSizeCombo, new[] { 10.0, 12, 14, 16, 18, 20, 22, 24, 28, 32, 36 });
        }

        private static void FillSizes(ComboBox combo, double[] sizes)
        {
            if (combo.Items.Count > 0) return;
            foreach (var s in sizes)
            {
                var text = ((int)s).ToString();
                combo.Items.Add(new ComboBoxItem { Content = text, Tag = text });
            }
        }

        private static readonly string[] SystemFonts =
        {
            "Microsoft YaHei UI", "Microsoft YaHei", "Microsoft YaHei Light",
            "DengXian", "DengXian Light", "SimSun", "NSimSun", "SimHei",
            "KaiTi", "FangSong", "YouYuan", "LiSu",
            "STXihei", "STKaiti", "STSong", "STFangsong", "FZShuTi", "FZYaoti",
            "Source Han Sans SC", "Source Han Serif SC", "Noto Sans SC",
            "Segoe UI", "Segoe UI Light", "Segoe UI Semibold",
            "Arial", "Arial Black", "Calibri", "Cambria", "Candara", "Consolas",
            "Cascadia Code", "Courier New", "Georgia", "Impact", "Tahoma",
            "Times New Roman", "Trebuchet MS", "Verdana"
        };

        private async void pickFontButton_Click(object sender, RoutedEventArgs e)
        {
            var list = new ListView
            {
                Height = 380,
                SelectionMode = ListViewSelectionMode.Single
            };
            foreach (var name in SystemFonts)
            {
                list.Items.Add(new ListViewItem
                {
                    Content = name,
                    FontFamily = new FontFamily(name)
                });
            }
            if (list.Items.Count > 0) list.SelectedIndex = 0;

            var dialog = new ContentDialog
            {
                Title = "选择系统字体",
                Content = new ScrollViewer { Content = list },
                PrimaryButtonText = "应用",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary
            };

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary) return;

            var item = list.SelectedItem as ListViewItem;
            if (item == null) return;
            var fontName = item.Content as string;
            customFontBox.Text = fontName;
            SetString("uiFontCustom", fontName);
            SetString("uiFont", "custom");
            SelectComboByTag(uiFontCombo, "custom");
            ApplyUiFont();
        }
    }
}
