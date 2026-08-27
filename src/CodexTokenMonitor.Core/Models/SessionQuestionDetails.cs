namespace CodexTokenMonitor.Core.Models;

public sealed record SessionQuestionDetail(
    int Sequence,
    string? TurnId,
    DateTimeOffset StartedAt,
    string PromptPreview,
    bool PromptTruncated,
    string Model,
    int ModelCallCount,
    TokenUsage Usage,
    decimal EstimatedCostUsd,
    bool IsCostPriced);

public sealed record SessionQuestionHistory(
    string SessionId,
    IReadOnlyList<SessionQuestionDetail> Questions,
    int TotalQuestions,
    bool IsTruncated,
    bool SourceAvailable);
