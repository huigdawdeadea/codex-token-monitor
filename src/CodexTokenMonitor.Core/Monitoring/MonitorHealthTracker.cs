using CodexTokenMonitor.Core.Models;

namespace CodexTokenMonitor.Core.Monitoring;

public sealed class MonitorHealthTracker
{
    private readonly object _gate = new();
    private MonitorHealthSnapshot _snapshot = new("starting", null, null, null, 0);

    public MonitorHealthSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            return _snapshot;
        }
    }

    public void RecordSuccess(int watchedFiles)
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            _snapshot = new MonitorHealthSnapshot("healthy", now, now, null, watchedFiles);
        }
    }

    public void RecordScanning(int processedFiles, int watchedFiles)
    {
        lock (_gate)
        {
            _snapshot = _snapshot with
            {
                Status = "scanning",
                LastScanAt = DateTimeOffset.UtcNow,
                ErrorCode = $"initial_scan_{processedFiles}_of_{watchedFiles}",
                WatchedFiles = watchedFiles
            };
        }
    }

    public void RecordDegraded(string errorCode, int watchedFiles)
    {
        lock (_gate)
        {
            _snapshot = _snapshot with
            {
                Status = "degraded",
                LastScanAt = DateTimeOffset.UtcNow,
                ErrorCode = errorCode,
                WatchedFiles = watchedFiles
            };
        }
    }
}
