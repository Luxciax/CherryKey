using System.Diagnostics;
using System.Text.Json;

namespace CherryKey.Services;

/// <summary>
/// Locates Cherry Studio v1 Chromium Local Storage LevelDB and Cherry Studio v2 SQLite data.
/// Discovery is deliberately bounded: known locations are checked first, then a small set of
/// hints, and finally a short breadth-first fallback scan. It must never freeze the WPF UI.
/// </summary>
public sealed class CherryDatabaseLocator
{
    private static readonly string[] AppFolderNames =
    [
        "CherryStudio", "Cherry Studio", "cherry-studio", "cherrystudio",
        "CherryStudio2", "CherryStudio-V2"
    ];

    private static readonly TimeSpan MaximumScanDuration = TimeSpan.FromSeconds(7);
    private readonly AppSettingsStore _settings;

    public CherryDatabaseLocator(AppSettingsStore settings)
    {
        _settings = settings;
    }

    public int LastScanCount { get; private set; }
    public string LastScanSummary { get; private set; } = "尚未扫描";

    public string? Locate(
        CancellationToken cancellationToken = default,
        IReadOnlySet<string>? excludedPaths = null)
    {
        var stopwatch = Stopwatch.StartNew();
        LastScanCount = 0;
        LastScanSummary = "正在扫描";
        AppLog.Write("Data-source discovery started.");

        var candidates = new List<Candidate>();
        var candidateSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var searchRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        // 1. Known deterministic locations. These are checked before any recursive work.
        foreach (var root in new[] { roaming, local, common })
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            foreach (var folderName in AppFolderNames)
            {
                var appRoot = Path.Combine(root, folderName);
                AddSearchRoot(searchRoots, appRoot);
                AddSourceVariants(candidates, candidateSet, appRoot, priority: 0);
                AddPartitionSourceVariants(candidates, candidateSet, searchRoots, appRoot, priority: 3);
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
                AddSourceVariants(candidates, candidateSet, appRoot, priority: 8);
                AddPartitionSourceVariants(candidates, candidateSet, searchRoots, appRoot, priority: 11);
            }
        }

        if (!string.IsNullOrWhiteSpace(documents))
        {
            var appRoot = Path.Combine(documents, "CherryStudio");
            AddSearchRoot(searchRoots, appRoot);
            AddSourceVariants(candidates, candidateSet, appRoot, priority: 10);
        }

        // A saved custom path is useful, but it is tried after the official locations so an old
        // invalid setting cannot hide a valid default v1/v2 data source.
        var savedPath = CherryDataSource.NormalizeSelectedPath(_settings.LoadDataSourcePath());
        AddCandidate(candidates, candidateSet, savedPath, priority: 20);

        var direct = FirstExisting(candidates, excludedPaths, cancellationToken);
        if (direct is not null)
        {
            LastScanSummary = $"快速检查 {LastScanCount} 个已知位置后发现 {CherryDataSource.GetDisplayName(direct)}";
            AppLog.Write($"Data-source discovery found known path: {direct}");
            return direct;
        }

        ThrowIfExpired(stopwatch, cancellationToken);

        // 2. Running-process locations and top-level folders containing "cherry".
        AddRunningCherryLocations(candidates, candidateSet, searchRoots, cancellationToken);
        AddTopLevelCherryDirectories(roaming, searchRoots, cancellationToken);
        AddTopLevelCherryDirectories(local, searchRoots, cancellationToken);

        direct = FirstExisting(candidates, excludedPaths, cancellationToken);
        if (direct is not null)
        {
            LastScanSummary = $"检查 {LastScanCount} 个程序位置后发现 {CherryDataSource.GetDisplayName(direct)}";
            AppLog.Write($"Data-source discovery found process-related path: {direct}");
            return direct;
        }

        ThrowIfExpired(stopwatch, cancellationToken);

        // 3. Read a bounded number of nearby JSON files for relocated userData hints.
        foreach (var root in searchRoots.ToArray())
        {
            ThrowIfExpired(stopwatch, cancellationToken);
            AddJsonPathHintsBounded(
                root,
                candidates,
                candidateSet,
                searchRoots,
                cancellationToken,
                maxDepth: 2,
                maxDirectories: 120,
                maxFiles: 60);
        }

        direct = FirstExisting(candidates, excludedPaths, cancellationToken);
        if (direct is not null)
        {
            LastScanSummary = $"读取迁移配置后发现 {CherryDataSource.GetDisplayName(direct)}";
            AppLog.Write($"Data-source discovery found JSON-hinted path: {direct}");
            return direct;
        }

        ThrowIfExpired(stopwatch, cancellationToken);

        // 4. Last-resort bounded BFS. Never scan an entire drive or thousands of deep folders.
        foreach (var root in searchRoots.OrderBy(path => path.Length).ToArray())
        {
            ThrowIfExpired(stopwatch, cancellationToken);
            foreach (var source in EnumerateDataSources(
                         root,
                         maxDepth: 4,
                         maxDirectories: 500,
                         stopwatch,
                         cancellationToken))
            {
                AddCandidate(candidates, candidateSet, source, priority: 100);
            }
        }

        direct = FirstExisting(candidates, excludedPaths, cancellationToken);
        LastScanSummary = direct is null
            ? $"在 {stopwatch.Elapsed.TotalSeconds:0.0} 秒内检查 {LastScanCount} 个候选位置，未发现 v1/v2 数据源"
            : $"在 {stopwatch.Elapsed.TotalSeconds:0.0} 秒内发现 {CherryDataSource.GetDisplayName(direct)}";
        AppLog.Write($"Data-source discovery completed. Result={direct ?? "<none>"}; {LastScanSummary}");
        return direct;
    }

    private string? FirstExisting(
        IEnumerable<Candidate> candidates,
        IReadOnlySet<string>? excludedPaths,
        CancellationToken cancellationToken)
    {
        foreach (var candidate in candidates
                     .OrderBy(item => item.Priority)
                     .ThenBy(item => item.Path.Length))
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastScanCount++;
            try
            {
                var normalized = CherryDataSource.NormalizeSelectedPath(candidate.Path);
                if (string.IsNullOrWhiteSpace(normalized))
                {
                    continue;
                }

                var fullPath = Path.GetFullPath(normalized);
                if (excludedPaths?.Contains(fullPath) == true)
                {
                    continue;
                }

                if (CherryDataSource.IsValid(fullPath))
                {
                    return fullPath;
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

    private static void AddPartitionSourceVariants(
        List<Candidate> candidates,
        HashSet<string> candidateSet,
        HashSet<string> searchRoots,
        string appRoot,
        int priority)
    {
        var partitionsRoot = Path.Combine(appRoot, "Partitions");
        if (!Directory.Exists(partitionsRoot))
        {
            return;
        }

        try
        {
            var count = 0;
            foreach (var partition in Directory.EnumerateDirectories(
                         partitionsRoot,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                if (++count > 32)
                {
                    break;
                }

                AddSearchRoot(searchRoots, partition);
                AddCandidate(
                    candidates,
                    candidateSet,
                    Path.Combine(partition, "Local Storage", "leveldb"),
                    priority);
                AddCandidate(
                    candidates,
                    candidateSet,
                    Path.Combine(partition, "Data", CherryDataSource.SqliteFileName),
                    priority);
            }
        }
        catch
        {
            // Partition discovery is optional; bounded BFS and manual selection remain available.
        }
    }

    private static void AddRunningCherryLocations(
        List<Candidate> candidates,
        HashSet<string> candidateSet,
        HashSet<string> searchRoots,
        CancellationToken cancellationToken)
    {
        foreach (var process in Process.GetProcesses())
        {
            cancellationToken.ThrowIfCancellationRequested();
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
                AddSourceVariants(candidates, candidateSet, directory, priority: 12);

                var parent = Directory.GetParent(directory);
                if (parent is not null)
                {
                    AddSearchRoot(searchRoots, parent.FullName);
                    AddSourceVariants(candidates, candidateSet, parent.FullName, priority: 13);
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

    private static void AddTopLevelCherryDirectories(
        string root,
        HashSet<string> searchRoots,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return;
        }

        try
        {
            foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
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

    private static void AddJsonPathHintsBounded(
        string root,
        List<Candidate> candidates,
        HashSet<string> candidateSet,
        HashSet<string> searchRoots,
        CancellationToken cancellationToken,
        int maxDepth,
        int maxDirectories,
        int maxFiles)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        var queue = new Queue<(string Path, int Depth)>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        queue.Enqueue((root, 0));
        var directoryCount = 0;
        var fileCount = 0;

        while (queue.Count > 0 && directoryCount < maxDirectories && fileCount < maxFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (directory, depth) = queue.Dequeue();
            if (!visited.Add(directory))
            {
                continue;
            }

            directoryCount++;
            try
            {
                foreach (var file in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (++fileCount > maxFiles)
                    {
                        break;
                    }

                    TryReadJsonPathHints(file, candidates, candidateSet, searchRoots);
                }

                if (depth >= maxDepth)
                {
                    continue;
                }

                foreach (var child in Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly))
                {
                    var name = Path.GetFileName(child);
                    if (!ShouldSkipDirectory(name))
                    {
                        queue.Enqueue((child, depth + 1));
                    }
                }
            }
            catch
            {
                // Ignore inaccessible folders.
            }
        }
    }

    private static void TryReadJsonPathHints(
        string file,
        List<Candidate> candidates,
        HashSet<string> candidateSet,
        HashSet<string> searchRoots)
    {
        try
        {
            var info = new FileInfo(file);
            if (info.Length > 1024 * 1024)
            {
                return;
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
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    foreach (var nested in EnumerateStrings(property.Value))
                    {
                        yield return nested;
                    }
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

    private static IEnumerable<string> EnumerateDataSources(
        string root,
        int maxDepth,
        int maxDirectories,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
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
            ThrowIfExpired(stopwatch, cancellationToken);
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

            try
            {
                foreach (var child in Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly))
                {
                    var name = Path.GetFileName(child);
                    if (!ShouldSkipDirectory(name))
                    {
                        queue.Enqueue((child, depth + 1));
                    }
                }
            }
            catch
            {
                // Ignore inaccessible folders.
            }
        }
    }

    private static void ThrowIfExpired(Stopwatch stopwatch, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (stopwatch.Elapsed > MaximumScanDuration)
        {
            throw new OperationCanceledException("Cherry Studio 数据源扫描超过 7 秒，已停止深度扫描。", null, cancellationToken);
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
