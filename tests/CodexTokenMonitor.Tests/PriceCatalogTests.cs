using System.Text;
using CodexTokenMonitor.Core.Models;
using CodexTokenMonitor.Core.Pricing;
using CodexTokenMonitor.Core.Storage;
using Microsoft.Data.Sqlite;

namespace CodexTokenMonitor.Tests;

public sealed class PriceCatalogTests
{
    [Fact]
    public void AppliesPriceIntervalsAtEventTime()
    {
        var first = DateTimeOffset.Parse("2026-08-01T00:00:00Z");
        var second = DateTimeOffset.Parse("2026-08-18T00:00:00Z");
        var catalog = new PriceCatalog([
            Version(1, "third", PriceSourceKinds.ThirdPartyReference, first, 1m, 0.1m, 2m),
            Version(2, "third", PriceSourceKinds.ThirdPartyReference, second, 2m, 0.2m, 4m)
        ]);
        TimedModelUsage[] events =
        [
            new("gpt-test", first.AddDays(10), Usage(1_000_000, 0, 1_000_000)),
            new("gpt-test", second.AddDays(2), Usage(1_000_000, 0, 1_000_000))
        ];

        var period = UsageCostEstimator.EstimateTimedPeriod(
            new DateOnly(2026, 8, 1),
            new DateOnly(2026, 8, 31),
            events,
            catalog);

        Assert.Equal(9m, period.EstimatedCostUsd);
        Assert.Equal(4_000_000, period.PricedTokens);
        Assert.Equal(PriceSourceKinds.ThirdPartyReference, Assert.Single(period.Coverage!).SourceKind);
    }

    [Fact]
    public void UsesNearestFollowingRemotePriceBeforeFirstObservation()
    {
        var effective = DateTimeOffset.Parse("2026-08-18T00:00:00Z");
        var catalog = new PriceCatalog([
            Version(1, "third", PriceSourceKinds.ThirdPartyReference, effective, 2m, 0.2m, 4m)
        ]);

        var period = UsageCostEstimator.EstimateTimedPeriod(
            new DateOnly(2026, 8, 1),
            new DateOnly(2026, 8, 1),
            [new TimedModelUsage("gpt-test", effective.AddDays(-10), Usage(1_000_000, 0, 0))],
            catalog);

        var model = Assert.Single(period.Models);
        Assert.Equal(2m, model.EstimatedCostUsd);
        Assert.True(model.HasBackfilledPrice);
    }

    [Fact]
    public void OfficialHistoryOverridesLaterThirdPartyReference()
    {
        var first = DateTimeOffset.Parse("2026-08-01T00:00:00Z");
        var catalog = new PriceCatalog([
            Version(1, "official", PriceSourceKinds.Official, first, 3m, 0.3m, 6m),
            Version(2, "third", PriceSourceKinds.ThirdPartyReference, first.AddDays(17), 2m, 0.2m, 4m)
        ]);

        var resolved = catalog.Resolve("gpt-test", PriceComponent.Input, first.AddDays(20));

        Assert.NotNull(resolved);
        Assert.Equal(3m, resolved.UsdPerMillion);
        Assert.Equal(PriceSourceKinds.Official, resolved.SourceKind);
    }

    [Fact]
    public void UnknownModelRemainsUnpricedWithoutAnExactMatch()
    {
        var catalog = new PriceCatalog([]);

        Assert.Null(catalog.Resolve("gpt-5.6-sol-preview", PriceComponent.Input, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void LiteLlmParserOnlyReturnsExplicitObservedPublicModels()
    {
        using var source = new LiteLlmPriceSource(enabled: false);
        var json = Encoding.UTF8.GetBytes("""
            {
              "openai/gpt-test": {
                "input_cost_per_token": 0.000002,
                "cache_read_input_token_cost": 0.0000002,
                "output_cost_per_token": 0.000004
              },
              "openai/gpt-other": {
                "input_cost_per_token": 0.000001,
                "output_cost_per_token": 0.000002
              }
            }
            """);

        var result = source.Parse(
            json,
            ["gpt-test", "private-model"],
            DateTimeOffset.Parse("2026-08-28T00:00:00Z"),
            "HASH",
            "REV");

        var price = Assert.Single(result);
        Assert.Equal("gpt-test", price.Model);
        Assert.Equal(2m, price.InputUsdPerMillion);
        Assert.Equal(0.2m, price.CachedInputUsdPerMillion);
        Assert.Equal(4m, price.OutputUsdPerMillion);
    }

    [Fact]
    public void LiteLlmParserRejectsConflictingDuplicateAliases()
    {
        using var source = new LiteLlmPriceSource(enabled: false);
        var json = Encoding.UTF8.GetBytes("""
            {
              "gpt-test": { "input_cost_per_token": 0.000001 },
              "openai/gpt-test": { "input_cost_per_token": 0.000002 }
            }
            """);

        Assert.Throws<InvalidDataException>(() => source.Parse(
            json,
            ["gpt-test"],
            DateTimeOffset.Parse("2026-08-28T00:00:00Z"),
            "HASH",
            null));
    }

    [Fact]
    public void OfficialParserReadsStandardTableAndExactObservedModels()
    {
        using var source = new OpenAiOfficialPriceSource(enabled: false);
        var markdown = Encoding.UTF8.GetBytes("""
            # Pricing

            ### Standard pricing data
            | Model | Short context input | Short context cached input | Short context cache writes | Short context output | Long context input |
            | --- | --- | --- | --- | --- | --- |
            | gpt-5.6-sol | $4.00 | $0.40 | $5.00 | $20.00 | $8.00 |
            | gpt-5.5 (<272K context length) | $5.00 | $0.50 | - | $30.00 | $10.00 |

            Specialized models
            Standard
            ### Grouped Pricing Table data
            | Category | Model | Input | Cached input | Output |
            | --- | --- | --- | --- | --- |
            | Codex | gpt-5.3-codex | $1.75 | $0.175 | $14.00 |

            Fast mode
            ### Grouped Pricing Table data
            | Category | Model | Input | Cached input | Output |
            | --- | --- | --- | --- | --- |
            | Codex | gpt-5.3-codex | $3.50 | $0.35 | $28.00 |

            ### Batch pricing data
            | Model | Short context input | Short context cached input | Short context cache writes | Short context output |
            | --- | --- | --- | --- | --- |
            | gpt-5.6-sol | $2.00 | $0.20 | $2.50 | $10.00 |
            """);

        var prices = source.Parse(
            markdown,
            ["gpt-5.6-sol", "gpt-5.5", "gpt-5.3-codex", "gpt-5.6-sol-preview"],
            DateTimeOffset.Parse("2026-08-28T00:00:00Z"),
            "HASH",
            "REV");

        Assert.Equal(3, prices.Count);
        var sol = prices.Single(price => price.Model == "gpt-5.6-sol");
        Assert.Equal(4m, sol.InputUsdPerMillion);
        Assert.Equal(0.4m, sol.CachedInputUsdPerMillion);
        Assert.Equal(20m, sol.OutputUsdPerMillion);
        var gpt55 = prices.Single(price => price.Model == "gpt-5.5");
        Assert.Equal(0.5m, gpt55.CachedInputUsdPerMillion);
        Assert.Equal(30m, gpt55.OutputUsdPerMillion);
        var codex = prices.Single(price => price.Model == "gpt-5.3-codex");
        Assert.Equal(1.75m, codex.InputUsdPerMillion);
        Assert.Equal(0.175m, codex.CachedInputUsdPerMillion);
        Assert.Equal(14m, codex.OutputUsdPerMillion);
    }

    [Fact]
    public async Task QuarantinesLargeChangeUntilItRepeats()
    {
        using var temporary = new TemporaryDirectory();
        var database = new MonitorDatabase(Path.Combine(temporary.Path, "monitor.db"));
        await database.InitializeAsync();
        var firstAt = DateTimeOffset.Parse("2026-08-01T00:00:00Z");
        var changedAt = DateTimeOffset.Parse("2026-08-18T00:00:00Z");
        await database.SaveFetchedPricesAsync(Fetch(Candidate(firstAt, 1m, "A")));

        var firstChange = await database.SaveFetchedPricesAsync(Fetch(Candidate(changedAt, 2m, "B")));
        var repeatedChange = await database.SaveFetchedPricesAsync(Fetch(Candidate(changedAt.AddDays(1), 2m, "C")));
        var versions = await database.GetPriceVersionsAsync();

        Assert.Equal(1, firstChange.QuarantinedCount);
        Assert.Equal(1, repeatedChange.ActivatedCount);
        Assert.Equal(2, versions.Count);
        Assert.Equal(changedAt, versions.MaxBy(version => version.EffectiveFrom)!.EffectiveFrom);
        Assert.Empty(await database.GetQuarantinedPriceChangesAsync());
    }

    [Fact]
    public async Task ConfirmsMultipleHistoricalLargeChangesIndependently()
    {
        using var temporary = new TemporaryDirectory();
        var database = new MonitorDatabase(Path.Combine(temporary.Path, "monitor.db"));
        await database.InitializeAsync();
        var firstAt = DateTimeOffset.Parse("2026-08-01T00:00:00Z");
        var secondAt = DateTimeOffset.Parse("2026-08-08T00:00:00Z");
        var thirdAt = DateTimeOffset.Parse("2026-08-18T00:00:00Z");
        await database.SaveFetchedPricesAsync(Fetch(Candidate(firstAt, 1m, "A")));
        var firstFetch = Fetch(
            Candidate(secondAt, 2m, "B"),
            Candidate(thirdAt, 4m, "C"));

        var firstObservation = await database.SaveFetchedPricesAsync(firstFetch);
        var pending = await database.GetQuarantinedPriceChangesAsync();
        var repeatedObservation = await database.SaveFetchedPricesAsync(firstFetch);
        var versions = await database.GetPriceVersionsAsync();

        Assert.Equal(2, firstObservation.QuarantinedCount);
        Assert.Equal(2, pending.Count);
        Assert.Contains(pending, change => change.EffectiveFrom == secondAt);
        Assert.Contains(pending, change => change.EffectiveFrom == thirdAt);
        Assert.Equal(2, repeatedObservation.ActivatedCount);
        Assert.Equal(3, versions.Count);
        Assert.Empty(await database.GetQuarantinedPriceChangesAsync());
    }

    [Fact]
    public async Task PersistsEarlierHistoricalBoundaryWhenRateMatchesCurrentVersion()
    {
        using var temporary = new TemporaryDirectory();
        var database = new MonitorDatabase(Path.Combine(temporary.Path, "monitor.db"));
        await database.InitializeAsync();
        var historicalAt = DateTimeOffset.Parse("2026-08-01T00:00:00Z");
        var currentAt = DateTimeOffset.Parse("2026-08-18T00:00:00Z");
        await database.SaveFetchedPricesAsync(Fetch(Candidate(currentAt, 2m, "CURRENT")));

        var result = await database.SaveFetchedPricesAsync(Fetch(Candidate(historicalAt, 2m, "HISTORY")));
        var versions = await database.GetPriceVersionsAsync();

        Assert.Equal(1, result.ActivatedCount);
        Assert.Equal(2, versions.Count);
        Assert.Equal(historicalAt, versions.Min(version => version.EffectiveFrom));
    }

    [Fact]
    public async Task InitializeMigratesLegacyPriceCandidatePrimaryKeyWithoutDataLoss()
    {
        using var temporary = new TemporaryDirectory();
        var databasePath = Path.Combine(temporary.Path, "monitor.db");
        await using (var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            var create = connection.CreateCommand();
            create.CommandText = """
                CREATE TABLE price_change_candidates(
                    source_id TEXT NOT NULL,
                    model TEXT NOT NULL COLLATE NOCASE,
                    effective_utc TEXT NOT NULL,
                    input_usd_per_million TEXT NULL,
                    cached_input_usd_per_million TEXT NULL,
                    output_usd_per_million TEXT NULL,
                    maximum_change_ratio TEXT NOT NULL,
                    matching_observations INTEGER NOT NULL,
                    first_observed_utc TEXT NOT NULL,
                    last_observed_utc TEXT NOT NULL,
                    content_hash TEXT NOT NULL,
                    PRIMARY KEY(source_id, model)
                );
                INSERT INTO price_change_candidates VALUES(
                    'third', 'gpt-test', '2026-08-18T00:00:00.0000000+00:00',
                    '2', '0.2', '4', '1', 1,
                    '2026-08-28T00:00:00.0000000+00:00',
                    '2026-08-28T00:00:00.0000000+00:00', 'HASH');
                """;
            await create.ExecuteNonQueryAsync();
        }

        var database = new MonitorDatabase(databasePath);
        await database.InitializeAsync();
        var changes = await database.GetQuarantinedPriceChangesAsync();

        Assert.Single(changes);
        await using var verify = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
        await verify.OpenAsync();
        var inspect = verify.CreateCommand();
        inspect.CommandText = "SELECT group_concat(name, ',') FROM (SELECT name FROM pragma_table_info('price_change_candidates') WHERE pk > 0 ORDER BY pk)";
        Assert.Equal("source_id,model,effective_utc", await inspect.ExecuteScalarAsync());
    }

    [Fact]
    public async Task DatabaseCalculatesEventsAcrossAnIntradayPriceBoundary()
    {
        using var temporary = new TemporaryDirectory();
        var database = new MonitorDatabase(Path.Combine(temporary.Path, "monitor.db"));
        await database.InitializeAsync();
        var localNoon = DateTime.Today.AddHours(12);
        var boundary = new DateTimeOffset(localNoon, TimeZoneInfo.Local.GetUtcOffset(localNoon));
        await database.SaveFetchedPricesAsync(Fetch(Candidate(boundary.AddHours(-2), 1m, "FIRST")));
        await database.SaveFetchedPricesAsync(Fetch(Candidate(boundary, 1.4m, "SECOND")));
        ParsedTokenEvent[] events =
        [
            new(boundary.AddHours(-1), Usage(1_000_000, 0, 0), Usage(1_000_000, 0, 0), 10_000_000),
            new(boundary.AddHours(1), Usage(2_000_000, 0, 0), Usage(1_000_000, 0, 0), 10_000_000)
        ];
        await database.ApplyFileResultAsync(new RolloutFileResult(
            "prices.jsonl",
            100,
            false,
            "price-session",
            new ParsedSessionMetadata("price-session", boundary.AddHours(-1), "gpt-test", "desktop"),
            events[^1],
            3,
            3,
            0,
            0,
            null,
            events));

        var summary = await database.GetUsageSummaryAsync(7);

        Assert.Equal(2.4m, summary.CostEstimate.Daily.EstimatedCostUsd);
        var coverage = Assert.Single(summary.CostEstimate.Daily.Coverage!);
        Assert.Equal(2_000_000, coverage.Tokens);
        Assert.Equal(PriceSourceKinds.ThirdPartyReference, coverage.SourceKind);
    }

    [Fact]
    public async Task CoordinatorPersistsAValidatedSourceRefresh()
    {
        using var temporary = new TemporaryDirectory();
        var database = new MonitorDatabase(Path.Combine(temporary.Path, "monitor.db"));
        await database.InitializeAsync();
        var candidate = Candidate(DateTimeOffset.UtcNow, 1m, "HASH");
        await using var coordinator = new PricingUpdateCoordinator(
            database,
            [new FakePriceSource(Fetch(candidate))]);

        var status = await coordinator.RefreshNowAsync();

        var source = Assert.Single(status.Sources);
        Assert.Equal("third", source.SourceId);
        Assert.NotNull(source.LastSuccessAt);
        Assert.False(status.IsStale);
        Assert.Single(await database.GetPriceVersionsAsync());
    }

    private static ModelPriceVersion Version(
        long id,
        string sourceId,
        string sourceKind,
        DateTimeOffset effective,
        decimal input,
        decimal cached,
        decimal output) => new(
            id,
            sourceId,
            "gpt-test",
            input,
            cached,
            output,
            sourceKind,
            sourceId,
            "https://example.invalid/prices",
            effective,
            effective,
            "HASH",
            null,
            "test",
            false,
            effective);

    private static TokenUsage Usage(long input, long cached, long output) => new(
        input,
        cached,
        0,
        output,
        0,
        input + output);

    private static PriceCandidate Candidate(DateTimeOffset effective, decimal input, string hash) => new(
        "gpt-test",
        input,
        input / 10m,
        input * 2m,
        effective,
        effective,
        PriceSourceKinds.ThirdPartyReference,
        "third",
        "Third party",
        "https://example.invalid/prices",
        hash,
        hash,
        "test");

    private static PriceFetchResult Fetch(params PriceCandidate[] candidates) => new(
        candidates[0].SourceId,
        candidates.Max(candidate => candidate.RetrievedAt),
        candidates[^1].ContentHash,
        candidates[^1].SourceRevision,
        candidates);

    private sealed class FakePriceSource(PriceFetchResult result) : IPriceSource
    {
        public string SourceId => "third";

        public string SourceKind => PriceSourceKinds.ThirdPartyReference;

        public string DisplayName => "Test source";

        public string SourceUrl => "https://example.invalid/prices";

        public bool Enabled => true;

        public string GateStatus => "verified_for_test";

        public Task<PriceFetchResult> FetchAsync(
            IReadOnlyCollection<string> observedModels,
            CancellationToken cancellationToken = default) => Task.FromResult(result);
    }
}
