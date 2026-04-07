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
            Console.WriteLine("House keeping thread started.");
            
            try
            {
                // 使用配置中的间隔时间
                using var houseKeepingTimer = new PeriodicTimer(TimeSpan.FromMinutes(settings.ScanIntervalMinutes));
                
                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        await houseKeepingTimer.WaitForNextTickAsync(cancellationToken);
                        
                        // 使用配置中的延迟时间
                        await Task.Delay(TimeSpan.FromSeconds(settings.ThrottleDelaySeconds), cancellationToken);
                        
                        EnergyManager.ThrottleAllUserBackgroundProcesses();
                    }
                    catch (OperationCanceledException)
                    {
                        // 正常退出
                        break;
                    }
                    catch (Exception ex)
                    {
                        if (settings.EnableLogging)
                        {
                            Console.WriteLine($"Housekeeping error: {ex.Message}");
                        }
                        // 继续运行而不是崩溃
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // 正常退出
            }
        }

        [STAThread]
        static async Task<int> Main(string[] args)
        {
            // 加载配置
            var config = ConfigurationHelper.LoadConfiguration();
            _settings = ConfigurationHelper.GetAppSettings(config);

            // Well, this program only works for Windows Version starting with Cobalt...
            // Nickel or higher will be better, but at least it works in Cobalt
            //
            // In .NET 5.0 and later, System.Environment.OSVersion always returns the actual OS version.
            if (Environment.OSVersion.Version.Build < 26100)
            {
                Console.WriteLine("E: You are too poor to use this program.");
                Console.WriteLine("E: Please upgrade to Windows 11 24H2 for best result, and consider ThinkPad Z13 as your next laptop.");
                // ERROR_CALL_NOT_IMPLEMENTED
                Environment.Exit(120);
            }

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

            // 退出时取消后台任务
            cts.Cancel();
            
            try
            {
                // 等待后台任务完成（最多等待5秒）
                await Task.WhenAny(houseKeepingTask, Task.Delay(TimeSpan.FromSeconds(5)));
            }
            catch (TimeoutException)
            {
                Console.WriteLine("Housekeeping thread did not respond to cancellation in time.");
            }
            
            HookManager.UnsubscribeWindowEvents();
            
            return 0;
        }
    }
}