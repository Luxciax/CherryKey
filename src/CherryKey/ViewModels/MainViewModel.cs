using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Data;
using CherryKey.Infrastructure;
using CherryKey.Models;
using CherryKey.Services;
using CherryKey.Views;
using Microsoft.Win32;

namespace CherryKey.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly AppSettingsStore _settings = new();
    private readonly CherryDatabaseReader _reader = new();
    private readonly ClipboardService _clipboard = new();
    private readonly ExportTemplateService _templates = new();
    private readonly CherryDatabaseLocator _locator;

    private string _searchText = string.Empty;
    private ProviderRecord? _selectedProvider;
    private string _databasePath = string.Empty;
    private string _connectionStatus = "尚未连接";
    private string _statusMessage = "正在自动查找 Cherry Studio 数据库…";
    private string _lastRefreshText = "尚未刷新";
    private bool _isBusy;
    private FileSystemWatcher? _watcher;
    private System.Threading.Timer? _refreshTimer;
    private bool _disposed;

    public MainViewModel()
    {
        _locator = new CherryDatabaseLocator(_settings);
        ProvidersView = CollectionViewSource.GetDefaultView(Providers);
        ProvidersView.Filter = FilterProvider;

        RefreshCommand = new RelayCommand(Refresh, () => !IsBusy);
        RescanDatabaseCommand = new RelayCommand(RescanDatabase, () => !IsBusy);
        ChooseDatabaseCommand = new RelayCommand(ChooseDatabase, () => !IsBusy);
        CopyKeyCommand = new RelayCommand(CopyKey, HasProviderAndKey);
        ToggleKeyCommand = new RelayCommand(ToggleKey, HasProviderAndKey);
        CopyApiKeyItemCommand = new RelayCommand(CopyApiKeyItem, parameter => parameter is ApiKeyRecord);
        ToggleApiKeyItemCommand = new RelayCommand(ToggleApiKeyItem, parameter => parameter is ApiKeyRecord);
        CopyBaseUrlCommand = new RelayCommand(CopyBaseUrl, HasProvider);
        CopyModelCommand = new RelayCommand(CopyModel, HasProviderAndModel);
        CopyModelItemCommand = new RelayCommand(CopyModelItem, parameter => parameter is ModelRecord);
        CopyAllCommand = new RelayCommand(() => CopyTemplate(_templates.BuildAll), HasProvider);
        CopyClaudeCommand = new RelayCommand(() => CopyTemplate(_templates.BuildClaudeCode), HasProvider);
        CopyOpenAiCommand = new RelayCommand(() => CopyTemplate(_templates.BuildOpenAi), HasProvider);
        CopyGeminiCommand = new RelayCommand(() => CopyTemplate(_templates.BuildGemini), HasProvider);
        CopyCodexCommand = new RelayCommand(() => CopyTemplate(_templates.BuildCodex), HasProvider);
        OpenCustomTemplateCommand = new RelayCommand(OpenCustomTemplate, HasProvider);
        ExportJsonCommand = new RelayCommand(() => Export("JSON 文件|*.json", ".json", _templates.BuildJson), HasProvider);
        ExportMarkdownCommand = new RelayCommand(() => Export("Markdown 文件|*.md", ".md", _templates.BuildMarkdown), HasProvider);
    }

    public ObservableCollection<ProviderRecord> Providers { get; } = [];
    public ICollectionView ProvidersView { get; }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                ProvidersView.Refresh();
            }
        }
    }

    public ProviderRecord? SelectedProvider
    {
        get => _selectedProvider;
        set
        {
            if (SetProperty(ref _selectedProvider, value))
            {
                NotifyCommandStates();
            }
        }
    }

    public string DatabasePath
    {
        get => _databasePath;
        private set
        {
            if (SetProperty(ref _databasePath, value))
            {
                OnPropertyChanged(nameof(DatabaseDisplayPath));
                OnPropertyChanged(nameof(ConnectionBadgeText));
            }
        }
    }

    public string DatabaseDisplayPath => string.IsNullOrWhiteSpace(DatabasePath)
        ? _locator.LastScanSummary
        : DatabasePath;

    public string ConnectionBadgeText => string.IsNullOrWhiteSpace(DatabasePath) ? "未连接" : "已连接";
    public string ProviderCountText => $"共 {Providers.Count} 个供应商";

    public string ConnectionStatus
    {
        get => _connectionStatus;
        private set => SetProperty(ref _connectionStatus, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string LastRefreshText
    {
        get => _lastRefreshText;
        private set => SetProperty(ref _lastRefreshText, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                NotifyCommandStates();
            }
        }
    }

    public RelayCommand RefreshCommand { get; }
    public RelayCommand RescanDatabaseCommand { get; }
    public RelayCommand ChooseDatabaseCommand { get; }
    public RelayCommand CopyKeyCommand { get; }
    public RelayCommand ToggleKeyCommand { get; }
    public RelayCommand CopyApiKeyItemCommand { get; }
    public RelayCommand ToggleApiKeyItemCommand { get; }
    public RelayCommand CopyBaseUrlCommand { get; }
    public RelayCommand CopyModelCommand { get; }
    public RelayCommand CopyModelItemCommand { get; }
    public RelayCommand CopyAllCommand { get; }
    public RelayCommand CopyClaudeCommand { get; }
    public RelayCommand CopyOpenAiCommand { get; }
    public RelayCommand CopyGeminiCommand { get; }
    public RelayCommand CopyCodexCommand { get; }
    public RelayCommand OpenCustomTemplateCommand { get; }
    public RelayCommand ExportJsonCommand { get; }
    public RelayCommand ExportMarkdownCommand { get; }

    public event EventHandler? FocusSearchRequested;

    public void Initialize() => RescanDatabase();

    public void RequestFocusSearch() => FocusSearchRequested?.Invoke(this, EventArgs.Empty);

    private void RescanDatabase()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            ConnectionStatus = "正在自动发现 Cherry Studio 数据库";
            StatusMessage = "正在检查默认目录、迁移配置、便携目录和正在运行的 Cherry Studio…";

            DatabasePath = _locator.Locate() ?? string.Empty;
            OnPropertyChanged(nameof(DatabaseDisplayPath));

            if (string.IsNullOrWhiteSpace(DatabasePath))
            {
                ClearProviders();
                ConnectionStatus = "未自动发现 Cherry Studio 数据库";
                StatusMessage = "已完成自动扫描。若 Cherry Studio 使用了完全自定义的数据目录，请点击“选择数据库”。";
                return;
            }

            _settings.SaveDatabasePath(DatabasePath);
            ReadDatabaseCore();
        }
        catch (Exception ex)
        {
            ClearProviders();
            ConnectionStatus = "数据库扫描失败";
            StatusMessage = ex.Message;
            AppLog.Write("Database auto-discovery failed.", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Refresh()
    {
        if (IsBusy)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(DatabasePath) || !File.Exists(DatabasePath))
        {
            RescanDatabase();
            return;
        }

        IsBusy = true;
        try
        {
            ReadDatabaseCore();
        }
        catch (Exception ex)
        {
            ConnectionStatus = "读取失败";
            StatusMessage = ex.Message;
            AppLog.Write("Database refresh failed.", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ReadDatabaseCore()
    {
        var selectedId = SelectedProvider?.Id;
        var records = _reader.Read(DatabasePath);

        Providers.Clear();
        foreach (var provider in records)
        {
            Providers.Add(provider);
        }

        SelectedProvider = Providers.FirstOrDefault(provider => provider.Id == selectedId)
                           ?? Providers.FirstOrDefault(provider => provider.IsEnabled)
                           ?? Providers.FirstOrDefault();

        ProvidersView.Refresh();
        OnPropertyChanged(nameof(ProviderCountText));
        ConnectionStatus = "已自动发现 Cherry Studio 数据库";
        LastRefreshText = DateTime.Now.ToString("HH:mm:ss");
        StatusMessage = $"已读取 {Providers.Count} 个供应商；{_locator.LastScanSummary}；数据库全程只读。";
        SetupWatcher();
    }

    private void ClearProviders()
    {
        Providers.Clear();
        SelectedProvider = null;
        ProvidersView.Refresh();
        OnPropertyChanged(nameof(ProviderCountText));
    }

    private void ChooseDatabase()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 Cherry Studio 数据库",
            Filter = "Cherry Studio 数据库|cherrystudio.sqlite|SQLite 数据库|*.sqlite;*.db|所有文件|*.*",
            CheckFileExists = true,
            FileName = "cherrystudio.sqlite"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        DatabasePath = dialog.FileName;
        _settings.SaveDatabasePath(DatabasePath);
        Refresh();
    }

    private bool FilterProvider(object item)
    {
        if (item is not ProviderRecord provider)
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(SearchText)
               || provider.SearchText.Contains(SearchText.Trim().ToLowerInvariant(), StringComparison.Ordinal);
    }

    private bool HasProvider() => SelectedProvider is not null;
    private bool HasProviderAndKey() => SelectedProvider?.SelectedApiKey is not null;
    private bool HasProviderAndModel() => SelectedProvider?.SelectedModel is not null;

    private void CopyKey()
    {
        if (SelectedProvider?.SelectedApiKey is { } key)
        {
            CopyApiKey(key);
        }
    }

    private void ToggleKey()
    {
        if (SelectedProvider?.SelectedApiKey is { } key)
        {
            key.IsRevealed = !key.IsRevealed;
        }
    }

    private void CopyApiKeyItem(object? parameter)
    {
        if (parameter is not ApiKeyRecord key)
        {
            return;
        }

        if (SelectedProvider is not null)
        {
            SelectedProvider.SelectedApiKey = key;
        }

        CopyApiKey(key);
    }

    private void ToggleApiKeyItem(object? parameter)
    {
        if (parameter is not ApiKeyRecord key)
        {
            return;
        }

        if (SelectedProvider is not null)
        {
            SelectedProvider.SelectedApiKey = key;
        }

        key.IsRevealed = !key.IsRevealed;
    }

    private void CopyApiKey(ApiKeyRecord key)
    {
        if (string.IsNullOrEmpty(key.Key))
        {
            StatusMessage = "当前 API Key 为空。";
            return;
        }

        _clipboard.Copy(key.Key);
        StatusMessage = "API Key 已复制，30 秒后仅在剪贴板内容未变化时自动清除。";
    }

    private void CopyBaseUrl()
    {
        if (string.IsNullOrWhiteSpace(SelectedProvider?.BaseUrl))
        {
            StatusMessage = "该 Base URL 由 Cherry 预设继承，当前数据库未保存可复制的覆盖值。";
            return;
        }

        _clipboard.Copy(SelectedProvider.BaseUrl);
        StatusMessage = "Base URL 已复制。";
    }

    private void CopyModel()
    {
        if (SelectedProvider?.SelectedModel is { } model)
        {
            CopyModelValue(model);
        }
    }

    private void CopyModelItem(object? parameter)
    {
        if (parameter is not ModelRecord model)
        {
            return;
        }

        if (SelectedProvider is not null)
        {
            SelectedProvider.SelectedModel = model;
        }

        CopyModelValue(model);
    }

    private void CopyModelValue(ModelRecord model)
    {
        _clipboard.Copy(model.Id);
        StatusMessage = "模型 ID 已复制。";
    }

    private void CopyTemplate(Func<ProviderRecord, string> builder)
    {
        if (SelectedProvider is null)
        {
            return;
        }

        _clipboard.Copy(builder(SelectedProvider));
        StatusMessage = "配置文本已复制，30 秒后仅在内容未变化时自动清除。";
    }

    private void OpenCustomTemplate()
    {
        if (SelectedProvider is null)
        {
            return;
        }

        var window = new TemplateWindow(SelectedProvider, _templates, _clipboard)
        {
            Owner = Application.Current.MainWindow
        };
        window.ShowDialog();
    }

    private void Export(string filter, string extension, Func<ProviderRecord, string> builder)
    {
        if (SelectedProvider is null)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "导出供应商配置",
            Filter = filter,
            DefaultExt = extension,
            AddExtension = true,
            FileName = SanitizeFileName(SelectedProvider.Name) + extension
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        File.WriteAllText(dialog.FileName, builder(SelectedProvider), new UTF8Encoding(false));
        StatusMessage = $"已导出：{dialog.FileName}";
    }

    private void SetupWatcher()
    {
        _watcher?.Dispose();
        _refreshTimer?.Dispose();

        var directory = Path.GetDirectoryName(DatabasePath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return;
        }

        _refreshTimer = new System.Threading.Timer(
            _ => Application.Current.Dispatcher.Invoke(Refresh),
            null,
            Timeout.Infinite,
            Timeout.Infinite);

        _watcher = new FileSystemWatcher(directory, "cherrystudio.sqlite*")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            EnableRaisingEvents = true
        };

        FileSystemEventHandler changed = (_, _) => _refreshTimer?.Change(700, Timeout.Infinite);
        RenamedEventHandler renamed = (_, _) => _refreshTimer?.Change(700, Timeout.Infinite);
        _watcher.Changed += changed;
        _watcher.Created += changed;
        _watcher.Deleted += changed;
        _watcher.Renamed += renamed;
    }

    private void NotifyCommandStates()
    {
        foreach (var command in new[]
                 {
                     RefreshCommand, RescanDatabaseCommand, ChooseDatabaseCommand,
                     CopyKeyCommand, ToggleKeyCommand, CopyApiKeyItemCommand, ToggleApiKeyItemCommand,
                     CopyBaseUrlCommand, CopyModelCommand, CopyModelItemCommand,
                     CopyAllCommand, CopyClaudeCommand, CopyOpenAiCommand, CopyGeminiCommand,
                     CopyCodexCommand, OpenCustomTemplateCommand, ExportJsonCommand, ExportMarkdownCommand
                 })
        {
            command.NotifyCanExecuteChanged();
        }
    }

    private static string SanitizeFileName(string value)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(invalid, '_');
        }

        return string.IsNullOrWhiteSpace(value) ? "provider" : value;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _watcher?.Dispose();
        _refreshTimer?.Dispose();
    }
}
