using EnergyStarZ.Utilities;
using System.Globalization;
using System.Text.Json;

namespace EnergyStarZ.Resources
{
    public static class LocalizationManager
    {
        private static Dictionary<string, Dictionary<string, string>> _localizedStrings = null!;
        private static CultureInfo _currentCulture = null!;
        private static LocalizationData? _cachedData;
        private static readonly object _lock = new();

        private const string LocalizationFilePath = "Resources/Localization.json";

        static LocalizationManager()
        {
            try
            {
                LoadLocalizationData();
                var cultureCode = GetSavedCulture();
                _currentCulture = new CultureInfo(cultureCode);
            }
            catch (Exception ex)
            {
                // 如果加载失败，使用默认值，避免应用启动时崩溃
                AppLogger.Warn($"Failed to load localization data: {ex.Message}. Using defaults.");
                _localizedStrings = new Dictionary<string, Dictionary<string, string>>();
                _currentCulture = new CultureInfo("en-US");
            }
        }

        private static void LoadLocalizationData()
        {
            lock (_lock)
            {
                var jsonContent = File.ReadAllText(LocalizationFilePath);
                _cachedData = JsonSerializer.Deserialize<LocalizationData>(jsonContent)!;

                _localizedStrings = new Dictionary<string, Dictionary<string, string>>();

                foreach (var property in typeof(UIData).GetProperties())
                {
                    var value = property.GetValue(_cachedData.UI) as Dictionary<string, string>;
                    if (value != null)
                    {
                        _localizedStrings[property.Name] = value;
                    }
                }
            }
        }

        public static string GetString(string key)
        {
            if (_localizedStrings.TryGetValue(key, out var cultureMap))
            {
                if (cultureMap.TryGetValue(_currentCulture.Name, out var value))
                    return value;

                if (cultureMap.TryGetValue("en-US", out var fallback))
                    return fallback;
            }

            return key;
        }

        public static void SetCulture(CultureInfo culture)
        {
            _currentCulture = culture;
            SaveCulture(culture.Name);
        }

        public static CultureInfo GetCurrentCulture()
        {
            return _currentCulture;
        }

        public static List<CultureInfo> GetSupportedCultures()
        {
            if (_cachedData?.Language?.SupportedCultures != null)
            {
                return _cachedData.Language.SupportedCultures.Select(code => new CultureInfo(code)).ToList();
            }

            return [];
        }

        private static string GetSavedCulture()
        {
            try
            {
                if (_cachedData == null)
                {
                    var jsonContent = File.ReadAllText(LocalizationFilePath);
                    _cachedData = JsonSerializer.Deserialize<LocalizationData>(jsonContent)!;
                }

                return _cachedData.Language?.CurrentCulture ?? "en-US";
            }
            catch
            {
                return "en-US";
            }
        }

        private static void SaveCulture(string cultureName)
        {
            try
            {
                lock (_lock)
                {
                    var jsonString = File.ReadAllText(LocalizationFilePath);
                    var localizationData = JsonSerializer.Deserialize<LocalizationData>(jsonString)!;

                    if (localizationData.Language != null)
                    {
                        localizationData.Language.CurrentCulture = cultureName;

                        var options = new JsonSerializerOptions { WriteIndented = true };
                        var updatedJsonString = JsonSerializer.Serialize(localizationData, options);

                        // 原子写入：先写入临时文件，再替换原文件
                        var tempFilePath = LocalizationFilePath + ".tmp";
                        File.WriteAllText(tempFilePath, updatedJsonString);
                        File.Move(tempFilePath, LocalizationFilePath, overwrite: true);

                        _cachedData = localizationData;
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"Failed to save language preference: {ex.Message}");
                // If saving fails, just continue with the in-memory change
            }
        }
    }

    // Data classes for deserialization
    public class LocalizationData
    {
        public required LanguageSettings Language { get; set; }
        public required UIData UI { get; set; }
    }

    public class LanguageSettings
    {
        public string? CurrentCulture { get; set; }
        public required List<string> SupportedCultures { get; set; }
    }

    public class UIData
    {
        public required Dictionary<string, string> AutoMode { get; set; }
        public required Dictionary<string, string> ManualMode { get; set; }
        public required Dictionary<string, string> PausedMode { get; set; }
        public required Dictionary<string, string> EditConfiguration { get; set; }
        public required Dictionary<string, string> ReloadConfiguration { get; set; }
        public required Dictionary<string, string> Exit { get; set; }
        public required Dictionary<string, string> SwitchLanguage { get; set; }
        public required Dictionary<string, string> English { get; set; }
        public required Dictionary<string, string> Chinese { get; set; }
        public required Dictionary<string, string> PowerModeChanged { get; set; }
        public required Dictionary<string, string> AutoModeActivated { get; set; }
        public required Dictionary<string, string> ManualModeActivated { get; set; }
        public required Dictionary<string, string> PausedModeActivated { get; set; }
        public required Dictionary<string, string> ConfigurationReloaded { get; set; }
        public required Dictionary<string, string> ApplicationStarted { get; set; }
        public required Dictionary<string, string> AutoPowerMode { get; set; }
        public required Dictionary<string, string> AutoPowerModeEnabled { get; set; }
        public required Dictionary<string, string> AutoPowerModeDisabled { get; set; }
    }
}
