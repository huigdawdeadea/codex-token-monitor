using CodexTokenMonitor.Core.Models;
using CodexTokenMonitor.Core.Storage;
using System.Text.Json;

namespace CodexTokenMonitor.Core.Pricing;

public sealed class PricingUpdateCoordinator : IAsyncDisposable
{
    public static readonly TimeSpan UpdateInterval = TimeSpan.FromHours(24);
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(48);
    private readonly MonitorDatabase _database;
    private readonly IReadOnlyList<IPriceSource> _sources;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _runTask;
    private DateTimeOffset? _nextAutomaticCheckAt;
    private int _isRefreshing;

    public PricingUpdateCoordinator(MonitorDatabase database, IEnumerable<IPriceSource> sources)
    {
        _database = database;
        _sources = sources.ToArray();
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_runTask is not null)
        {
            return;
        }

        await EnsureSourceStatusesAsync(cancellationToken);
        _runTask = Task.Run(() => RunAsync(_lifetime.Token), CancellationToken.None);
    }

    public async Task StopAsync()
    {
        _lifetime.Cancel();
        if (_runTask is not null)
        {
            try
            {
                await _runTask;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    public async Task<PricingStatusSnapshot> RefreshNowAsync(
        CancellationToken cancellationToken = default)
    {
        await RefreshSourcesAsync(cancellationToken);
        return await GetStatusAsync(cancellationToken);
    }

    public async Task<PricingStatusSnapshot> GetStatusAsync(
        CancellationToken cancellationToken = default)
    {
        var statuses = await _database.GetPriceSourceStatusesAsync(cancellationToken);
        var lastSuccess = statuses
            .Where(status => status.SourceKind is PriceSourceKinds.Official or PriceSourceKinds.ThirdPartyReference)
            .Select(status => status.LastSuccessAt)
            .Where(value => value is not null)
            .Max();
        var now = DateTimeOffset.UtcNow;
        var versions = await _database.GetPriceVersionsAsync(cancellationToken);
        return new PricingStatusSnapshot(
            now,
            lastSuccess,
            _nextAutomaticCheckAt,
            lastSuccess is null || now - lastSuccess.Value > StaleAfter,
            Volatile.Read(ref _isRefreshing) == 1,
            statuses,
            await _database.GetQuarantinedPriceChangesAsync(cancellationToken),
            versions.OrderByDescending(version => version.EffectiveFrom).ThenByDescending(version => version.Id).Take(50).ToArray(),
            statuses.FirstOrDefault(status => status.SourceKind == PriceSourceKinds.Official)?.GateStatus
                ?? "disabled_pending_official_source_verification",
            statuses.FirstOrDefault(status => status.SourceKind == PriceSourceKinds.ThirdPartyReference)?.GateStatus
                ?? "disabled_pending_live_schema_and_license_verification");
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        foreach (var source in _sources.OfType<IDisposable>())
        {
            source.Dispose();
        }

        _refreshGate.Dispose();
        _lifetime.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await RefreshSourcesAsync(cancellationToken);
            _nextAutomaticCheckAt = DateTimeOffset.UtcNow + UpdateInterval;
            await Task.Delay(UpdateInterval, cancellationToken);
        }
    }

    private async Task RefreshSourcesAsync(CancellationToken cancellationToken)
    {
        if (!await _refreshGate.WaitAsync(0, cancellationToken))
        {
            return;
        }

        Interlocked.Exchange(ref _isRefreshing, 1);
        try
        {
            var models = await _database.GetObservedModelsAsync(cancellationToken);
            foreach (var source in _sources.Where(source => source.Enabled))
            {
                var existing = (await _database.GetPriceSourceStatusesAsync(cancellationToken))
                    .FirstOrDefault(status => status.SourceId == source.SourceId)
                    ?? BuildInitialStatus(source);
                var attemptedAt = DateTimeOffset.UtcNow;
                try
                {
                    var fetch = await source.FetchAsync(models, cancellationToken);
                    await _database.SaveFetchedPricesAsync(fetch, cancellationToken);
                    await _database.SavePriceSourceStatusAsync(existing with
                    {
                        LastAttemptAt = attemptedAt,
                        LastSuccessAt = fetch.RetrievedAt,
                        NextCheckAt = fetch.RetrievedAt + UpdateInterval,
                        LastError = null,
                        LastContentHash = fetch.ContentHash,
                        LastSourceRevision = fetch.SourceRevision,
                        ConsecutiveFailures = 0
                    }, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception error)
                {
                    await _database.SavePriceSourceStatusAsync(existing with
                    {
                        LastAttemptAt = attemptedAt,
                        NextCheckAt = attemptedAt + UpdateInterval,
                        LastError = ClassifyError(error),
                        ConsecutiveFailures = existing.ConsecutiveFailures + 1
                    }, cancellationToken);
                }
            }
        }
        finally
        {
            _nextAutomaticCheckAt = DateTimeOffset.UtcNow + UpdateInterval;
            Interlocked.Exchange(ref _isRefreshing, 0);
            _refreshGate.Release();
        }
    }

    private async Task EnsureSourceStatusesAsync(CancellationToken cancellationToken)
    {
        var existing = await _database.GetPriceSourceStatusesAsync(cancellationToken);
        foreach (var source in _sources)
        {
            var current = existing.FirstOrDefault(status => status.SourceId == source.SourceId);
            await _database.SavePriceSourceStatusAsync(current is null
                ? BuildInitialStatus(source)
                : current with
                {
                    SourceKind = source.SourceKind,
                    DisplayName = source.DisplayName,
                    SourceUrl = source.SourceUrl,
                    Enabled = source.Enabled,
                    GateStatus = source.GateStatus
                }, cancellationToken);
        }

        if (existing.All(status => status.SourceId != "openai-official"))
        {
            await _database.SavePriceSourceStatusAsync(new PriceSourceStatus(
                "openai-official",
                PriceSourceKinds.Official,
                "OpenAI 官方价格",
                "",
                false,
                "disabled_pending_official_source_verification",
                null,
                null,
                null,
                null,
                null,
                null,
                0), cancellationToken);
        }
    }

    private static PriceSourceStatus BuildInitialStatus(IPriceSource source) => new(
        source.SourceId,
        source.SourceKind,
        source.DisplayName,
        source.SourceUrl,
        source.Enabled,
        source.GateStatus,
        null,
        null,
        null,
        null,
        null,
        null,
        0);

    private static string ClassifyError(Exception error) => error switch
    {
        TaskCanceledException => "timeout",
        HttpRequestException request when request.StatusCode is not null => $"http_{(int)request.StatusCode.Value}",
        HttpRequestException => "network_error",
        InvalidDataException => "invalid_price_payload",
        JsonException => "invalid_json",
        _ => "unexpected_error"
    };
}
