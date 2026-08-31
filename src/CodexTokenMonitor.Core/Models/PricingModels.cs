namespace CodexTokenMonitor.Core.Models;

public static class PriceSourceKinds
{
    public const string Official = "official";
    public const string ThirdPartyReference = "third_party_reference";
    public const string BuiltInDefault = "built_in_default";

    public static int Priority(string sourceKind) => sourceKind switch
    {
        Official => 0,
        ThirdPartyReference => 1,
        BuiltInDefault => 2,
        _ => 3
    };
}

public sealed record ModelPriceVersion(
    long Id,
    string SourceId,
    string Model,
    decimal? InputUsdPerMillion,
    decimal? CachedInputUsdPerMillion,
    decimal? OutputUsdPerMillion,
    string SourceKind,
    string SourceName,
    string SourceUrl,
    DateTimeOffset EffectiveFrom,
    DateTimeOffset RetrievedAt,
    string? ContentHash,
    string? SourceRevision,
    string ParserVersion,
    bool IsBackfill,
    DateTimeOffset CreatedAt);

public sealed record PriceSourceStatus(
    string SourceId,
    string SourceKind,
    string DisplayName,
    string SourceUrl,
    bool Enabled,
    string GateStatus,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? LastSuccessAt,
    DateTimeOffset? NextCheckAt,
    string? LastError,
    string? LastContentHash,
    string? LastSourceRevision,
    int ConsecutiveFailures);

public sealed record QuarantinedPriceChange(
    string SourceId,
    string Model,
    DateTimeOffset EffectiveFrom,
    decimal? InputUsdPerMillion,
    decimal? CachedInputUsdPerMillion,
    decimal? OutputUsdPerMillion,
    decimal MaximumChangeRatio,
    int MatchingObservations,
    DateTimeOffset FirstObservedAt,
    DateTimeOffset LastObservedAt,
    string ContentHash);

public sealed record PricingStatusSnapshot(
    DateTimeOffset GeneratedAt,
    DateTimeOffset? LastSuccessfulUpdateAt,
    DateTimeOffset? NextAutomaticCheckAt,
    bool IsStale,
    bool IsRefreshing,
    IReadOnlyList<PriceSourceStatus> Sources,
    IReadOnlyList<QuarantinedPriceChange> QuarantinedChanges,
    IReadOnlyList<ModelPriceVersion> RecentVersions,
    string OfficialGate,
    string ThirdPartyGate);

public sealed record PriceCoverageSummary(
    string SourceKind,
    long Tokens,
    decimal EstimatedCostUsd);

public sealed record TimedModelUsage(
    string Model,
    DateTimeOffset EventAt,
    TokenUsage Usage);

public sealed record PriceCandidate(
    string Model,
    decimal? InputUsdPerMillion,
    decimal? CachedInputUsdPerMillion,
    decimal? OutputUsdPerMillion,
    DateTimeOffset EffectiveFrom,
    DateTimeOffset RetrievedAt,
    string SourceKind,
    string SourceId,
    string SourceName,
    string SourceUrl,
    string ContentHash,
    string? SourceRevision,
    string ParserVersion);

public sealed record PriceFetchResult(
    string SourceId,
    DateTimeOffset RetrievedAt,
    string ContentHash,
    string? SourceRevision,
    IReadOnlyList<PriceCandidate> Prices);

public sealed record PriceActivationResult(
    int ActivatedCount,
    int QuarantinedCount,
    int UnchangedCount);
