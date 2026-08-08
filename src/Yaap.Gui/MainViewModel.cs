using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using Yaap.Core;

namespace Yaap.Gui;

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private const int MaxRecentTargets = 10;
    private const int ConfigurationHistoryLimit = 1000;
    private const int DefaultHistoryLimit = 500;

    private readonly IProfileRunner _profileRunner;
    private readonly Func<string, CancellationToken, Task<TargetInfo>> _targetDiscoverer;
    private readonly Func<string, CancellationToken, Task<BinlogAnalysis>> _binlogAnalyzer;
    private readonly Func<Guid, CancellationToken, Task<ProfileRun>> _historyLoader;
    private readonly Func<RunSummary, bool> _confirmDelete;
    private readonly Func<bool> _confirmDeleteAll;
    private readonly TimeSpan _targetDiscoveryDelay;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private IReadOnlyDictionary<string, string> _latestConfigurationByTarget =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _profileCancellation;
    private CancellationTokenSource? _operationCancellation;
    private CancellationTokenSource? _targetDiscoveryCancellation;
    private CancellationTokenSource? _historyRefreshCancellation;
    private CancellationTokenSource? _resultFilterCancellation;
    private CancellationTokenSource? _labelSaveCancellation;
    private Guid? _labelSaveId;
    private Task _targetDiscoveryTask = Task.CompletedTask;
    private Task _historyRefreshTask = Task.CompletedTask;
    private Task _resultFilterTask = Task.CompletedTask;
    private Task _labelSaveTask = Task.CompletedTask;
    private Task _shutdownTask = Task.CompletedTask;
    private long _targetDiscoveryGeneration;
    private Guid? _loadingHistoryId;
    private string _targetPath = string.Empty;
    private string _configuration = string.Empty;
    private string _historyPath = string.Empty;
    private string _artifactsPath = string.Empty;
    private string _searchText = string.Empty;
    private string _historyStatus = "すべて";
    private string _historyFrom = string.Empty;
    private string _historyTo = string.Empty;
    private string _historyLimit = DefaultHistoryLimit.ToString(System.Globalization.CultureInfo.InvariantCulture);
    private string _resultFilter = string.Empty;
    private string _statusText = "準備完了";
    private string _selectedHistoryLabel = string.Empty;
    private string _committedHistoryLabel = string.Empty;
    private string _exportPath = string.Empty;
    private string _binlogPath = string.Empty;
    private bool _isolated = true;
    private bool _restore = true;
    private bool _cleanBeforeEach = true;
    private bool _isRunning;
    private bool _isOperationRunning;
    private bool _showOperationBusySurface;
    private bool _isDiscoveringTarget;
    private bool _hasValidTarget;
    private bool _historyInitialized;
    private bool _historyRefreshPending;
    private bool _disposed;
    private ProcessDidNotTerminateException? _shutdownBlocker;
    private int _warmupCount = 1;
    private int _iterationCount = 3;
    private int _retentionCount = 50;
    private ProfileMode _selectedMode = ProfileMode.Warm;
    private ThemeOption _selectedTheme;
    private RecentTarget? _selectedRecentTarget;
    private RunSummary? _selectedHistory;
    private RunDiagnostic? _selectedDiagnostic;
    private HistoryChoice? _selectedBaseline;
    private HistoryChoice? _selectedCandidate;
    private ProfileRun? _selectedRun;
    private ComparisonResult? _comparison;
    private IReadOnlyList<StatisticalMetric> _analyzers = Array.Empty<StatisticalMetric>();
    private IReadOnlyList<GeneratorMetric> _generators = Array.Empty<GeneratorMetric>();
    private IReadOnlyList<ResultTreeNode> _analyzerTree = Array.Empty<ResultTreeNode>();
    private IReadOnlyList<ResultTreeNode> _generatorTree = Array.Empty<ResultTreeNode>();
    private readonly Stack<LabelEdit> _labelUndo = new();
    private readonly Stack<LabelEdit> _labelRedo = new();
    private bool _isApplyingLabelHistory;
    private string? _statusTitleOverride;
    private Wpf.Ui.Controls.InfoBarSeverity? _statusSeverityOverride;

    public MainViewModel(
        IProfileRunner? profileRunner = null,
        Func<string, CancellationToken, Task<TargetInfo>>? targetDiscoverer = null,
        TimeSpan? targetDiscoveryDelay = null,
        Func<RunSummary, bool>? confirmDelete = null,
        Func<bool>? confirmDeleteAll = null,
        Func<string, CancellationToken, Task<BinlogAnalysis>>? binlogAnalyzer = null,
        Func<Guid, CancellationToken, Task<ProfileRun>>? historyLoader = null)
    {
        _profileRunner = profileRunner ?? new ProfileRunner();
        _targetDiscoverer = targetDiscoverer ?? TargetDiscovery.DiscoverAsync;
        _binlogAnalyzer = binlogAnalyzer ?? ((path, cancellationToken) =>
            new BinlogAnalyzer().AnalyzeAsync(path, cancellationToken));
        _historyLoader = historyLoader ?? ((id, cancellationToken) =>
            Store().LoadAsync(id, cancellationToken));
        _confirmDelete = confirmDelete ?? ConfirmDelete;
        _confirmDeleteAll = confirmDeleteAll ?? ConfirmDeleteAll;
        _targetDiscoveryDelay = targetDiscoveryDelay ?? TimeSpan.FromMilliseconds(350);
        _selectedTheme = ThemeOptions[0];
        BrowseCommand = new RelayCommand(Browse, () => !IsBusy);
        BrowseBinlogCommand = new RelayCommand(BrowseBinlog, () => !IsBusy);
        BrowseExportCommand = new RelayCommand(BrowseExport, () => !IsBusy);
        BrowseHistoryDirectoryCommand = new RelayCommand(BrowseHistoryDirectory, () => !IsBusy);
        BrowseArtifactsDirectoryCommand = new RelayCommand(BrowseArtifactsDirectory, () => !IsBusy);
        OpenHistoryDirectoryCommand = new RelayCommand(OpenHistoryDirectory, () => !IsBusy);
        StartCommand = new AsyncRelayCommand(StartAsync, CanStart, SetError);
        ClearHistoryPeriodCommand = new RelayCommand(
            ClearHistoryPeriod,
            () => !IsBusy && HasHistoryPeriod);
        RefreshHistoryCommand = CreateOperationCommand("履歴を更新しています。", RefreshHistoryAsync);
        LoadSelectedCommand = CreateOperationCommand(
            "選択した測定結果を読み込んでいます。",
            LoadSelectedAsync,
            () => SelectedHistory is not null,
            showBusySurface: false);
        DeleteSelectedCommand = CreateOperationCommand(
            "履歴を削除しています。",
            DeleteSelectedAsync,
            () => SelectedHistory is not null);
        DeleteAllHistoryCommand = CreateOperationCommand(
            "すべての履歴を削除しています。",
            DeleteAllHistoryAsync);
        UndoLabelCommand = CreateOperationCommand(
            "ラベルの変更を元に戻しています。",
            UndoLabelAsync,
            () => _labelUndo.Count > 0);
        RedoLabelCommand = CreateOperationCommand(
            "ラベルの変更をやり直しています。",
            RedoLabelAsync,
            () => _labelRedo.Count > 0);
        CompareCommand = CreateOperationCommand(
            "測定結果を比較しています。",
            CompareAsync,
            () => SelectedBaseline is not null &&
                SelectedCandidate is not null &&
                SelectedBaseline.Id != SelectedCandidate.Id);
        ExportCommand = CreateOperationCommand(
            "測定結果を出力しています。",
            ExportAsync,
            () => SelectedRun is not null);
        AnalyzeBinlogCommand = CreateOperationCommand(
            "binlogを解析しています。",
            AnalyzeBinlogAsync,
            () => !string.IsNullOrWhiteSpace(BinlogPath));
        CancelCommand = new RelayCommand(CancelActiveOperation, () => IsBusy);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<RunSummary> History { get; } = new();

    public ObservableCollection<HistoryChoice> ComparisonChoices { get; } = new();

    public ObservableCollection<string> Configurations { get; } = new();

    public ObservableCollection<RecentTarget> RecentTargets { get; } = new();

    public IReadOnlyList<ProfileMode> Modes { get; } = Enum.GetValues<ProfileMode>();

    public static IReadOnlyList<ThemeOption> ThemeOptions { get; } =
        new[]
        {
            new ThemeOption(AppThemeMode.Auto, "自動"),
            new ThemeOption(AppThemeMode.Light, "ライト"),
            new ThemeOption(AppThemeMode.Dark, "ダーク"),
        };

    public IReadOnlyList<string> HistoryStatuses { get; } =
        new[] { "すべて", "実行中", "成功", "部分結果", "失敗", "キャンセル" };

    public string TargetPath
    {
        get => _targetPath;
        set
        {
            if (Set(ref _targetPath, value))
            {
                if (TargetDiscovery.IsSupportedPath(value))
                {
                    AddOrPromoteRecentTarget(value, DateTimeOffset.UtcNow);
                }

                RecentTarget? matchingRecentTarget = RecentTargets.FirstOrDefault(item =>
                    PathsEqual(item.Path, value));
                Set(ref _selectedRecentTarget, matchingRecentTarget, nameof(SelectedRecentTarget));
                _hasValidTarget = false;
                Configurations.Clear();
                Configuration = string.Empty;
                QueueTargetDiscovery();
                RaiseCommandStates();
            }
        }
    }

    public string Configuration
    {
        get => _configuration;
        set
        {
            if (Set(ref _configuration, value ?? string.Empty))
            {
                if (!IsDiscoveringTarget && _hasValidTarget)
                {
                    SetStatusOverride(
                        CurrentMeasurementState.Text,
                        Wpf.Ui.Controls.InfoBarSeverity.Informational);
                    StatusText = string.IsNullOrWhiteSpace(Configuration)
                        ? "ビルド構成を入力または選択してください。"
                        : Configurations.Any(configuration => configuration.Equals(
                            Configuration,
                            StringComparison.OrdinalIgnoreCase))
                            ? $"{Configuration} 構成で測定できます。"
                            : "対象からは検出されていません。入力した構成名を dotnet build に渡します。";
                }
                else
                {
                    ClearStatusOverride();
                }

                RaiseCommandStates();
            }
        }
    }

    public string HistoryPath
    {
        get => _historyPath;
        set
        {
            if (Set(ref _historyPath, value))
            {
                _historyInitialized = false;
                _latestConfigurationByTarget =
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                OnPropertyChanged(nameof(AdvancedSettingsSummary));
                OnPropertyChanged(nameof(HistoryDirectoryPath));
                if (_hasValidTarget && !IsBusy)
                {
                    QueueTargetDiscovery();
                }
            }
        }
    }

    public string ArtifactsPath
    {
        get => _artifactsPath;
        set
        {
            if (Set(ref _artifactsPath, value))
            {
                OnPropertyChanged(nameof(AdvancedSettingsSummary));
            }
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (Set(ref _searchText, value))
            {
                QueueHistoryRefresh();
            }
        }
    }

    public string HistoryStatus
    {
        get => _historyStatus;
        set
        {
            if (Set(ref _historyStatus, value))
            {
                QueueHistoryRefresh();
            }
        }
    }

    public string HistoryFrom
    {
        get => _historyFrom;
        set
        {
            if (Set(ref _historyFrom, value))
            {
                ((RelayCommand)ClearHistoryPeriodCommand).RaiseCanExecuteChanged();
                if (string.IsNullOrWhiteSpace(value) || TryParseHistoryDateText(value, out _))
                {
                    OnPropertyChanged(nameof(HistoryFromDate));
                    QueueHistoryRefresh();
                }
            }
        }
    }

    public string HistoryTo
    {
        get => _historyTo;
        set
        {
            if (Set(ref _historyTo, value))
            {
                ((RelayCommand)ClearHistoryPeriodCommand).RaiseCanExecuteChanged();
                if (string.IsNullOrWhiteSpace(value) || TryParseHistoryDateText(value, out _))
                {
                    OnPropertyChanged(nameof(HistoryToDate));
                    QueueHistoryRefresh();
                }
            }
        }
    }

    public DateTime? HistoryFromDate
    {
        get => TryParseHistoryDateText(_historyFrom, out DateTime date) ? date.Date : null;
        set => HistoryFrom = FormatHistoryDate(value);
    }

    public DateTime? HistoryToDate
    {
        get => TryParseHistoryDateText(_historyTo, out DateTime date) ? date.Date : null;
        set => HistoryTo = FormatHistoryDate(value);
    }

    public string HistoryLimit
    {
        get => _historyLimit;
        set
        {
            if (Set(ref _historyLimit, value))
            {
                QueueHistoryRefresh();
            }
        }
    }

    public string ResultFilter
    {
        get => _resultFilter;
        set
        {
            if (Set(ref _resultFilter, value))
            {
                QueueResultFilter(TimeSpan.FromMilliseconds(180));
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public string SelectedHistoryLabel
    {
        get => _selectedHistoryLabel;
        set
        {
            value ??= string.Empty;
            string limited = value.Length <= HistoryStore.MaximumLabelLength
                ? value
                : value[..HistoryStore.MaximumLabelLength];
            if (Set(ref _selectedHistoryLabel, limited))
            {
                QueueLabelSave(TimeSpan.FromMilliseconds(400));
            }
        }
    }

    public HistoryChoice? SelectedBaseline
    {
        get => _selectedBaseline;
        set
        {
            if (Set(ref _selectedBaseline, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public HistoryChoice? SelectedCandidate
    {
        get => _selectedCandidate;
        set
        {
            if (Set(ref _selectedCandidate, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public string ExportPath
    {
        get => _exportPath;
        set => Set(ref _exportPath, value);
    }

    public string BinlogPath
    {
        get => _binlogPath;
        set
        {
            if (Set(ref _binlogPath, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public bool Isolated
    {
        get => _isolated;
        set
        {
            if (Set(ref _isolated, value))
            {
                OnPropertyChanged(nameof(AdvancedSettingsSummary));
            }
        }
    }

    public bool Restore
    {
        get => _restore;
        set
        {
            if (Set(ref _restore, value))
            {
                OnPropertyChanged(nameof(AdvancedSettingsSummary));
            }
        }
    }

    public bool CleanBeforeEach
    {
        get => _cleanBeforeEach;
        set => Set(ref _cleanBeforeEach, value);
    }

    public int WarmupCount
    {
        get => _warmupCount;
        set => Set(ref _warmupCount, value);
    }

    public int IterationCount
    {
        get => _iterationCount;
        set => Set(ref _iterationCount, value);
    }

    public int RetentionCount
    {
        get => _retentionCount;
        set => Set(ref _retentionCount, value);
    }

    public ProfileMode SelectedMode
    {
        get => _selectedMode;
        set
        {
            if (Set(ref _selectedMode, value) && value != ProfileMode.Custom)
            {
                ProfileOptions defaults = ProfileOptions.ForMode(TargetPath, value);
                WarmupCount = defaults.WarmupCount;
                IterationCount = defaults.IterationCount;
                CleanBeforeEach = defaults.CleanBeforeEach;
            }
        }
    }

    public ThemeOption SelectedTheme
    {
        get => _selectedTheme;
        set
        {
            if (value is not null)
            {
                Set(ref _selectedTheme, value);
            }
        }
    }

    public RecentTarget? SelectedRecentTarget
    {
        get => _selectedRecentTarget;
        set
        {
            if (Set(ref _selectedRecentTarget, value) && value is not null &&
                !PathsEqual(TargetPath, value.Path))
            {
                TargetPath = value.Path;
            }
        }
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (Set(ref _isRunning, value))
            {
                OnPropertyChanged(nameof(IsBusy));
                OnPropertyChanged(nameof(IsBusySurfaceVisible));
                OnPropertyChanged(nameof(BusyTitleText));
                RaiseCommandStates();
            }
        }
    }

    public bool IsOperationRunning
    {
        get => _isOperationRunning;
        private set
        {
            if (Set(ref _isOperationRunning, value))
            {
                OnPropertyChanged(nameof(IsBusy));
                OnPropertyChanged(nameof(IsBusySurfaceVisible));
                OnPropertyChanged(nameof(BusyTitleText));
                RaiseCommandStates();
            }
        }
    }

    public bool IsBusy => IsRunning || IsOperationRunning;

    public bool IsBusySurfaceVisible => IsRunning ||
        (IsOperationRunning && _showOperationBusySurface);

    public bool IsInlineOperationVisible => IsOperationRunning && !_showOperationBusySurface;

    public bool IsDiscoveringTarget
    {
        get => _isDiscoveringTarget;
        private set
        {
            if (Set(ref _isDiscoveringTarget, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public RunSummary? SelectedHistory
    {
        get => _selectedHistory;
        set
        {
            if (!_isApplyingLabelHistory && _selectedHistory?.Id != value?.Id)
            {
                FlushPendingLabelSave();
            }

            if (Set(ref _selectedHistory, value))
            {
                if (_loadingHistoryId is Guid loadingId && value?.Id != loadingId)
                {
                    _operationCancellation?.Cancel();
                }

                _selectedHistoryLabel = value?.Label ?? string.Empty;
                _committedHistoryLabel = _selectedHistoryLabel;
                OnPropertyChanged(nameof(SelectedHistoryLabel));
                RaiseCommandStates();
            }
        }
    }

    public ProfileRun? SelectedRun
    {
        get => _selectedRun;
        private set => SetSelectedRun(value);
    }

    public RunDiagnostic? SelectedDiagnostic
    {
        get => _selectedDiagnostic;
        set => Set(ref _selectedDiagnostic, value);
    }

    public ComparisonResult? Comparison
    {
        get => _comparison;
        private set
        {
            if (Set(ref _comparison, value))
            {
                OnPropertyChanged(nameof(ComparisonSummary));
            }
        }
    }

    public string ComparisonSummary => Comparison is null
        ? string.Empty
        : $"生成ファイル数差: {Comparison.GeneratedFileCountDelta:+#;-#;0}、バイト数差: {Comparison.GeneratedByteCountDelta:+#;-#;0}";

    public string MeasurementStateText => CurrentMeasurementState.Text;

    public string StatusTitleText => _statusTitleOverride ?? (SelectedRun?.Status switch
    {
        RunStatus.Failed => "測定失敗",
        RunStatus.Partial => "部分結果",
        RunStatus.Canceled => "測定キャンセル",
        RunStatus.Succeeded => "測定完了",
        _ => MeasurementStateText,
    });

    public Wpf.Ui.Controls.InfoBarSeverity StatusSeverity => _statusSeverityOverride ?? (SelectedRun?.Status switch
    {
        RunStatus.Succeeded => Wpf.Ui.Controls.InfoBarSeverity.Success,
        RunStatus.Partial or RunStatus.Canceled => Wpf.Ui.Controls.InfoBarSeverity.Warning,
        RunStatus.Failed => Wpf.Ui.Controls.InfoBarSeverity.Error,
        _ => Wpf.Ui.Controls.InfoBarSeverity.Informational,
    });

    public string BusyTitleText => IsRunning ? MeasurementStateText : "処理中";

    public string AdvancedSettingsSummary =>
        $"詳細設定（restore: {(Restore ? "有効" : "無効")}、分離出力: {(Isolated ? "有効" : "無効")}）";

    public string HistoryDirectoryPath => Store().RootPath;

    public string HistoryCountText => $"履歴 {History.Count} 件";

    public string ApplicationVersion { get; } =
        typeof(MainViewModel).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion.Split('+')[0] ?? "不明";

    public IReadOnlyList<StatisticalMetric> Analyzers => _analyzers;

    public IReadOnlyList<GeneratorMetric> Generators => _generators;

    public IReadOnlyList<ResultTreeNode> AnalyzerTree => _analyzerTree;

    public IReadOnlyList<ResultTreeNode> GeneratorTree => _generatorTree;

    public IReadOnlyList<RunDiagnostic> Diagnostics => SelectedRun?.Diagnostics ?? Array.Empty<RunDiagnostic>();

    public ICommand StartCommand { get; }

    public ICommand BrowseCommand { get; }

    public ICommand BrowseBinlogCommand { get; }

    public ICommand BrowseExportCommand { get; }

    public ICommand BrowseHistoryDirectoryCommand { get; }

    public ICommand BrowseArtifactsDirectoryCommand { get; }

    public ICommand OpenHistoryDirectoryCommand { get; }

    public ICommand CancelCommand { get; }

    public ICommand RefreshHistoryCommand { get; }

    public ICommand ClearHistoryPeriodCommand { get; }

    public ICommand LoadSelectedCommand { get; }

    public ICommand DeleteSelectedCommand { get; }

    public ICommand DeleteAllHistoryCommand { get; }

    public ICommand UndoLabelCommand { get; }

    public ICommand RedoLabelCommand { get; }

    public ICommand CompareCommand { get; }

    public ICommand ExportCommand { get; }

    public ICommand AnalyzeBinlogCommand { get; }

    public Task WaitForHistoryRefreshAsync() => _historyRefreshTask;

    public Task WaitForLabelSaveAsync() => _labelSaveTask;

    public Task WaitForResultFilterAsync() => _resultFilterTask;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCancellation.Token);
        try
        {
            await RefreshHistoryAsync(linked.Token);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SetError(exception);
        }
    }

    public Task WaitForTargetDiscoveryAsync() => _targetDiscoveryTask;

    public Task ShutdownAsync()
    {
        if (_disposed)
        {
            return Task.CompletedTask;
        }

        if (!_shutdownTask.IsCompleted)
        {
            return _shutdownTask;
        }

        _shutdownTask = ShutdownCoreAsync();
        return _shutdownTask;
    }

    public bool CanAcceptDroppedTarget(IReadOnlyList<string> paths)
    {
        return !IsBusy && paths.Count == 1 && TargetDiscovery.IsSupportedPath(paths[0]);
    }

    public bool TrySetDroppedTarget(IReadOnlyList<string> paths)
    {
        if (IsBusy)
        {
            StatusText = "処理中は対象を変更できません。";
            return false;
        }

        if (paths.Count != 1)
        {
            StatusText = "1つの .sln、.slnx、または .csproj をドロップしてください。";
            return false;
        }

        if (!TargetDiscovery.IsSupportedPath(paths[0]))
        {
            StatusText = "存在する .sln、.slnx、または .csproj のみドロップできます。";
            return false;
        }

        TargetPath = Path.GetFullPath(paths[0]);
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetimeCancellation.Cancel();
        CancellationTokenSource? discovery = Interlocked.Exchange(ref _targetDiscoveryCancellation, null);
        discovery?.Cancel();
        CancelAndDispose(ref _historyRefreshCancellation);
        CancelAndDispose(ref _resultFilterCancellation);
        CancelAndDispose(ref _labelSaveCancellation);
        _profileCancellation?.Cancel();
        _operationCancellation?.Cancel();
        _lifetimeCancellation.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task ShutdownCoreAsync()
    {
        CancelActiveOperation();
        CancellationTokenSource? discovery = Interlocked.Exchange(ref _targetDiscoveryCancellation, null);
        discovery?.Cancel();
        CancelAndDispose(ref _historyRefreshCancellation);
        CancelAndDispose(ref _resultFilterCancellation);

        Task[] activeCommands = AsyncCommands()
            .Select(command => command.Completion)
            .ToArray();
        await Task.WhenAll(activeCommands);
        await _labelSaveTask;
        if (_shutdownBlocker is { } blocker && IsProcessRunning(blocker.ProcessId))
        {
            throw blocker;
        }

        _shutdownBlocker = null;

        Dispose();
    }

    private static bool IsProcessRunning(int processId)
    {
        try
        {
            using System.Diagnostics.Process process = System.Diagnostics.Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private IEnumerable<AsyncRelayCommand> AsyncCommands()
    {
        foreach (ICommand command in new[]
                 {
                     StartCommand,
                     RefreshHistoryCommand,
                     LoadSelectedCommand,
                     DeleteSelectedCommand,
                     DeleteAllHistoryCommand,
                     UndoLabelCommand,
                     RedoLabelCommand,
                     CompareCommand,
                     ExportCommand,
                     AnalyzeBinlogCommand,
                 })
        {
            if (command is AsyncRelayCommand asyncCommand)
            {
                yield return asyncCommand;
            }
        }
    }

    private void QueueTargetDiscovery()
    {
        long generation = Interlocked.Increment(ref _targetDiscoveryGeneration);
        CancellationTokenSource? previous = Interlocked.Exchange(ref _targetDiscoveryCancellation, null);
        previous?.Cancel();
        if (_disposed || IsBusy)
        {
            return;
        }

        string path = TargetPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            IsDiscoveringTarget = false;
            _hasValidTarget = false;
            StatusText = "測定対象を指定してください。";
            _targetDiscoveryTask = Task.CompletedTask;
            return;
        }

        CancellationTokenSource cancellation = new();
        _targetDiscoveryCancellation = cancellation;
        _targetDiscoveryTask = DiscoverTargetAsync(path, generation, cancellation);
    }

    private async Task DiscoverTargetAsync(
        string path,
        long generation,
        CancellationTokenSource cancellation)
    {
        try
        {
            IsDiscoveringTarget = true;
            StatusText = "構成を自動検出しています。";
            await Task.Delay(_targetDiscoveryDelay, cancellation.Token);
            if (!TargetDiscovery.HasSupportedExtension(path))
            {
                if (IsCurrentDiscovery(path, generation))
                {
                    _hasValidTarget = false;
                    Configurations.Clear();
                    StatusText = "存在する .sln、.slnx、または .csproj を指定してください。";
                }

                return;
            }

            TargetInfo target = await Task.Run(
                () => _targetDiscoverer(path, cancellation.Token),
                cancellation.Token);
            if (!IsCurrentDiscovery(path, generation))
            {
                return;
            }

            await EnsureConfigurationHistoryAsync(cancellation.Token);
            if (!IsCurrentDiscovery(path, generation))
            {
                return;
            }

            string[] discovered = target.Configurations
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(configuration => configuration, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            Configurations.Clear();
            foreach (string configuration in discovered)
            {
                Configurations.Add(configuration);
            }

            _hasValidTarget = discovered.Length > 0;
            Configuration = SelectPreferredConfiguration(path, discovered);
            StatusText = _hasValidTarget
                ? $"構成を {Configurations.Count} 件検出し、{Configuration} を選択しました。"
                : "利用できるビルド構成を検出できませんでした。";
            RaiseCommandStates();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (IsCurrentDiscovery(path, generation))
            {
                _hasValidTarget = false;
                Configurations.Clear();
                Configuration = string.Empty;
                SetError(exception);
            }
        }
        finally
        {
            if (ReferenceEquals(_targetDiscoveryCancellation, cancellation))
            {
                _targetDiscoveryCancellation = null;
                IsDiscoveringTarget = false;
            }

            cancellation.Dispose();
        }
    }

    private bool IsCurrentDiscovery(string path, long generation)
    {
        return !_disposed &&
            generation == Interlocked.Read(ref _targetDiscoveryGeneration) &&
            path.Equals(TargetPath, StringComparison.Ordinal);
    }

    private bool CanStart()
    {
        return !IsOperationRunning && CurrentMeasurementState.CanStart;
    }

    private MeasurementStatePresentation CurrentMeasurementState =>
        MeasurementStatePresentation.Create(
            IsRunning,
            IsDiscoveringTarget,
            _hasValidTarget,
            TargetPath,
            Configuration,
            Configurations);

    private void Browse()
    {
        Microsoft.Win32.OpenFileDialog dialog = new()
        {
            CheckFileExists = true,
            Filter = "C# ビルド対象 (*.sln;*.slnx;*.csproj)|*.sln;*.slnx;*.csproj|すべてのファイル (*.*)|*.*",
            Multiselect = false,
            Title = "測定対象を選択",
        };
        if (dialog.ShowDialog() == true)
        {
            TargetPath = dialog.FileName;
        }
    }

    private void BrowseBinlog()
    {
        Microsoft.Win32.OpenFileDialog dialog = new()
        {
            CheckFileExists = true,
            Filter = "MSBuildバイナリログ (*.binlog)|*.binlog|すべてのファイル (*.*)|*.*",
            Multiselect = false,
            Title = "解析するbinlogを選択",
        };
        if (dialog.ShowDialog() == true)
        {
            BinlogPath = dialog.FileName;
        }
    }

    private void BrowseExport()
    {
        string? selectedExtension = GetSupportedExportExtension(ExportPath);
        string targetName = Path.GetFileNameWithoutExtension(SelectedRun?.TargetName ?? "yaap-result");
        string suggestedName = string.IsNullOrWhiteSpace(ExportPath)
            ? $"{targetName}-{DateTime.Now:yyyyMMdd-HHmmss}"
            : selectedExtension is null
                ? Path.GetFileNameWithoutExtension(ExportPath)
                : Path.GetFileName(ExportPath);
        Microsoft.Win32.SaveFileDialog dialog = new()
        {
            AddExtension = true,
            FileName = suggestedName,
            Filter = "JSONファイル (*.json)|*.json|CSVファイル (*.csv)|*.csv|Markdownファイル (*.md;*.markdown)|*.md;*.markdown",
            FilterIndex = selectedExtension?.ToLowerInvariant() switch
            {
                ".csv" => 2,
                ".md" or ".markdown" => 3,
                _ => 1,
            },
            OverwritePrompt = true,
            Title = "測定結果の形式と保存先を選択",
        };
        string? currentDirectory = Path.GetDirectoryName(ExportPath);
        if (!string.IsNullOrWhiteSpace(currentDirectory) && Directory.Exists(currentDirectory))
        {
            dialog.InitialDirectory = Path.GetFullPath(currentDirectory);
        }

        if (dialog.ShowDialog() == true)
        {
            ExportPath = dialog.FileName;
        }
    }

    private void BrowseHistoryDirectory()
    {
        BrowseDirectory(
            "履歴を保存するフォルダーを選択",
            HistoryPath,
            path => HistoryPath = path);
    }

    private void BrowseArtifactsDirectory()
    {
        BrowseDirectory(
            "分離出力先フォルダーを選択",
            ArtifactsPath,
            path => ArtifactsPath = path);
    }

    private static void BrowseDirectory(
        string title,
        string currentPath,
        Action<string> apply)
    {
        Microsoft.Win32.OpenFolderDialog dialog = new()
        {
            Multiselect = false,
            Title = title,
        };
        if (!string.IsNullOrWhiteSpace(currentPath) && Directory.Exists(currentPath))
        {
            dialog.InitialDirectory = Path.GetFullPath(currentPath);
        }

        if (dialog.ShowDialog() == true)
        {
            apply(dialog.FolderName);
        }
    }

    private void OpenHistoryDirectory()
    {
        try
        {
            string path = Store().RootPath;
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            SetError(new YaapException(YaapErrors.HistoryFailed(exception.Message), exception));
        }
    }

    private async Task StartAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(TargetPath))
        {
            throw new YaapException(YaapErrors.InvalidInput("Target path is empty."));
        }

        IsRunning = true;
        _profileCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCancellation.Token);
        try
        {
            Progress<ProfileProgress> progress = new(item => StatusText = item.Message);
            SelectedRun = await _profileRunner.RunAsync(
                new ProfileOptions
                {
                    TargetPath = TargetPath,
                    Configuration = Configuration,
                    Mode = SelectedMode,
                    WarmupCount = WarmupCount,
                    IterationCount = IterationCount,
                    CleanBeforeEach = CleanBeforeEach,
                    Restore = Restore,
                    Isolated = Isolated,
                    ArtifactsPath = Isolated ? EmptyToNull(ArtifactsPath) : null,
                    HistoryPath = EmptyToNull(HistoryPath),
                    RetentionCount = RetentionCount,
                },
                progress,
                _profileCancellation.Token);
            StatusText = FormatRunOutcome(SelectedRun);
            if (!_disposed)
            {
                await RefreshHistoryAsync(_lifetimeCancellation.Token);
            }
        }
        finally
        {
            _profileCancellation.Dispose();
            _profileCancellation = null;
            IsRunning = false;
        }
    }

    private void QueueHistoryRefresh()
    {
        if (_disposed || !_historyInitialized)
        {
            return;
        }

        if (IsBusy)
        {
            _historyRefreshPending = true;
            return;
        }

        _historyRefreshPending = false;

        CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCancellation.Token);
        CancellationTokenSource? previous = Interlocked.Exchange(
            ref _historyRefreshCancellation,
            cancellation);
        previous?.Cancel();
        _historyRefreshTask = RefreshHistoryAfterDelayAsync(cancellation);
    }

    private bool HasHistoryPeriod =>
        !string.IsNullOrWhiteSpace(_historyFrom) || !string.IsNullOrWhiteSpace(_historyTo);

    private void ClearHistoryPeriod()
    {
        if (!HasHistoryPeriod)
        {
            return;
        }

        _historyFrom = string.Empty;
        _historyTo = string.Empty;
        OnPropertyChanged(nameof(HistoryFrom));
        OnPropertyChanged(nameof(HistoryTo));
        OnPropertyChanged(nameof(HistoryFromDate));
        OnPropertyChanged(nameof(HistoryToDate));
        ((RelayCommand)ClearHistoryPeriodCommand).RaiseCanExecuteChanged();
        QueueHistoryRefresh();
    }

    private async Task RefreshHistoryAfterDelayAsync(CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellation.Token);
            await RefreshHistoryAsync(cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SetError(exception);
        }
        finally
        {
            Interlocked.CompareExchange(ref _historyRefreshCancellation, null, cancellation);
            cancellation.Dispose();
        }
    }

    private async Task RefreshHistoryAsync(CancellationToken cancellationToken)
    {
        RunStatus? status = HistoryStatus switch
        {
            "実行中" => RunStatus.Running,
            "成功" => RunStatus.Succeeded,
            "部分結果" => RunStatus.Partial,
            "失敗" => RunStatus.Failed,
            "キャンセル" => RunStatus.Canceled,
            _ => null,
        };
        DateTimeOffset? from = ParseOptionalDateTime(HistoryFrom, "開始日", endOfDay: false);
        DateTimeOffset? to = ParseOptionalDateTime(HistoryTo, "終了日", endOfDay: true);
        int? limit = ParseOptionalLimit(HistoryLimit);
        if (from > to)
        {
            throw new YaapException(YaapErrors.InvalidOption("履歴の開始日は終了日以前にしてください。"));
        }

        await EnsureConfigurationHistoryAsync(cancellationToken);
        IReadOnlyList<RunSummary> summaries = await ListHistoryAsync(
            new HistoryQuery(
                EmptyToNull(SearchText),
                status,
                from,
                to,
                limit),
            cancellationToken);
        Guid? selectedId = SelectedHistory?.Id;
        History.Clear();
        int added = 0;
        foreach (RunSummary summary in summaries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            History.Add(summary);
            if (++added % 100 == 0)
            {
                await Task.Yield();
            }
        }

        SelectedHistory = selectedId is null
            ? null
            : History.FirstOrDefault(item => item.Id == selectedId.Value);
        UpdateComparisonChoices(summaries);
        OnPropertyChanged(nameof(HistoryCountText));
    }

    private async Task LoadSelectedAsync(CancellationToken cancellationToken)
    {
        Guid? requestedId = SelectedHistory?.Id;
        if (requestedId is null)
        {
            return;
        }

        _loadingHistoryId = requestedId;
        try
        {
            ProfileRun run = await _historyLoader(requestedId.Value, cancellationToken);
            string filter = ResultFilter;
            ResultProjection projection = await Task.Run(
                () => BuildResultProjection(run, filter, cancellationToken),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (SelectedHistory?.Id != requestedId)
            {
                return;
            }

            SetSelectedRun(run, projection, filter);
            SelectedBaseline = ComparisonChoices.FirstOrDefault(item => item.Id == run.Id);
            ClearStatusOverride();
            StatusText = $"測定結果を読み込みました: {run.TargetName}";
        }
        finally
        {
            if (_loadingHistoryId == requestedId)
            {
                _loadingHistoryId = null;
            }
        }
    }

    private async Task DeleteSelectedAsync(CancellationToken cancellationToken)
    {
        RunSummary? selected = SelectedHistory;
        if (selected is null)
        {
            return;
        }

        if (!_confirmDelete(selected))
        {
            StatusText = "履歴を削除しませんでした。";
            return;
        }

        await Store().DeleteAsync(selected.Id, cancellationToken);
        SelectedRun = null;
        SelectedHistory = null;
        await RefreshHistoryAsync(cancellationToken);
    }

    private async Task DeleteAllHistoryAsync(CancellationToken cancellationToken)
    {
        if (!_confirmDeleteAll())
        {
            StatusText = "履歴を削除しませんでした。";
            return;
        }

        int deleted = await Store().DeleteAllAsync(cancellationToken);
        SelectedRun = null;
        SelectedHistory = null;
        Comparison = null;
        await RefreshHistoryAsync(cancellationToken);
        StatusText = $"履歴を {deleted:N0} 件削除しました。";
    }

    private async Task CompareAsync(CancellationToken cancellationToken)
    {
        if (SelectedBaseline is null || SelectedCandidate is null)
        {
            throw new YaapException(YaapErrors.InvalidOption("比較する2つの測定結果を選択してください。"));
        }

        if (SelectedBaseline.Id == SelectedCandidate.Id)
        {
            throw new YaapException(YaapErrors.InvalidOption("異なる2つの測定結果を選択してください。"));
        }

        HistoryStore history = Store();
        ProfileRun baseline = await history.LoadAsync(SelectedBaseline.Id, cancellationToken);
        ProfileRun candidate = await history.LoadAsync(SelectedCandidate.Id, cancellationToken);
        Comparison = await Task.Run(
            () => RunComparison.Compare(baseline, candidate, cancellationToken),
            cancellationToken);
        StatusText = $"比較しました: {Comparison.Metrics.Count} 項目";
    }

    private async Task ExportAsync(CancellationToken cancellationToken)
    {
        if (SelectedRun is null || string.IsNullOrWhiteSpace(ExportPath))
        {
            throw new YaapException(YaapErrors.InvalidOption("保存ファイルを選択してください。"));
        }

        Yaap.Core.ExportFormat format = GetExportFormat(ExportPath);
        HistoryStore history = Store();
        if (Directory.Exists(history.GetRunDirectory(SelectedRun.Id)))
        {
            await RunExporter.ExportAsync(
                SelectedRun,
                format,
                ExportPath,
                history.StreamGeneratedOutputsAsync(SelectedRun.Id, cancellationToken),
                cancellationToken);
        }
        else
        {
            await RunExporter.ExportAsync(
                SelectedRun,
                format,
                ExportPath,
                cancellationToken);
        }
        StatusText = $"出力しました: {Path.GetFullPath(ExportPath)}";
    }

    private async Task AnalyzeBinlogAsync(CancellationToken cancellationToken)
    {
        string path = Path.GetFullPath(BinlogPath);
        BinlogAnalysis analysis = await _binlogAnalyzer(path, cancellationToken);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        MeasurementResult measurement = new(
            1,
            now,
            0,
            true,
            path,
            analysis.Analyzers,
            analysis.Generators,
            Array.Empty<GeneratedOutput>(),
            analysis.Diagnostics);
        ProfileRun run = new()
        {
            TargetPath = path,
            TargetName = Path.GetFileName(path),
            Configuration = "binlog",
            Mode = ProfileMode.Custom,
            WarmupCount = 0,
            IterationCount = 1,
            CleanBeforeEach = false,
            Restore = false,
            StartedAt = now,
            FinishedAt = now,
            Status = analysis.Diagnostics.Count == 0 ? RunStatus.Succeeded : RunStatus.Partial,
            Environment = new EnvironmentSnapshot(
                Environment.OSVersion.VersionString,
                System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                Environment.ProcessorCount,
                "binlog",
                Environment.Version.ToString(),
                null,
                null,
                false),
            Measurements = new[] { measurement },
            Analyzers = Statistics.AggregateAnalyzers(new[] { measurement }),
            Generators = Statistics.AggregateGenerators(new[] { measurement }),
            Diagnostics = analysis.Diagnostics,
        };
        SelectedRun = run;
        StatusText = $"binlogを解析しました: {analysis.EventCount:N0} イベント";
    }

    private void QueueLabelSave(TimeSpan delay)
    {
        if (_disposed || _isApplyingLabelHistory || SelectedHistory is null)
        {
            return;
        }

        CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCancellation.Token);
        CancellationTokenSource? previous = Interlocked.Exchange(
            ref _labelSaveCancellation,
            cancellation);
        Guid id = SelectedHistory.Id;
        if (_labelSaveId == id)
        {
            previous?.Cancel();
        }

        _labelSaveId = id;
        string value = SelectedHistoryLabel;
        string before = _committedHistoryLabel;
        Task save = SaveLabelAfterDelayAsync(id, value, before, delay, cancellation);
        _labelSaveTask = Task.WhenAll(_labelSaveTask, save);
    }

    private async Task SaveLabelAfterDelayAsync(
        Guid id,
        string value,
        string before,
        TimeSpan delay,
        CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(delay, cancellation.Token);
            string normalized = value.Trim();
            if (normalized.Equals(before, StringComparison.Ordinal))
            {
                return;
            }

            bool updated = await Store().UpdateLabelIfCurrentAsync(id, before, normalized, cancellation.Token);
            if (!updated)
            {
                throw new YaapException(YaapErrors.HistoryFailed(
                    "履歴ラベルが別のYAAPで更新されました。一覧を更新してから編集してください。"));
            }
            ApplyPersistedLabel(id, normalized);
            _labelUndo.Push(new LabelEdit(id, before, normalized));
            _labelRedo.Clear();
            RaiseCommandStates();
            StatusText = "履歴ラベルを保存しました。";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SetError(exception);
        }
        finally
        {
            if (ReferenceEquals(
                    Interlocked.CompareExchange(ref _labelSaveCancellation, null, cancellation),
                    cancellation))
            {
                _labelSaveId = null;
            }
            cancellation.Dispose();
        }
    }

    private async Task UndoLabelAsync(CancellationToken cancellationToken)
    {
        FlushPendingLabelSave();
        await _labelSaveTask;
        if (_labelUndo.Count == 0)
        {
            return;
        }

        LabelEdit edit = _labelUndo.Peek();
        bool updated = await Store().UpdateLabelIfCurrentAsync(edit.Id, edit.After, edit.Before, cancellationToken);
        if (!updated)
        {
            throw new YaapException(YaapErrors.HistoryFailed(
                "履歴ラベルが別のYAAPで更新されたため、元に戻せません。一覧を更新してください。"));
        }
        _labelUndo.Pop();
        ApplyPersistedLabel(edit.Id, edit.Before);
        _labelRedo.Push(edit);
        RaiseCommandStates();
    }

    private async Task RedoLabelAsync(CancellationToken cancellationToken)
    {
        FlushPendingLabelSave();
        await _labelSaveTask;
        if (_labelRedo.Count == 0)
        {
            return;
        }

        LabelEdit edit = _labelRedo.Peek();
        bool updated = await Store().UpdateLabelIfCurrentAsync(edit.Id, edit.Before, edit.After, cancellationToken);
        if (!updated)
        {
            throw new YaapException(YaapErrors.HistoryFailed(
                "履歴ラベルが別のYAAPで更新されたため、やり直せません。一覧を更新してください。"));
        }
        _labelRedo.Pop();
        ApplyPersistedLabel(edit.Id, edit.After);
        _labelUndo.Push(edit);
        RaiseCommandStates();
    }

    private void ApplyPersistedLabel(Guid id, string? label)
    {
        string? normalized = string.IsNullOrWhiteSpace(label) ? null : label.Trim();
        int index = -1;
        for (int position = 0; position < History.Count; position++)
        {
            if (History[position].Id == id)
            {
                index = position;
                break;
            }
        }

        if (index >= 0)
        {
            RunSummary updated = History[index] with { Label = normalized };
            bool selected = SelectedHistory?.Id == id;
            History[index] = updated;
            if (selected)
            {
                _isApplyingLabelHistory = true;
                SelectedHistory = updated;
                _selectedHistoryLabel = normalized ?? string.Empty;
                _committedHistoryLabel = _selectedHistoryLabel;
                OnPropertyChanged(nameof(SelectedHistoryLabel));
                _isApplyingLabelHistory = false;
            }

            UpdateComparisonChoices(History);
        }
    }

    private void FlushPendingLabelSave()
    {
        if (_labelSaveCancellation is null || SelectedHistory is null)
        {
            return;
        }

        _labelSaveCancellation.Cancel();
        QueueLabelSave(TimeSpan.Zero);
    }

    private void UpdateComparisonChoices(IEnumerable<RunSummary> summaries)
    {
        Guid? baselineId = SelectedBaseline?.Id;
        Guid? candidateId = SelectedCandidate?.Id;
        ComparisonChoices.Clear();
        foreach (RunSummary summary in summaries.OrderByDescending(item => item.StartedAt))
        {
            ComparisonChoices.Add(HistoryChoice.FromSummary(summary));
        }

        SelectedBaseline = ComparisonChoices.FirstOrDefault(item => item.Id == baselineId) ??
            ComparisonChoices.Skip(1).FirstOrDefault() ??
            ComparisonChoices.FirstOrDefault();
        SelectedCandidate = ComparisonChoices.FirstOrDefault(item => item.Id == candidateId) ??
            ComparisonChoices.FirstOrDefault(item => item.Id != SelectedBaseline?.Id);
    }

    private void QueueResultFilter(TimeSpan delay)
    {
        if (_disposed)
        {
            return;
        }

        CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCancellation.Token);
        CancellationTokenSource? previous = Interlocked.Exchange(
            ref _resultFilterCancellation,
            cancellation);
        previous?.Cancel();
        ProfileRun? run = SelectedRun;
        string filter = ResultFilter;
        _resultFilterTask = ApplyResultFilterAfterDelayAsync(run, filter, delay, cancellation);
    }

    private async Task ApplyResultFilterAfterDelayAsync(
        ProfileRun? run,
        string filter,
        TimeSpan delay,
        CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(delay, cancellation.Token);
            ResultProjection projection = await Task.Run(
                () => BuildResultProjection(run, filter, cancellation.Token),
                cancellation.Token);
            if (ReferenceEquals(run, SelectedRun) && filter.Equals(ResultFilter, StringComparison.Ordinal))
            {
                ApplyResultProjection(projection);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            Interlocked.CompareExchange(ref _resultFilterCancellation, null, cancellation);
            cancellation.Dispose();
        }
    }

    private static ResultProjection BuildResultProjection(
        ProfileRun? run,
        string filter,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<StatisticalMetric> analyzers = run?.Analyzers
            .Select(item => CheckResultFilterCancellation(item, cancellationToken))
            .Where(item => MatchesFilter(filter, item.Identity, item.Assembly, item.DiagnosticId))
            .ToArray() ?? Array.Empty<StatisticalMetric>();
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<GeneratorMetric> generators = run?.Generators
            .Select(item => CheckResultFilterCancellation(item, cancellationToken))
            .Where(item => MatchesFilter(filter, item.Identity, item.Assembly) ||
                item.Outputs
                    .Select(output => CheckResultFilterCancellation(output, cancellationToken))
                    .Any(output => MatchesFilter(filter, output.RelativePath)))
            .ToArray() ?? Array.Empty<GeneratorMetric>();
        cancellationToken.ThrowIfCancellationRequested();
        return new ResultProjection(
            analyzers,
            generators,
            ResultTreeBuilder.BuildAnalyzers(
                run?.Analyzers ?? Array.Empty<StatisticalMetric>(),
                filter,
                cancellationToken),
            ResultTreeBuilder.BuildGenerators(
                run?.Generators ?? Array.Empty<GeneratorMetric>(),
                filter,
                cancellationToken));
    }

    private static T CheckResultFilterCancellation<T>(
        T value,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return value;
    }

    private void ApplyResultProjection(ResultProjection projection)
    {
        _analyzers = projection.Analyzers;
        _generators = projection.Generators;
        _analyzerTree = projection.AnalyzerTree;
        _generatorTree = projection.GeneratorTree;
        OnPropertyChanged(nameof(Analyzers));
        OnPropertyChanged(nameof(Generators));
        OnPropertyChanged(nameof(AnalyzerTree));
        OnPropertyChanged(nameof(GeneratorTree));
    }

    private void SetSelectedRun(
        ProfileRun? value,
        ResultProjection? preparedProjection = null,
        string? preparedFilter = null)
    {
        ClearStatusOverride();
        if (!Set(ref _selectedRun, value))
        {
            return;
        }

        CancellationTokenSource? previous = Interlocked.Exchange(
            ref _resultFilterCancellation,
            null);
        previous?.Cancel();
        if (preparedProjection is not null &&
            string.Equals(preparedFilter, ResultFilter, StringComparison.Ordinal))
        {
            ApplyResultProjection(preparedProjection);
        }
        else
        {
            ApplyResultProjection(ResultProjection.Empty);
            QueueResultFilter(TimeSpan.Zero);
        }

        OnPropertyChanged(nameof(Diagnostics));
        _selectedDiagnostic = value?.Diagnostics.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedDiagnostic));
        OnPropertyChanged(nameof(StatusTitleText));
        OnPropertyChanged(nameof(StatusSeverity));
        RaiseCommandStates();
    }

    public static Yaap.Core.ExportFormat GetExportFormat(string path) =>
        GetSupportedExportExtension(path)?.ToLowerInvariant() switch
        {
            ".json" => Yaap.Core.ExportFormat.Json,
            ".csv" => Yaap.Core.ExportFormat.Csv,
            ".md" or ".markdown" => Yaap.Core.ExportFormat.Markdown,
            _ => throw new YaapException(YaapErrors.InvalidOption(
                "保存ファイルの拡張子は .json、.csv、.md、.markdown のいずれかを指定してください。")),
        };

    private static string? GetSupportedExportExtension(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        string extension = Path.GetExtension(path);
        return extension.ToLowerInvariant() switch
        {
            ".json" or ".csv" or ".md" or ".markdown" => extension,
            _ => null,
        };
    }

    private HistoryStore Store() => new(EmptyToNull(HistoryPath));

    private AsyncRelayCommand CreateOperationCommand(
        string status,
        Func<CancellationToken, Task> operation,
        Func<bool>? canExecute = null,
        bool showBusySurface = true)
    {
        return new AsyncRelayCommand(
            cancellationToken => RunOperationAsync(
                status,
                operation,
                showBusySurface,
                cancellationToken),
            () => !IsBusy && (canExecute?.Invoke() ?? true),
            SetError);
    }

    private async Task RunOperationAsync(
        string status,
        Func<CancellationToken, Task> operation,
        bool showBusySurface,
        CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            return;
        }

        CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCancellation.Token);
        if (Interlocked.CompareExchange(ref _operationCancellation, linked, null) is not null)
        {
            linked.Dispose();
            throw new InvalidOperationException("Another GUI operation is already running.");
        }

        _showOperationBusySurface = showBusySurface;
        IsOperationRunning = true;
        OnPropertyChanged(nameof(IsInlineOperationVisible));
        SetStatusOverride("処理中", Wpf.Ui.Controls.InfoBarSeverity.Informational);
        StatusText = status;
        try
        {
            await operation(linked.Token);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            if (!_disposed)
            {
                StatusText = "処理をキャンセルしました。";
                SetStatusOverride(
                    "処理キャンセル",
                    Wpf.Ui.Controls.InfoBarSeverity.Warning);
            }
        }
        finally
        {
            Interlocked.CompareExchange(ref _operationCancellation, null, linked);
            linked.Dispose();
            if (!_disposed)
            {
                IsOperationRunning = false;
                _showOperationBusySurface = false;
                OnPropertyChanged(nameof(IsInlineOperationVisible));
                if (_statusTitleOverride == "処理中")
                {
                    ClearStatusOverride();
                }

                if (_historyRefreshPending)
                {
                    QueueHistoryRefresh();
                }
            }
        }
    }

    private void CancelActiveOperation()
    {
        try
        {
            _profileCancellation?.Cancel();
            _operationCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static string FormatRunOutcome(ProfileRun run)
    {
        string prefix = run.Status switch
        {
            RunStatus.Succeeded => "測定が完了しました",
            RunStatus.Partial => "測定は一部のみ完了しました",
            RunStatus.Failed => "測定に失敗しました",
            RunStatus.Canceled => "測定をキャンセルしました",
            _ => "測定結果を更新しました",
        };
        RunDiagnostic? primary = run.Diagnostics.FirstOrDefault();
        if (primary is null || run.Status == RunStatus.Succeeded)
        {
            return $"{prefix}: {run.TargetName}";
        }

        return $"{prefix}: {run.TargetName}。{primary.Code}: {primary.Message} " +
            "原因ログと対処は「トラブルシュート」タブで確認できます。";
    }

    private void SetError(Exception exception)
    {
        if (_disposed || exception is OperationCanceledException)
        {
            return;
        }

        SetStatusOverride("処理失敗", Wpf.Ui.Controls.InfoBarSeverity.Error);
        if (exception is ProcessDidNotTerminateException blocker)
        {
            _shutdownBlocker = blocker;
        }

        StatusText = exception is YaapException yaap
            ? $"{yaap.Diagnostic.Code}: {yaap.Diagnostic.Message} {yaap.Diagnostic.Detail} {yaap.Diagnostic.SuggestedAction}"
            : exception.Message;
    }

    private void SetStatusOverride(
        string title,
        Wpf.Ui.Controls.InfoBarSeverity severity)
    {
        _statusTitleOverride = title;
        _statusSeverityOverride = severity;
        OnPropertyChanged(nameof(StatusTitleText));
        OnPropertyChanged(nameof(StatusSeverity));
    }

    private void ClearStatusOverride()
    {
        if (_statusTitleOverride is null && _statusSeverityOverride is null)
        {
            return;
        }

        _statusTitleOverride = null;
        _statusSeverityOverride = null;
        OnPropertyChanged(nameof(StatusTitleText));
        OnPropertyChanged(nameof(StatusSeverity));
    }

    private void RaiseCommandStates()
    {
        OnPropertyChanged(nameof(MeasurementStateText));
        OnPropertyChanged(nameof(StatusTitleText));
        foreach (ICommand command in new[]
                 {
                     StartCommand,
                     RefreshHistoryCommand,
                     LoadSelectedCommand,
                     DeleteSelectedCommand,
                     DeleteAllHistoryCommand,
                     UndoLabelCommand,
                     RedoLabelCommand,
                     CompareCommand,
                     ExportCommand,
                     AnalyzeBinlogCommand,
                 })
        {
            ((AsyncRelayCommand)command).RaiseCanExecuteChanged();
        }

        ((RelayCommand)CancelCommand).RaiseCanExecuteChanged();
        ((RelayCommand)BrowseCommand).RaiseCanExecuteChanged();
        ((RelayCommand)BrowseBinlogCommand).RaiseCanExecuteChanged();
        ((RelayCommand)BrowseExportCommand).RaiseCanExecuteChanged();
        ((RelayCommand)BrowseHistoryDirectoryCommand).RaiseCanExecuteChanged();
        ((RelayCommand)BrowseArtifactsDirectoryCommand).RaiseCanExecuteChanged();
        ((RelayCommand)OpenHistoryDirectoryCommand).RaiseCanExecuteChanged();
        ((RelayCommand)ClearHistoryPeriodCommand).RaiseCanExecuteChanged();
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private static string? EmptyToNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string FormatHistoryDate(DateTime? value) => value?.ToString(
        "yyyy/MM/dd",
        CultureInfo.InvariantCulture) ?? string.Empty;

    private static DateTimeOffset? ParseOptionalDateTime(
        string value,
        string label,
        bool endOfDay)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!TryParseHistoryDateText(value, out DateTime result))
        {
            throw new YaapException(YaapErrors.InvalidOption(
                $"{label}を日付として解釈できません。例: 2026/01/31、2026-01-31、31/Jan/2026"));
        }

        bool containsTime = value.Contains(':', StringComparison.Ordinal);
        DateTime local = containsTime
            ? result
            : endOfDay
                ? result.Date.AddDays(1).AddTicks(-1)
                : result.Date;
        return new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Local));
    }

    public static bool TryParseHistoryDateText(string value, out DateTime result)
    {
        DateTimeStyles styles = DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal;
        return DateTime.TryParse(value, CultureInfo.CurrentCulture, styles, out result) ||
            DateTime.TryParse(value, CultureInfo.InvariantCulture, styles, out result);
    }

    private static int? ParseOptionalLimit(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return DefaultHistoryLimit;
        }

        return int.TryParse(
            value,
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out int result) && result is >= 1 and <= 10000
            ? result
            : throw new YaapException(YaapErrors.InvalidOption("履歴の表示件数は 1～10000 を指定してください。"));
    }

    private static bool ConfirmDelete(RunSummary summary)
    {
        MessageBoxResult result = MessageBox.Show(
            $"{summary.StartedAt.LocalDateTime:yyyy/MM/dd HH:mm} の「{summary.TargetName}」を削除します。元に戻せません。",
            "履歴の削除確認",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        return result == MessageBoxResult.Yes;
    }

    private static bool ConfirmDeleteAll()
    {
        MessageBoxResult result = MessageBox.Show(
            "保存されているすべての測定履歴を削除します。元に戻せません。",
            "すべての履歴の削除確認",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        return result == MessageBoxResult.Yes;
    }

    private string SelectPreferredConfiguration(
        string targetPath,
        IEnumerable<string> configurations)
    {
        string[] available = configurations
            .Where(configuration => !string.IsNullOrWhiteSpace(configuration))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(configuration => configuration, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (available.Length == 0)
        {
            return string.Empty;
        }

        string? normalizedPath = TryNormalizePath(targetPath);
        if (normalizedPath is not null &&
            _latestConfigurationByTarget.TryGetValue(normalizedPath, out string? historical))
        {
            string? historicalMatch = available.FirstOrDefault(configuration =>
                configuration.Equals(historical, StringComparison.OrdinalIgnoreCase));
            if (historicalMatch is not null)
            {
                return historicalMatch;
            }
        }

        return available.FirstOrDefault(configuration =>
                   configuration.Equals("Release", StringComparison.OrdinalIgnoreCase)) ??
            available.FirstOrDefault(configuration =>
                configuration.Equals("Debug", StringComparison.OrdinalIgnoreCase)) ??
            available[0];
    }

    private async Task EnsureConfigurationHistoryAsync(CancellationToken cancellationToken)
    {
        if (_historyInitialized)
        {
            return;
        }

        IReadOnlyList<RunSummary> summaries = await ListHistoryAsync(
            new HistoryQuery(Limit: ConfigurationHistoryLimit),
            cancellationToken);
        ConfigurationHistory configurationHistory = await Task.Run(
            () => BuildConfigurationHistory(summaries, cancellationToken),
            cancellationToken);
        ApplyConfigurationHistory(configurationHistory);
    }

    private Task<IReadOnlyList<RunSummary>> ListHistoryAsync(
        HistoryQuery query,
        CancellationToken cancellationToken)
    {
        return Task.Run(
            () => Store().ListAsync(query, cancellationToken),
            cancellationToken);
    }

    private static ConfigurationHistory BuildConfigurationHistory(
        IEnumerable<RunSummary> summaries,
        CancellationToken cancellationToken)
    {
        Dictionary<string, string> latest = new(StringComparer.OrdinalIgnoreCase);
        List<RecentTarget> recentTargets = new();
        foreach (RunSummary summary in summaries.OrderByDescending(summary => summary.StartedAt))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? normalizedPath = TryNormalizePath(summary.TargetPath);
            if (normalizedPath is not null)
            {
                if (latest.TryAdd(normalizedPath, summary.Configuration))
                {
                    recentTargets.Add(new RecentTarget(
                        Path.GetFileName(normalizedPath),
                        normalizedPath,
                        summary.StartedAt));
                }
            }
        }

        return new ConfigurationHistory(latest, recentTargets);
    }

    private void ApplyConfigurationHistory(ConfigurationHistory configurationHistory)
    {
        _latestConfigurationByTarget = configurationHistory.Latest;
        MergeRecentTargets(configurationHistory.RecentTargets);
        _historyInitialized = true;
    }

    private void AddOrPromoteRecentTarget(string path, DateTimeOffset lastUsed)
    {
        string? normalizedPath = TryNormalizePath(path);
        if (normalizedPath is null || !TargetDiscovery.IsSupportedPath(normalizedPath))
        {
            return;
        }

        RecentTarget? existing = RecentTargets.FirstOrDefault(item =>
            PathsEqual(item.Path, normalizedPath));
        if (existing is not null)
        {
            RecentTargets.Remove(existing);
        }

        RecentTargets.Insert(0, new RecentTarget(
            Path.GetFileName(normalizedPath),
            normalizedPath,
            lastUsed));
        while (RecentTargets.Count > MaxRecentTargets)
        {
            RecentTargets.RemoveAt(RecentTargets.Count - 1);
        }

        Set(
            ref _selectedRecentTarget,
            RecentTargets[0],
            nameof(SelectedRecentTarget));
    }

    private void MergeRecentTargets(IEnumerable<RecentTarget> targets)
    {
        Dictionary<string, RecentTarget> merged = new(StringComparer.OrdinalIgnoreCase);
        foreach (RecentTarget target in RecentTargets.Concat(targets))
        {
            string? normalizedPath = TryNormalizePath(target.Path);
            if (normalizedPath is null)
            {
                continue;
            }

            RecentTarget normalized = target with
            {
                Name = Path.GetFileName(normalizedPath),
                Path = normalizedPath,
            };
            if (!merged.TryGetValue(normalizedPath, out RecentTarget? existing) ||
                normalized.LastUsed > existing.LastUsed)
            {
                merged[normalizedPath] = normalized;
            }
        }

        RecentTargets.Clear();
        foreach (RecentTarget target in merged.Values
                     .OrderByDescending(item => item.LastUsed)
                     .Take(MaxRecentTargets))
        {
            RecentTargets.Add(target);
        }

        Set(
            ref _selectedRecentTarget,
            RecentTargets.FirstOrDefault(item => PathsEqual(item.Path, TargetPath)),
            nameof(SelectedRecentTarget));
    }

    private static string? TryNormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static bool MatchesFilter(string? filter, params string?[] values)
    {
        return string.IsNullOrWhiteSpace(filter) ||
            values.Any(value => value?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true);
    }

    private static void CancelAndDispose(ref CancellationTokenSource? source)
    {
        CancellationTokenSource? cancellation = Interlocked.Exchange(ref source, null);
        if (cancellation is null)
        {
            return;
        }

        cancellation.Cancel();
    }

    private static bool PathsEqual(string? left, string? right)
    {
        string? normalizedLeft = TryNormalizePath(left);
        string? normalizedRight = TryNormalizePath(right);
        return normalizedLeft is not null && normalizedRight is not null &&
            normalizedLeft.Equals(normalizedRight, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class RelayCommand : ICommand
    {
        private readonly Action _execute;
        private readonly Func<bool> _canExecute;

        public RelayCommand(Action execute, Func<bool> canExecute)
        {
            _execute = execute;
            _canExecute = canExecute;
        }

        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => _canExecute();

        public void Execute(object? parameter) => _execute();

        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed record ConfigurationHistory(
        IReadOnlyDictionary<string, string> Latest,
        IReadOnlyList<RecentTarget> RecentTargets);

    private sealed record LabelEdit(Guid Id, string Before, string After);

    private sealed record ResultProjection(
        IReadOnlyList<StatisticalMetric> Analyzers,
        IReadOnlyList<GeneratorMetric> Generators,
        IReadOnlyList<ResultTreeNode> AnalyzerTree,
        IReadOnlyList<ResultTreeNode> GeneratorTree)
    {
        public static ResultProjection Empty { get; } = new(
            Array.Empty<StatisticalMetric>(),
            Array.Empty<GeneratorMetric>(),
            Array.Empty<ResultTreeNode>(),
            Array.Empty<ResultTreeNode>());
    }
}

public sealed record RecentTarget(string Name, string Path, DateTimeOffset LastUsed);

public sealed record HistoryChoice(Guid Id, string DisplayText)
{
    public static HistoryChoice FromSummary(RunSummary summary)
    {
        string label = string.IsNullOrWhiteSpace(summary.Label)
            ? string.Empty
            : $"{summary.Label} — ";
        return new HistoryChoice(
            summary.Id,
            $"{label}{summary.StartedAt.LocalDateTime:yyyy/MM/dd HH:mm} | {summary.TargetName} | {summary.Configuration}");
    }
}

public sealed record ResultTreeNode(
    string Name,
    string Detail,
    IReadOnlyList<ResultTreeNode> Children);

public static class ResultTreeBuilder
{
    public static IReadOnlyList<ResultTreeNode> BuildAnalyzers(
        IEnumerable<StatisticalMetric> metrics,
        string? filter,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return metrics
                .Select(item => CheckCancellation(item, cancellationToken))
                .Where(item => Matches(filter, item.Identity, item.Assembly, item.DiagnosticId))
                .GroupBy(item => item.Assembly, StringComparer.OrdinalIgnoreCase)
                .OrderBy(
                    group => group.Key,
                    CreateCancellationAwareComparer<string>(static value => value, cancellationToken))
                .Select(group => CheckCancellation(new ResultTreeNode(
                    group.Key,
                    $"{group.Count()} 項目",
                    group.OrderBy(
                            item => item,
                            CreateCancellationAwareComparer<StatisticalMetric>(
                                static item => item.Identity,
                                cancellationToken))
                        .Select(item => CheckCancellation(new ResultTreeNode(
                            item.DiagnosticId is null
                                ? item.Identity
                                : $"{item.DiagnosticId}: {item.Identity}",
                            $"{item.Kind}、平均 {item.MeanMilliseconds:N3} ms",
                            Array.Empty<ResultTreeNode>()), cancellationToken))
                        .ToArray()), cancellationToken))
                .ToArray();
        }
        catch (InvalidOperationException exception) when (
            cancellationToken.IsCancellationRequested &&
            exception.InnerException is OperationCanceledException)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    public static IReadOnlyList<ResultTreeNode> BuildGenerators(
        IEnumerable<GeneratorMetric> metrics,
        string? filter,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return metrics
                .Select(item => CheckCancellation(item, cancellationToken))
                .Where(item => Matches(filter, item.Identity, item.Assembly) ||
                    item.Outputs
                        .Select(output => CheckCancellation(output, cancellationToken))
                        .Any(output => Matches(filter, output.RelativePath)))
                .GroupBy(item => item.Assembly, StringComparer.OrdinalIgnoreCase)
                .OrderBy(
                    group => group.Key,
                    CreateCancellationAwareComparer<string>(static value => value, cancellationToken))
                .Select(group => CheckCancellation(new ResultTreeNode(
                    group.Key,
                    $"{group.Count()} Generator",
                    group.OrderBy(
                            item => item,
                            CreateCancellationAwareComparer<GeneratorMetric>(
                                static item => item.Identity,
                                cancellationToken))
                        .Select(item => CheckCancellation(new ResultTreeNode(
                            item.Identity,
                            item.OutputsTruncated
                                ? $"平均 {item.MeanMilliseconds:N3} ms、生成 {item.GeneratedFileCount} ファイル（先頭100件を表示、全件はexport）"
                                : $"平均 {item.MeanMilliseconds:N3} ms、生成 {item.GeneratedFileCount} ファイル",
                            FilterOutputs(item, filter)
                                .Select(output => CheckCancellation(output, cancellationToken))
                                .OrderBy(
                                    output => output,
                                    CreateCancellationAwareComparer<GeneratedOutput>(
                                        static output => output.RelativePath,
                                        cancellationToken))
                                .Select(output => CheckCancellation(new ResultTreeNode(
                                    output.RelativePath,
                                    $"{output.ByteCount:N0} バイト、{output.LineCount:N0} 行",
                                    Array.Empty<ResultTreeNode>()), cancellationToken))
                                .ToArray()), cancellationToken))
                        .ToArray()), cancellationToken))
                .ToArray();
        }
        catch (InvalidOperationException exception) when (
            cancellationToken.IsCancellationRequested &&
            exception.InnerException is OperationCanceledException)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private static T CheckCancellation<T>(T value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return value;
    }

    private static IComparer<T> CreateCancellationAwareComparer<T>(
        Func<T, string> keySelector,
        CancellationToken cancellationToken)
    {
        return Comparer<T>.Create((left, right) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return StringComparer.OrdinalIgnoreCase.Compare(
                keySelector(left),
                keySelector(right));
        });
    }

    private static IEnumerable<GeneratedOutput> FilterOutputs(GeneratorMetric generator, string? filter)
    {
        return Matches(filter, generator.Identity, generator.Assembly)
            ? generator.Outputs
            : generator.Outputs.Where(output => Matches(filter, output.RelativePath));
    }

    private static bool Matches(string? filter, string? first, string? second = null)
    {
        return string.IsNullOrWhiteSpace(filter) ||
            first?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true ||
            second?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true;
    }

    private static bool Matches(
        string? filter,
        string? first,
        string? second,
        string? third)
    {
        return string.IsNullOrWhiteSpace(filter) ||
            first?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true ||
            second?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true ||
            third?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true;
    }
}
