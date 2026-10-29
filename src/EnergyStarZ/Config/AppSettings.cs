using Microsoft.Extensions.Configuration;

namespace EnergyStarZ.Config
{
    public class AppSettings
    {
        private int _scanIntervalMinutes = 10;
        private int _lruCacheSize = 5;
        private int _switchDebounceMs = 500;
        private int _lruDecayMinutes = 5;
        private int _lruMinSize = 3;
        private int _lruMaxSize = 15;

        public int ScanIntervalMinutes 
        { 
            get => _scanIntervalMinutes; 
            set => _scanIntervalMinutes = Math.Max(1, value); 
        }
        
        public bool EnableLogging { get; set; } = true;
        public string LogLevel { get; set; } = "Information";
        public string InitialMode { get; set; } = "Auto";
        public List<string> BypassProcessList { get; set; } = new List<string>();

        /// <summary>
        /// 可能正在播音的进程名单（启发式豁免，避免节流导致音频卡顿）
        /// </summary>
        public List<string> AudibleProcessList { get; set; } = new List<string>
        {
            "spotify.exe", "music.ui.exe", "vlc.exe", "wmplayer.exe", "foobar2000.exe",
            "qqmusic.exe", "cloudmusic.exe", "kugou.exe", "audiodg.exe",
            "zoom.exe", "teams.exe", "discord.exe", "wechat.exe", "qq.exe",
            "tim.exe", "dingtalk.exe", "lark.exe", "feishu.exe", "slack.exe",
        };

        /// <summary>
        /// 混合架构 CPU 上把后台进程绑定到能效核（探测失败时自动禁用）
        /// </summary>
        public bool EnableEfficiencyCoreBinding { get; set; } = true;

        /// <summary>
        /// 对被节流进程设置低内存优先级（效果很强，但可能导致内存密集型后台程序明显变慢）
        /// </summary>
        public bool EnableLowMemoryPriority { get; set; } = true;

        /// <summary>
        /// 交流电供电时也施加低内存优先级（默认关闭：交流电上以后台任务速度优先）
        /// </summary>
        public bool LowMemoryPriorityOnAcPower { get; set; } = false;

        /// <summary>
        /// 启用自动电源模式后，接通交流电时是否完全暂停管理。
        /// 默认 false：交流电上继续节流，收益是前台流畅度而非省电
        /// </summary>
        public bool PauseOnAcPower { get; set; } = false;

        /// <summary>
        /// 用户空闲多少分钟后进入深度模式（更激进节流）
        /// </summary>
        public int DeepModeIdleMinutes { get; set; } = 5;

        /// <summary>
        /// 电量低于该百分比时提升一档节流强度
        /// </summary>
        public int LowBatteryEscalatePercent { get; set; } = 20;
        
        public int LRUCacheSize 
        { 
            get => _lruCacheSize; 
            set => _lruCacheSize = Math.Clamp(value, 1, 50); 
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