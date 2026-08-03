using System.Windows;

namespace CherryKey;

public partial class App : System.Windows.Application
{
    protected override void OnExit(ExitEventArgs e)
    {
        if (MainWindow is IDisposable disposable)
        {
            disposable.Dispose();
        }

        base.OnExit(e);
    }
}
