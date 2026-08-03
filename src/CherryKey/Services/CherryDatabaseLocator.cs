using System.Diagnostics;
using System.Text.Json;

namespace CherryKey.Services;

/// <summary>
/// Locates both Cherry Studio v1 Chromium Local Storage LevelDB and Cherry Studio v2 SQLite data.
/// The class name is kept for source compatibility with older CherryKey builds.
/// </summary>
public sealed class CherryDatabaseLocator
{
    private static readonly string[] AppFolderNames =
    [
        "CherryStudio", "Cherry Studio", "cherry-studio", "cherrystudio",
        "CherryStudio2", "CherryStudio-V2"
    ];

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
        var candidates = new List<Candidate>();
        var candidateSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var searchRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var savedPath = CherryDataSource.NormalizeSelectedPath(_settings.LoadDataSourcePath());
        var savedPriority = CherryDataSource.GetKind(savedPath) == CherryDataSourceKind.V1LevelDb ? 18 : 0;
        AddCandidate(candidates, candidateSet, savedPath, priority: savedPriority);

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

            foreach (var folderName in AppFolderNames)
            {
                var appRoot = Path.Combine(root, folderName);
                AddSearchRoot(searchRoots, appRoot);
                AddSourceVariants(candidates, candidateSet, appRoot, priority: 10);
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
                AddSourceVariants(candidates, candidateSet, appRoot, priority: 25);
            }
        }

        if (!string.IsNullOrWhiteSpace(documents))
        {
            var appRoot = Path.Combine(documents, "CherryStudio");
            AddSearchRoot(searchRoots, appRoot);
            AddSourceVariants(candidates, candidateSet, appRoot, priority: 30);
        }

        AddRunningCherryLocations(candidates, candidateSet, searchRoots);
        AddTopLevelCherryDirectories(roaming, searchRoots);
        AddTopLevelCherryDirectories(local, searchRoots);

        // Cherry Studio can relocate Electron userData. Small preboot/preferences JSON files
        // in the original location may retain the custom path, so use those strings as hints.
        foreach (var root in searchRoots.ToArray())
        {
            AddJsonPathHints(root, candidates, candidateSet, searchRoots);
        }

        var direct = FirstExisting(candidates);
        if (direct is not null)
        {
            LastScanSummary = $"自动检查了 {LastScanCount} 个候选位置，识别为 {CherryDataSource.GetDisplayName(direct)}";
            return direct;
        }

        foreach (var root in searchRoots.OrderBy(path => path.Length))
        {
            foreach (var source in EnumerateDataSources(root, maxDepth: 6, maxDirectories: 3500))
            {
                AddCandidate(candidates, candidateSet, source, priority: 100);
            }
        }

        direct = FirstExisting(candidates);
        LastScanSummary = direct is null
            ? $"已扫描 {LastScanCount} 个候选位置，未发现 v1 LevelDB 或 v2 SQLite"
            : $"自动扫描 {LastScanCount} 个候选位置后发现 {CherryDataSource.GetDisplayName(direct)}";
        return direct;
    }

    private string? FirstExisting(IEnumerable<Candidate> candidates)
    {
        foreach (var candidate in candidates
                     .OrderBy(item => item.Priority)
                     .ThenBy(item => item.Path.Length))
        {
            LastScanCount++;
            try
            {
                var normalized = CherryDataSource.NormalizeSelectedPath(candidate.Path);
                if (CherryDataSource.IsValid(normalized))
                {
                    return Path.GetFullPath(normalized!);
                }
            }
            catch
            {
                // Continue past malformed, missing, or inaccessible candidates.
            }
        }

        return null;
    }

    private static void AddSourceVariants(
        List<Candidate> candidates,
        HashSet<string> candidateSet,
        string root,
        int priority)
    {
        // v2 SQLite. Prefer it when both v1 and v2 data remain after an upgrade.
        AddCandidate(candidates, candidateSet, Path.Combine(root, "Data", CherryDataSource.SqliteFileName), priority);
        AddCandidate(candidates, candidateSet, Path.Combine(root, "data", CherryDataSource.SqliteFileName), priority);
        AddCandidate(candidates, candidateSet, Path.Combine(root, CherryDataSource.SqliteFileName), priority + 1);
        AddCandidate(candidates, candidateSet, Path.Combine(root, "User Data", "Data", CherryDataSource.SqliteFileName), priority + 1);
        AddCandidate(candidates, candidateSet, Path.Combine(root, "data", "Data", CherryDataSource.SqliteFileName), priority + 1);

        // v1 Chromium Local Storage LevelDB.
        AddCandidate(candidates, candidateSet, Path.Combine(root, "Local Storage", "leveldb"), priority + 5);
        AddCandidate(candidates, candidateSet, Path.Combine(root, "local storage", "leveldb"), priority + 5);
        AddCandidate(candidates, candidateSet, Path.Combine(root, "User Data", "Default", "Local Storage", "leveldb"), priority + 6);
        AddCandidate(candidates, candidateSet, Path.Combine(root, "data", "Local Storage", "leveldb"), priority + 6);
    }

    private static void AddRunningCherryLocations(
        List<Candidate> candidates,
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
                var directory = string.IsNullOrWhiteSpace(executable) ? null : Path.GetDirectoryName(executable);
                if (string.IsNullOrWhiteSpace(directory))
                {
                    continue;
                }

                AddSearchRoot(searchRoots, directory);
                AddSourceVariants(candidates, candidateSet, directory, priority: 15);

                var parent = Directory.GetParent(directory);
                if (parent is not null)
                {
                    AddSearchRoot(searchRoots, parent.FullName);
                    AddSourceVariants(candidates, candidateSet, parent.FullName, priority: 16);
                }
            }
            catch
            {
                // Access to another process module can be denied; this is only an optional hint.
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
        List<Candidate> candidates,
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
                // Ignore unrelated or malformed JSON.
            }
        }
    }

    private static IEnumerable<string> EnumerateStrings(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                var value = element.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    yield return value;
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var nested in EnumerateStrings(item))
                    {
                        yield return nested;
                    }
                }
                break;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    foreach (var nested in EnumerateStrings(property.Value))
                    {
                        yield return nested;
                    }
                }
                break;
        }
    }

    private static void AddPathHint(
        string value,
        string? jsonDirectory,
        List<Candidate> candidates,
        HashSet<string> candidateSet,
        HashSet<string> searchRoots)
    {
        if (value.Length is < 3 or > 1024)
        {
            return;
        }

        var expanded = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
        if (!Path.IsPathFullyQualified(expanded) && !string.IsNullOrWhiteSpace(jsonDirectory))
        {
            try
            {
                expanded = Path.GetFullPath(Path.Combine(jsonDirectory, expanded));
            }
            catch
            {
                return;
            }
        }

        if (!Path.IsPathFullyQualified(expanded))
        {
            return;
        }

        if (string.Equals(Path.GetFileName(expanded), CherryDataSource.SqliteFileName, StringComparison.OrdinalIgnoreCase))
        {
            AddCandidate(candidates, candidateSet, expanded, priority: 5);
            return;
        }

        AddSearchRoot(searchRoots, expanded);
        AddSourceVariants(candidates, candidateSet, expanded, priority: 5);
    }

    private static IEnumerable<string> EnumerateDataSources(string root, int maxDepth, int maxDirectories)
    {
        if (!Directory.Exists(root))
        {
            yield break;
        }

        var queue = new Queue<(string Path, int Depth)>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        queue.Enqueue((root, 0));
        var directoryCount = 0;

        while (queue.Count > 0 && directoryCount < maxDirectories)
        {
            var (directory, depth) = queue.Dequeue();
            if (!visited.Add(directory))
            {
                continue;
            }

            directoryCount++;

            var sqlite = Path.Combine(directory, CherryDataSource.SqliteFileName);
            if (File.Exists(sqlite))
            {
                yield return sqlite;
            }

            if (string.Equals(Path.GetFileName(directory), "leveldb", StringComparison.OrdinalIgnoreCase)
                && CherryDataSource.IsLevelDbDirectory(directory))
            {
                yield return directory;
                continue;
            }

            if (depth >= maxDepth)
            {
                continue;
            }

            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly).ToArray();
            }
            catch
            {
                continue;
            }

            foreach (var child in children)
            {
                var name = Path.GetFileName(child);
                if (ShouldSkipDirectory(name))
                {
                    continue;
                }

                queue.Enqueue((child, depth + 1));
            }
        }
    }

    private static bool ShouldSkipDirectory(string name) => name.Equals("Cache", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Code Cache", StringComparison.OrdinalIgnoreCase)
        || name.Equals("GPUCache", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Crashpad", StringComparison.OrdinalIgnoreCase)
        || name.Equals("logs", StringComparison.OrdinalIgnoreCase)
        || name.Equals("node_modules", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Service Worker", StringComparison.OrdinalIgnoreCase);

    private static void AddCandidate(
        List<Candidate> candidates,
        HashSet<string> candidateSet,
        string? path,
        int priority)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            var normalized = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
            if (candidateSet.Add(normalized))
            {
                candidates.Add(new Candidate(normalized, priority));
            }
        }
        catch
        {
            // Ignore malformed hints.
        }
    }

    private static void AddSearchRoot(HashSet<string> roots, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            roots.Add(Path.GetFullPath(Environment.ExpandEnvironmentVariables(path)));
        }
        catch
        {
            // Ignore malformed hints.
        }
    }

    private sealed record Candidate(string Path, int Priority);
}
