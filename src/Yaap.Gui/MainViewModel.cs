using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Yaap.Core;

namespace Yaap.Gui;

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private const int MaxRecentTargets = 10;

    private readonly ProfileRunner _profileRunner;
    private readonly Func<string, CancellationToken, Task<TargetInfo>> _targetDiscoverer;
    private readonly TimeSpan _targetDiscoveryDelay;
    private IReadOnlyDictionary<string, string> _latestConfigurationByTarget =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _profileCancellation;
    private CancellationTokenSource? _targetDiscoveryCancellation;
    private Task _targetDiscoveryTask = Task.CompletedTask;
    private long _targetDiscoveryGeneration;
    private string _targetPath = string.Empty;
    private string _configuration = string.Empty;
    private string _historyPath = string.Empty;
    private string _artifactsPath = string.Empty;
    private string _searchText = string.Empty;
    private string _historyStatus = "すべて";
    private string _resultFilter = string.Empty;
    private string _statusText = "準備完了";
    private string _baselineId = string.Empty;
    private string _candidateId = string.Empty;
    private string _exportPath = string.Empty;
    private string _exportFormat = "json";
    private bool _isolated = true;
    private bool _cleanBeforeEach = true;
    private bool _isRunning;
    private bool _isDiscoveringTarget;
    private bool _hasValidTarget;
    private bool _historyInitialized;
    private bool _disposed;
    private int _warmupCount = 1;
    private int _iterationCount = 3;
    private int _retentionCount = 50;
    private ProfileMode _selectedMode = ProfileMode.Warm;
    private ThemeOption _selectedTheme;
    private RecentTarget? _selectedRecentTarget;
    private RunSummary? _selectedHistory;
    private ProfileRun? _selectedRun;
    private ComparisonResult? _comparison;

    public MainViewModel(
        ProfileRunner? profileRunner = null,
        Func<string, CancellationToken, Task<TargetInfo>>? targetDiscoverer = null,
        TimeSpan? targetDiscoveryDelay = null)
    {
        _profileRunner = profileRunner ?? new ProfileRunner();
        _targetDiscoverer = targetDiscoverer ?? TargetDiscovery.DiscoverAsync;
        _targetDiscoveryDelay = targetDiscoveryDelay ?? TimeSpan.FromMilliseconds(350);
        _selectedTheme = ThemeOptions[0];
        BrowseCommand = new RelayCommand(Browse, () => !IsRunning);
        StartCommand = new AsyncRelayCommand(StartAsync, CanStart, SetError);
        RefreshHistoryCommand = new AsyncRelayCommand(RefreshHistoryAsync, () => !IsRunning, SetError);
        LoadSelectedCommand = new AsyncRelayCommand(LoadSelectedAsync, () => SelectedHistory is not null, SetError);
        DeleteSelectedCommand = new AsyncRelayCommand(DeleteSelectedAsync, () => SelectedHistory is not null && !IsRunning, SetError);
        CompareCommand = new AsyncRelayCommand(CompareAsync, () => !IsRunning, SetError);
        ExportCommand = new AsyncRelayCommand(ExportAsync, () => SelectedRun is not null && !IsRunning, SetError);
        CancelCommand = new RelayCommand(() => _profileCancellation?.Cancel(), () => IsRunning);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<RunSummary> History { get; } = new();

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
                if (_hasValidTarget && !IsRunning)
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
        set => Set(ref _searchText, value);
    }

    public string HistoryStatus
    {
        get => _historyStatus;
        set => Set(ref _historyStatus, value);
    }

    public string ResultFilter
    {
        get => _resultFilter;
        set
        {
            if (Set(ref _resultFilter, value))
            {
                OnPropertyChanged(nameof(Analyzers));
                OnPropertyChanged(nameof(Generators));
                OnPropertyChanged(nameof(AnalyzerTree));
                OnPropertyChanged(nameof(GeneratorTree));
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public string BaselineId
    {
        get => _baselineId;
        set => Set(ref _baselineId, value);
    }

    public string CandidateId
    {
        get => _candidateId;
        set => Set(ref _candidateId, value);
    }

    public string ExportPath
    {
        get => _exportPath;
        set => Set(ref _exportPath, value);
    }

    public string ExportFormat
    {
        get => _exportFormat;
        set => Set(ref _exportFormat, value);
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
                RaiseCommandStates();
            }
        }
    }

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
            if (Set(ref _selectedHistory, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public ProfileRun? SelectedRun
    {
        get => _selectedRun;
        private set
        {
            if (Set(ref _selectedRun, value))
            {
                OnPropertyChanged(nameof(Analyzers));
                OnPropertyChanged(nameof(Generators));
                OnPropertyChanged(nameof(AnalyzerTree));
                OnPropertyChanged(nameof(GeneratorTree));
                OnPropertyChanged(nameof(Diagnostics));
                RaiseCommandStates();
            }
        }
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

    public string AdvancedSettingsSummary => Isolated
        ? "詳細設定（分離出力: 有効）"
        : "詳細設定（分離出力: 無効）";

    public string HistoryCountText => $"履歴 {History.Count} 件";

    public string ApplicationVersion { get; } =
        typeof(MainViewModel).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion.Split('+')[0] ?? "不明";

    public IReadOnlyList<StatisticalMetric> Analyzers => SelectedRun?.Analyzers
        .Where(item => MatchesResultFilter(item.Identity, item.Assembly, item.DiagnosticId))
        .ToArray() ?? Array.Empty<StatisticalMetric>();

    public IReadOnlyList<GeneratorMetric> Generators => SelectedRun?.Generators
        .Where(MatchesGeneratorFilter)
        .ToArray() ?? Array.Empty<GeneratorMetric>();

    public IReadOnlyList<ResultTreeNode> AnalyzerTree => ResultTreeBuilder.BuildAnalyzers(
        SelectedRun?.Analyzers ?? Array.Empty<StatisticalMetric>(),
        ResultFilter);

    public IReadOnlyList<ResultTreeNode> GeneratorTree => ResultTreeBuilder.BuildGenerators(
        SelectedRun?.Generators ?? Array.Empty<GeneratorMetric>(),
        ResultFilter);

    public IReadOnlyList<RunDiagnostic> Diagnostics => SelectedRun?.Diagnostics ?? Array.Empty<RunDiagnostic>();

    public ICommand StartCommand { get; }

    public ICommand BrowseCommand { get; }

    public ICommand CancelCommand { get; }

    public ICommand RefreshHistoryCommand { get; }

    public ICommand LoadSelectedCommand { get; }

    public ICommand DeleteSelectedCommand { get; }

    public ICommand CompareCommand { get; }

    public ICommand ExportCommand { get; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await RefreshHistoryAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SetError(exception);
        }
    }

    public Task WaitForTargetDiscoveryAsync() => _targetDiscoveryTask;

    public bool CanAcceptDroppedTarget(IReadOnlyList<string> paths)
    {
        return !IsRunning && paths.Count == 1 && TargetDiscovery.IsSupportedPath(paths[0]);
    }

    public bool TrySetDroppedTarget(IReadOnlyList<string> paths)
    {
        if (IsRunning)
        {
            StatusText = "測定中は対象を変更できません。";
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
        CancellationTokenSource? discovery = Interlocked.Exchange(ref _targetDiscoveryCancellation, null);
        discovery?.Cancel();
        _profileCancellation?.Cancel();
        GC.SuppressFinalize(this);
    }

    private void QueueTargetDiscovery()
    {
        long generation = Interlocked.Increment(ref _targetDiscoveryGeneration);
        CancellationTokenSource? previous = Interlocked.Exchange(ref _targetDiscoveryCancellation, null);
        previous?.Cancel();
        if (_disposed || IsRunning)
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
        return CurrentMeasurementState.CanStart;
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

    private async Task StartAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(TargetPath))
        {
            throw new YaapException(YaapErrors.InvalidInput("Target path is empty."));
        }

        IsRunning = true;
        _profileCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
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
                    Isolated = Isolated,
                    ArtifactsPath = Isolated ? EmptyToNull(ArtifactsPath) : null,
                    HistoryPath = EmptyToNull(HistoryPath),
                    RetentionCount = RetentionCount,
                },
                progress,
                _profileCancellation.Token);
            StatusText = $"{SelectedRun.Status}: {SelectedRun.Id:D}";
            await RefreshHistoryAsync(CancellationToken.None);
        }
        finally
        {
            _profileCancellation.Dispose();
            _profileCancellation = null;
            IsRunning = false;
        }
    }

    private async Task RefreshHistoryAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<RunSummary> allSummaries = await Store().ListAsync(
            cancellationToken: cancellationToken);
        SetConfigurationHistory(allSummaries);

        RunStatus? status = HistoryStatus switch
        {
            "実行中" => RunStatus.Running,
            "成功" => RunStatus.Succeeded,
            "部分結果" => RunStatus.Partial,
            "失敗" => RunStatus.Failed,
            "キャンセル" => RunStatus.Canceled,
            _ => null,
        };
        IEnumerable<RunSummary> summaries = allSummaries.Where(summary =>
            (status is null || summary.Status == status) &&
            (string.IsNullOrWhiteSpace(SearchText) ||
             summary.TargetName.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
             summary.TargetPath.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
             summary.Id.ToString("D").Contains(SearchText, StringComparison.OrdinalIgnoreCase)));
        History.Clear();
        foreach (RunSummary summary in summaries)
        {
            History.Add(summary);
        }

        OnPropertyChanged(nameof(HistoryCountText));
    }

    private async Task LoadSelectedAsync(CancellationToken cancellationToken)
    {
        if (SelectedHistory is null)
        {
            return;
        }

        SelectedRun = await Store().LoadAsync(SelectedHistory.Id, cancellationToken);
        BaselineId = SelectedRun.Id.ToString("D");
        StatusText = $"履歴を読み込みました: {SelectedRun.Id:D}";
    }

    private async Task DeleteSelectedAsync(CancellationToken cancellationToken)
    {
        if (SelectedHistory is null)
        {
            return;
        }

        await Store().DeleteAsync(SelectedHistory.Id, cancellationToken);
        SelectedRun = null;
        SelectedHistory = null;
        await RefreshHistoryAsync(cancellationToken);
    }

    private async Task CompareAsync(CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(BaselineId, out Guid baselineId) ||
            !Guid.TryParse(CandidateId, out Guid candidateId))
        {
            throw new YaapException(YaapErrors.InvalidInput("Comparison requires two run IDs."));
        }

        HistoryStore history = Store();
        ProfileRun baseline = await history.LoadAsync(baselineId, cancellationToken);
        ProfileRun candidate = await history.LoadAsync(candidateId, cancellationToken);
        Comparison = RunComparison.Compare(baseline, candidate);
        StatusText = $"比較しました: {Comparison.Metrics.Count} 項目";
    }

    private async Task ExportAsync(CancellationToken cancellationToken)
    {
        if (SelectedRun is null || string.IsNullOrWhiteSpace(ExportPath))
        {
            throw new YaapException(YaapErrors.InvalidInput("Export path is empty."));
        }

        Yaap.Core.ExportFormat format = ExportFormat.ToLowerInvariant() switch
        {
            "csv" => Yaap.Core.ExportFormat.Csv,
            "md" or "markdown" => Yaap.Core.ExportFormat.Markdown,
            _ => Yaap.Core.ExportFormat.Json,
        };
        await RunExporter.ExportAsync(SelectedRun, format, ExportPath, cancellationToken);
        StatusText = $"出力しました: {Path.GetFullPath(ExportPath)}";
    }

    private HistoryStore Store() => new(EmptyToNull(HistoryPath));

    private void SetError(Exception exception)
    {
        StatusText = exception is YaapException yaap
            ? $"{yaap.Diagnostic.Code}: {yaap.Diagnostic.Message} {yaap.Diagnostic.SuggestedAction}"
            : exception.Message;
    }

    private void RaiseCommandStates()
    {
        OnPropertyChanged(nameof(MeasurementStateText));
        foreach (ICommand command in new[]
                 {
                     StartCommand,
                     RefreshHistoryCommand,
                     LoadSelectedCommand,
                     DeleteSelectedCommand,
                     CompareCommand,
                     ExportCommand,
                 })
        {
            ((AsyncRelayCommand)command).RaiseCanExecuteChanged();
        }

        ((RelayCommand)CancelCommand).RaiseCanExecuteChanged();
        ((RelayCommand)BrowseCommand).RaiseCanExecuteChanged();
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

        IReadOnlyList<RunSummary> summaries = await Task.Run(
            () => Store().ListAsync(cancellationToken: cancellationToken),
            cancellationToken);
        SetConfigurationHistory(summaries);
    }

    private void SetConfigurationHistory(IEnumerable<RunSummary> summaries)
    {
        Dictionary<string, string> latest = new(StringComparer.OrdinalIgnoreCase);
        List<RecentTarget> recentTargets = new();
        foreach (RunSummary summary in summaries.OrderByDescending(summary => summary.StartedAt))
        {
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

        _latestConfigurationByTarget = latest;
        MergeRecentTargets(recentTargets);
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

    private bool MatchesResultFilter(params string?[] values)
    {
        return string.IsNullOrWhiteSpace(ResultFilter) ||
            values.Any(value => value?.Contains(ResultFilter, StringComparison.OrdinalIgnoreCase) == true);
    }

    private bool MatchesGeneratorFilter(GeneratorMetric generator)
    {
        return MatchesResultFilter(generator.Identity, generator.Assembly) ||
            generator.Outputs.Any(output => MatchesResultFilter(output.RelativePath));
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
}

public sealed record RecentTarget(string Name, string Path, DateTimeOffset LastUsed);

public sealed record ResultTreeNode(
    string Name,
    string Detail,
    IReadOnlyList<ResultTreeNode> Children);

public static class ResultTreeBuilder
{
    public static IReadOnlyList<ResultTreeNode> BuildAnalyzers(
        IEnumerable<StatisticalMetric> metrics,
        string? filter)
    {
        return metrics
            .Where(item => Matches(filter, item.Identity, item.Assembly, item.DiagnosticId))
            .GroupBy(item => item.Assembly, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => new ResultTreeNode(
                group.Key,
                $"{group.Count()} 項目",
                group.OrderBy(item => item.Identity, StringComparer.OrdinalIgnoreCase)
                    .Select(item => new ResultTreeNode(
                        item.DiagnosticId is null
                            ? item.Identity
                            : $"{item.DiagnosticId}: {item.Identity}",
                        $"{item.Kind}、平均 {item.MeanMilliseconds:N3} ms",
                        Array.Empty<ResultTreeNode>()))
                    .ToArray()))
            .ToArray();
    }

    public static IReadOnlyList<ResultTreeNode> BuildGenerators(
        IEnumerable<GeneratorMetric> metrics,
        string? filter)
    {
        return metrics
            .Where(item => Matches(filter, item.Identity, item.Assembly) ||
                item.Outputs.Any(output => Matches(filter, output.RelativePath)))
            .GroupBy(item => item.Assembly, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => new ResultTreeNode(
                group.Key,
                $"{group.Count()} Generator",
                group.OrderBy(item => item.Identity, StringComparer.OrdinalIgnoreCase)
                    .Select(item => new ResultTreeNode(
                        item.Identity,
                        $"平均 {item.MeanMilliseconds:N3} ms、生成 {item.GeneratedFileCount} ファイル",
                        FilterOutputs(item, filter)
                            .OrderBy(output => output.RelativePath, StringComparer.OrdinalIgnoreCase)
                            .Select(output => new ResultTreeNode(
                                output.RelativePath,
                                $"{output.ByteCount:N0} バイト、{output.LineCount:N0} 行",
                                Array.Empty<ResultTreeNode>()))
                            .ToArray()))
                    .ToArray()))
            .ToArray();
    }

    private static IEnumerable<GeneratedOutput> FilterOutputs(GeneratorMetric generator, string? filter)
    {
        return Matches(filter, generator.Identity, generator.Assembly)
            ? generator.Outputs
            : generator.Outputs.Where(output => Matches(filter, output.RelativePath));
    }

    private static bool Matches(string? filter, params string?[] values)
    {
        return string.IsNullOrWhiteSpace(filter) ||
            values.Any(value => value?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true);
    }
}
