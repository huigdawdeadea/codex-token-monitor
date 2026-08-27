namespace CodexTokenMonitor.Core.Models;

public sealed record MonitorSettings
{
    public int ActiveWindowMinutes { get; init; } = 5;
    public int TitleRetentionDays { get; init; } = 90;
    public bool CaptureTitles { get; init; } = true;

    public MonitorSettings Normalize() => this with
    {
        ActiveWindowMinutes = Math.Clamp(ActiveWindowMinutes, 1, 120),
        TitleRetentionDays = Math.Clamp(TitleRetentionDays, 1, 3650)
    };
}
