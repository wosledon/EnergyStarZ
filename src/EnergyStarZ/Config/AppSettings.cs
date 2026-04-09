using Microsoft.Extensions.Configuration;

namespace EnergyStarZ.Config
{
    public class AppSettings
    {
        public int ScanIntervalMinutes { get; set; } = 10;
        public int ThrottleDelaySeconds { get; set; } = 30;
        public bool EnableLogging { get; set; } = true;
        public string LogLevel { get; set; } = "Information";
        public string InitialMode { get; set; } = "Auto";
        public List<string> BypassProcessList { get; set; } = new List<string>();
        public int LRUCacheSize { get; set; } = 5;
        public int TimeoutSeconds { get; set; } = 30;
        public bool EnableAutoPowerMode { get; set; } = false;
        public int SwitchDebounceMs { get; set; } = 500;
    }

    public static class ConfigurationHelper
    {
        public static IConfigurationRoot LoadConfiguration()
        {
            var builder = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
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