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
        private readonly List<StorageFile> _wallFiles = new List<StorageFile>();
        private int _wallIndex;

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

            _loading = true;
            isTileFresh.IsOn = GetBool("tileOn", false);
            isWorkerFirst.IsOn = TileSource.PreferWorker;
            isDisplayRequest.IsOn = GetBool("keepOn", false);
            isfullScreen.IsOn = GetBool("fullscreen", false);
            BackGroundColorRedSlider.Value = GetDouble("bgR", 255);
            BackGroundColorGreenSlider.Value = GetDouble("bgG", 255);
            BackGroundColorBlueSlider.Value = GetDouble("bgB", 255);
            BackGroundAcrylicBlueSlider.Value = GetDouble("bgA", 10);
            FontColorRedSlider.Value = GetDouble("fgR", 0);
            FontColorGreenSlider.Value = GetDouble("fgG", 0);
            FontColorBlueSlider.Value = GetDouble("fgB", 0);
            SelectComboByTag(tileIntervalCombo, TileIntervalTag);
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
            SelectComboByTag(uiFontCombo, GetString("uiFont", ""));
            customFontBox.Text = GetString("uiFontCustom", "");
            SelectComboByTag(uiTitleSizeCombo, GetString("uiTitleSize", "30"));
            SelectComboByTag(uiQuoteSizeCombo, GetString("uiQuoteSize", "36"));
            SelectComboByTag(uiFromSizeCombo, GetString("uiFromSize", "18"));
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
        private string TileIntervalTag
        {
            get { return GetString("tileInterval", "Realtime"); }
            set { SetString("tileInterval", value); }
        }

        private static PeriodicUpdateRecurrence ParseInterval(string tag)
        {
            switch (tag)
            {
                case "HalfHour": return PeriodicUpdateRecurrence.HalfHour;
                case "Hour": return PeriodicUpdateRecurrence.Hour;
                case "SixHours": return PeriodicUpdateRecurrence.SixHours;
                case "TwelveHours": return PeriodicUpdateRecurrence.TwelveHours;
                case "Daily": return PeriodicUpdateRecurrence.Daily;
                default: return PeriodicUpdateRecurrence.HalfHour;
            }
        }

        /// <summary>按当前设置启动 / 停止磁贴更新。“实时”模式改为 App 自己拉一次 XML 立刻推给磁贴。</summary>
        private async Task ApplyTileAsync()
        {
            var updater = TileUpdateManager.CreateTileUpdaterForApplication();
            if (!isTileFresh.IsOn)
            {
                try { updater.StopPeriodicUpdate(); } catch { }
                SetTileStatus("磁贴已关闭");
                return;
            }

            SetTileStatus("正在连接磁贴服务器…");
            var uri = await TileSource.ResolveAsync();
            var host = uri.Host;

            if (TileIntervalTag == "Realtime")
            {
                try { updater.StopPeriodicUpdate(); } catch { }
                var ok = await PushTileNowAsync(uri);
                SetTileStatus(ok ? "已刷新（" + host + "）" : "刷新失败：连不上 " + host);
            }
            else
            {
                try
                {
                    updater.StartPeriodicUpdate(uri, ParseInterval(TileIntervalTag));
                    SetTileStatus("已设为每 " + IntervalLabel(TileIntervalTag) + " 刷新（" + host + "）");
                }
                catch (Exception ex)
                {
                    SetTileStatus("设置失败：" + ex.Message);
                }
            }
        }

        private void SetTileStatus(string text)
        {
            tileStatusText.Text = text ?? "";
        }

        private static string IntervalLabel(string tag)
        {
            switch (tag)
            {
                case "HalfHour": return "30 分钟";
                case "Hour": return "1 小时";
                case "SixHours": return "6 小时";
                case "TwelveHours": return "12 小时";
                case "Daily": return "24 小时";
                default: return "实时";
            }
        }

        private async void refreshTileButton_Click(object sender, RoutedEventArgs e)
        {
            SetTileStatus("正在刷新…");
            await ApplyTileAsync();
        }

        /// <summary>立刻下载磁贴 XML 并作为本地通知推上去（实时刷新用）。</summary>
        private async Task<bool> PushTileNowAsync(Uri uri)
        {
            try
            {
                using (var http = new System.Net.Http.HttpClient())
                {
                    http.Timeout = TimeSpan.FromSeconds(8);
                    var xml = await http.GetStringAsync(uri);
                    var doc = new Windows.Data.Xml.Dom.XmlDocument();
                    doc.LoadXml(xml);
                    TileUpdateManager.CreateTileUpdaterForApplication().Update(new TileNotification(doc));
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        private async void isTileFresh_Toggled(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            SetBool("tileOn", isTileFresh.IsOn);
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

        private async void isWorkerFirst_Toggled(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            TileSource.PreferWorker = isWorkerFirst.IsOn;
            await ApplyTileAsync();
            await LoadDailyQuoteAsync();
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
            try
            {
                var uri = await TileSource.ResolveAsync();
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

                    var body = texts.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x) && x.Trim() != "每日一言");
                    var from = texts.LastOrDefault();

                    dailyQuoteText.Text = string.IsNullOrWhiteSpace(body) ? "（暂时取不到每日一言）" : body.Trim();
                    dailyQuoteFrom.Text = (from != null && from != body && from.Trim().StartsWith("—"))
                        ? from.Trim()
                        : "";
                }
            }
            catch
            {
                dailyQuoteText.Text = "（暂时取不到每日一言，请检查网络）";
                dailyQuoteFrom.Text = "";
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
            var family = new FontFamily(name);
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

            headerText.FontSize = ParseDouble(GetString("uiTitleSize", "30"), 30);
            dailyQuoteText.FontSize = ParseDouble(GetString("uiQuoteSize", "36"), 36);
            dailyQuoteFrom.FontSize = ParseDouble(GetString("uiFromSize", "18"), 18);

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
                wallScrim.Visibility = Visibility.Collapsed;
                return;
            }

            wallImage.Visibility = Visibility.Visible;
            wallScrim.Visibility = Visibility.Visible;

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
                if (isfullScreen.IsOn)
                    Windows.UI.ViewManagement.ApplicationView.GetForCurrentView().TryEnterFullScreenMode();
                else
                    Windows.UI.ViewManagement.ApplicationView.GetForCurrentView().ExitFullScreenMode();
            }
            catch { }
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

        private void SetBcakGroundColor()
        {
            try
            {
                var color = Color.FromArgb(255,
                    (byte)BackGroundColorRedSlider.Value,
                    (byte)BackGroundColorGreenSlider.Value,
                    (byte)BackGroundColorBlueSlider.Value);

                if (Windows.Foundation.Metadata.ApiInformation.IsTypePresent("Windows.UI.Xaml.Media.XamlCompositionBrushBase"))
                {
                    var brush = new AcrylicBrush
                    {
                        BackgroundSource = AcrylicBackgroundSource.HostBackdrop,
                        TintColor = color,
                        FallbackColor = color,
                        TintOpacity = BackGroundAcrylicBlueSlider.Value / 100
                    };
                    mainGrid.Background = brush;
                }
                else
                {
                    mainGrid.Background = new SolidColorBrush(color);
                }
            }
            catch { }
        }

        /// <summary>语音指令入口（App.OnActivated 调用）。</summary>
        public void openAuto()
        {
            mainSplitView.IsPaneOpen = true;
        }
    }
}
