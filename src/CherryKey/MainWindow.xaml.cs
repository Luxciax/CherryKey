using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using CherryKey.Services;
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

    private readonly MainViewModel _viewModel;
    private readonly bool _disableTray;
    private WinForms.NotifyIcon? _notifyIcon;
    private System.Drawing.Icon? _trayIcon;
    private HwndSource? _source;
    private bool _allowExit;
    private bool _disposed;
    private bool _trayReady;
    private bool _isLightTheme;

    public Task InitializationTask { get; private set; } = Task.CompletedTask;

    public MainWindow(bool disableTray = false)
    {
        _disableTray = disableTray;
        AppLog.Write($"Constructing MainWindow. disableTray={disableTray}.");

        InitializeComponent();
        _viewModel = new MainViewModel();
        DataContext = _viewModel;

        Loaded += MainWindow_Loaded;
        ContentRendered += MainWindow_ContentRendered;
        SourceInitialized += MainWindow_SourceInitialized;
        Closing += MainWindow_Closing;
        StateChanged += MainWindow_StateChanged;
        _viewModel.FocusSearchRequested += (_, _) => FocusSearch();
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        AppLog.Write("MainWindow Loaded event. Scheduling background data-source discovery.");
        FocusSearch();
        InitializationTask = InitializeAfterFirstRenderAsync();
    }

    private async Task InitializeAfterFirstRenderAsync()
    {
        // Loaded runs inside Window.Show(). Yield first so the window can paint before any
        // filesystem or LevelDB work starts. All discovery/reading is performed off the UI thread.
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);

        try
        {
            await _viewModel.InitializeAsync();
            AppLog.Write("ViewModel background initialization completed.");
        }
        catch (Exception ex)
        {
            AppLog.Write("ViewModel initialization failed.", ex);
            MessageBox.Show(
                $"读取 Cherry Studio 配置时发生错误：\n\n{ex.Message}\n\n窗口仍可使用，请手动选择数据源。",
                "CherryKey",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void MainWindow_ContentRendered(object? sender, EventArgs e)
    {
        AppLog.Write("MainWindow ContentRendered event.");
        if (_disableTray || _trayReady)
        {
            return;
        }

        // Tray support is optional. A tray failure must never prevent the main window from appearing.
        try
        {
            InitializeTray();
            _trayReady = true;
            AppLog.Write("Tray icon initialized.");
        }
        catch (Exception ex)
        {
            _trayReady = false;
            AppLog.Write("Tray initialization failed; continuing without tray support.", ex);
        }
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            _source = HwndSource.FromHwnd(handle);
            _source?.AddHook(WndProc);
            var registered = RegisterHotKey(handle, HotkeyId, ModControl | ModShift, VkK);
            AppLog.Write($"Global hotkey registration result={registered}.");
        }
        catch (Exception ex)
        {
            AppLog.Write("Global hotkey initialization failed; continuing without it.", ex);
        }
    }

    private void InitializeTray()
    {
        try
        {
            var executablePath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(executablePath))
            {
                _trayIcon = System.Drawing.Icon.ExtractAssociatedIcon(executablePath);
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("Unable to load the embedded CherryKey icon for the tray.", ex);
        }

        _notifyIcon = new WinForms.NotifyIcon
        {
            Text = "CherryKey",
            Icon = _trayIcon ?? System.Drawing.SystemIcons.Application,
            Visible = true
        };

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("打开 CherryKey", null, (_, _) => Dispatcher.Invoke(ShowFromTray));
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

        // Never make the application disappear without a recovery path.
        if (!_trayReady || _notifyIcon is null)
        {
            _allowExit = true;
            return;
        }

        e.Cancel = true;
        Hide();
        AppLog.Write("Main window hidden to tray.");
        try
        {
            _notifyIcon.ShowBalloonTip(1200, "CherryKey", "程序仍在托盘运行，按 Ctrl + Shift + K 可快速打开。", WinForms.ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            AppLog.Write("Unable to display tray balloon.", ex);
        }
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized && _trayReady)
        {
            Hide();
            AppLog.Write("Main window minimized to tray.");
        }
    }

    private void ShowFromTray()
    {
        ShowInTaskbar = true;
        Show();
        WindowState = WindowState.Normal;
        Visibility = Visibility.Visible;
        Activate();
        Topmost = true;
        Topmost = false;
        FocusSearch();
        AppLog.Write("Main window restored from tray/hotkey.");
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
        Application.Current.Shutdown();
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

    private void ToggleTheme_Click(object sender, RoutedEventArgs e)
    {
        _isLightTheme = !_isLightTheme;
        ApplyTheme(_isLightTheme);
        ThemeGlyph.Text = _isLightTheme ? "☾" : "☼";
        ThemeButton.ToolTip = _isLightTheme ? "切换到暗色主题" : "切换到亮色主题";
    }

    private static void ApplyTheme(bool light)
    {
        var palette = light
            ? new Dictionary<string, string>
            {
                ["WindowBrush"] = "#F5F7FB",
                ["TitleBarBrush"] = "#FFFFFF",
                ["SidebarBrush"] = "#F9FAFC",
                ["PanelBrush"] = "#FFFFFF",
                ["PanelRaisedBrush"] = "#F7F8FC",
                ["PanelHoverBrush"] = "#F0F2F8",
                ["CardBrush"] = "#FFFFFF",
                ["InputBrush"] = "#F5F7FB",
                ["TextBrush"] = "#182235",
                ["TextSecondaryBrush"] = "#4D5A70",
                ["MutedBrush"] = "#768299",
                ["MutedDarkBrush"] = "#A6AFBD",
                ["BorderBrush"] = "#E0E5EE",
                ["BorderSoftBrush"] = "#E0E5EE",
                ["BorderStrongBrush"] = "#CCD4E1",
                ["AccentBrush"] = "#7043DF",
                ["AccentLightBrush"] = "#7043DF",
                ["AccentDarkBrush"] = "#5C2EC5",
                ["AccentSoftBrush"] = "#EEE9FB",
                ["SuccessBrush"] = "#16A76A",
                ["SuccessDarkBrush"] = "#E8F6F0",
                ["DangerBrush"] = "#DC5568",
            }
            : new Dictionary<string, string>
            {
                ["WindowBrush"] = "#080D16",
                ["TitleBarBrush"] = "#0B1220",
                ["SidebarBrush"] = "#0B1422",
                ["PanelBrush"] = "#111C2D",
                ["PanelRaisedBrush"] = "#152238",
                ["PanelHoverBrush"] = "#192944",
                ["CardBrush"] = "#131F33",
                ["InputBrush"] = "#101B2C",
                ["TextBrush"] = "#F3F6FB",
                ["TextSecondaryBrush"] = "#BAC5D6",
                ["MutedBrush"] = "#7F8DA5",
                ["MutedDarkBrush"] = "#506078",
                ["BorderBrush"] = "#23334C",
                ["BorderSoftBrush"] = "#23334C",
                ["BorderStrongBrush"] = "#314662",
                ["AccentBrush"] = "#8758F3",
                ["AccentLightBrush"] = "#A786F6",
                ["AccentDarkBrush"] = "#6D3BD7",
                ["AccentSoftBrush"] = "#24203E",
                ["SuccessBrush"] = "#2DD486",
                ["SuccessDarkBrush"] = "#143A2C",
                ["DangerBrush"] = "#EF6C7B",
            };

        foreach (var (key, colorText) in palette)
        {
            var color = (Color)ColorConverter.ConvertFromString(colorText);
            Application.Current.Resources[key] = new SolidColorBrush(color);
        }

        Application.Current.Resources["AccentGradient"] = CreateGradient(
            light ? "#7043DF" : "#8758F3",
            light ? "#5C2EC5" : "#6D3BD7");
        Application.Current.Resources["HeaderGlowGradient"] = CreateGradient(
            light ? "#FFFFFF" : "#111C2D",
            light ? "#F7F3FF" : "#171D35");
    }

    private static LinearGradientBrush CreateGradient(string start, string end)
    {
        return new LinearGradientBrush(
            (Color)ColorConverter.ConvertFromString(start),
            (Color)ColorConverter.ConvertFromString(end),
            0);
    }

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

        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle != nint.Zero)
            {
                UnregisterHotKey(handle, HotkeyId);
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("Failed to unregister hotkey.", ex);
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

        _trayIcon?.Dispose();
    }
}
