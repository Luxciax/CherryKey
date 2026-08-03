using System.Text;

namespace CherryKey.Services;

public static class AppLog
{
    private static readonly object Sync = new();
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CherryKey",
        "Logs");

    public static string LogPath { get; } = Path.Combine(LogDirectory, "startup.log");

    public static void Write(string message, Exception? exception = null)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            var builder = new StringBuilder()
                .Append('[').Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append("] ")
                .AppendLine(message);

            if (exception is not null)
            {
                builder.AppendLine(exception.ToString());
            }

            lock (Sync)
            {
                File.AppendAllText(LogPath, builder.ToString(), new UTF8Encoding(false));
            }
        }
        catch
        {
            // Logging must never crash the application.
        }
    }
}
