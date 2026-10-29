using EnergyStarZ.Interop;
using EnergyStarZ.Config;
using EnergyStarZ.Utilities;
using Microsoft.Extensions.Configuration;
using System.Windows.Forms;

namespace EnergyStarZ
{
    internal class Program
    {
        private static AppSettings _settings = null!;

        static async Task HouseKeepingThreadProc(AppSettings settings, CancellationToken cancellationToken)
        {
            AppLogger.Info("House keeping thread started.");

            var interval = TimeSpan.FromMinutes(Math.Max(1, settings.ScanIntervalMinutes));
            using var houseKeepingTimer = new PeriodicTimer(interval);

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        await houseKeepingTimer.WaitForNextTickAsync(cancellationToken);

                        // PeriodicTimer 已经提供了准确的间隔，不需要额外的 Delay
                        EnergyManager.ThrottleAllUserBackgroundProcesses();

                        // 托盘"重载配置"会原地更新 settings，间隔变化时同步定时器，
                        // 使 ScanIntervalMinutes 无需重启即可生效
                        var currentInterval = TimeSpan.FromMinutes(Math.Max(1, settings.ScanIntervalMinutes));
                        if (currentInterval != interval)
                        {
                            interval = currentInterval;
                            houseKeepingTimer.Period = interval;
                            AppLogger.Info($"House keeping interval updated to {settings.ScanIntervalMinutes} minute(s).");
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Error($"Housekeeping error: {ex.Message}");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // 正常退出
            }
        }

        private static void OnUiThreadException(object sender, ThreadExceptionEventArgs e)
        {
            AppLogger.Error($"UI thread exception: {e.Exception}");
        }

        private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            AppLogger.Error($"Fatal unhandled exception: {e.ExceptionObject}");

            try
            {
                // 尽量恢复被节流的进程，避免退出后用户应用保持低优先级
                EnergyManager.RestoreAllProcessesToNormalAsync().Wait(TimeSpan.FromSeconds(3));
            }
            catch (Exception ex)
            {
                AppLogger.Error($"Failed to restore processes after crash: {ex.Message}");
            }

            AppLogger.Flush();
        }

        private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            AppLogger.Error($"Unobserved task exception: {e.Exception}");
            e.SetObserved();
        }

        [STAThread]
        static int Main(string[] args)
        {
            // 单实例检查：多个实例会同时安装钩子并互相节流
            using var singleInstanceMutex = new Mutex(
                initiallyOwned: true,
                @"Local\EnergyStarZ-7D2E4A18-3C6B-4F5D-9E1A-8B0C2D4F6A3C",
                out bool isNewInstance);

            if (!isNewInstance)
            {
                MessageBox.Show(
                    "EnergyStarZ is already running. Please check your system tray.",
                    "EnergyStarZ - Already Running",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return 0;
            }

            // 加载配置（失败时回退默认值，避免因配置文件缺失或损坏而无法启动）
            try
            {
                var config = ConfigurationHelper.LoadConfiguration();
                _settings = ConfigurationHelper.GetAppSettings(config);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Failed to load appsettings.json. Default settings will be used.\n\n{ex.Message}",
                    "EnergyStarZ - Configuration",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                _settings = new AppSettings();
            }

            // Well, this program only works for Windows Version starting with Cobalt...
            // Nickel or higher will be better, but at least it works in Cobalt
            //
            // In .NET 5.0 and later, System.Environment.OSVersion always returns the actual OS version.
            if (Environment.OSVersion.Version.Build < EnergyManager.RequiredWindowsBuild)
            {
                MessageBox.Show(
                    $"This program requires Windows 11 24H2 (build {EnergyManager.RequiredWindowsBuild}) or later.\n\nPlease upgrade to Windows 11 24H2 for best result, and consider ThinkPad Z13 as your next laptop.",
                    "EnergyStarZ - Unsupported OS",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return EnergyManager.ExitErrorCodeValue;
            }

            // 初始化 EnergyManager 单例
            using var energyManager = EnergyManager.Instance;

            // 应用配置（托盘菜单的"重载配置"之后会原地更新同一实例）
            EnergyManager.LoadConfiguration(_settings);

            // 初始化 Windows Forms
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // 全局异常兜底：避免未处理异常直接杀死进程且不留任何痕迹
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += OnUiThreadException;
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

            // 设置初始模式
            if (Enum.TryParse<PowerMode>(_settings.InitialMode, true, out var initialMode))
            {
                EnergyManager.CurrentMode = initialMode;
            }
            else
            {
                EnergyManager.CurrentMode = PowerMode.Auto; // 默认为自动模式
            }

            // 启用自动电源模式（如果配置中启用）
            EnergyManager.SetAutoPowerModeEnabled(_settings.EnableAutoPowerMode);

            // 创建系统托盘应用程序上下文
            var applicationContext = new SystemTrayApplicationContext(_settings);

            // 启动后台任务
            using var cts = new CancellationTokenSource();
            var houseKeepingTask = Task.Run(() => HouseKeepingThreadProc(_settings, cts.Token), cts.Token);

            // 根据初始模式决定是否订阅窗口事件
            if (EnergyManager.CurrentMode != PowerMode.Paused)
            {
                HookManager.SubscribeToWindowEvents();
            }

            EnergyManager.ThrottleAllUserBackgroundProcesses();

            // 运行系统托盘应用程序
            Application.Run(applicationContext);

            // 退出时清理资源
            AppLogger.Info("Application exiting, cleaning up...");
            AppLogger.Flush();

            // 取消后台任务
            cts.Cancel();

            try
            {
                // 等待后台任务完成（最多等待5秒）
                houseKeepingTask.Wait(TimeSpan.FromSeconds(5));
            }
            catch (Exception)
            {
                AppLogger.Warn("Housekeeping thread did not respond to cancellation in time.");
            }

            // 取消窗口事件订阅
            HookManager.UnsubscribeWindowEvents();

            // 显式释放 EnergyManager 单例（释放非托管资源）
            energyManager.Dispose();

            return 0;
        }
    }
}