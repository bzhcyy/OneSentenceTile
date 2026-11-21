using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI;
using Windows.Storage;
using Windows.UI.ViewManagement;
using Windows.UI.Notifications;
using System.Threading.Tasks;
using Windows.ApplicationModel.Background;
using Windows.Media.Playback;
using Windows.Media.Core;
using Windows.ApplicationModel.Core;

// https://go.microsoft.com/fwlink/?LinkId=402352&clcid=0x804 上介绍了“空白页”项模板

namespace OneSentenceTile
{
    /// <summary>
    /// 可用于自身或导航至 Frame 内部的空白页。
    /// </summary>
    public sealed partial class MainPage : Page
    {
        DispatcherTimer timer;//定义定时器
        bool _loading = false;   //正在套用设置，别触发保存

        public MainPage()
        {
            this.InitializeComponent();
        }

        private async void Page_Loaded(object sender, RoutedEventArgs e)  //页面加载完毕
        {
            GetSettings();
            timer = new DispatcherTimer();
            timer.Interval = new TimeSpan(0, 0, 1);
            timer.Tick += Timer_Tick;//每秒触发这个事件，以刷新时间
            timer.Start();  //开始计时器

            var storageFile =
                        await Windows.Storage.StorageFile.GetFileFromApplicationUriAsync(
                        new Uri("ms-appx:///VoiceCommandDictionary.xml"));
            await Windows.ApplicationModel.VoiceCommands.VoiceCommandDefinitionManager
                        .InstallCommandDefinitionsFromStorageFileAsync(storageFile);     //加载语音字典

            Windows.ApplicationModel.Core.CoreApplication.GetCurrentView().TitleBar.ExtendViewIntoTitleBar = true;
            //覆盖电脑状态栏

            try  //加载秒数统计（只用来算实际时间）
            {
                long total;
                total = await GetTotalSecond();
                SaveTotalSecond(total);
            }
            catch { }

            _loading = true;
            isWorkerFirst.IsOn = TileSource.PreferWorker;
            SelectIntervalInCombo();
            SelectTypeInCombo();
            _loading = false;

            await LoadDailyQuote();


            if (Windows.Foundation.Metadata.ApiInformation.IsTypePresent("Windows.UI.Xaml.Media.XamlCompositionBrushBase"))
            {
                Windows.UI.Xaml.Media.AcrylicBrush myBrush = new Windows.UI.Xaml.Media.AcrylicBrush();
                myBrush.BackgroundSource = Windows.UI.Xaml.Media.AcrylicBackgroundSource.HostBackdrop;
                myBrush.TintColor = Color.FromArgb(255,255,255,255);
                myBrush.FallbackColor = Color.FromArgb(255, 255, 255, 255);
                myBrush.TintOpacity = 0.3;


                mainSplitView.Background = myBrush;

            }
        }

        private async void Timer_Tick(object sender, object e)    //1s定时执行
        {
            await ShowRealTime();
        }

        private void settings_Click(object sender, RoutedEventArgs e)  //设置按钮
        {
            mainSplitView.IsPaneOpen = !mainSplitView.IsPaneOpen;  //开关SplitView设置页
        }
        
        private void isfullScreen_Toggled(object sender, RoutedEventArgs e)  //全屏按钮
        {
            if (isfullScreen.IsOn)
            {
                Windows.UI.ViewManagement.ApplicationView.GetForCurrentView().TryEnterFullScreenMode();  //全屏
            }
            else
            {
                Windows.UI.ViewManagement.ApplicationView.GetForCurrentView().ExitFullScreenMode();
            }
            SaveSettings();
        }

        private void ApplyBackGroungColor_Click(object sender, RoutedEventArgs e) //应用背景颜色
        {
            SaveSettings();
        }

        private void ApplyFontColor_Click(object sender, RoutedEventArgs e)//应用字体颜色
        {
            SaveSettings();
        }

        private async void SaveTotalSecond(long seconds)
        {
            StorageFolder folder;
            folder = ApplicationData.Current.RoamingFolder; //获取应用目录的文件夹
            try
            {
                var file_demonstration = await folder.CreateFileAsync("seconds", CreationCollisionOption.ReplaceExisting);
                //创建文件

                using (Stream file = await file_demonstration.OpenStreamForWriteAsync())
                {
                    using (StreamWriter write = new StreamWriter(file))
                    {
                        write.Write(string.Format($"{seconds}"));
                    }
                }
            }
            catch { }
        }  //保存总秒数

        private async Task<long> GetTotalSecond()
        {
            StorageFolder folder;
            folder = ApplicationData.Current.RoamingFolder; //获取应用目录的文件夹

            var file_demonstration = await folder.CreateFileAsync("seconds", CreationCollisionOption.OpenIfExists);
            //创建文件

            string s;

            using (Stream file = await file_demonstration.OpenStreamForReadAsync())
            {
                using (StreamReader read = new StreamReader(file))
                {
                    s = read.ReadToEnd();
                }
            }

            long seconds;
            try
            {
                seconds = long.Parse(s);
            }
            catch
            {
                seconds = 0;
            }

            return seconds;
        }  //获取总秒数

        private async void SaveSettings()    //保存设置
        {
            StorageFolder folder;
            folder = ApplicationData.Current.RoamingFolder;
            try
            {
                var file_demonstration = await folder.CreateFileAsync("settings", CreationCollisionOption.ReplaceExisting);
                using (Stream file = await file_demonstration.OpenStreamForWriteAsync())
                {
                    using (StreamWriter write = new StreamWriter(file))
                    {
                        write.Write(string.Format("{0};{1};{2};{3};{4};{5};{6};{7};{8};{9}",
                                                    isfullScreen.IsOn,
                                                    isTileFresh.IsOn,
                                                    BackGroundColorRedSlider.Value,
                                                    BackGroundColorGreenSlider.Value,
                                                    BackGroundColorBlueSlider.Value,
                                                    FontColorRedSlider.Value,
                                                    FontColorGreenSlider.Value,
                                                    FontColorBlueSlider.Value,
                                                    isDisplayRequest.IsOn,
                                                    BackGroundAcrylicBlueSlider.Value
                                                   ));
                    }
                }
            }
            catch { }
        }

        private async void GetSettings()
        {
            BackGroundColorRedSlider.Value = 255;
            BackGroundColorGreenSlider.Value = 255;
            BackGroundColorBlueSlider.Value = 255;

            StorageFolder folder;
            folder = ApplicationData.Current.RoamingFolder;
            var file_demonstration = await folder.CreateFileAsync("settings", CreationCollisionOption.OpenIfExists);

            string s;
            using (Stream file = await file_demonstration.OpenStreamForReadAsync())
            {
                using (StreamReader read = new StreamReader(file))
                {
                    s = read.ReadToEnd();
                }
            }

            if (!string.IsNullOrEmpty(s) && s.IndexOf(";") >= 1)
            {
                string[] v = s.Split(';');
                // 老版本存了 12 段（含已删除的自动+1s、音效），新版本 10 段；按段数判断
                bool legacy = v.Length >= 12;
                try
                {
                    if (v.Length > 0) isfullScreen.IsOn = (v[0] == "True");
                    if (legacy)
                    {
                        BackGroundColorRedSlider.Value = double.Parse(v[2]);
                        BackGroundColorGreenSlider.Value = double.Parse(v[3]);
                        BackGroundColorBlueSlider.Value = double.Parse(v[4]);
                        FontColorRedSlider.Value = double.Parse(v[5]);
                        FontColorGreenSlider.Value = double.Parse(v[6]);
                        FontColorBlueSlider.Value = double.Parse(v[7]);
                        if (v.Length > 8) isTileFresh.IsOn = (v[8] == "True");
                        if (v.Length > 9) isDisplayRequest.IsOn = (v[9] == "True");
                        if (v.Length > 11) BackGroundAcrylicBlueSlider.Value = double.Parse(v[11]);
                    }
                    else
                    {
                        if (v.Length > 1) isTileFresh.IsOn = (v[1] == "True");
                        if (v.Length > 2) BackGroundColorRedSlider.Value = double.Parse(v[2]);
                        if (v.Length > 3) BackGroundColorGreenSlider.Value = double.Parse(v[3]);
                        if (v.Length > 4) BackGroundColorBlueSlider.Value = double.Parse(v[4]);
                        if (v.Length > 5) FontColorRedSlider.Value = double.Parse(v[5]);
                        if (v.Length > 6) FontColorGreenSlider.Value = double.Parse(v[6]);
                        if (v.Length > 7) FontColorBlueSlider.Value = double.Parse(v[7]);
                        if (v.Length > 8) isDisplayRequest.IsOn = (v[8] == "True");
                        if (v.Length > 9) BackGroundAcrylicBlueSlider.Value = double.Parse(v[9]);
                    }
                }
                catch { }
            }
            SetBcakGroundColor();
            SetForeColor();
        }   //加载设置

        private void BackGroundColorRedSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)  //背景颜色调节
        {
            SetBcakGroundColor();
        }

        private void BackGroundColorGreenSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)  //背景颜色调节
        {
            SetBcakGroundColor();
        }

        private void BackGroundColorBlueSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)  //背景颜色调节
        {
            SetBcakGroundColor();
        }

        private void FontColorRedSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)  //颜色调节
        {
            SetForeColor();
        }

        private void FontColorGreenSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)  //颜色调节
        {
            SetForeColor();
        }

        private void FontColorBlueSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)  //颜色调节
        {
            SetForeColor();
        }


        private async void isTileFresh_Toggled(object sender, RoutedEventArgs e)  //磁贴设置按钮
        {
            if (isTileFresh.IsOn)
            {
                await setLiveTile();
            }
            else
            {
                try
                {
                    TileUpdateManager.CreateTileUpdaterForApplication().StopPeriodicUpdate();
                }
                catch { }
            }
            if (!_loading) SaveSettings();
        }


        private async Task setLiveTile()   //开启磁贴
        {
            await RestartTileUpdate();
        }

        // 按设置的间隔重新开始周期更新
        private async Task RestartTileUpdate()
        {
            try
            {
                var uri = await TileSource.ResolveAsync();
                TileUpdateManager.CreateTileUpdaterForApplication()
                    .StartPeriodicUpdate(uri, ParseInterval(TileIntervalTag));
            }
            catch { }
        }

        // ---------------- 磁贴间隔 ----------------
        private string TileIntervalTag
        {
            get
            {
                var v = ApplicationData.Current.RoamingSettings.Values["tileInterval"];
                return (v as string) ?? "HalfHour";
            }
            set { ApplicationData.Current.RoamingSettings.Values["tileInterval"] = value; }
        }

        private static PeriodicUpdateRecurrence ParseInterval(string tag)
        {
            switch (tag)
            {
                case "Hour": return PeriodicUpdateRecurrence.Hour;
                case "SixHours": return PeriodicUpdateRecurrence.SixHours;
                case "TwelveHours": return PeriodicUpdateRecurrence.TwelveHours;
                case "Daily": return PeriodicUpdateRecurrence.Daily;
                default: return PeriodicUpdateRecurrence.HalfHour;
            }
        }

        private void SelectIntervalInCombo()
        {
            var tag = TileIntervalTag;
            foreach (ComboBoxItem item in tileIntervalCombo.Items)
            {
                if ((string)item.Tag == tag)
                {
                    tileIntervalCombo.SelectedItem = item;
                    return;
                }
            }
            tileIntervalCombo.SelectedIndex = 0;
        }

        private void tileIntervalCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var item = tileIntervalCombo.SelectedItem as ComboBoxItem;
            if (item == null) return;
            TileIntervalTag = (string)item.Tag;
            if (_loading) return;
            if (isTileFresh.IsOn)
            {
                var ignored = RestartTileUpdate();
            }
        }

        private void SelectTypeInCombo()
        {
            var tag = TileSource.HitokotoType;
            foreach (ComboBoxItem item in hitokotoTypeCombo.Items)
            {
                if (((string)item.Tag) == tag)
                {
                    hitokotoTypeCombo.SelectedItem = item;
                    return;
                }
            }
            hitokotoTypeCombo.SelectedIndex = 0;
        }

        private async void hitokotoTypeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var item = hitokotoTypeCombo.SelectedItem as ComboBoxItem;
            if (item == null) return;
            TileSource.HitokotoType = (string)item.Tag;
            if (_loading) return;
            if (isTileFresh.IsOn)
            {
                await RestartTileUpdate();
            }
            await LoadDailyQuote();
        }

        private async void isWorkerFirst_Toggled(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            TileSource.PreferWorker = isWorkerFirst.IsOn;
            if (isTileFresh.IsOn)
            {
                await RestartTileUpdate();
            }
            await LoadDailyQuote();
        }

        // ---------------- 每日一言 ----------------
        private async Task LoadDailyQuote()
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
                        ? new System.Collections.Generic.List<string>()
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

        private Windows.System.Display.DisplayRequest _displayRequest;

        private void isDisplayRequest_Toggled(object sender, RoutedEventArgs e) //常亮按钮
        {
            if (isDisplayRequest.IsOn)
            {
                //create the request instance if needed
                if (_displayRequest == null)
                    _displayRequest = new Windows.System.Display.DisplayRequest();
                //make request to put in active state
                _displayRequest.RequestActive();
            }
            else
            {
                //must be same instance, so quit if it doesn't exist
                if (_displayRequest == null)
                    return;
                //undo the request
                _displayRequest.RequestRelease();
            }
            SaveSettings();
        }


        private void SetForeColor()  //设置字体颜色
        {
            SolidColorBrush color = new SolidColorBrush(Color.FromArgb(255, (byte)FontColorRedSlider.Value, (byte)FontColorGreenSlider.Value, (byte)FontColorBlueSlider.Value));
            dailyQuoteText.Foreground = color;    //应用字体颜色
            dailyQuoteFrom.Foreground = color;
            settings.Foreground = color;
            secondTotalShow.Foreground = color;
            realTime.Foreground = color;

            if (Windows.Foundation.Metadata.ApiInformation.IsTypePresent("Windows.UI.ViewManagement.StatusBar"))
            {
#if MOBILE_SDK
                StatusBar statusBar = StatusBar.GetForCurrentView();
                //statusBar.BackgroundColor = Color.FromArgb(255, (byte)BackGroundColorRedSlider.Value, (byte)BackGroundColorGreenSlider.Value, (byte)BackGroundColorBlueSlider.Value);
                statusBar.ForegroundColor = Color.FromArgb(255, (byte)FontColorRedSlider.Value, (byte)FontColorGreenSlider.Value, (byte)FontColorBlueSlider.Value);
                statusBar.BackgroundOpacity = 1;
#endif
            }//手机状态栏颜色
        }

        private void SetBcakGroundColor()  //设置背景颜色
        {
            try
            {
                if (Windows.Foundation.Metadata.ApiInformation.IsTypePresent("Windows.UI.Xaml.Media.XamlCompositionBrushBase"))
                {
                    Windows.UI.Xaml.Media.AcrylicBrush myBrush = new Windows.UI.Xaml.Media.AcrylicBrush();
                    myBrush.BackgroundSource = Windows.UI.Xaml.Media.AcrylicBackgroundSource.HostBackdrop;
                    myBrush.TintColor = Color.FromArgb(255, (byte)BackGroundColorRedSlider.Value, (byte)BackGroundColorGreenSlider.Value, (byte)BackGroundColorBlueSlider.Value);
                    myBrush.FallbackColor = Color.FromArgb(255, (byte)BackGroundColorRedSlider.Value, (byte)BackGroundColorGreenSlider.Value, (byte)BackGroundColorBlueSlider.Value);
                    myBrush.TintOpacity = BackGroundAcrylicBlueSlider.Value / 100;

                    if(mainGrid != null)
                        mainGrid.Background = myBrush;

                }
                else
                {
                    SolidColorBrush color = new SolidColorBrush(Color.FromArgb(255, (byte)BackGroundColorRedSlider.Value, (byte)BackGroundColorGreenSlider.Value, (byte)BackGroundColorBlueSlider.Value));
                    if (mainGrid != null)
                        mainGrid.Background = color;    //应用背景颜色

                    if (Windows.Foundation.Metadata.ApiInformation.IsTypePresent("Windows.UI.ViewManagement.StatusBar"))
                    {
#if MOBILE_SDK
                        StatusBar statusBar = StatusBar.GetForCurrentView();
                        statusBar.BackgroundColor = Color.FromArgb(255, (byte)BackGroundColorRedSlider.Value, (byte)BackGroundColorGreenSlider.Value, (byte)BackGroundColorBlueSlider.Value);
                        //statusBar.ForegroundColor = Color.FromArgb(255, (byte)FontColorRedSlider.Value, (byte)FontColorGreenSlider.Value, (byte)FontColorBlueSlider.Value);
                        statusBar.BackgroundOpacity = 1;
#endif
                    }//手机状态栏颜色
                }
            }
            catch { }
        }

        private async Task ShowRealTime()  //显示被续过的时间
        {
            try
            {
                long total;
                total = await GetTotalSecond();
                DateTime now = DateTime.Now;
                DateTime timeDeleted = now.AddSeconds(total);
                realTime.Text = "你的实际时间：" + timeDeleted.ToString("yyyy年MM月dd日 HH:mm:ss");
            }
            catch { }
        }

        public void openAuto()  //语音调用的东西
        {
        }

        private void BackGroundAcrylicBlueSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            SetBcakGroundColor();
        }
    }
}
