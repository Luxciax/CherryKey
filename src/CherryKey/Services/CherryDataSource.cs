namespace CherryKey.Services;

public enum CherryDataSourceKind
{
    Unknown,
    V1LevelDb,
    V2Sqlite
}

public static class CherryDataSource
{
    public const string SqliteFileName = "cherrystudio.sqlite";
    public const string PersistedStateKey = "persist:cherry-studio";

    public static CherryDataSourceKind GetKind(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return CherryDataSourceKind.Unknown;
        }

        try
        {
            if (File.Exists(path) &&
                string.Equals(Path.GetFileName(path), SqliteFileName, StringComparison.OrdinalIgnoreCase))
            {
                return CherryDataSourceKind.V2Sqlite;
            }

            if (Directory.Exists(path) && IsLevelDbDirectory(path))
            {
                return CherryDataSourceKind.V1LevelDb;
            }
        }
        catch
        {
            // Invalid or inaccessible paths are treated as unknown.
        }

        return CherryDataSourceKind.Unknown;
    }

    public static bool IsValid(string? path) => GetKind(path) != CherryDataSourceKind.Unknown;

    public static bool IsLevelDbDirectory(string path)
    {
        if (!Directory.Exists(path) || !File.Exists(Path.Combine(path, "CURRENT")))
        {
            return false;
        }

        try
        {
            return Directory.EnumerateFiles(path, "MANIFEST-*", SearchOption.TopDirectoryOnly).Any()
                   || Directory.EnumerateFiles(path, "*.ldb", SearchOption.TopDirectoryOnly).Any()
                   || Directory.EnumerateFiles(path, "*.log", SearchOption.TopDirectoryOnly).Any();
        }
        catch
        {
            return false;
        }
    }

    public static string? NormalizeSelectedPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            if (Directory.Exists(fullPath))
            {
                if (IsLevelDbDirectory(fullPath))
                {
                    return fullPath;
                }

                var sqlite = Path.Combine(fullPath, SqliteFileName);
                if (File.Exists(sqlite))
                {
                    return sqlite;
                }

                var dataSqlite = Path.Combine(fullPath, "Data", SqliteFileName);
                if (File.Exists(dataSqlite))
                {
                    return dataSqlite;
                }

                var levelDb = Path.Combine(fullPath, "Local Storage", "leveldb");
                if (IsLevelDbDirectory(levelDb))
                {
                    return levelDb;
                }

                return fullPath;
            }

            if (!File.Exists(fullPath))
            {
                return fullPath;
            }

            if (string.Equals(Path.GetFileName(fullPath), SqliteFileName, StringComparison.OrdinalIgnoreCase))
            {
                return fullPath;
            }

            var extension = Path.GetExtension(fullPath);
            var fileName = Path.GetFileName(fullPath);
            if (string.Equals(fileName, "CURRENT", StringComparison.OrdinalIgnoreCase)
                || string.Equals(fileName, "LOCK", StringComparison.OrdinalIgnoreCase)
                || fileName.StartsWith("MANIFEST-", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".ldb", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".log", StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetDirectoryName(fullPath);
            }

            return fullPath;
        }
        catch
        {
            return path;
        }
    }

    public static string GetDisplayName(string? path) => GetKind(path) switch
    {
        CherryDataSourceKind.V1LevelDb => "Cherry Studio v1 · LevelDB",
        CherryDataSourceKind.V2Sqlite => "Cherry Studio v2 · SQLite",
        _ => "未知数据源"
    };
}
