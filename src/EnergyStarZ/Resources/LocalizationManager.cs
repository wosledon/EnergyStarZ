using System.Globalization;
using System.Text.Json;

namespace EnergyStarZ.Resources
{
    public static class LocalizationManager
    {
        private static Dictionary<string, Dictionary<string, string>> _localizedStrings;
        private static CultureInfo _currentCulture;

        static LocalizationManager()
        {
            LoadLocalizationData();
            var cultureCode = GetSavedCulture();
            _currentCulture = new CultureInfo(cultureCode);
        }

        private static void LoadLocalizationData()
        {
            var jsonContent = File.ReadAllText("Resources/Localization.json");
            var localizationData = JsonSerializer.Deserialize<LocalizationData>(jsonContent);

            _localizedStrings = new Dictionary<string, Dictionary<string, string>>();

            // Populate the localized strings dictionary
            foreach (var property in typeof(UIData).GetProperties())
            {
                var value = property.GetValue(localizationData.UI) as Dictionary<string, string>;
                _localizedStrings[property.Name] = value;
            }
        }

        public static string GetString(string key)
        {
            if (_localizedStrings.ContainsKey(key) && 
                _localizedStrings[key].ContainsKey(_currentCulture.Name))
            {
                return _localizedStrings[key][_currentCulture.Name];
            }

            // Fallback to en-US if the key is not found in current culture
            if (_localizedStrings.ContainsKey(key) && 
                _localizedStrings[key].ContainsKey("en-US"))
            {
                return _localizedStrings[key]["en-US"];
            }

            // Return the key itself if no translation is found
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
            var jsonContent = File.ReadAllText("Resources/Localization.json");
            var localizationData = JsonSerializer.Deserialize<LocalizationData>(jsonContent);
            return localizationData.Language.SupportedCultures.Select(code => new CultureInfo(code)).ToList();
        }

        private static string GetSavedCulture()
        {
            try
            {
                var jsonContent = File.ReadAllText("Resources/Localization.json");
                var localizationData = JsonSerializer.Deserialize<LocalizationData>(jsonContent);
                return localizationData.Language.CurrentCulture ?? "en-US";
            }
            catch
            {
                return "en-US"; // Default to English if there's an issue
            }
        }

        private static void SaveCulture(string cultureName)
        {
            try
            {
                var filePath = "Resources/Localization.json";
                var jsonString = File.ReadAllText(filePath);
                var localizationData = JsonSerializer.Deserialize<LocalizationData>(jsonString);

                localizationData.Language.CurrentCulture = cultureName;

                var options = new JsonSerializerOptions { WriteIndented = true };
                var updatedJsonString = JsonSerializer.Serialize(localizationData, options);
                File.WriteAllText(filePath, updatedJsonString);
            }
            catch
            {
                // If saving fails, just continue with the in-memory change
            }
        }
    }

    // Data classes for deserialization
    public class LocalizationData
    {
        public LanguageSettings Language { get; set; }
        public UIData UI { get; set; }
    }

    public class LanguageSettings
    {
        public string CurrentCulture { get; set; }
        public List<string> SupportedCultures { get; set; }
    }

    public class UIData
    {
        public Dictionary<string, string> AutoMode { get; set; }
        public Dictionary<string, string> ManualMode { get; set; }
        public Dictionary<string, string> PausedMode { get; set; }
        public Dictionary<string, string> EditConfiguration { get; set; }
        public Dictionary<string, string> ReloadConfiguration { get; set; }
        public Dictionary<string, string> Exit { get; set; }
        public Dictionary<string, string> SwitchLanguage { get; set; }
        public Dictionary<string, string> English { get; set; }
        public Dictionary<string, string> Chinese { get; set; }
        public Dictionary<string, string> PowerModeChanged { get; set; }
        public Dictionary<string, string> AutoModeActivated { get; set; }
        public Dictionary<string, string> ManualModeActivated { get; set; }
        public Dictionary<string, string> PausedModeActivated { get; set; }
        public Dictionary<string, string> ConfigurationReloaded { get; set; }
        public Dictionary<string, string> ApplicationStarted { get; set; }
    }
}