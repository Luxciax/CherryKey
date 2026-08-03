using System.Windows;

using WpfApplication = System.Windows.Application;
using WpfClipboard = System.Windows.Clipboard;

namespace CherryKey.Services;

public sealed class ClipboardService
{
    private readonly object _sync = new();
    private Guid _latestCopy;

    public void Copy(string? text, TimeSpan? clearAfter = null)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        WpfClipboard.SetText(text);
        var copyId = Guid.NewGuid();

        lock (_sync)
        {
            _latestCopy = copyId;
        }

        _ = ClearLaterAsync(copyId, text, clearAfter ?? TimeSpan.FromSeconds(30));
    }

    private async Task ClearLaterAsync(Guid copyId, string copiedText, TimeSpan delay)
    {
        await Task.Delay(delay).ConfigureAwait(false);

        await WpfApplication.Current.Dispatcher.InvokeAsync(() =>
        {
            lock (_sync)
            {
                if (_latestCopy != copyId)
                {
                    return;
                }
            }

            try
            {
                if (WpfClipboard.ContainsText() && WpfClipboard.GetText() == copiedText)
                {
                    WpfClipboard.Clear();
                }
            }
            catch
            {
                // Clipboard may be temporarily locked by another application.
            }
        });
    }
}
