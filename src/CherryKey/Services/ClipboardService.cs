using System.Windows;

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

        Clipboard.SetText(text);
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

        await Application.Current.Dispatcher.InvokeAsync(() =>
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
                if (Clipboard.ContainsText() && Clipboard.GetText() == copiedText)
                {
                    Clipboard.Clear();
                }
            }
            catch
            {
                // Clipboard may be temporarily locked by another application.
            }
        });
    }
}
