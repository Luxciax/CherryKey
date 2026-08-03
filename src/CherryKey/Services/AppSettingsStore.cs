using System.IO;
using System.Text.Json;

namespace CherryKey.Services;

public sealed class AppSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _settingsPath;

    public AppSettingsStore()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CherryKey");
        Directory.CreateDirectory(directory);
        _settingsPath = Path.Combine(directory, "settings.json");
    }

    public string? LoadDatabasePath()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return null;
            }

            var settings = JsonSerializer.Deserialize<SettingsModel>(File.ReadAllText(_settingsPath));
            return settings?.DatabasePath;
        }
        catch
        {
            return null;
        }
    }

    public void SaveDatabasePath(string path)
    {
        var settings = new SettingsModel { DatabasePath = path };
        File.WriteAllText(_settingsPath, JsonSerializer.Serialize(settings, JsonOptions));
    }

    private sealed class SettingsModel
    {
        public string? DatabasePath { get; init; }
    }
}
