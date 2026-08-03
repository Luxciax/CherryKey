using System.Text;
using System.Text.Json;
using CherryKey.Models;

namespace CherryKey.Services;

public sealed class ExportTemplateService
{
    public string BuildAll(ProviderRecord provider)
    {
        var key = provider.SelectedApiKey?.Key ?? string.Empty;
        var model = provider.SelectedModel?.Id ?? string.Empty;

        return $"""
                名称：{provider.Name}
                Provider ID：{provider.Id}
                协议：{provider.ProtocolDisplay}
                Base URL：{provider.BaseUrl}
                API Key：{key}
                模型：{model}
                """;
    }

    public string BuildClaudeCode(ProviderRecord provider)
    {
        var key = EscapePowerShell(provider.SelectedApiKey?.Key);
        var model = EscapePowerShell(provider.SelectedModel?.Id);
        var baseUrl = EscapePowerShell(provider.BaseUrl);

        return $"""
                $env:ANTHROPIC_AUTH_TOKEN="{key}"
                $env:ANTHROPIC_BASE_URL="{baseUrl}"
                $env:ANTHROPIC_MODEL="{model}"
                """;
    }

    public string BuildOpenAi(ProviderRecord provider)
    {
        var key = EscapePowerShell(provider.SelectedApiKey?.Key);
        var baseUrl = EscapePowerShell(provider.BaseUrl);
        var model = EscapePowerShell(provider.SelectedModel?.Id);

        return $"""
                $env:OPENAI_API_KEY="{key}"
                $env:OPENAI_BASE_URL="{baseUrl}"
                $env:OPENAI_MODEL="{model}"
                """;
    }

    public string BuildGemini(ProviderRecord provider)
    {
        var key = EscapePowerShell(provider.SelectedApiKey?.Key);
        var baseUrl = EscapePowerShell(provider.BaseUrl);
        var model = EscapePowerShell(provider.SelectedModel?.Id);

        var builder = new StringBuilder();
        builder.AppendLine($"$env:GEMINI_API_KEY=\"{key}\"");
        if (!string.IsNullOrWhiteSpace(baseUrl))
        {
            builder.AppendLine($"$env:GOOGLE_GEMINI_BASE_URL=\"{baseUrl}\"");
        }

        builder.Append($"$env:GEMINI_MODEL=\"{model}\"");
        return builder.ToString();
    }

    public string BuildCodex(ProviderRecord provider)
    {
        var providerName = SanitizeTomlKey(provider.Id);
        var wireApi = provider.DefaultEndpoint.Contains("response", StringComparison.OrdinalIgnoreCase)
            ? "responses"
            : "chat";

        return $"""
                [model_providers.{providerName}]
                name = "{EscapeToml(provider.Name)}"
                base_url = "{EscapeToml(provider.BaseUrl)}"
                env_key = "CHERRYKEY_API_KEY"
                wire_api = "{wireApi}"

                model = "{EscapeToml(provider.SelectedModel?.Id ?? string.Empty)}"
                model_provider = "{providerName}"
                """;
    }

    public string BuildJson(ProviderRecord provider)
    {
        var payload = new
        {
            providerId = provider.Id,
            providerName = provider.Name,
            presetProviderId = provider.PresetProviderId,
            protocol = provider.ProtocolDisplay,
            endpointType = provider.DefaultEndpoint,
            baseUrl = provider.BaseUrl,
            apiKey = provider.SelectedApiKey?.Key,
            modelId = provider.SelectedModel?.Id,
            authType = provider.AuthType,
            authHeader = provider.AuthHeader
        };

        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    }

    public string BuildMarkdown(ProviderRecord provider) =>
        $"""
         # {provider.Name}

         - Provider ID：`{provider.Id}`
         - 协议：`{provider.ProtocolDisplay}`
         - Base URL：`{provider.BaseUrl}`
         - API Key：`{provider.SelectedApiKey?.Key}`
         - 模型：`{provider.SelectedModel?.Id}`
         - 状态：{provider.StatusText}
         """;

    public string RenderCustom(string template, ProviderRecord provider)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["providerId"] = provider.Id,
            ["providerName"] = provider.Name,
            ["presetProviderId"] = provider.PresetProviderId ?? string.Empty,
            ["protocol"] = provider.ProtocolDisplay,
            ["endpointType"] = provider.DefaultEndpoint,
            ["baseUrl"] = provider.BaseUrl,
            ["apiKey"] = provider.SelectedApiKey?.Key ?? string.Empty,
            ["apiKeyLabel"] = provider.SelectedApiKey?.Label ?? string.Empty,
            ["modelId"] = provider.SelectedModel?.Id ?? string.Empty,
            ["modelName"] = provider.SelectedModel?.DisplayName ?? string.Empty,
            ["authType"] = provider.AuthType,
            ["authHeader"] = provider.AuthHeader
        };

        foreach (var pair in values)
        {
            template = template.Replace($"{{{{{pair.Key}}}}}", pair.Value, StringComparison.OrdinalIgnoreCase);
        }

        return template;
    }

    private static string EscapePowerShell(string? value) => (value ?? string.Empty).Replace("\"", "`\"");
    private static string EscapeToml(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    private static string SanitizeTomlKey(string value)
    {
        var cleaned = new string(value.Select(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-' ? ch : '_').ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "cherrykey" : cleaned;
    }
}
