using System;
using System.IO;
using System.Text.Json;

namespace IslandEye
{
    /// <summary>User settings (weather location, °C/°F) saved in %AppData%\IslandEye\settings.json</summary>
    public class AppSettings
    {
        public string City { get; set; } = "";
        public double Lat { get; set; }
        public double Lon { get; set; }
        public bool HasLocation { get; set; }
        public bool Fahrenheit { get; set; }
        public bool NotchMode { get; set; }
        public bool? LiteMode { get; set; }   // null = decide automatically from installed RAM
        public string LastAnnouncedUpdate { get; set; } = "";

        private static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IslandEye", "settings.json");

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
            }
            catch { }
            return new AppSettings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
            }
            catch { }
        }
    }
}
