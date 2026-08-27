namespace CodexTokenMonitor.Core.Models;

public sealed record SessionSnapshot
{
    public required string SessionId { get; init; }
    public required string RolloutPath { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
    public required DateTimeOffset LastActivityAt { get; init; }
    public string? Title { get; init; }
    public DateTimeOffset? TitleUpdatedAt { get; init; }
    public string? Model { get; init; }
    public string? ReasoningEffort { get; init; }
    public string? ProjectPath { get; init; }
    public string? ProjectName { get; init; }
    public TokenUsage TotalUsage { get; init; } = TokenUsage.Empty;
    public TokenUsage LastUsage { get; init; } = TokenUsage.Empty;
    public long ModelContextWindow { get; init; }
    public bool IsMuted { get; init; }
    public string DataStatus { get; init; } = "healthy";
    public string? ErrorCode { get; init; }
    public required DateTimeOffset LastSuccessAt { get; init; }
    public SessionAnnotation Annotation { get; init; } = SessionAnnotation.Unmarked;

    public int? EstimatedContextPercent => ModelContextWindow > 0
        ? (int)Math.Clamp(Math.Round(LastUsage.TotalTokens * 100d / ModelContextWindow), 0, 100)
        : null;

    public bool IsActive(DateTimeOffset now, TimeSpan activityWindow) =>
        now - LastActivityAt <= activityWindow;
}

public sealed record SessionEnrichment(
    string SessionId,
    string? Title,
    DateTimeOffset? TitleUpdatedAt,
    string? Model,
    string? ReasoningEffort);

public sealed record ReasoningSummary(
    string ReasoningEffort,
    int SessionCount,
    long TotalTokens,
    long MedianTokens);

public sealed record ModelSummary(
    string Model,
    int SessionCount,
    long TotalTokens,
    long MedianTokens,
    long InputTokens,
    long OutputTokens,
    long CachedInputTokens,
    long ReasoningOutputTokens,
    IReadOnlyList<ReasoningSummary> Reasoning);
