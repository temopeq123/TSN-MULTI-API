using System;
using System.IO;
using System.Text.Json;

namespace TSN_MULTI_API.Core
{
    public static class AppDefaults
    {
        public const string ApiKey = "60904342-e040-4180-b037-e55d313c6d3a";
    }

    public class AppSettings
    {
        public string StatementsDirectory { get; set; } = string.Empty;
        public string DefaultDocumentsDirectory { get; set; } = string.Empty;
        public string DefaultCertificateThumbprint { get; set; } = string.Empty;
        public string DefaultApiKey { get; set; } = string.Empty;
        public string DefaultDescription { get; set; } = "Членская книжка ТСН Сапожок";
    }

    public class SettingsManager
    {
        private readonly string _settingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");
        public AppSettings Current { get; private set; }

        public SettingsManager()
        {
            LoadSettings();
        }

        public void LoadSettings()
        {
            if (File.Exists(_settingsPath))
            {
                var json = File.ReadAllText(_settingsPath);
                Current = JsonSerializer.Deserialize<AppSettings>(json) ?? GetDefaultSettings();
            }
            else
            {
                Current = GetDefaultSettings();
                SaveSettings();
            }

            if (string.IsNullOrWhiteSpace(Current.DefaultApiKey))
                Current.DefaultApiKey = AppDefaults.ApiKey;
        }

        public void SaveSettings()
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(_settingsPath, JsonSerializer.Serialize(Current, options));
        }

        private AppSettings GetDefaultSettings()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            return new AppSettings
            {
                StatementsDirectory = Path.Combine(baseDir, "Statements"),
                DefaultDocumentsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                DefaultApiKey = AppDefaults.ApiKey,
                DefaultDescription = "Членская книжка ТСН Сапожок"
            };
        }
    }
}
