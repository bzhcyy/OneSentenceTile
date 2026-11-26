using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.ApplicationModel.Background;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.Storage;
using Windows.UI.Notifications;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

namespace OneSentenceTile
{
    /// <summary>
    /// 提供特定于应用程序的行为，以补充默认的应用程序类。
    /// </summary>
    sealed partial class App : Application
    {
        /// <summary>
        /// 初始化单一实例应用程序对象。这是执行的创作代码的第一行，
        /// 已执行，逻辑上等同于 main() 或 WinMain()。
        /// </summary>
        public App()
        {
            this.InitializeComponent();
            this.Suspending += OnSuspending;
        }

        /// <summary>
        /// 在应用程序由最终用户正常启动时进行调用。
        /// 将在启动应用程序以打开特定文件等情况下使用。
        /// </summary>
        /// <param name="e">有关启动请求和过程的详细信息。</param>
        protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
            Frame rootFrame = Window.Current.Content as Frame;

            // 不要在窗口已包含内容时重复应用程序初始化，
            // 只需确保窗口处于活动状态
            if (rootFrame == null)
            {
                // 创建要充当导航上下文的框架，并导航到第一页
                rootFrame = new Frame();

                rootFrame.NavigationFailed += OnNavigationFailed;

                if (e.PreviousExecutionState == ApplicationExecutionState.Terminated)
                {
                    //TODO: 从之前挂起的应用程序加载状态
                }

                // 将框架放在当前窗口中
                Window.Current.Content = rootFrame;
            }

            // 每次启动都重新探一次：按设置里选中的源挑地址，连不上自动换另一个
            ConfigureTileSource();

            if (e.PrelaunchActivated == false)
            {
                if (rootFrame.Content == null)
                {
                    // 当导航堆栈尚未还原时，导航到第一页，
                    // 并通过将所需信息作为导航参数传入来配置
                    // 参数
                    rootFrame.Navigate(typeof(MainPage), e.Arguments);
                }
                // 确保当前窗口处于活动状态
                Window.Current.Activate();
            }
        }

        /// <summary>
        /// 启动时为系统周期更新注册可直接读取的 XML 地址。
        /// 官方 API 是 JSON，系统不能直接解析，因此官方一言模式在后台使用 Worker / Pages XML。
        /// 即使设置为「实时」，也注册 Windows 最短 30 分钟周期，避免关 App 或重启后停止更新。
        /// </summary>
        private async void ConfigureTileSource()
        {
            var updater = TileUpdateManager.CreateTileUpdaterForApplication();
            try
            {
                var values = ApplicationData.Current.RoamingSettings.Values;

                // 启动时先清除上一次注册，避免旧的周期任务覆盖当前设置。
                try { updater.StopPeriodicUpdate(); } catch { }

                // 未设置时沿用应用默认开启；明确关闭时，不再注册后台更新。
                var tileOn = values["tileOn"];
                if (tileOn != null && !(bool)tileOn)
                {
                    System.Diagnostics.Debug.WriteLine("动态磁贴已关闭，跳过周期更新注册。");
                    return;
                }

                // 必须读取用户实际选择的间隔，不能每次启动都强制改成 30 分钟。
                var interval = values["tileInterval"] as string ?? "Realtime";
                // 旧版本可能保存了已移除的 15 分钟选项，统一迁移到 30 分钟。
                if (interval == "QuarterHour")
                {
                    interval = "HalfHour";
                    values["tileInterval"] = interval;
                }
                // 实时只代表 App 打开时立即拉取；关闭 App 后 Windows 最短只能每 30 分钟拉一次。
                var periodicInterval = interval == "Realtime" ? "HalfHour" : interval;
                Windows.UI.Notifications.PeriodicUpdateRecurrence recurrence;
                switch (periodicInterval)
                {
                    case "HalfHour":
                        // Windows 磁贴周期更新的最短系统间隔为 30 分钟。
                        recurrence = PeriodicUpdateRecurrence.HalfHour;
                        break;
                    case "Hour":
                        recurrence = PeriodicUpdateRecurrence.Hour;
                        break;
                    case "SixHours":
                        recurrence = PeriodicUpdateRecurrence.SixHours;
                        break;
                    case "TwelveHours":
                        recurrence = PeriodicUpdateRecurrence.TwelveHours;
                        break;
                    case "Daily":
                        recurrence = PeriodicUpdateRecurrence.Daily;
                        break;
                    default:
                        recurrence = PeriodicUpdateRecurrence.HalfHour;
                        periodicInterval = "HalfHour";
                        break;
                }

                // 系统周期更新必须使用可直接返回 Tile XML 的 HTTPS 地址。
                // ResolveAsync 会按用户选择的源探测 Worker / Pages，并附加样式参数。
                var uri = await TileSource.ResolveAsync();

                // 防止异步探测期间用户关闭磁贴或更改刷新间隔。
                values = ApplicationData.Current.RoamingSettings.Values;
                var currentTileOn = values["tileOn"];
                if (currentTileOn != null && !(bool)currentTileOn) return;
                var currentInterval = values["tileInterval"] as string ?? "Realtime";
                if (currentInterval == "QuarterHour") currentInterval = "HalfHour";
                if (currentInterval != interval) return;

                updater.StartPeriodicUpdate(uri, recurrence);
                System.Diagnostics.Debug.WriteLine("已注册系统磁贴周期更新：" + periodicInterval + "（设置：" + interval + "）；源：" + uri);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("注册系统磁贴周期更新失败：" + ex);
                // 启动不应因此失败；清单中的 Pages TileUpdate 地址仍可作为首次启动前的系统备用源。
            }
        }

        /// <summary>
        /// 导航到特定页失败时调用
        /// </summary>
        ///<param name="sender">导航失败的框架</param>
        ///<param name="e">有关导航失败的详细信息</param>
        void OnNavigationFailed(object sender, NavigationFailedEventArgs e)
        {
            throw new Exception("Failed to load Page " + e.SourcePageType.FullName);
        }

        /// <summary>
        /// 在将要挂起应用程序执行时调用。  在不知道应用程序
        /// 无需知道应用程序会被终止还是会恢复，
        /// 并让内存内容保持不变。
        /// </summary>
        /// <param name="sender">挂起的请求的源。</param>
        /// <param name="e">有关挂起请求的详细信息。</param>
        private void OnSuspending(object sender, SuspendingEventArgs e)
        {
            var deferral = e.SuspendingOperation.GetDeferral();
            //TODO: 保存应用程序状态并停止任何后台活动
            deferral.Complete();
        }
        
        protected override void OnActivated(IActivatedEventArgs e)
        {
            // Was the app activated by a voice command?
            
            if (e.Kind != Windows.ApplicationModel.Activation.ActivationKind.VoiceCommand)
            {
                return;
            }

            var commandArgs = e as Windows.ApplicationModel.Activation.VoiceCommandActivatedEventArgs;

            var speechRecognitionResult = commandArgs.Result;
            string voiceCommandName = speechRecognitionResult.RulePath[0];

            Frame rootFrame = Window.Current.Content as Frame;
            MainPage page = rootFrame.Content as MainPage;

            if (voiceCommandName == "OneSentenceTile")
            {
                page.openAuto();
            }
        }
    }
}
