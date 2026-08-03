using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using CherryKey.ViewModels;
using WinForms = System.Windows.Forms;

namespace CherryKey;

public partial class MainWindow : Window, IDisposable
{
    private const int HotkeyId = 0x434B;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint VkK = 0x4B;
    private const int WmHotkey = 0x0312;

    private readonly MainViewModel _viewModel = new();
    private WinForms.NotifyIcon? _notifyIcon;
    private HwndSource? _source;
    private bool _allowExit;
    private bool _disposed;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;

        Loaded += MainWindow_Loaded;
        SourceInitialized += MainWindow_SourceInitialized;
        Closing += MainWindow_Closing;
        StateChanged += MainWindow_StateChanged;
        _viewModel.FocusSearchRequested += (_, _) => FocusSearch();
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        InitializeTray();
        _viewModel.Initialize();
        FocusSearch();
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        _source = HwndSource.FromHwnd(handle);
        _source?.AddHook(WndProc);
        RegisterHotKey(handle, HotkeyId, ModControl | ModShift, VkK);
    }

    private void InitializeTray()
    {
        _notifyIcon = new WinForms.NotifyIcon
        {
            Text = "CherryKey",
            Icon = System.Drawing.SystemIcons.Application,
            Visible = true
        };

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("打开 CherryKey", null, (_, _) => ShowFromTray());
        menu.Items.Add("刷新", null, (_, _) => Dispatcher.Invoke(() => _viewModel.RefreshCommand.Execute(null)));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Dispatcher.Invoke(ExitApplication));

        _notifyIcon.ContextMenuStrip = menu;
        _notifyIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowFromTray);
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowExit)
        {
            return;
        }

        e.Cancel = true;
        Hide();
        _notifyIcon?.ShowBalloonTip(1200, "CherryKey", "程序仍在托盘运行，按 Ctrl + Shift + K 可快速打开。", WinForms.ToolTipIcon.Info);
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            Hide();
        }
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        FocusSearch();
    }

    private void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void ExitApplication()
    {
        _allowExit = true;
        Close();
        System.Windows.Application.Current.Shutdown();
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            ShowFromTray();
            handled = true;
        }

        return 0;
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint hWnd, int id);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _viewModel.Dispose();

        var handle = new WindowInteropHelper(this).Handle;
        if (handle != nint.Zero)
        {
            UnregisterHotKey(handle, HotkeyId);
        }

        if (_source is not null)
        {
            _source.RemoveHook(WndProc);
            _source.Dispose();
        }

        if (_notifyIcon is not null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
        }
    }
}
