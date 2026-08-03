using System.Collections.ObjectModel;
using CherryKey.Infrastructure;
using MediaBrush = System.Windows.Media.Brush;
using MediaColor = System.Windows.Media.Color;
using MediaColorConverter = System.Windows.Media.ColorConverter;
using MediaSolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace CherryKey.Models;

public sealed class ProviderRecord : ObservableObject
{
    private ApiKeyRecord? _selectedApiKey;
    private ModelRecord? _selectedModel;

    public required string Id { get; init; }
    public string? PresetProviderId { get; init; }
    public required string Name { get; init; }
    public string BaseUrl { get; init; } = string.Empty;
    public string DefaultEndpoint { get; init; } = string.Empty;
    public string EndpointTypes { get; init; } = string.Empty;
    public string AuthType { get; init; } = string.Empty;
    public string AuthHeader { get; init; } = string.Empty;
    public string AuthPrefix { get; init; } = string.Empty;
    public string Notes { get; init; } = string.Empty;
    public bool IsEnabled { get; init; }

    public ObservableCollection<ApiKeyRecord> ApiKeys { get; } = [];
    public ObservableCollection<ModelRecord> Models { get; } = [];

    public ApiKeyRecord? SelectedApiKey
    {
        get => _selectedApiKey;
        set => SetProperty(ref _selectedApiKey, value);
    }

    public ModelRecord? SelectedModel
    {
        get => _selectedModel;
        set => SetProperty(ref _selectedModel, value);
    }

    public string Initial => string.IsNullOrWhiteSpace(Name) ? "?" : Name.Trim()[0].ToString().ToUpperInvariant();
    public MediaBrush AvatarBrush => CreateAvatarBrush(Id);
    public string PrimaryModelId => SelectedModel?.Id ?? Models.FirstOrDefault()?.Id ?? "未配置模型";
    public string ModelCountText => Models.Count.ToString();
    public string KeyCountText => ApiKeys.Count.ToString();
    public string StatusText => IsEnabled ? "已启用" : "已停用";
    public string ProtocolDisplay => string.IsNullOrWhiteSpace(DefaultEndpoint)
        ? (string.IsNullOrWhiteSpace(EndpointTypes) ? "未知协议" : EndpointTypes)
        : HumanizeEndpoint(DefaultEndpoint);
    public string BaseUrlDisplay => string.IsNullOrWhiteSpace(BaseUrl) ? "由 Cherry 预设继承或未保存" : BaseUrl;
    public string ModelSummary => Models.Count == 0 ? "未读取到模型" : $"{Models.Count} 个模型";
    public string KeySummary => ApiKeys.Count == 0 ? "无 API Key" : $"{ApiKeys.Count} 个 Key";
    public string SearchText => $"{Name} {Id} {PresetProviderId} {BaseUrl} {DefaultEndpoint} {EndpointTypes} {string.Join(" ", Models.Select(m => m.Id))}".ToLowerInvariant();

    public void SelectDefaults()
    {
        SelectedApiKey = ApiKeys.FirstOrDefault(k => k.IsEnabled) ?? ApiKeys.FirstOrDefault();
        SelectedModel = Models.FirstOrDefault(m => m.IsEnabled && !m.IsHidden && !m.IsDeprecated)
                        ?? Models.FirstOrDefault(m => !m.IsDeprecated)
                        ?? Models.FirstOrDefault();
    }

    private static MediaBrush CreateAvatarBrush(string seed)
    {
        string[] colors = ["#6D45D7", "#3154C7", "#147B72", "#2D7A3B", "#9A4D16", "#7A3BB5", "#146B98"];
        var hash = 17;
        foreach (var character in seed)
        {
            hash = unchecked(hash * 31 + character);
        }

        var color = (MediaColor)MediaColorConverter.ConvertFromString(colors[Math.Abs(hash % colors.Length)]);
        var brush = new MediaSolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static string HumanizeEndpoint(string value) => value.Trim().ToLowerInvariant() switch
    {
        "anthropic" or "anthropic-messages" or "anthropic_messages" => "Anthropic Messages",
        "openai" or "openai-chat" or "openai_chat" or "chat-completions" => "OpenAI Chat Completions",
        "openai-responses" or "openai_responses" or "responses" => "OpenAI Responses",
        "google" or "gemini" or "google-generative-ai" => "Gemini 原生",
        "azure-openai" or "azure_openai" => "Azure OpenAI",
        _ => value
    };
}
