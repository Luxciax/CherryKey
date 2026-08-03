using CherryKey.Infrastructure;

namespace CherryKey.Models;

public sealed class ApiKeyRecord : ObservableObject
{
    private bool _isRevealed;

    public required string Id { get; init; }
    public required string Key { get; init; }
    public string Label { get; init; } = "API Key";
    public bool IsEnabled { get; init; } = true;

    public bool IsRevealed
    {
        get => _isRevealed;
        set
        {
            if (SetProperty(ref _isRevealed, value))
            {
                OnPropertyChanged(nameof(DisplayKey));
                OnPropertyChanged(nameof(RevealButtonText));
            }
        }
    }

    public string DisplayKey => IsRevealed ? Key : Mask(Key);
    public string RevealButtonText => IsRevealed ? "隐藏" : "显示";
    public string StatusText => IsEnabled ? "可用" : "已停用";

    private static string Mask(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "（空）";
        }

        if (value.Length <= 8)
        {
            return new string('•', value.Length);
        }

        return $"{value[..Math.Min(7, value.Length)]}{new string('•', Math.Min(20, Math.Max(8, value.Length - 11)))}{value[^4..]}";
    }

    public override string ToString() => string.IsNullOrWhiteSpace(Label) ? DisplayKey : Label;
}
