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
    bool IsPriced);

public sealed record UsageCostPeriod(
    DateOnly StartDate,
    DateOnly EndDate,
    TokenUsage Usage,
    long PricedTokens,
    long UnpricedTokens,
    decimal EstimatedCostUsd,
    IReadOnlyList<ModelUsageCost> Models);

public sealed record ModelPriceRate(
    string Model,
    decimal InputUsdPerMillion,
    decimal CachedInputUsdPerMillion,
    decimal OutputUsdPerMillion);

public sealed record UsageCostEstimate(
    UsageCostPeriod Daily,
    UsageCostPeriod Weekly,
    UsageCostPeriod Monthly,
    IReadOnlyList<ModelPriceRate> Rates,
    string Currency,
    DateOnly PricingAsOf,
    DateTimeOffset CalculatedAt);

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

public sealed record UsageRankings(
    IReadOnlyList<DailyUsageSummary> Days,
    IReadOnlyList<SessionUsageRanking> Sessions,
    IReadOnlyList<ProjectUsageRanking> Projects,
    string TimeZoneId);
