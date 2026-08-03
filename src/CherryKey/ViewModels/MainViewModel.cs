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
    private string _statusMessage = "正在自动查找 Cherry Studio v1/v2 数据源…";
    private string _lastRefreshText = "尚未刷新";
    private bool _isBusy;
    private FileSystemWatcher? _watcher;
    private System.Threading.Timer? _refreshTimer;
    private CancellationTokenSource? _operationCts;
    private bool _disposed;

    public MainViewModel()
    {
        _locator = new CherryDatabaseLocator(_settings);
        ProvidersView = CollectionViewSource.GetDefaultView(Providers);
        ProvidersView.Filter = FilterProvider;

        RefreshCommand = new RelayCommand(() => _ = RefreshAsync(), () => !IsBusy);
        RescanDatabaseCommand = new RelayCommand(() => _ = RescanDatabaseAsync(), () => !IsBusy);
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
                OnPropertyChanged(nameof(DataSourceTypeText));
            }
        }
    }

    public string DatabaseDisplayPath => string.IsNullOrWhiteSpace(DatabasePath)
        ? _locator.LastScanSummary
        : DatabasePath;

    public string ConnectionBadgeText => string.IsNullOrWhiteSpace(DatabasePath) ? "未连接" : "已连接";
    public string DataSourceTypeText => CherryDataSource.GetDisplayName(DatabasePath);
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

    public Task InitializeAsync() => RescanDatabaseAsync();

    public void RequestFocusSearch() => FocusSearchRequested?.Invoke(this, EventArgs.Empty);

    private async Task RescanDatabaseAsync()
    {
        if (IsBusy)
        {
            return;
        }

        var operation = BeginOperation(TimeSpan.FromSeconds(12));
        var token = operation.Token;
        IsBusy = true;

        try
        {
            ConnectionStatus = "正在自动发现 Cherry Studio 数据源";
            StatusMessage = "正在后台检查 v1 LevelDB、v2 SQLite、迁移配置和便携目录；窗口不会被阻塞。";
            AppLog.Write("Background data-source scan requested.");

            var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Exception? lastReadError = null;

            for (var attempt = 1; attempt <= 4; attempt++)
            {
                token.ThrowIfCancellationRequested();
                var candidate = await Task.Run(
                    () => _locator.Locate(token, excluded),
                    token);

                OnPropertyChanged(nameof(DatabaseDisplayPath));
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    break;
                }

                var fullCandidate = Path.GetFullPath(candidate);
                ConnectionStatus = $"正在读取 {CherryDataSource.GetDisplayName(fullCandidate)}";
                StatusMessage = fullCandidate;
                AppLog.Write($"Trying discovered data source (attempt {attempt}): {fullCandidate}");

                try
                {
                    var records = await Task.Run(() => _reader.Read(fullCandidate))
                        .WaitAsync(TimeSpan.FromSeconds(6), token);
                    token.ThrowIfCancellationRequested();

                    ApplyRecords(fullCandidate, records);
                    _settings.SaveDataSourcePath(fullCandidate);
                    AppLog.Write($"Data source loaded successfully. Providers={records.Count}; Path={fullCandidate}");
                    return;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    lastReadError = ex;
                    excluded.Add(fullCandidate);
                    AppLog.Write($"Rejected discovered data source and continuing: {fullCandidate}", ex);
                }
            }

            DatabasePath = string.Empty;
            ClearProviders();
            ConnectionStatus = "未发现可读取的 Cherry Studio 数据源";
            StatusMessage = lastReadError is null
                ? "快速扫描已完成。仍未发现时请点击“选择数据源”。"
                : $"发现候选文件，但均无法解析：{lastReadError.Message}。详细诊断：{AppLog.LogPath}";
        }
        catch (OperationCanceledException)
        {
            DatabasePath = string.Empty;
            ClearProviders();
            ConnectionStatus = "自动扫描已停止";
            StatusMessage = "自动扫描超过安全时限，已停止以避免界面卡死。请点击“选择数据源”手动指定。";
            AppLog.Write("Background data-source scan timed out or was cancelled.");
        }
        catch (Exception ex)
        {
            DatabasePath = string.Empty;
            ClearProviders();
            ConnectionStatus = "数据源扫描失败";
            StatusMessage = $"{ex.Message}；详细诊断：{AppLog.LogPath}";
            AppLog.Write("Cherry data-source auto-discovery failed.", ex);
        }
        finally
        {
            EndOperation(operation);
            IsBusy = false;
        }
    }

    private async Task RefreshAsync()
    {
        if (IsBusy)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(DatabasePath) || !CherryDataSource.IsValid(DatabasePath))
        {
            await RescanDatabaseAsync();
            return;
        }

        var operation = BeginOperation(TimeSpan.FromSeconds(10));
        var token = operation.Token;
        IsBusy = true;

        try
        {
            ConnectionStatus = $"正在刷新 {CherryDataSource.GetDisplayName(DatabasePath)}";
            var sourcePath = DatabasePath;
            var records = await Task.Run(() => _reader.Read(sourcePath))
                .WaitAsync(TimeSpan.FromSeconds(8), token);
            token.ThrowIfCancellationRequested();
            ApplyRecords(sourcePath, records);
            _settings.SaveDataSourcePath(sourcePath);
            AppLog.Write($"Data source refreshed successfully. Providers={records.Count}; Path={sourcePath}");
        }
        catch (OperationCanceledException)
        {
            ConnectionStatus = "刷新超时";
            StatusMessage = "读取超过 10 秒，已停止等待；窗口仍可继续使用。";
            AppLog.Write("Data-source refresh timed out or was cancelled.");
        }
        catch (Exception ex)
        {
            ConnectionStatus = "读取失败";
            StatusMessage = $"{ex.Message}；详细诊断：{AppLog.LogPath}";
            AppLog.Write("Cherry data-source refresh failed.", ex);
        }
        finally
        {
            EndOperation(operation);
            IsBusy = false;
        }
    }

    private void ApplyRecords(string sourcePath, IReadOnlyList<ProviderRecord> records)
    {
        var selectedId = SelectedProvider?.Id;
        DatabasePath = sourcePath;

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
        ConnectionStatus = $"已连接 {CherryDataSource.GetDisplayName(DatabasePath)}";
        LastRefreshText = DateTime.Now.ToString("HH:mm:ss");
        StatusMessage = $"已读取 {Providers.Count} 个供应商；{_locator.LastScanSummary}；数据源全程只读。";
        SetupWatcher();
    }

    private CancellationTokenSource BeginOperation(TimeSpan timeout)
    {
        _operationCts?.Cancel();
        _operationCts?.Dispose();
        var operation = new CancellationTokenSource(timeout);
        _operationCts = operation;
        return operation;
    }

    private void EndOperation(CancellationTokenSource operation)
    {
        if (ReferenceEquals(_operationCts, operation))
        {
            _operationCts = null;
        }

        operation.Dispose();
    }

    private void ClearProviders()
    {
        _watcher?.Dispose();
        _watcher = null;
        _refreshTimer?.Dispose();
        _refreshTimer = null;
        Providers.Clear();
        SelectedProvider = null;
        ProvidersView.Refresh();
        OnPropertyChanged(nameof(ProviderCountText));
    }

    private void ChooseDatabase()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 Cherry Studio 数据源",
            Filter = "Cherry Studio v1/v2 数据源|cherrystudio.sqlite;CURRENT;*.ldb;*.log|v2 SQLite|cherrystudio.sqlite;*.sqlite;*.db|v1 LevelDB 文件|CURRENT;*.ldb;*.log|所有文件|*.*",
            CheckFileExists = true,
            FileName = "cherrystudio.sqlite"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var normalized = CherryDataSource.NormalizeSelectedPath(dialog.FileName);
        if (!CherryDataSource.IsValid(normalized))
        {
            ConnectionStatus = "无法识别所选数据源";
            StatusMessage = "v1 请在 Local Storage\\leveldb 中选择 CURRENT 或任意 .ldb/.log；v2 请选择 cherrystudio.sqlite。";
            return;
        }

        DatabasePath = normalized!;
        _ = RefreshAsync();
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

        var kind = CherryDataSource.GetKind(DatabasePath);
        var directory = kind == CherryDataSourceKind.V1LevelDb
            ? DatabasePath
            : Path.GetDirectoryName(DatabasePath);
        var filter = kind == CherryDataSourceKind.V1LevelDb ? "*" : "cherrystudio.sqlite*";

        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return;
        }

        _refreshTimer = new System.Threading.Timer(
            _ => Application.Current.Dispatcher.BeginInvoke(new Action(() => _ = RefreshAsync())),
            null,
            Timeout.Infinite,
            Timeout.Infinite);

        _watcher = new FileSystemWatcher(directory, filter)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            EnableRaisingEvents = true
        };

        FileSystemEventHandler changed = (_, _) => _refreshTimer?.Change(900, Timeout.Infinite);
        RenamedEventHandler renamed = (_, _) => _refreshTimer?.Change(900, Timeout.Infinite);
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
        _operationCts?.Cancel();
        _operationCts?.Dispose();
        _operationCts = null;
        _watcher?.Dispose();
        _refreshTimer?.Dispose();
    }
}
