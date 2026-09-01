using System.Collections.Concurrent;
using System.Text.Json;
using CodexTokenMonitor.Core.Metadata;
using CodexTokenMonitor.Core.Models;
using CodexTokenMonitor.Core.Parsing;
using CodexTokenMonitor.Core.Storage;

namespace CodexTokenMonitor.Core.Monitoring;

public sealed class CodexSessionMonitor : IAsyncDisposable
{
    private readonly string _codexHome;
    private readonly string _sessionsRoot;
    private readonly MonitorDatabase _database;
    private readonly RolloutFileProcessor _fileProcessor;
    private readonly CodexMetadataReader _metadataReader;
    private readonly MonitorHealthTracker _health;
    private readonly SkillUsageAnalyzer _skillUsageAnalyzer;
    private readonly TimeSpan _scanInterval;
    private readonly TimeSpan _metadataInterval;
    private readonly TimeSpan _definitionInterval = TimeSpan.FromMinutes(10);
    private readonly SemaphoreSlim _scanSignal = new(0, 1);
    private readonly ConcurrentDictionary<string, byte> _changedPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _rebuildStatusGate = new();
    private IReadOnlyList<SkillDefinitionSnapshot> _skillDefinitions = [];
    private RebuildStatusSnapshot _rebuildStatus = new("idle", null, 0, 0, null, null, null);
    private int _signalPending;
    private int _fullRefreshPending = 1;
    private int _fullRebuildPending;
    private FileSystemWatcher? _watcher;
    private Task? _monitorTask;

    public CodexSessionMonitor(
        string codexHome,
        MonitorDatabase database,
        RolloutFileProcessor fileProcessor,
        CodexMetadataReader metadataReader,
        MonitorHealthTracker health,
        SkillUsageAnalyzer? skillUsageAnalyzer = null,
        TimeSpan? scanInterval = null,
        TimeSpan? metadataInterval = null)
    {
        _codexHome = Path.GetFullPath(codexHome);
        _sessionsRoot = Path.Combine(_codexHome, "sessions");
        _database = database;
        _fileProcessor = fileProcessor;
        _metadataReader = metadataReader;
        _health = health;
        _skillUsageAnalyzer = skillUsageAnalyzer ?? new SkillUsageAnalyzer(new SkillTokenCounter(), new RolloutEventParser());
        _scanInterval = scanInterval ?? TimeSpan.FromSeconds(60);
        _metadataInterval = metadataInterval ?? TimeSpan.FromSeconds(30);
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_monitorTask is not null)
        {
            return;
        }

        await _database.InitializeAsync(cancellationToken);
        await _database.PrepareUsageBackfillAsync(cancellationToken);
        ConfigureWatcher();
        _monitorTask = Task.Run(() => RunSafelyAsync(_lifetime.Token), CancellationToken.None);
    }

    public void RequestRefresh()
    {
        Interlocked.Exchange(ref _fullRefreshPending, 1);
        SignalScan();
    }

    public RebuildStatusSnapshot GetRebuildStatus()
    {
        lock (_rebuildStatusGate)
        {
            return _rebuildStatus;
        }
    }

    public bool RequestFullRebuild()
    {
        if (Interlocked.Exchange(ref _fullRebuildPending, 1) != 0)
        {
            return false;
        }

        SetRebuildStatus(new RebuildStatusSnapshot(
            "queued", "derived", 0, 0, null, null, null));
        SignalScan();
        return true;
    }

    public async Task StopAsync()
    {
        _lifetime.Cancel();
        _watcher?.Dispose();
        RequestRefresh();
        if (_monitorTask is null)
        {
            return;
        }

        try
        {
            await _monitorTask;
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _scanSignal.Dispose();
        _lifetime.Dispose();
    }

    private void ConfigureWatcher()
    {
        if (!Directory.Exists(_sessionsRoot))
        {
            return;
        }

        _watcher = new FileSystemWatcher(_sessionsRoot, "rollout-*.jsonl")
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true
        };
        _watcher.Changed += OnRolloutChanged;
        _watcher.Created += OnRolloutChanged;
        _watcher.Renamed += OnRolloutRenamed;
        _watcher.Error += OnWatcherError;
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var nextMetadataRead = DateTimeOffset.MinValue;
        var nextReconciliation = DateTimeOffset.MinValue;
        var nextDefinitionRead = DateTimeOffset.MinValue;
        while (!cancellationToken.IsCancellationRequested)
        {
            var now = DateTimeOffset.UtcNow;
            var rebuilding = Interlocked.Exchange(ref _fullRebuildPending, 0) != 0;
            if (rebuilding)
            {
                SetRebuildStatus(new RebuildStatusSnapshot(
                    "running", "derived", 0, 0, now, null, null));
                await _database.ResetDerivedDataAsync(cancellationToken);
                Interlocked.Exchange(ref _fullRefreshPending, 1);
            }

            var fullRefresh = Interlocked.Exchange(ref _fullRefreshPending, 0) != 0 || now >= nextReconciliation;
            IReadOnlyList<string>? changedPaths = null;
            if (fullRefresh)
            {
                await ScanAllAsync(cancellationToken);
                nextReconciliation = now + _scanInterval;
            }
            else
            {
                changedPaths = DrainChangedPaths();
                if (changedPaths.Count > 0)
                {
                    await ScanAllAsync(cancellationToken, changedPaths);
                }
            }

            var refreshDefinitions = _skillDefinitions.Count == 0 || now >= nextDefinitionRead;
            if (refreshDefinitions)
            {
                _skillDefinitions = await _skillUsageAnalyzer.ScanDefinitionsAsync(_codexHome, cancellationToken);
                await _database.ReplaceSkillDefinitionsAsync(_skillDefinitions, cancellationToken);
                nextDefinitionRead = now + _definitionInterval;
            }

            if (fullRefresh)
            {
                await ScanSkillUsageAsync(cancellationToken);
            }
            else if (changedPaths is { Count: > 0 })
            {
                await ScanSkillUsageAsync(cancellationToken, changedPaths);
            }

            if (rebuilding)
            {
                var watched = _health.GetSnapshot().WatchedFiles;
                SetRebuildStatus(new RebuildStatusSnapshot(
                    "completed", "derived", watched, watched, now, DateTimeOffset.UtcNow, null));
                await _database.NotifyDataChangedAsync(["settings"], cancellationToken);
            }

            if (now >= nextMetadataRead)
            {
                await RefreshMetadataAsync(cancellationToken);
                nextMetadataRead = now + _metadataInterval;
            }

            var nextWake = new[] { nextMetadataRead, nextReconciliation, nextDefinitionRead }.Min();
            var delay = nextWake - DateTimeOffset.UtcNow;
            if (delay < TimeSpan.FromMilliseconds(50))
            {
                delay = TimeSpan.FromMilliseconds(50);
            }

            await _scanSignal.WaitAsync(delay, cancellationToken);
            Interlocked.Exchange(ref _signalPending, 0);
        }
    }

    private async Task ScanSkillUsageAsync(
        CancellationToken cancellationToken,
        IReadOnlyCollection<string>? requestedPaths = null)
    {
        if (!Directory.Exists(_sessionsRoot))
        {
            return;
        }

        var paths = requestedPaths ?? Directory
            .EnumerateFiles(_sessionsRoot, "rollout-*.jsonl", SearchOption.AllDirectories)
            .ToArray();
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists)
                {
                    continue;
                }

                var checkpoint = await _database.GetSkillScanCheckpointAsync(path, cancellationToken);
                if (checkpoint is not null &&
                    checkpoint.ParserVersion == SkillUsageAnalyzer.ParserVersion &&
                    checkpoint.Offset == info.Length &&
                    checkpoint.Length == info.Length &&
                    checkpoint.LastWriteUtcTicks == info.LastWriteTimeUtc.Ticks)
                {
                    continue;
                }

                var delta = await _skillUsageAnalyzer.AnalyzeRolloutIncrementAsync(
                    path,
                    _skillDefinitions,
                    checkpoint,
                    cancellationToken);
                await _database.ApplySkillRolloutDeltaAsync(delta, SkillUsageAnalyzer.ParserVersion, cancellationToken);
            }
            catch (FileNotFoundException)
            {
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private async Task RunSafelyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RunAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _health.RecordDegraded(ToSafeErrorCode(exception), _health.GetSnapshot().WatchedFiles);
        }
    }

    private async Task ScanAllAsync(
        CancellationToken cancellationToken,
        IReadOnlyCollection<string>? requestedPaths = null)
    {
        if (!Directory.Exists(_sessionsRoot))
        {
            _health.RecordDegraded("codex_sessions_missing", 0);
            return;
        }

        string[] files;
        try
        {
            files = requestedPaths is null
                ? Directory.GetFiles(_sessionsRoot, "rollout-*.jsonl", SearchOption.AllDirectories)
                : requestedPaths
                    .Where(path => Path.GetFileName(path).StartsWith("rollout-", StringComparison.OrdinalIgnoreCase) &&
                        path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
        }
        catch (IOException)
        {
            _health.RecordDegraded("codex_sessions_unavailable", 0);
            return;
        }
        catch (UnauthorizedAccessException)
        {
            _health.RecordDegraded("codex_sessions_access_denied", 0);
            return;
        }

        string? scanError = null;
        var processedFiles = 0;
        var reportInitialProgress = requestedPaths is null && _health.GetSnapshot().LastSuccessfulScanAt is null;
        foreach (var path in files.Order(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reportInitialProgress)
            {
                _health.RecordScanning(processedFiles, files.Length);
            }

            try
            {
                var checkpoint = await _database.GetCheckpointAsync(path, cancellationToken);
                var length = new FileInfo(path).Length;
                if (checkpoint is not null && checkpoint.Offset == length)
                {
                    continue;
                }

                var result = await _fileProcessor.ProcessAsync(path, checkpoint, cancellationToken);
                if (checkpoint is null && result.CompleteLines > 0 && result.SessionId is null)
                {
                    scanError = "unrecognized_rollout_format";
                    continue;
                }

                await _database.ApplyFileResultAsync(result, cancellationToken);

                if (result.ParseErrors > 0 || result.OversizedLines > 0)
                {
                    scanError = result.ParseErrors > 0
                        ? result.LastErrorCode ?? "rollout_parse_error"
                        : "oversized_jsonl_line";
                }
            }
            catch (FileNotFoundException)
            {
                // A rollout can rotate between enumeration and shared-open.
            }
            catch (IOException)
            {
                scanError = "rollout_read_failed";
            }
            catch (UnauthorizedAccessException)
            {
                scanError = "rollout_access_denied";
            }

            processedFiles++;
        }

        var watchedFiles = requestedPaths is null
            ? files.Length
            : Math.Max(_health.GetSnapshot().WatchedFiles, files.Length);
        if (scanError is null)
        {
            _health.RecordSuccess(watchedFiles);
        }
        else
        {
            _health.RecordDegraded(scanError, watchedFiles);
        }
    }

    private async Task RefreshMetadataAsync(CancellationToken cancellationToken)
    {
        var settings = await _database.GetSettingsAsync(cancellationToken);
        var result = await _metadataReader.ReadAsync(_codexHome, settings.CaptureTitles, cancellationToken);
        await _database.ApplyEnrichmentAsync(result.Entries, settings.CaptureTitles, cancellationToken);
        await _database.PurgeExpiredTitlesAsync(
            DateTimeOffset.UtcNow.AddDays(-settings.TitleRetentionDays),
            cancellationToken);

        if (result.ErrorCode is not null)
        {
            _health.RecordDegraded(result.ErrorCode, _health.GetSnapshot().WatchedFiles);
        }
    }

    private void OnRolloutChanged(object sender, FileSystemEventArgs args) => QueueChangedPath(args.FullPath);

    private void OnRolloutRenamed(object sender, RenamedEventArgs args) => QueueChangedPath(args.FullPath);

    private void OnWatcherError(object sender, ErrorEventArgs args)
    {
        _health.RecordDegraded("file_watcher_error", _health.GetSnapshot().WatchedFiles);
        RequestRefresh();
    }

    private void QueueChangedPath(string path)
    {
        _changedPaths[Path.GetFullPath(path)] = 0;
        SignalScan();
    }

    private IReadOnlyList<string> DrainChangedPaths()
    {
        var paths = _changedPaths.Keys.ToArray();
        foreach (var path in paths)
        {
            _changedPaths.TryRemove(path, out _);
        }

        return paths;
    }

    private void SignalScan()
    {
        if (Interlocked.Exchange(ref _signalPending, 1) == 0)
        {
            _scanSignal.Release();
        }
    }

    private void SetRebuildStatus(RebuildStatusSnapshot status)
    {
        lock (_rebuildStatusGate)
        {
            _rebuildStatus = status;
        }
    }

    private static string ToSafeErrorCode(Exception exception) => exception switch
    {
        Microsoft.Data.Sqlite.SqliteException => "monitor_loop_sqlite_failed",
        JsonException => "monitor_loop_json_failed",
        IOException => "monitor_loop_io_failed",
        UnauthorizedAccessException => "monitor_loop_access_denied",
        _ => $"monitor_loop_{exception.GetType().Name.ToLowerInvariant()}"
    };
}
