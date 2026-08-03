using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace CherryKey;

public partial class App : System.Windows.Application
{
    private static readonly object LogSync = new();

    internal static string LogFilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CherryKey",
        "Logs",
        "startup.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        RegisterGlobalExceptionHandlers();
        WriteLog($"Application starting. Version={Environment.Version}; Args={string.Join(' ', e.Args)}");

        base.OnStartup(e);

        try
        {
            var smokeTest = e.Args.Any(arg => string.Equals(arg, "--smoke-test", StringComparison.OrdinalIgnoreCase));
            var noTray = smokeTest ||
                         e.Args.Any(arg => string.Equals(arg, "--no-tray", StringComparison.OrdinalIgnoreCase));

            var window = new MainWindow(enableTray: !noTray);
            MainWindow = window;
            window.Show();
            window.Activate();

            WriteLog($"Main window shown. TrayEnabled={!noTray}");

            if (smokeTest)
            {
                Dispatcher.BeginInvoke(
                    DispatcherPriority.ApplicationIdle,
                    new Action(() =>
                    {
                        WriteLog("Smoke test passed.");
                        Shutdown(0);
                    }));
            }
        }
        catch (Exception ex)
        {
            WriteLog("Fatal startup exception.", ex);
            ShowFatalError(ex);
            Shutdown(-1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            if (MainWindow is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
        catch (Exception ex)
        {
            WriteLog("Failed to dispose main window.", ex);
        }

        WriteLog($"Application exited. Code={e.ApplicationExitCode}");
        base.OnExit(e);
    }

    private void RegisterGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            WriteLog("Unhandled dispatcher exception.", args.Exception);
            ShowFatalError(args.Exception);
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            WriteLog(
                $"Unhandled AppDomain exception. IsTerminating={args.IsTerminating}",
                args.ExceptionObject as Exception);
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            WriteLog("Unobserved task exception.", args.Exception);
            args.SetObserved();
        };
    }

    internal static void WriteLog(string message, Exception? exception = null)
    {
        try
        {
            lock (LogSync)
            {
                var directory = Path.GetDirectoryName(LogFilePath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var builder = new StringBuilder()
                    .Append('[')
                    .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                    .Append("] ")
                    .AppendLine(message);

                if (exception is not null)
                {
                    builder.AppendLine(exception.ToString());
                }

                File.AppendAllText(LogFilePath, builder.ToString(), new UTF8Encoding(false));
            }
        }
        catch
        {
            // Logging must never become another startup failure.
        }
    }

    private static void ShowFatalError(Exception exception)
    {
        try
        {
            System.Windows.MessageBox.Show(
                $"CherryKey 启动或运行时发生错误。\n\n{exception.Message}\n\n日志：\n{LogFilePath}",
                "CherryKey",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch
        {
            // Nothing else can be shown safely.
        }
    }
}
