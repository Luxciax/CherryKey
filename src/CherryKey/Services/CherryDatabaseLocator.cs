using System.IO;

namespace CherryKey.Services;

public sealed class CherryDatabaseLocator
{
    private readonly AppSettingsStore _settings;

    public CherryDatabaseLocator(AppSettingsStore settings)
    {
        _settings = settings;
    }

    public string? Locate()
    {
        var candidates = new List<string?> { _settings.LoadDatabasePath() };

        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        foreach (var root in new[] { roaming, local })
        {
            candidates.Add(Path.Combine(root, "CherryStudio", "Data", "cherrystudio.sqlite"));
            candidates.Add(Path.Combine(root, "Cherry Studio", "Data", "cherrystudio.sqlite"));
            candidates.Add(Path.Combine(root, "cherry-studio", "Data", "cherrystudio.sqlite"));
            candidates.Add(Path.Combine(root, "cherrystudio", "Data", "cherrystudio.sqlite"));
        }

        var direct = candidates
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .FirstOrDefault(File.Exists);

        if (direct is not null)
        {
            return direct;
        }

        foreach (var root in new[] { roaming, local })
        {
            try
            {
                var rootDirectory = new DirectoryInfo(root);
                foreach (var directory in rootDirectory.EnumerateDirectories("*Cherry*", SearchOption.TopDirectoryOnly))
                {
                    var database = Path.Combine(directory.FullName, "Data", "cherrystudio.sqlite");
                    if (File.Exists(database))
                    {
                        return database;
                    }
                }
            }
            catch
            {
                // Ignore inaccessible directories. Manual selection remains available.
            }
        }

        return null;
    }
}
