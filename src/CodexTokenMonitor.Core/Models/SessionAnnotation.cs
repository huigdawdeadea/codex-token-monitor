namespace CodexTokenMonitor.Core.Models;

public sealed record SessionAnnotation(
    string TaskType,
    string Outcome,
    int? HumanMinutes,
    string? ReworkReason,
    DateTimeOffset? UpdatedAt)
{
    public static SessionAnnotation Unmarked { get; } = new(
        "unclassified",
        "unmarked",
        null,
        null,
        null);

    public bool IsAnnotated => Outcome != "unmarked";
}
