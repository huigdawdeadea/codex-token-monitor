namespace CodexTokenMonitor.Core.Models;

public sealed record MonitorHealthSnapshot(
    string Status,
    DateTimeOffset? LastScanAt,
    DateTimeOffset? LastSuccessfulScanAt,
    string? ErrorCode,
    int WatchedFiles);
