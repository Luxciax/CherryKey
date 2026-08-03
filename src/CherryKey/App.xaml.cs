using System.Windows;
using System.Windows.Threading;
using CherryKey.Services;

namespace CherryKey;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        AppLog.Write($"CherryKey starting. Version={typeof(App).Assembly.GetName().Version}; Args={string.Join(' ', e.Args)}");

        var smokeTest = e.Args.Any(arg => string.Equals(arg, "--smoke-test", StringComparison.OrdinalIgnoreCase));
        var noTray = smokeTest || e.Args.Any(arg => string.Equals(arg, "--no-tray", StringComparison.OrdinalIgnoreCase));

        try
        {
            var window = new MainWindow(noTray);
            MainWindow = window;

            if (smokeTest)
            {
                window.Measure(new Size(1440, 900));
                window.Arrange(new Rect(0, 0, 1440, 900));
                window.UpdateLayout();
                AppLog.Write("Startup smoke test passed: MainWindow XAML loaded and layout completed.");
                window.Dispose();
                Shutdown(0);
                return;
            }

            window.Show();
            window.Activate();

            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
            {
                EnsureWindowVisible(window);
                AppLog.Write($"Main window shown. Visibility={window.Visibility}; State={window.WindowState}.");
            });
        }
        catch (Exception ex)
        {
            ShowFatalStartupError("CherryKey 启动失败", ex);
            Shutdown(-1);
        }
    }

    private static void EnsureWindowVisible(Window window)
    {
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        var workArea = SystemParameters.WorkArea;
        if (window.Left > workArea.Right - 80 || window.Top > workArea.Bottom - 80 ||
            window.Left + window.Width < workArea.Left + 80 || window.Top + window.Height < workArea.Top + 80)
        {
            window.Left = workArea.Left + Math.Max(0, (workArea.Width - window.ActualWidth) / 2);
            window.Top = workArea.Top + Math.Max(0, (workArea.Height - window.ActualHeight) / 2);
        }

        window.ShowInTaskbar = true;
        window.Visibility = Visibility.Visible;
        window.Topmost = true;
        window.Topmost = false;
        window.Activate();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Write("Unhandled UI exception.", e.Exception);
        MessageBox.Show(
            $"CherryKey 发生未处理错误：\n\n{e.Exception.Message}\n\n日志：{AppLog.LogPath}",
            "CherryKey 错误",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }

    private static void OnDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e) =>
        AppLog.Write("Unhandled AppDomain exception.", e.ExceptionObject as Exception);

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        AppLog.Write("Unobserved task exception.", e.Exception);
        e.SetObserved();
    }

    private static void ShowFatalStartupError(string title, Exception exception)
    {
        AppLog.Write(title, exception);
        MessageBox.Show(
            $"{title}：\n\n{exception.Message}\n\n详细日志已保存到：\n{AppLog.LogPath}",
            title,
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (MainWindow is IDisposable disposable)
        {
            disposable.Dispose();
        }

        AppLog.Write($"CherryKey exiting. Code={e.ApplicationExitCode}.");
        base.OnExit(e);
    }
}
