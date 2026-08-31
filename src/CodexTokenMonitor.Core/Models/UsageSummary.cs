namespace CodexTokenMonitor.Core.Models;

public sealed record DailyUsageSummary(
    DateOnly Date,
    TokenUsage Usage,
    int SessionCount);

public sealed record ModelUsageCost(
    string Model,
    TokenUsage Usage,
    long UncachedInputTokens,
    long PricedTokens,
    decimal EstimatedCostUsd,
    bool IsPriced,
    IReadOnlyList<PriceCoverageSummary>? Coverage = null,
    bool HasBackfilledPrice = false);

public sealed record UsageCostPeriod(
    DateOnly StartDate,
    DateOnly EndDate,
    TokenUsage Usage,
    long PricedTokens,
    long UnpricedTokens,
    decimal EstimatedCostUsd,
    IReadOnlyList<ModelUsageCost> Models,
    IReadOnlyList<PriceCoverageSummary>? Coverage = null,
    long OfficialPricedTokens = 0,
    decimal OfficialEstimatedCostUsd = 0m);

public sealed record ModelPriceRate(
    string Model,
    decimal InputUsdPerMillion,
    decimal CachedInputUsdPerMillion,
    decimal OutputUsdPerMillion,
    string SourceKind = PriceSourceKinds.BuiltInDefault,
    DateTimeOffset? EffectiveFrom = null);

public sealed record UsageCostEstimate(
    UsageCostPeriod Daily,
    UsageCostPeriod Weekly,
    UsageCostPeriod Monthly,
    IReadOnlyList<ModelPriceRate> Rates,
    string Currency,
    DateOnly PricingAsOf,
    DateTimeOffset CalculatedAt,
    PricingStatusSnapshot? PricingStatus = null);

public sealed record UsageSummary(
    DailyUsageSummary Today,
    TokenUsage AllTime,
    int TotalSessions,
    IReadOnlyList<DailyUsageSummary> Days,
    string TimeZoneId,
    UsageCostEstimate CostEstimate);

public sealed record DailySessionUsage(
    string SessionId,
    string DisplayName,
    string Model,
    string ReasoningEffort,
    TokenUsage Usage,
    DateTimeOffset LastEventAt,
    SessionAnnotation Annotation);

public sealed record DailyUsageAnalysis(
    DailyUsageSummary Selected,
    DailyUsageSummary PreviousDay,
    long SevenDayAverageTokens,
    IReadOnlyList<DailySessionUsage> TopSessions,
    int AnnotatedSessionCount,
    int CompletedSessionCount,
    long CompletedTokens,
    int ReworkSessionCount,
    long ReworkTokens);

public sealed record SessionUsageRanking(
    string SessionId,
    string DisplayName,
    string Model,
    string ReasoningEffort,
    TokenUsage Usage,
    DateTimeOffset LastActivityAt,
    SessionAnnotation Annotation);

public sealed record ProjectUsageRanking(
    string DisplayName,
    string? ProjectPath,
    int SessionCount,
    int ModelCount,
    TokenUsage Usage,
    decimal EstimatedCostUsd,
    long UnpricedTokens,
    DateTimeOffset LastActivityAt);

public sealed record DailyProjectSessionUsage(
    string SessionId,
    string DisplayName,
    string Model,
    string ReasoningEffort,
    TokenUsage Usage,
    DateTimeOffset LastEventAt);

public sealed record DailyProjectUsage(
    string DisplayName,
    string? ProjectPath,
    string ProjectStatus,
    TokenUsage Usage,
    DateTimeOffset LastEventAt,
    IReadOnlyList<DailyProjectSessionUsage> Sessions);

public sealed record DailyProjectUsageBreakdown(
    DateOnly Date,
    DateOnly? EarliestDate,
    DateOnly LatestDate,
    TokenUsage Usage,
    int SessionCount,
    IReadOnlyList<DailyProjectUsage> Projects,
    string TimeZoneId);

public sealed record UsageRankings(
    IReadOnlyList<DailyUsageSummary> Days,
    IReadOnlyList<SessionUsageRanking> Sessions,
    IReadOnlyList<ProjectUsageRanking> Projects,
    string TimeZoneId);
