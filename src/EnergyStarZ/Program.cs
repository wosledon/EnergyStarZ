using EnergyStarZ.Interop;
using EnergyStarZ.Config;
using Microsoft.Extensions.Configuration;
using System.Windows.Forms;

namespace EnergyStarZ
{
    internal class Program
    {
        private static AppSettings _settings = null!;

        static async Task HouseKeepingThreadProc(AppSettings settings, CancellationToken cancellationToken)
        {
            Console.WriteLine($"[{DateTime.UtcNow:O}] House keeping thread started.");

            try
            {
                using var houseKeepingTimer = new PeriodicTimer(TimeSpan.FromMinutes(settings.ScanIntervalMinutes));

                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        await houseKeepingTimer.WaitForNextTickAsync(cancellationToken);

                        await Task.Delay(TimeSpan.FromSeconds(settings.ThrottleDelaySeconds), cancellationToken);

                        EnergyManager.ThrottleAllUserBackgroundProcesses();
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        if (settings.EnableLogging)
                        {
                            Console.WriteLine($"[{DateTime.UtcNow:O}] Housekeeping error: {ex.Message}");
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // 正常退出
            }
        }

        [STAThread]
        static int Main(string[] args)
        {
            // 加载配置
            var config = ConfigurationHelper.LoadConfiguration();
            _settings = ConfigurationHelper.GetAppSettings(config);

            // Well, this program only works for Windows Version starting with Cobalt...
            // Nickel or higher will be better, but at least it works in Cobalt
            //
            // In .NET 5.0 and later, System.Environment.OSVersion always returns the actual OS version.
            if (Environment.OSVersion.Version.Build < EnergyManager.RequiredWindowsBuild)
            {
                Console.WriteLine($"E: This program requires Windows 11 24H2 (build {EnergyManager.RequiredWindowsBuild}) or later.");
                Console.WriteLine("E: Please upgrade to Windows 11 24H2 for best result, and consider ThinkPad Z13 as your next laptop.");
                // ERROR_CALL_NOT_IMPLEMENTED
                return EnergyManager.ExitErrorCodeValue;
            }

            // 初始化 EnergyManager 单例
            using var energyManager = EnergyManager.Instance;

            // 初始化 Windows Forms
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

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
            Console.WriteLine($"[{DateTime.UtcNow:O}] Application exiting, cleaning up...");

            // 取消后台任务
            cts.Cancel();

            try
            {
                // 等待后台任务完成（最多等待5秒）
                houseKeepingTask.Wait(TimeSpan.FromSeconds(5));
            }
            catch (Exception)
            {
                Console.WriteLine($"[{DateTime.UtcNow:O}] Housekeeping thread did not respond to cancellation in time.");
            }

            // 取消窗口事件订阅
            HookManager.UnsubscribeWindowEvents();

            // 显式释放 EnergyManager 单例（释放非托管资源）
            energyManager.Dispose();

            return 0;
        }
    }
}