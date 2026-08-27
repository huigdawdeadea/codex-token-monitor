namespace CodexTokenMonitor.Core.Models;

public abstract record ParsedRolloutEvent(DateTimeOffset Timestamp);

public sealed record ParsedSessionMetadata(
    string SessionId,
    DateTimeOffset Timestamp,
    string? Model,
    string? ThreadSource,
    string? ProjectPath = null,
    string? ProjectName = null) : ParsedRolloutEvent(Timestamp);

public sealed record ParsedTokenEvent(
    DateTimeOffset Timestamp,
    TokenUsage TotalUsage,
    TokenUsage LastUsage,
    long ModelContextWindow) : ParsedRolloutEvent(Timestamp);

public sealed record FileCheckpoint(string Path, long Offset, string? SessionId);

public sealed record RolloutFileResult(
    string Path,
    long NextOffset,
    bool OffsetWasReset,
    string? SessionId,
    ParsedSessionMetadata? Metadata,
    ParsedTokenEvent? LatestTokenEvent,
    int CompleteLines,
    int RecognizedEvents,
    int ParseErrors,
    int OversizedLines,
    string? LastErrorCode = null,
    IReadOnlyList<ParsedTokenEvent>? TokenEvents = null);
