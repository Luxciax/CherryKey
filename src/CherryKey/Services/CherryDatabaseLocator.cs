using System.Diagnostics;
using System.Text.Json;

namespace CherryKey.Services;

public sealed class CherryDatabaseLocator
{
    private const string DatabaseFileName = "cherrystudio.sqlite";
    private readonly AppSettingsStore _settings;

    public CherryDatabaseLocator(AppSettingsStore settings)
    {
        _settings = settings;
    }

    public int LastScanCount { get; private set; }
    public string LastScanSummary { get; private set; } = "尚未扫描";

    public string? Locate()
    {
        LastScanCount = 0;
        var candidates = new List<string>();
        var candidateSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var searchRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddCandidate(candidates, candidateSet, _settings.LoadDatabasePath());

        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        foreach (var root in new[] { roaming, local, common })
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            foreach (var folderName in new[]
                     {
                         "CherryStudio", "Cherry Studio", "cherry-studio", "cherrystudio",
                         "CherryStudio2", "CherryStudio-V2"
                     })
            {
                var appRoot = Path.Combine(root, folderName);
                AddSearchRoot(searchRoots, appRoot);
                AddDatabaseVariants(candidates, candidateSet, appRoot);
            }
        }

        if (!string.IsNullOrWhiteSpace(profile))
        {
            foreach (var appRoot in new[]
                     {
                         Path.Combine(profile, ".cherrystudio"),
                         Path.Combine(profile, ".config", "CherryStudio"),
                         Path.Combine(profile, "CherryStudio")
                     })
            {
                AddSearchRoot(searchRoots, appRoot);
                AddDatabaseVariants(candidates, candidateSet, appRoot);
            }
        }

        if (!string.IsNullOrWhiteSpace(documents))
        {
            var appRoot = Path.Combine(documents, "CherryStudio");
            AddSearchRoot(searchRoots, appRoot);
            AddDatabaseVariants(candidates, candidateSet, appRoot);
        }

        AddRunningCherryLocations(candidates, candidateSet, searchRoots);
        AddTopLevelCherryDirectories(roaming, searchRoots);
        AddTopLevelCherryDirectories(local, searchRoots);

        // Cherry Studio can relocate userData. The original location normally keeps a small
        // JSON preference/preboot file, so extract path-like strings before doing a wider scan.
        foreach (var root in searchRoots.ToArray())
        {
            AddJsonPathHints(root, candidates, candidateSet, searchRoots);
        }

        var direct = FirstExisting(candidates);
        if (direct is not null)
        {
            LastScanSummary = $"自动检查了 {LastScanCount} 个候选位置";
            return direct;
        }

        foreach (var root in searchRoots.OrderBy(path => path.Length))
        {
            foreach (var database in EnumerateDatabaseFiles(root, maxDepth: 6, maxDirectories: 3500))
            {
                AddCandidate(candidates, candidateSet, database);
            }
        }

        direct = FirstExisting(candidates);
        LastScanSummary = direct is null
            ? $"已扫描 {LastScanCount} 个候选位置，未发现数据库"
            : $"自动扫描 {LastScanCount} 个候选位置后发现数据库";
        return direct;
    }

    private string? FirstExisting(IEnumerable<string> candidates)
    {
        foreach (var path in candidates)
        {
            LastScanCount++;
            try
            {
                if (File.Exists(path) &&
                    string.Equals(Path.GetFileName(path), DatabaseFileName, StringComparison.OrdinalIgnoreCase))
                {
                    return Path.GetFullPath(path);
                }
            }
            catch
            {
                // Ignore malformed or inaccessible paths and continue scanning.
            }
        }

        return null;
    }

    private static void AddDatabaseVariants(List<string> candidates, HashSet<string> candidateSet, string root)
    {
        AddCandidate(candidates, candidateSet, Path.Combine(root, "Data", DatabaseFileName));
        AddCandidate(candidates, candidateSet, Path.Combine(root, "data", DatabaseFileName));
        AddCandidate(candidates, candidateSet, Path.Combine(root, DatabaseFileName));
        AddCandidate(candidates, candidateSet, Path.Combine(root, "User Data", "Data", DatabaseFileName));
    }

    private static void AddRunningCherryLocations(
        List<string> candidates,
        HashSet<string> candidateSet,
        HashSet<string> searchRoots)
    {
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (!process.ProcessName.Contains("cherry", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var executable = process.MainModule?.FileName;
                if (string.IsNullOrWhiteSpace(executable))
                {
                    continue;
                }

                var directory = Path.GetDirectoryName(executable);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    AddSearchRoot(searchRoots, directory);
                    AddDatabaseVariants(candidates, candidateSet, directory);

                    var parent = Directory.GetParent(directory);
                    if (parent is not null && parent.Name.Contains("cherry", StringComparison.OrdinalIgnoreCase))
                    {
                        AddSearchRoot(searchRoots, parent.FullName);
                        AddDatabaseVariants(candidates, candidateSet, parent.FullName);
                    }
                }
            }
            catch
            {
                // Access to another process module can be denied. It is only an optional hint.
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    private static void AddTopLevelCherryDirectories(string root, HashSet<string> searchRoots)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return;
        }

        try
        {
            foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
            {
                if (Path.GetFileName(directory).Contains("cherry", StringComparison.OrdinalIgnoreCase))
                {
                    AddSearchRoot(searchRoots, directory);
                }
            }
        }
        catch
        {
            // Manual selection remains available.
        }
    }

    private static void AddJsonPathHints(
        string root,
        List<string> candidates,
        HashSet<string> candidateSet,
        HashSet<string> searchRoots)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories).Take(200).ToArray();
        }
        catch
        {
            return;
        }

        foreach (var file in files)
        {
            try
            {
                var info = new FileInfo(file);
                if (info.Length > 2 * 1024 * 1024)
                {
                    continue;
                }

                using var document = JsonDocument.Parse(File.ReadAllText(file));
                foreach (var value in EnumerateStrings(document.RootElement))
                {
                    AddPathHint(value, Path.GetDirectoryName(file), candidates, candidateSet, searchRoots);
                }
            }
            catch
            {
                // Ignore unrelated or malformed JSON files.
            }
        }
    }

    private static IEnumerable<string> EnumerateStrings(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                var stringValue = element.GetString();
                if (!string.IsNullOrWhiteSpace(stringValue))
                {
                    yield return stringValue;
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var nestedValue in EnumerateStrings(item))
                    {
                        yield return nestedValue;
                    }
                }
                break;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    foreach (var nestedValue in EnumerateStrings(property.Value))
                    {
                        yield return nestedValue;
                    }
                }
                break;
        }
    }

    private static void AddPathHint(
        string value,
        string? jsonDirectory,
        List<string> candidates,
        HashSet<string> candidateSet,
        HashSet<string> searchRoots)
    {
        if (value.Length > 1024 || (!value.Contains('\\') && !value.Contains('/') && !value.Contains("%")))
        {
            return;
        }

        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
            if (!Path.IsPathRooted(expanded) && !string.IsNullOrWhiteSpace(jsonDirectory))
            {
                expanded = Path.GetFullPath(Path.Combine(jsonDirectory, expanded));
            }

            if (string.Equals(Path.GetFileName(expanded), DatabaseFileName, StringComparison.OrdinalIgnoreCase))
            {
                AddCandidate(candidates, candidateSet, expanded);
                AddSearchRoot(searchRoots, Path.GetDirectoryName(expanded));
                return;
            }

            if (Directory.Exists(expanded))
            {
                AddSearchRoot(searchRoots, expanded);
                AddDatabaseVariants(candidates, candidateSet, expanded);
            }
        }
        catch
        {
            // Not every JSON string containing a slash is a local path.
        }
    }

    private static IEnumerable<string> EnumerateDatabaseFiles(string root, int maxDepth, int maxDirectories)
    {
        if (!Directory.Exists(root))
        {
            yield break;
        }

        var queue = new Queue<(string Path, int Depth)>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        queue.Enqueue((root, 0));
        var examined = 0;

        while (queue.Count > 0 && examined < maxDirectories)
        {
            var (directory, depth) = queue.Dequeue();
            if (!visited.Add(directory))
            {
                continue;
            }

            examined++;
            string? foundDatabase = null;
            try
            {
                var candidate = Path.Combine(directory, DatabaseFileName);
                if (File.Exists(candidate))
                {
                    foundDatabase = candidate;
                }
            }
            catch
            {
                continue;
            }

            if (foundDatabase is not null)
            {
                yield return foundDatabase;
            }

            if (depth >= maxDepth)
            {
                continue;
            }

            try
            {
                foreach (var child in Directory.EnumerateDirectories(directory))
                {
                    var name = Path.GetFileName(child);
                    if (ShouldSkipDirectory(name))
                    {
                        continue;
                    }

                    queue.Enqueue((child, depth + 1));
                }
            }
            catch
            {
                // Ignore inaccessible folders.
            }
        }
    }

    private static bool ShouldSkipDirectory(string name) => name.Equals("Cache", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Code Cache", StringComparison.OrdinalIgnoreCase)
        || name.Equals("GPUCache", StringComparison.OrdinalIgnoreCase)
        || name.Equals("node_modules", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Temp", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Crashpad", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Service Worker", StringComparison.OrdinalIgnoreCase);

    private static void AddSearchRoot(HashSet<string> roots, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            roots.Add(Path.GetFullPath(path));
        }
        catch
        {
            // Ignore invalid paths.
        }
    }

    private static void AddCandidate(List<string> candidates, HashSet<string> candidateSet, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            var fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
            if (candidateSet.Add(fullPath))
            {
                candidates.Add(fullPath);
            }
        }
        catch
        {
            // Ignore invalid paths.
        }
    }
}
