namespace CherryKey.Models;

public sealed class ModelRecord
{
    public required string Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string EndpointTypes { get; init; } = string.Empty;
    public bool IsEnabled { get; init; } = true;
    public bool IsHidden { get; init; }
    public bool IsDeprecated { get; init; }
    public string Notes { get; init; } = string.Empty;

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Id : Name;
    public string StateText => IsDeprecated ? "已弃用" : IsHidden ? "已隐藏" : IsEnabled ? "启用" : "停用";

    public override string ToString() => DisplayName;
}
