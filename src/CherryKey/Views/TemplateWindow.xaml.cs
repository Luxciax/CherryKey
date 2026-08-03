using System.Windows;
using CherryKey.Models;
using CherryKey.Services;

namespace CherryKey.Views;

public partial class TemplateWindow : Window
{
    private readonly ProviderRecord _provider;
    private readonly ExportTemplateService _templates;
    private readonly ClipboardService _clipboard;

    public TemplateWindow(
        ProviderRecord provider,
        ExportTemplateService templates,
        ClipboardService clipboard)
    {
        InitializeComponent();
        _provider = provider;
        _templates = templates;
        _clipboard = clipboard;

        TemplateBox.Text = """
                           API_KEY={{apiKey}}
                           BASE_URL={{baseUrl}}
                           MODEL={{modelId}}
                           PROVIDER={{providerName}}
                           PROTOCOL={{protocol}}
                           """;
        TemplateBox.TextChanged += (_, _) => RenderPreview();
        RenderPreview();
    }

    private void RenderPreview() =>
        PreviewBox.Text = _templates.RenderCustom(TemplateBox.Text, _provider);

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        _clipboard.Copy(PreviewBox.Text);
        DialogResult = true;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
