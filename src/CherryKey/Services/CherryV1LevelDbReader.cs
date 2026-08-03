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

    public IReadOnlyList<ProviderRecord> Read(string levelDbDirectory)
    {
        if (!CherryDataSource.IsLevelDbDirectory(levelDbDirectory))
        {
            throw new InvalidDataException("所选目录不是有效的 Cherry Studio v1 Local Storage LevelDB。请选择 Local Storage\\leveldb 目录。");
        }

        var snapshotPath = CreateSnapshot(levelDbDirectory);
        try
        {
            var persistedJson = ReadPersistedState(snapshotPath);
            var providers = ParseProviders(persistedJson);

            foreach (var provider in providers)
            {
                provider.SelectDefaults();
            }

            return providers
                .OrderByDescending(provider => provider.IsEnabled)
                .ThenBy(provider => provider.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }
        finally
        {
            TryDeleteDirectory(snapshotPath);
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
                CopyLevelDbFiles(sourceDirectory, destination);

                if (!CherryDataSource.IsLevelDbDirectory(destination))
                {
                    throw new IOException("LevelDB 快照不完整，可能正好遇到 Cherry Studio 写入。稍后会自动重试。");
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

    private static string ReadPersistedState(string snapshotPath)
    {
        var options = new Options { CreateIfMissing = false };
        using var database = new DB(options, snapshotPath);
        using var iterator = database.CreateIterator();

        string? fallback = null;
        for (iterator.SeekToFirst(); iterator.IsValid(); iterator.Next())
        {
            var keyBytes = iterator.Key();
            var valueBytes = iterator.Value();

            var isTargetKey = ContainsAscii(keyBytes, CherryDataSource.PersistedStateKey)
                              || DecodeTextCandidates(keyBytes).Any(text =>
                                  text.Contains(CherryDataSource.PersistedStateKey, StringComparison.Ordinal));

            foreach (var text in DecodeTextCandidates(valueBytes))
            {
                if (!TryExtractPersistedJson(text, out var json))
                {
                    continue;
                }

                if (isTargetKey)
                {
                    return json;
                }

                // Fallback for Chromium schema variants where the script key is encoded in a
                // non-obvious binary prefix. Only accept data that structurally resembles the
                // Cherry Redux state.
                fallback ??= json;
            }
        }

        if (fallback is not null)
        {
            return fallback;
        }

        throw new InvalidDataException(
            "LevelDB 中没有找到 persist:cherry-studio。该目录可能不是 Cherry Studio v1 的 Local Storage，或当前版本使用了不同的存储结构。");
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

        var firstBrace = text.IndexOf('{');
        var lastBrace = text.LastIndexOf('}');
        if (firstBrace >= 0 && lastBrace > firstBrace)
        {
            var sliced = text[firstBrace..(lastBrace + 1)];
            if (!string.Equals(sliced, text, StringComparison.Ordinal))
            {
                yield return sliced;
            }
        }
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

    private static IReadOnlyList<ProviderRecord> ParseProviders(string persistedJson)
    {
        using var rootDocument = JsonDocument.Parse(persistedJson);
        return ParseRootElement(rootDocument.RootElement);
    }

    private static IReadOnlyList<ProviderRecord> ParseRootElement(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.String)
        {
            return ParseNestedJson(root.GetString(), "Redux Persist 根状态");
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Cherry Studio v1 持久化状态不是有效的 JSON 对象。");
        }

        if (TryGetProperty(root, "llm", out var llm))
        {
            return ParseLlmElement(llm);
        }

        return ParseLlmElement(root);
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
            attempts.Add((1, Encoding.UTF8));
        }

        attempts.Add((0, Encoding.UTF8));
        if (bytes.Length % 2 == 0)
        {
            attempts.Add((0, Encoding.Unicode));
            attempts.Add((0, Encoding.BigEndianUnicode));
        }
        if (bytes.Length > 1)
        {
            attempts.Add((1, Encoding.UTF8));
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

    private static bool ContainsAscii(byte[] bytes, string value)
    {
        var pattern = Encoding.ASCII.GetBytes(value);
        if (bytes.Length < pattern.Length)
        {
            return false;
        }

        for (var index = 0; index <= bytes.Length - pattern.Length; index++)
        {
            var matched = true;
            for (var patternIndex = 0; patternIndex < pattern.Length; patternIndex++)
            {
                if (bytes[index + patternIndex] == pattern[patternIndex])
                {
                    continue;
                }

                matched = false;
                break;
            }

            if (matched)
            {
                return true;
            }
        }

        return false;
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
