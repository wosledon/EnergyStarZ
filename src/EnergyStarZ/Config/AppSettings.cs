using Microsoft.Extensions.Configuration;

namespace EnergyStarZ.Config
{
    public class AppSettings
    {
        private int _scanIntervalMinutes = 10;
        private int _throttleDelaySeconds = 30;
        private int _lruCacheSize = 5;
        private int _timeoutSeconds = 30;
        private int _switchDebounceMs = 500;
        private int _lruDecayMinutes = 5;
        private int _lruMinSize = 3;
        private int _lruMaxSize = 15;

        public int ScanIntervalMinutes 
        { 
            get => _scanIntervalMinutes; 
            set => _scanIntervalMinutes = Math.Max(1, value); 
        }
        
        public int ThrottleDelaySeconds 
        { 
            get => _throttleDelaySeconds; 
            set => _throttleDelaySeconds = Math.Max(1, value); 
        }
        
        public bool EnableLogging { get; set; } = true;
        public string LogLevel { get; set; } = "Information";
        public string InitialMode { get; set; } = "Auto";
        public List<string> BypassProcessList { get; set; } = new List<string>();
        
        public int LRUCacheSize 
        { 
            get => _lruCacheSize; 
            set => _lruCacheSize = Math.Clamp(value, 1, 50); 
        }
        
        public int TimeoutSeconds 
        { 
            get => _timeoutSeconds; 
            set => _timeoutSeconds = Math.Max(1, value); 
        }
        
        public bool EnableAutoPowerMode { get; set; } = false;
        
        public int SwitchDebounceMs 
        { 
            get => _switchDebounceMs; 
            set => _switchDebounceMs = Math.Clamp(value, 100, 5000); 
        }

        // LRU 时间衰减配置（分钟）
        public int LRUDecayMinutes 
        { 
            get => _lruDecayMinutes; 
            set => _lruDecayMinutes = Math.Max(1, value); 
        }

        // LRU 自动调整配置
        public int LRUMinSize 
        { 
            get => _lruMinSize; 
            set => _lruMinSize = Math.Clamp(value, 1, 10); 
        }
        
        public int LRUMaxSize 
        { 
            get => _lruMaxSize; 
            set => _lruMaxSize = Math.Clamp(value, 5, 50); 
        }
    }

    public static class ConfigurationHelper
    {
        public static IConfigurationRoot LoadConfiguration()
        {
            // 使用应用程序基目录而不是当前工作目录，更可靠
            var basePath = AppContext.BaseDirectory;
            
            var builder = new ConfigurationBuilder()
                .SetBasePath(basePath)
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);

            return builder.Build();
        }

        public static AppSettings GetAppSettings(IConfigurationRoot configuration)
        {
            var appSettings = new AppSettings();
            configuration.GetSection("AppSettings").Bind(appSettings);
            return appSettings;
        }
    }
}