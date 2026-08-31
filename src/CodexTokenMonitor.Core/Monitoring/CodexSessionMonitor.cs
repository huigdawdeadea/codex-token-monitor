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
    private readonly SemaphoreSlim _scanSignal = new(0, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private int _refreshPending;
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
        _scanInterval = scanInterval ?? TimeSpan.FromSeconds(5);
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
        if (Interlocked.Exchange(ref _refreshPending, 1) == 0)
        {
            _scanSignal.Release();
        }
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
        while (!cancellationToken.IsCancellationRequested)
        {
            await ScanAllAsync(cancellationToken);
            await ScanSkillUsageAsync(cancellationToken);
            if (DateTimeOffset.UtcNow >= nextMetadataRead)
            {
                await RefreshMetadataAsync(cancellationToken);
                nextMetadataRead = DateTimeOffset.UtcNow + _metadataInterval;
            }

            if (await _scanSignal.WaitAsync(_scanInterval, cancellationToken))
            {
                Interlocked.Exchange(ref _refreshPending, 0);
            }
        }
    }

    private async Task ScanSkillUsageAsync(CancellationToken cancellationToken)
    {
        var definitions = await _skillUsageAnalyzer.ScanDefinitionsAsync(_codexHome, cancellationToken);
        await _database.ReplaceSkillDefinitionsAsync(definitions, cancellationToken);
        if (!Directory.Exists(_sessionsRoot))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(_sessionsRoot, "rollout-*.jsonl", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var info = new FileInfo(path);
                if (!await _database.SkillRolloutNeedsRebuildAsync(
                    path,
                    info.Length,
                    info.LastWriteTimeUtc.Ticks,
                    SkillUsageAnalyzer.ParserVersion,
                    cancellationToken))
                {
                    continue;
                }

                var analysis = await _skillUsageAnalyzer.AnalyzeRolloutAsync(path, definitions, cancellationToken);
                await _database.ReplaceSkillRolloutAsync(analysis, SkillUsageAnalyzer.ParserVersion, cancellationToken);
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

    private async Task ScanAllAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_sessionsRoot))
        {
            _health.RecordDegraded("codex_sessions_missing", 0);
            return;
        }

        string[] files;
        try
        {
            files = Directory.GetFiles(_sessionsRoot, "rollout-*.jsonl", SearchOption.AllDirectories);
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
        var reportInitialProgress = _health.GetSnapshot().LastSuccessfulScanAt is null;
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

        if (scanError is null)
        {
            _health.RecordSuccess(files.Length);
        }
        else
        {
            _health.RecordDegraded(scanError, files.Length);
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

    private void OnRolloutChanged(object sender, FileSystemEventArgs args) => RequestRefresh();

    private void OnRolloutRenamed(object sender, RenamedEventArgs args) => RequestRefresh();

    private void OnWatcherError(object sender, ErrorEventArgs args)
    {
        _health.RecordDegraded("file_watcher_error", _health.GetSnapshot().WatchedFiles);
        RequestRefresh();
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
