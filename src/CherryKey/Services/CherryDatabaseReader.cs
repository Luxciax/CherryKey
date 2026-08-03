using System.Text.Json;
using CherryKey.Models;
using Microsoft.Data.Sqlite;

namespace CherryKey.Services;

public sealed class CherryDatabaseReader
{
    public IReadOnlyList<ProviderRecord> Read(string databasePath)
    {
        if (!File.Exists(databasePath))
        {
            throw new FileNotFoundException("找不到 Cherry Studio 数据库。", databasePath);
        }

        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Shared,
                Pooling = false
            }.ToString());

        connection.Open();

        if (!TableExists(connection, "user_provider"))
        {
            throw new InvalidDataException("数据库中不存在 user_provider 表，可能不是 Cherry Studio V2 数据库。");
        }

        var providers = ReadProviders(connection);
        if (TableExists(connection, "user_model"))
        {
            ReadModels(connection, providers);
        }

        foreach (var provider in providers.Values)
        {
            provider.SelectDefaults();
        }

        return providers.Values
            .OrderByDescending(p => p.IsEnabled)
            .ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static Dictionary<string, ProviderRecord> ReadProviders(SqliteConnection connection)
    {
        var columns = GetColumns(connection, "user_provider");
        var select = new[]
        {
            SelectColumn(columns, "provider_id"),
            SelectColumn(columns, "preset_provider_id"),
            SelectColumn(columns, "name"),
            SelectColumn(columns, "endpoint_configs"),
            SelectColumn(columns, "default_chat_endpoint"),
            SelectColumn(columns, "api_keys"),
            SelectColumn(columns, "auth_config"),
            SelectColumn(columns, "provider_settings"),
            SelectColumn(columns, "is_enabled")
        };

        var orderBy = columns.Contains("order_key") ? " ORDER BY order_key" : string.Empty;
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {string.Join(", ", select)} FROM user_provider{orderBy};";

        var result = new Dictionary<string, ProviderRecord>(StringComparer.OrdinalIgnoreCase);
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            var id = ReadString(reader, "provider_id");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var endpointJson = ReadString(reader, "endpoint_configs");
            var defaultEndpoint = ReadString(reader, "default_chat_endpoint");
            var endpointData = ParseEndpointConfigs(endpointJson, defaultEndpoint);
            var auth = ParseAuthConfig(ReadString(reader, "auth_config"));
            var notes = ParseNotes(ReadString(reader, "provider_settings"));

            var provider = new ProviderRecord
            {
                Id = id,
                PresetProviderId = NullIfWhiteSpace(ReadString(reader, "preset_provider_id")),
                Name = ReadString(reader, "name", id),
                BaseUrl = endpointData.BaseUrl,
                DefaultEndpoint = defaultEndpoint,
                EndpointTypes = endpointData.EndpointTypes,
                AuthType = auth.Type,
                AuthHeader = auth.Header,
                AuthPrefix = auth.Prefix,
                Notes = notes,
                IsEnabled = ReadBoolean(reader, "is_enabled")
            };

            foreach (var key in ParseApiKeys(ReadString(reader, "api_keys")))
            {
                provider.ApiKeys.Add(key);
            }

            result[id] = provider;
        }

        return result;
    }

    private static void ReadModels(SqliteConnection connection, Dictionary<string, ProviderRecord> providers)
    {
        var columns = GetColumns(connection, "user_model");
        var select = new[]
        {
            SelectColumn(columns, "provider_id"),
            SelectColumn(columns, "model_id"),
            SelectColumn(columns, "name"),
            SelectColumn(columns, "description"),
            SelectColumn(columns, "endpoint_types"),
            SelectColumn(columns, "is_enabled"),
            SelectColumn(columns, "is_hidden"),
            SelectColumn(columns, "is_deprecated"),
            SelectColumn(columns, "notes")
        };

        var orderBy = columns.Contains("order_key") ? " ORDER BY provider_id, order_key" : string.Empty;
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {string.Join(", ", select)} FROM user_model{orderBy};";

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var providerId = ReadString(reader, "provider_id");
            var modelId = ReadString(reader, "model_id");

            if (string.IsNullOrWhiteSpace(modelId) || !providers.TryGetValue(providerId, out var provider))
            {
                continue;
            }

            provider.Models.Add(new ModelRecord
            {
                Id = modelId,
                Name = ReadString(reader, "name"),
                Description = ReadString(reader, "description"),
                EndpointTypes = ParseStringArray(ReadString(reader, "endpoint_types")),
                IsEnabled = ReadBoolean(reader, "is_enabled", true),
                IsHidden = ReadBoolean(reader, "is_hidden"),
                IsDeprecated = ReadBoolean(reader, "is_deprecated"),
                Notes = ReadString(reader, "notes")
            });
        }
    }

    private static List<ApiKeyRecord> ParseApiKeys(string json)
    {
        var keys = new List<ApiKeyRecord>();
        if (string.IsNullOrWhiteSpace(json))
        {
            return keys;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.String)
            {
                var value = document.RootElement.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    keys.Add(new ApiKeyRecord { Id = "legacy-1", Key = value, Label = "主 Key" });
                }

                return keys;
            }

            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return keys;
            }

            var index = 0;
            foreach (var item in document.RootElement.EnumerateArray())
            {
                index++;
                if (item.ValueKind == JsonValueKind.String)
                {
                    var raw = item.GetString();
                    if (!string.IsNullOrWhiteSpace(raw))
                    {
                        keys.Add(new ApiKeyRecord { Id = $"legacy-{index}", Key = raw, Label = $"Key {index}" });
                    }

                    continue;
                }

                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var key = GetJsonString(item, "key", "apiKey", "value");
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                keys.Add(new ApiKeyRecord
                {
                    Id = GetJsonString(item, "id") ?? $"key-{index}",
                    Key = key,
                    Label = GetJsonString(item, "label", "name") ?? $"Key {index}",
                    IsEnabled = GetJsonBoolean(item, true, "isEnabled", "is_enabled", "enabled")
                });
            }
        }
        catch (JsonException)
        {
            // Legacy storage occasionally used a plain string instead of JSON.
            keys.Add(new ApiKeyRecord { Id = "legacy-1", Key = json.Trim(), Label = "主 Key" });
        }

        return keys;
    }

    private static EndpointData ParseEndpointConfigs(string json, string defaultEndpoint)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new EndpointData(string.Empty, string.Empty);
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new EndpointData(string.Empty, string.Empty);
            }

            var entries = new List<(string Type, string BaseUrl)>();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                var baseUrl = property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString() ?? string.Empty
                    : GetJsonString(property.Value, "baseUrl", "base_url", "apiHost", "api_host") ?? string.Empty;

                entries.Add((property.Name, baseUrl));
            }

            var selected = entries.FirstOrDefault(e =>
                !string.IsNullOrWhiteSpace(defaultEndpoint) &&
                string.Equals(e.Type, defaultEndpoint, StringComparison.OrdinalIgnoreCase));

            if (string.IsNullOrWhiteSpace(selected.BaseUrl))
            {
                selected = entries.FirstOrDefault(e => !string.IsNullOrWhiteSpace(e.BaseUrl));
            }

            return new EndpointData(
                selected.BaseUrl ?? string.Empty,
                string.Join(", ", entries.Select(e => e.Type)));
        }
        catch (JsonException)
        {
            return new EndpointData(string.Empty, string.Empty);
        }
    }

    private static AuthData ParseAuthConfig(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new AuthData(string.Empty, string.Empty, string.Empty);
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return new AuthData(
                GetJsonString(root, "type") ?? string.Empty,
                GetJsonString(root, "headerName", "header_name") ?? string.Empty,
                GetJsonString(root, "prefix") ?? string.Empty);
        }
        catch (JsonException)
        {
            return new AuthData(string.Empty, string.Empty, string.Empty);
        }
    }

    private static string ParseNotes(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return string.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return GetJsonString(document.RootElement, "notes", "description") ?? string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    private static string ParseStringArray(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return string.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind switch
            {
                JsonValueKind.Array => string.Join(", ", document.RootElement.EnumerateArray()
                    .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : item.ToString())
                    .Where(value => !string.IsNullOrWhiteSpace(value))),
                JsonValueKind.String => document.RootElement.GetString() ?? string.Empty,
                _ => json
            };
        }
        catch (JsonException)
        {
            return json;
        }
    }

    private static HashSet<string> GetColumns(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{table}\");";

        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            columns.Add(reader.GetString(reader.GetOrdinal("name")));
        }

        return columns;
    }

    private static bool TableExists(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name LIMIT 1;";
        command.Parameters.AddWithValue("$name", table);
        return command.ExecuteScalar() is not null;
    }

    private static string SelectColumn(HashSet<string> columns, string column) =>
        columns.Contains(column) ? $"\"{column}\"" : $"NULL AS \"{column}\"";

    private static string ReadString(SqliteDataReader reader, string column, string fallback = "")
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? fallback : Convert.ToString(reader.GetValue(ordinal)) ?? fallback;
    }

    private static bool ReadBoolean(SqliteDataReader reader, string column, bool fallback = false)
    {
        var ordinal = reader.GetOrdinal(column);
        if (reader.IsDBNull(ordinal))
        {
            return fallback;
        }

        var value = reader.GetValue(ordinal);
        return value switch
        {
            bool boolean => boolean,
            long number => number != 0,
            int number => number != 0,
            string text when bool.TryParse(text, out var boolean) => boolean,
            string text when long.TryParse(text, out var number) => number != 0,
            _ => fallback
        };
    }

    private static string? GetJsonString(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }

            if (value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
            {
                return value.ToString();
            }
        }

        return null;
    }

    private static bool GetJsonBoolean(JsonElement element, bool fallback, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value))
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

            if (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var boolean))
            {
                return boolean;
            }
        }

        return fallback;
    }

    private static string? NullIfWhiteSpace(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private sealed record EndpointData(string BaseUrl, string EndpointTypes);
    private sealed record AuthData(string Type, string Header, string Prefix);
}
