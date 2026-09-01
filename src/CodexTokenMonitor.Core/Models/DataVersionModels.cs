namespace CodexTokenMonitor.Core.Models;

public sealed record DataVersionSnapshot(
    long Overview,
    long Sessions,
    long Models,
    long Usage,
    long Rankings,
    long Skills,
    long Settings,
    DateTimeOffset ChangedAt);

public sealed record RebuildStatusSnapshot(
    string State,
    string? Scope,
    int ProcessedFiles,
    int TotalFiles,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? ErrorCode);
