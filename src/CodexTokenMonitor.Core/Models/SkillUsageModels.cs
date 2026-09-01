namespace CodexTokenMonitor.Core.Models;

public sealed record SkillDefinitionSnapshot(
    string Name,
    string Path,
    string SourceKind,
    bool IsActive,
    string ContentHash,
    long ByteCount,
    long CharacterCount,
    long BaseTokens,
    string Tokenizer,
    string TokenizerVersion,
    bool IsEstimated,
    DateTimeOffset ObservedAt);

public sealed record SkillInvocationSnapshot(
    string SessionId,
    string RolloutPath,
    string TaskId,
    string SkillName,
    string SkillPath,
    string VersionHash,
    string Confidence,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    string Model,
    long LoadedTokens,
    string EvidenceKind);

public sealed record SkillAttributionSnapshot(
    string SessionId,
    string RolloutPath,
    string TaskId,
    DateTimeOffset EventAt,
    string Model,
    string BucketType,
    string BucketKey,
    string SkillNames,
    string Confidence,
    TokenUsage Usage);

public sealed record SkillRolloutAnalysis(
    string RolloutPath,
    long Length,
    long LastWriteUtcTicks,
    IReadOnlyList<SkillInvocationSnapshot> Invocations,
    IReadOnlyList<SkillAttributionSnapshot> Attributions);

public sealed record SkillScanCheckpoint(
    string Path,
    long Offset,
    long Length,
    long LastWriteUtcTicks,
    int ParserVersion,
    string StateJson);

public sealed record SkillRolloutDelta(
    string RolloutPath,
    long Offset,
    long Length,
    long LastWriteUtcTicks,
    string StateJson,
    bool ReplaceExisting,
    int CompleteLines,
    int OversizedLines,
    IReadOnlyList<SkillInvocationSnapshot> Invocations,
    IReadOnlyList<SkillAttributionSnapshot> Attributions);

public sealed record SkillUsageSummary(
    int ActiveSkillCount,
    int InvocationCount,
    long ExclusiveTokens,
    long CollaborativeTokens,
    long UnattributedTokens,
    decimal CoveragePercent,
    IReadOnlyList<SkillUsageRanking> Skills,
    IReadOnlyList<SkillCollaborationSummary> Collaborations,
    DateTimeOffset GeneratedAt);

public sealed record SkillUsageRanking(
    string Name,
    bool IsActive,
    string SourceKind,
    long BaseTokens,
    bool IsEstimated,
    string Tokenizer,
    int InvocationCount,
    long AverageLoadedTokens,
    TokenUsage ExclusiveUsage,
    TokenUsage CollaborativeUsage,
    decimal EstimatedCostUsd,
    long UnpricedTokens,
    DateTimeOffset? LastUsedAt,
    IReadOnlyList<SkillVersionSummary> Versions);

public sealed record SkillVersionSummary(
    string ContentHash,
    string Path,
    long BaseTokens,
    long ByteCount,
    long CharacterCount,
    string Tokenizer,
    string TokenizerVersion,
    bool IsEstimated,
    DateTimeOffset ObservedAt);

public sealed record SkillCollaborationSummary(
    string Key,
    string SkillNames,
    int ModelCallCount,
    TokenUsage Usage,
    decimal EstimatedCostUsd,
    long UnpricedTokens,
    DateTimeOffset LastUsedAt);

public sealed record SkillUsageDetail(
    SkillUsageRanking Skill,
    IReadOnlyList<SkillInvocationSnapshot> Invocations,
    IReadOnlyList<SkillAttributionSnapshot> AttributionEvents);
