namespace CodexTokenMonitor.Core.Models;

public sealed record TokenUsage(
    long InputTokens,
    long CachedInputTokens,
    long CacheWriteInputTokens,
    long OutputTokens,
    long ReasoningOutputTokens,
    long TotalTokens)
{
    public static TokenUsage Empty { get; } = new(0, 0, 0, 0, 0, 0);
}
