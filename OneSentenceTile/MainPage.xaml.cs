using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.AccessCache;
using Windows.Storage.Pickers;
using Windows.UI;
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
            wallpaperUrlBox.Text = GetString("wallUrl", "");
            _loading = false;

            SetBcakGroundColor();
            SetForeColor();
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
                return;
            }

            var uri = await TileSource.ResolveAsync();
            if (TileIntervalTag == "Realtime")
            {
                try { updater.StopPeriodicUpdate(); } catch { }
                await PushTileNowAsync(uri);
            }
            else
            {
                updater.StartPeriodicUpdate(uri, ParseInterval(TileIntervalTag));
            }
        }

        /// <summary>立刻下载磁贴 XML 并作为本地通知推上去（实时刷新用）。</summary>
        private async Task PushTileNowAsync(Uri uri)
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
            }
            catch { }
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
