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

    public string? LoadDataSourcePath()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return null;
            }

            var settings = JsonSerializer.Deserialize<SettingsModel>(File.ReadAllText(_settingsPath));
            return settings?.DataSourcePath ?? settings?.DatabasePath;
        }
        catch
        {
            return null;
        }
    }

    public void SaveDataSourcePath(string path)
    {
        var settings = new SettingsModel
        {
            DataSourcePath = path,
            DatabasePath = path // Keep older CherryKey builds able to read the setting.
        };
        File.WriteAllText(_settingsPath, JsonSerializer.Serialize(settings, JsonOptions));
    }

    // Compatibility aliases for pre-v0.3 code and settings.
    public string? LoadDatabasePath() => LoadDataSourcePath();
    public void SaveDatabasePath(string path) => SaveDataSourcePath(path);

    private sealed class SettingsModel
    {
        public string? DataSourcePath { get; init; }
        public string? DatabasePath { get; init; }
    }
}
