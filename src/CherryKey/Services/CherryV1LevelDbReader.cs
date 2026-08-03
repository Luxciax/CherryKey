using System.Text;
using System.Text.Json;
using CherryKey.Models;
using LevelDB;

namespace CherryKey.Services;

/// <summary>
/// Reads Cherry Studio v1 Redux-Persist state from Chromium Local Storage LevelDB.
/// The live database is copied to a temporary read-only snapshot first, so Cherry Studio
/// may remain open and its LOCK file is never modified.
/// </summary>
public sealed class CherryV1LevelDbReader
{
    private const int MaxSnapshotAttempts = 3;
    private const string LegacyRootKey = "persist:root";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public IReadOnlyList<ProviderRecord> Read(string levelDbDirectory)
    {
        if (!CherryDataSource.IsLevelDbDirectory(levelDbDirectory))
        {
            throw new InvalidDataException("所选目录不是有效的 Cherry Studio v1 Local Storage LevelDB。请选择 Local Storage\\leveldb 目录。");
        }

        var snapshotPath = CreateSnapshot(levelDbDirectory);
        try
        {
            var candidates = ReadPersistedStateCandidates(snapshotPath);
            Exception? lastParseError = null;

            foreach (var candidate in candidates
                         .OrderByDescending(item => item.Score)
                         .ThenBy(item => item.ScriptKey, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var providers = ParseProviders(candidate.Json);
                    foreach (var provider in providers)
                    {
                        provider.SelectDefaults();
                    }

                    AppLog.Write(
                        $"Cherry v1 Redux state parsed. Key={SanitizeLogValue(candidate.ScriptKey)}; " +
                        $"Origin={SanitizeLogValue(candidate.Origin)}; Encoding={candidate.Encoding}; " +
                        $"Providers={providers.Count}.");

                    return providers
                        .OrderByDescending(provider => provider.IsEnabled)
                        .ThenBy(provider => provider.Name, StringComparer.CurrentCultureIgnoreCase)
                        .ToArray();
                }
                catch (Exception ex) when (ex is JsonException or InvalidDataException)
                {
                    lastParseError = ex;
                    AppLog.Write(
                        $"Rejected Cherry v1 persisted-state candidate. " +
                        $"Key={SanitizeLogValue(candidate.ScriptKey)}; Origin={SanitizeLogValue(candidate.Origin)}; " +
                        $"Encoding={candidate.Encoding}; JsonLength={candidate.Json.Length}.",
                        ex);
                }
            }

            var candidateKeys = candidates
                .Select(item => item.ScriptKey)
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .Select(SanitizeLogValue)
                .ToArray();

            var detail = candidateKeys.Length == 0
                ? "没有发现任何可识别的 Redux Persist 项"
                : $"发现候选键：{string.Join("、", candidateKeys)}";

            throw new InvalidDataException(
                $"已找到 Cherry Studio v1 LevelDB，但无法解析供应商数据；{detail}。" +
                "请确认这是实际使用中的 Local Storage\\leveldb，而不是旧目录或其他分区。",
                lastParseError);
        }
        finally
        {
            TryDeleteDirectory(snapshotPath);
        }
    }

    public static void RunDecoderSelfTest()
    {
        const string scriptKey = "persist:cherry-studio";
        const string persisted = "{\"llm\":\"{\\\"providers\\\":[{\\\"id\\\":\\\"self-test\\\",\\\"name\\\":\\\"Self Test\\\",\\\"apiKey\\\":\\\"not-a-real-key\\\",\\\"models\\\":[{\\\"id\\\":\\\"test-model\\\"}]}]}\",\"_persist\":\"{\\\"version\\\":1,\\\"rehydrated\\\":true}\"}";

        var originBytes = Encoding.UTF8.GetBytes("file://");
        var keyBytes = new byte[1 + originBytes.Length + 1 + 1 + scriptKey.Length];
        keyBytes[0] = (byte)'_';
        originBytes.CopyTo(keyBytes, 1);
        var separator = 1 + originBytes.Length;
        keyBytes[separator] = 0;
        keyBytes[separator + 1] = 1;
        Encoding.Latin1.GetBytes(scriptKey).CopyTo(keyBytes, separator + 2);

        var valuePayload = Encoding.Latin1.GetBytes(persisted);
        var valueBytes = new byte[valuePayload.Length + 1];
        valueBytes[0] = 1;
        valuePayload.CopyTo(valueBytes, 1);

        if (!TryDecodeLocalStorageEntry(
                keyBytes,
                valueBytes,
                out _,
                out var decodedKey,
                out var decodedValue,
                out _)
            || !string.Equals(decodedKey, scriptKey, StringComparison.Ordinal)
            || !TryExtractPersistedJson(decodedValue, out var json)
            || ParseProviders(json).Count != 1)
        {
            throw new InvalidOperationException("Cherry Studio v1 Local Storage parser self-test failed.");
        }
    }

    private static string CreateSnapshot(string sourceDirectory)
    {
        Exception? lastError = null;

        for (var attempt = 1; attempt <= MaxSnapshotAttempts; attempt++)
        {
            var destination = Path.Combine(
                Path.GetTempPath(),
                "CherryKey",
                "leveldb-snapshots",
                $"{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}");

            try
            {
                Directory.CreateDirectory(destination);
                var currentBefore = TryReadCurrentManifest(sourceDirectory);
                CopyLevelDbFiles(sourceDirectory, destination);
                var currentAfter = TryReadCurrentManifest(sourceDirectory);

                if (!string.Equals(currentBefore, currentAfter, StringComparison.Ordinal))
                {
                    throw new IOException("Cherry Studio 正在切换 LevelDB 清单，快照将自动重试。");
                }

                if (!CherryDataSource.IsLevelDbDirectory(destination))
                {
                    throw new IOException("LevelDB 快照不完整，可能正好遇到 Cherry Studio 写入。稍后会自动重试。");
                }

                if (!string.IsNullOrWhiteSpace(currentAfter)
                    && !File.Exists(Path.Combine(destination, currentAfter)))
                {
                    throw new IOException("LevelDB 快照缺少 CURRENT 指向的 MANIFEST，稍后会自动重试。");
                }

                return destination;
            }
            catch (Exception ex)
            {
                lastError = ex;
                TryDeleteDirectory(destination);
                if (attempt < MaxSnapshotAttempts)
                {
                    Thread.Sleep(180 * attempt);
                }
            }
        }

        throw new IOException(
            "无法创建 Cherry Studio v1 数据快照。请先关闭 Cherry Studio 后再刷新，或检查 Local Storage\\leveldb 的读取权限。",
            lastError);
    }

    private static string? TryReadCurrentManifest(string sourceDirectory)
    {
        try
        {
            var currentPath = Path.Combine(sourceDirectory, "CURRENT");
            return File.Exists(currentPath)
                ? File.ReadAllText(currentPath, Encoding.ASCII).Trim()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static void CopyLevelDbFiles(string sourceDirectory, string destinationDirectory)
    {
        foreach (var sourcePath in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            var fileName = Path.GetFileName(sourcePath);
            if (string.Equals(fileName, "LOCK", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var destinationPath = Path.Combine(destinationDirectory, fileName);
            CopySharedFile(sourcePath, destinationPath);
        }
    }

    private static void CopySharedFile(string sourcePath, string destinationPath)
    {
        const int bufferSize = 128 * 1024;
        using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize,
            FileOptions.SequentialScan);
        using var destination = new FileStream(
            destinationPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize,
            FileOptions.SequentialScan);
        source.CopyTo(destination, bufferSize);
    }

    private static IReadOnlyList<PersistedStateCandidate> ReadPersistedStateCandidates(string snapshotPath)
    {
        try
        {
            return ReadPersistedStateCandidatesCore(snapshotPath);
        }
        catch (Exception ex) when (IsLevelDbCorruption(ex))
        {
            AppLog.Write("LevelDB snapshot scan failed; attempting repair of the temporary snapshot.", ex);

            var repairOptions = new Options { CreateIfMissing = false };
            DB.Repair(repairOptions, snapshotPath);
            return ReadPersistedStateCandidatesCore(snapshotPath);
        }
    }

    private static IReadOnlyList<PersistedStateCandidate> ReadPersistedStateCandidatesCore(string snapshotPath)
    {
        var options = new Options { CreateIfMissing = false };
        using var database = new DB(options, snapshotPath);
        using var iterator = database.CreateIterator();

        var candidates = new List<PersistedStateCandidate>();
        var seenJson = new HashSet<string>(StringComparer.Ordinal);
        var persistKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var totalRecords = 0;
        var localStorageRecords = 0;

        for (iterator.SeekToFirst(); iterator.IsValid(); iterator.Next())
        {
            totalRecords++;
            var keyBytes = iterator.Key();
            var valueBytes = iterator.Value();

            if (TryDecodeLocalStorageEntry(
                    keyBytes,
                    valueBytes,
                    out var origin,
                    out var scriptKey,
                    out var value,
                    out var encoding))
            {
                localStorageRecords++;
                if (scriptKey.StartsWith("persist:", StringComparison.OrdinalIgnoreCase))
                {
                    persistKeys.Add(scriptKey);
                }

                if (TryExtractPersistedJson(value, out var json) && seenJson.Add(json))
                {
                    candidates.Add(new PersistedStateCandidate(
                        ScoreScriptKey(scriptKey, json),
                        scriptKey,
                        origin,
                        encoding,
                        json));
                }

                continue;
            }

            // Compatibility fallback for old Chromium/Electron variants or third-party LevelDB
            // wrappers that expose a slightly different binary prefix. Values are still accepted
            // only when they structurally resemble Cherry's Redux state.
            var keyText = DecodeTextCandidates(keyBytes).FirstOrDefault(text =>
                text.Contains("persist:", StringComparison.OrdinalIgnoreCase)) ?? string.Empty;

            foreach (var decodedValue in DecodeTextCandidates(valueBytes))
            {
                if (!TryExtractPersistedJson(decodedValue, out var json) || !seenJson.Add(json))
                {
                    continue;
                }

                var inferredKey = ExtractPersistKey(keyText) ?? "<structural-fallback>";
                if (inferredKey.StartsWith("persist:", StringComparison.OrdinalIgnoreCase))
                {
                    persistKeys.Add(inferredKey);
                }

                candidates.Add(new PersistedStateCandidate(
                    ScoreScriptKey(inferredKey, json),
                    inferredKey,
                    "<unknown>",
                    "fallback",
                    json));
            }
        }

        AppLog.Write(
            $"Cherry v1 LevelDB scan completed. Records={totalRecords}; " +
            $"LocalStorageEntries={localStorageRecords}; PersistKeys={persistKeys.Count}; " +
            $"Candidates={candidates.Count}; Keys=[{string.Join(", ", persistKeys.Take(8).Select(SanitizeLogValue))}].");

        if (candidates.Count == 0)
        {
            var keySummary = persistKeys.Count == 0
                ? "未发现 persist:* 项"
                : $"发现 {string.Join("、", persistKeys.Take(8).Select(SanitizeLogValue))}，但值不是可识别的 JSON";

            throw new InvalidDataException(
                $"LevelDB 可正常打开，但{keySummary}。" +
                "这通常表示选中了旧的 Local Storage 目录、Cherry Studio 使用了其他 Storage Partition，" +
                "或该版本尚未把 Redux 状态写入此目录。");
        }

        return candidates;
    }

    private static bool IsLevelDbCorruption(Exception exception)
    {
        return exception.Message.Contains("corruption", StringComparison.OrdinalIgnoreCase)
            || exception.Message.Contains("corrupt", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryDecodeLocalStorageEntry(
        byte[] keyBytes,
        byte[] valueBytes,
        out string origin,
        out string scriptKey,
        out string value,
        out string encoding)
    {
        origin = string.Empty;
        scriptKey = string.Empty;
        value = string.Empty;
        encoding = string.Empty;

        // Chromium localStorage schema:
        //   key   = "_" + serialized origin + '\0' + encoded script key
        //   value = encoded script value
        if (keyBytes.Length < 4 || keyBytes[0] != (byte)'_')
        {
            return false;
        }

        var separator = Array.IndexOf(keyBytes, (byte)0, 1);
        if (separator <= 1 || separator >= keyBytes.Length - 1)
        {
            return false;
        }

        try
        {
            origin = StrictUtf8.GetString(keyBytes, 1, separator - 1);
        }
        catch (DecoderFallbackException)
        {
            origin = Encoding.Latin1.GetString(keyBytes, 1, separator - 1);
        }

        if (!TryDecodeDomStorageString(keyBytes.AsSpan(separator + 1), out scriptKey, out var keyEncoding)
            || !TryDecodeDomStorageString(valueBytes, out value, out var valueEncoding))
        {
            return false;
        }

        encoding = $"key:{keyEncoding}/value:{valueEncoding}";
        return true;
    }

    private static bool TryDecodeDomStorageString(
        ReadOnlySpan<byte> bytes,
        out string value,
        out string encoding)
    {
        value = string.Empty;
        encoding = string.Empty;
        if (bytes.IsEmpty)
        {
            return false;
        }

        try
        {
            switch (bytes[0])
            {
                case 0: // Chromium kUTF16Format: UTF-16LE bytes after the marker.
                    if (bytes.Length <= 1)
                    {
                        value = string.Empty;
                        encoding = "utf16le";
                        return true;
                    }

                    var utf16Length = bytes.Length - 1;
                    if ((utf16Length & 1) != 0)
                    {
                        utf16Length--;
                    }

                    value = Encoding.Unicode.GetString(bytes.Slice(1, utf16Length));
                    encoding = "utf16le";
                    return true;

                case 1: // Chromium kLatin1Format. This is NOT UTF-8.
                    value = Encoding.Latin1.GetString(bytes[1..]);
                    encoding = "latin1";
                    return true;

                default:
                    try
                    {
                        value = StrictUtf8.GetString(bytes);
                        encoding = "utf8-unmarked";
                    }
                    catch (DecoderFallbackException)
                    {
                        value = Encoding.Latin1.GetString(bytes);
                        encoding = "latin1-unmarked";
                    }
                    return true;
            }
        }
        catch
        {
            value = string.Empty;
            encoding = string.Empty;
            return false;
        }
    }

    private static int ScoreScriptKey(string scriptKey, string json)
    {
        if (string.Equals(scriptKey, CherryDataSource.PersistedStateKey, StringComparison.OrdinalIgnoreCase))
        {
            return 100;
        }

        if (string.Equals(scriptKey, LegacyRootKey, StringComparison.OrdinalIgnoreCase))
        {
            return 90;
        }

        if (scriptKey.StartsWith("persist:", StringComparison.OrdinalIgnoreCase))
        {
            return 80;
        }

        if (json.Contains("\\\"llm\\\"", StringComparison.OrdinalIgnoreCase)
            || json.Contains("\"llm\"", StringComparison.OrdinalIgnoreCase)
            || json.Contains("\"providers\"", StringComparison.OrdinalIgnoreCase))
        {
            return 50;
        }

        return 10;
    }

    private static string? ExtractPersistKey(string text)
    {
        var start = text.IndexOf("persist:", StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return null;
        }

        var end = start;
        while (end < text.Length)
        {
            var character = text[end];
            if (char.IsControl(character) || char.IsWhiteSpace(character) || character is '"' or '\'' or '\\')
            {
                break;
            }
            end++;
        }

        return end > start ? text[start..end] : null;
    }

    private static bool TryExtractPersistedJson(string value, out string json)
    {
        json = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var cleaned = value.Trim('\0', '\uFEFF', '\u0001', '\u0002', '\u0003', ' ', '\r', '\n', '\t');
        foreach (var candidate in EnumerateJsonCandidates(cleaned))
        {
            try
            {
                using var document = JsonDocument.Parse(candidate);
                if (LooksLikeCherryPersistedState(document.RootElement))
                {
                    json = candidate;
                    return true;
                }
            }
            catch (JsonException)
            {
                // Try the next decoding/extraction candidate.
            }
        }

        return false;
    }

    private static IEnumerable<string> EnumerateJsonCandidates(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            yield break;
        }

        yield return text;

        foreach (var opening in new[] { '{', '[' })
        {
            var start = text.IndexOf(opening);
            if (start < 0 || !TryTakeBalancedJson(text, start, out var balanced))
            {
                continue;
            }

            if (!string.Equals(balanced, text, StringComparison.Ordinal))
            {
                yield return balanced;
            }
        }
    }

    private static bool TryTakeBalancedJson(string text, int start, out string json)
    {
        json = string.Empty;
        if (start < 0 || start >= text.Length || text[start] is not ('{' or '['))
        {
            return false;
        }

        var stack = new Stack<char>();
        var inString = false;
        var escaped = false;

        for (var index = start; index < text.Length; index++)
        {
            var character = text[index];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (character == '\\')
                {
                    escaped = true;
                }
                else if (character == '"')
                {
                    inString = false;
                }
                continue;
            }

            if (character == '"')
            {
                inString = true;
                continue;
            }

            if (character is '{' or '[')
            {
                stack.Push(character);
                continue;
            }

            if (character is not ('}' or ']'))
            {
                continue;
            }

            if (stack.Count == 0)
            {
                return false;
            }

            var opening = stack.Pop();
            if ((opening == '{' && character != '}') || (opening == '[' && character != ']'))
            {
                return false;
            }

            if (stack.Count == 0)
            {
                json = text[start..(index + 1)];
                return true;
            }
        }

        return false;
    }

    private static bool LooksLikeCherryPersistedState(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.String)
        {
            var nested = root.GetString();
            if (string.IsNullOrWhiteSpace(nested))
            {
                return false;
            }

            try
            {
                using var nestedDocument = JsonDocument.Parse(nested);
                return LooksLikeCherryPersistedState(nestedDocument.RootElement);
            }
            catch (JsonException)
            {
                return false;
            }
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        return TryGetProperty(root, "llm", out _)
               || TryGetProperty(root, "providers", out _)
               || (TryGetProperty(root, "_persist", out _) && root.EnumerateObject().Any());
    }

    private static IEnumerable<string> DecodeTextCandidates(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var attempts = new List<(int Offset, Encoding Encoding)>();

        if (bytes[0] == 0 && bytes.Length > 1)
        {
            attempts.Add((1, Encoding.Unicode));
        }
        else if (bytes[0] == 1 && bytes.Length > 1)
        {
            attempts.Add((1, Encoding.Latin1));
        }

        attempts.Add((0, Encoding.UTF8));
        attempts.Add((0, Encoding.Latin1));
        if (bytes.Length % 2 == 0)
        {
            attempts.Add((0, Encoding.Unicode));
            attempts.Add((0, Encoding.BigEndianUnicode));
        }
        if (bytes.Length > 1)
        {
            attempts.Add((1, Encoding.UTF8));
            attempts.Add((1, Encoding.Latin1));
        }

        foreach (var attempt in attempts)
        {
            var value = TryDecode(bytes, attempt.Offset, attempt.Encoding);
            if (!string.IsNullOrWhiteSpace(value) && seen.Add(value))
            {
                yield return value;
            }
        }
    }

    private static string? TryDecode(byte[] bytes, int offset, Encoding encoding)
    {
        try
        {
            return encoding.GetString(bytes, offset, bytes.Length - offset);
        }
        catch
        {
            return null;
        }
    }

    private static string SanitizeLogValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "<empty>";
        }

        var cleaned = new string(value
            .Where(character => !char.IsControl(character))
            .Take(120)
            .ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "<binary>" : cleaned;
    }

    private sealed record PersistedStateCandidate(
        int Score,
        string ScriptKey,
        string Origin,
        string Encoding,
        string Json);

    private static IReadOnlyList<ProviderRecord> ParseProviders(string persistedJson)
    {
        using var rootDocument = JsonDocument.Parse(persistedJson);
        return ParseRootElement(rootDocument.RootElement);
    }

    private static IReadOnlyList<ProviderRecord> ParseRootElement(JsonElement root)
    {
        if (TryParseRootElement(root, depth: 0, out var providers))
        {
            return providers;
        }

        throw new InvalidDataException(
            "Cherry Studio v1 持久化状态中没有找到可识别的 llm.providers。" +
            "可能是 Redux 结构发生变化，详细候选信息已写入 startup.log。");
    }

    private static bool TryParseRootElement(
        JsonElement root,
        int depth,
        out IReadOnlyList<ProviderRecord> providers)
    {
        providers = [];
        if (depth > 4)
        {
            return false;
        }

        if (root.ValueKind == JsonValueKind.String)
        {
            var nested = root.GetString();
            if (string.IsNullOrWhiteSpace(nested) || !TryParseJson(nested, out var nestedDocument))
            {
                return false;
            }

            using (nestedDocument)
            {
                return TryParseRootElement(nestedDocument.RootElement, depth + 1, out providers);
            }
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (TryGetProperty(root, "llm", out var llm))
        {
            try
            {
                providers = ParseLlmElement(llm);
                return providers.Count > 0;
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            {
                AppLog.Write("Found llm state but its providers shape was not recognized.", ex);
            }
        }

        if (TryGetProperty(root, "providers", out var directProviders))
        {
            try
            {
                providers = directProviders.ValueKind == JsonValueKind.String
                    ? ParseNestedProviderCollection(directProviders.GetString())
                    : ParseProviderCollection(directProviders);
                return providers.Count > 0;
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            {
                AppLog.Write("Found direct providers state but its shape was not recognized.", ex);
            }
        }

        // Compatibility with wrappers introduced by imports, backups, or older Redux layouts.
        foreach (var preferredName in new[] { "state", "data", "redux", "persistedState", "store" })
        {
            if (TryGetProperty(root, preferredName, out var wrapped)
                && TryParseRootElement(wrapped, depth + 1, out providers))
            {
                return true;
            }
        }

        // Last structural fallback: recursively inspect a bounded number of object/string fields.
        var inspected = 0;
        foreach (var property in root.EnumerateObject())
        {
            if (++inspected > 40
                || string.Equals(property.Name, "_persist", StringComparison.OrdinalIgnoreCase)
                || property.Value.ValueKind is not (JsonValueKind.Object or JsonValueKind.String))
            {
                continue;
            }

            if (TryParseRootElement(property.Value, depth + 1, out providers))
            {
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<ProviderRecord> ParseNestedProviderCollection(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        using var document = JsonDocument.Parse(json);
        return ParseProviderCollection(document.RootElement);
    }

    private static IReadOnlyList<ProviderRecord> ParseNestedJson(string? json, string label)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidDataException($"{label}为空。");
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return label.Contains("llm", StringComparison.OrdinalIgnoreCase)
                ? ParseLlmElement(document.RootElement)
                : ParseRootElement(document.RootElement);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"无法解析{label}。", ex);
        }
    }

    private static IReadOnlyList<ProviderRecord> ParseLlmElement(JsonElement llm)
    {
        if (llm.ValueKind == JsonValueKind.String)
        {
            return ParseNestedJson(llm.GetString(), "llm 状态");
        }

        if (llm.ValueKind != JsonValueKind.Object || !TryGetProperty(llm, "providers", out var providersElement))
        {
            throw new InvalidDataException("Cherry Studio v1 的 llm 状态中没有 providers 列表。");
        }

        if (providersElement.ValueKind == JsonValueKind.String)
        {
            var nested = providersElement.GetString();
            if (string.IsNullOrWhiteSpace(nested))
            {
                return [];
            }

            using var providersDocument = JsonDocument.Parse(nested);
            return ParseProviderCollection(providersDocument.RootElement);
        }

        return ParseProviderCollection(providersElement);
    }

    private static IReadOnlyList<ProviderRecord> ParseProviderCollection(JsonElement providersElement)
    {
        var providers = new List<ProviderRecord>();

        if (providersElement.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in providersElement.EnumerateArray())
            {
                index++;
                var provider = ParseProvider(item, $"provider-{index}");
                if (provider is not null)
                {
                    providers.Add(provider);
                }
            }
        }
        else if (providersElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in providersElement.EnumerateObject())
            {
                var provider = ParseProvider(property.Value, property.Name);
                if (provider is not null)
                {
                    providers.Add(provider);
                }
            }
        }

        if (providers.Count == 0)
        {
            throw new InvalidDataException("已读取 Cherry Studio v1 状态，但 providers 列表为空或格式无法识别。");
        }

        return providers;
    }

    private static ProviderRecord? ParseProvider(JsonElement element, string fallbackId)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            var nested = element.GetString();
            if (string.IsNullOrWhiteSpace(nested))
            {
                return null;
            }

            try
            {
                using var document = JsonDocument.Parse(nested);
                return ParseProvider(document.RootElement, fallbackId);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var id = GetString(element, "id", "providerId", "provider_id") ?? fallbackId;
        var type = GetString(element, "type", "providerType", "provider_type") ?? id;
        var name = GetString(element, "name", "displayName", "display_name") ?? id;

        var apiHost = GetString(element, "apiHost", "api_host", "baseUrl", "base_url", "apiUrl", "api_url") ?? string.Empty;
        var anthropicHost = GetString(element, "anthropicApiHost", "anthropic_api_host") ?? string.Empty;
        if (type.Contains("anthropic", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(anthropicHost))
        {
            apiHost = anthropicHost;
        }

        var auth = InferAuth(type);
        var provider = new ProviderRecord
        {
            Id = id,
            PresetProviderId = null,
            Name = name,
            BaseUrl = apiHost,
            DefaultEndpoint = type,
            EndpointTypes = type,
            AuthType = auth.Type,
            AuthHeader = auth.Header,
            AuthPrefix = auth.Prefix,
            Notes = GetString(element, "notes", "description") ?? string.Empty,
            IsEnabled = GetBoolean(element, true, "enabled", "isEnabled", "is_enabled")
        };

        foreach (var key in ParseApiKeys(element))
        {
            provider.ApiKeys.Add(key);
        }

        foreach (var model in ParseModels(element, type))
        {
            provider.Models.Add(model);
        }

        return provider;
    }

    private static IEnumerable<ApiKeyRecord> ParseApiKeys(JsonElement provider)
    {
        var values = new List<(string Value, string? Label, bool Enabled)>();
        foreach (var propertyName in new[] { "apiKey", "apiKeys", "api_key", "api_keys", "keys" })
        {
            if (TryGetProperty(provider, propertyName, out var element))
            {
                CollectApiKeys(element, values, propertyName, enabled: true);
            }
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var item in values)
        {
            var value = item.Value.Trim();
            if (string.IsNullOrWhiteSpace(value) || !seen.Add(value))
            {
                continue;
            }

            index++;
            yield return new ApiKeyRecord
            {
                Id = $"v1-key-{index}",
                Key = value,
                Label = string.IsNullOrWhiteSpace(item.Label)
                    ? (index == 1 ? "主 Key" : $"Key {index}")
                    : item.Label!,
                IsEnabled = item.Enabled
            };
        }
    }

    private static void CollectApiKeys(
        JsonElement element,
        List<(string Value, string? Label, bool Enabled)> values,
        string? inheritedLabel,
        bool enabled)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                var text = element.GetString();
                if (string.IsNullOrWhiteSpace(text))
                {
                    return;
                }

                var trimmed = text.Trim();
                if ((trimmed.StartsWith('[') || trimmed.StartsWith('{') || trimmed.StartsWith('"'))
                    && TryParseJson(trimmed, out var nestedDocument))
                {
                    using (nestedDocument)
                    {
                        CollectApiKeys(nestedDocument.RootElement, values, inheritedLabel, enabled);
                    }
                    return;
                }

                foreach (var part in SplitLegacyKeys(trimmed))
                {
                    values.Add((part, inheritedLabel, enabled));
                }
                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    CollectApiKeys(item, values, inheritedLabel, enabled);
                }
                break;

            case JsonValueKind.Object:
                var key = GetString(element, "key", "apiKey", "api_key", "value", "token");
                var label = GetString(element, "label", "name", "title") ?? inheritedLabel;
                var itemEnabled = GetBoolean(element, enabled, "enabled", "isEnabled", "is_enabled");
                if (!string.IsNullOrWhiteSpace(key))
                {
                    values.Add((key, label, itemEnabled));
                    return;
                }

                foreach (var property in element.EnumerateObject())
                {
                    CollectApiKeys(property.Value, values, property.Name, itemEnabled);
                }
                break;
        }
    }

    private static IEnumerable<string> SplitLegacyKeys(string value)
    {
        string[] parts = value.Contains('\n') || value.Contains('\r') || value.Contains(',')
            ? value.Split([',', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [value];

        return parts.Where(part => !string.IsNullOrWhiteSpace(part));
    }

    private static IEnumerable<ModelRecord> ParseModels(JsonElement provider, string providerType)
    {
        if (!TryGetProperty(provider, "models", out var modelsElement))
        {
            yield break;
        }

        if (modelsElement.ValueKind == JsonValueKind.String)
        {
            var nested = modelsElement.GetString();
            if (string.IsNullOrWhiteSpace(nested) || !TryParseJson(nested, out var document))
            {
                yield break;
            }

            using (document)
            {
                foreach (var model in ParseModelCollection(document.RootElement, providerType))
                {
                    yield return model;
                }
            }
            yield break;
        }

        foreach (var model in ParseModelCollection(modelsElement, providerType))
        {
            yield return model;
        }
    }

    private static IEnumerable<ModelRecord> ParseModelCollection(JsonElement element, string providerType)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var model = ParseModel(item, null, providerType);
                if (model is not null)
                {
                    yield return model;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                var model = ParseModel(property.Value, property.Name, providerType);
                if (model is not null)
                {
                    yield return model;
                }
            }
        }
    }

    private static ModelRecord? ParseModel(JsonElement element, string? fallbackId, string providerType)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            var value = element.GetString();
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            return new ModelRecord
            {
                Id = value,
                Name = value,
                EndpointTypes = providerType,
                IsEnabled = true
            };
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var id = GetString(element, "id", "modelId", "model_id", "value") ?? fallbackId;
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var modelType = GetString(element, "type", "endpointType", "endpoint_type") ?? providerType;
        var disabled = GetBoolean(element, false, "disabled", "isDisabled", "is_disabled");
        return new ModelRecord
        {
            Id = id,
            Name = GetString(element, "name", "displayName", "display_name") ?? id,
            Description = GetString(element, "description", "group") ?? string.Empty,
            EndpointTypes = modelType,
            IsEnabled = GetBoolean(element, !disabled, "enabled", "isEnabled", "is_enabled") && !disabled,
            IsHidden = GetBoolean(element, false, "hidden", "isHidden", "is_hidden"),
            IsDeprecated = GetBoolean(element, false, "deprecated", "isDeprecated", "is_deprecated"),
            Notes = GetString(element, "notes") ?? string.Empty
        };
    }

    private static AuthData InferAuth(string providerType)
    {
        var normalized = providerType.ToLowerInvariant();
        if (normalized.Contains("anthropic"))
        {
            return new AuthData("api-key", "x-api-key", string.Empty);
        }

        if (normalized.Contains("gemini") || normalized.Contains("google"))
        {
            return new AuthData("api-key", "x-goog-api-key", string.Empty);
        }

        if (normalized.Contains("azure"))
        {
            return new AuthData("api-key", "api-key", string.Empty);
        }

        return new AuthData("api-key", "Authorization", "Bearer");
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static string? GetString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetProperty(element, name, out var value))
            {
                continue;
            }

            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.ToString(),
                _ => null
            };
        }

        return null;
    }

    private static bool GetBoolean(JsonElement element, bool fallback, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetProperty(element, name, out var value))
            {
                continue;
            }

            if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                return value.GetBoolean();
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            {
                return number != 0;
            }

            if (value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString();
                if (bool.TryParse(text, out var boolean))
                {
                    return boolean;
                }
                if (int.TryParse(text, out number))
                {
                    return number != 0;
                }
            }
        }

        return fallback;
    }

    private static bool TryParseJson(string value, out JsonDocument document)
    {
        try
        {
            document = JsonDocument.Parse(value);
            return true;
        }
        catch (JsonException)
        {
            document = null!;
            return false;
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"Unable to delete temporary LevelDB snapshot: {path}", ex);
        }
    }

    private sealed record AuthData(string Type, string Header, string Prefix);
}
