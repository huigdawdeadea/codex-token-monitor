using CodexTokenMonitor.Core.Models;
using CodexTokenMonitor.Core.Storage;
using Microsoft.Data.Sqlite;

namespace CodexTokenMonitor.Tests;

public sealed class MonitorDatabaseTests
{
    [Fact]
    public async Task MigratesProjectColumnsAndSchedulesMetadataRescan()
    {
        using var temporary = new TemporaryDirectory();
        var databasePath = Path.Combine(temporary.Path, "monitor.db");
        var database = new MonitorDatabase(databasePath);
        await database.InitializeAsync();

        await using (var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            var legacy = connection.CreateCommand();
            legacy.CommandText = """
                ALTER TABLE sessions DROP COLUMN project_name;
                ALTER TABLE sessions DROP COLUMN project_path;
                INSERT INTO file_checkpoints(path, offset, session_id, updated_utc)
                VALUES('legacy.jsonl', 10, 'legacy-session', '2026-08-27T00:00:00Z');
                """;
            await legacy.ExecuteNonQueryAsync();
        }

        await database.InitializeAsync();

        Assert.Null(await database.GetCheckpointAsync("legacy.jsonl"));
        await using var verify = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
        await verify.OpenAsync();
        var inspect = verify.CreateCommand();
        inspect.CommandText = "PRAGMA table_info(sessions)";
        var columns = new List<string>();
        await using var reader = await inspect.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(1));
        }

        Assert.Contains("project_path", columns);
        Assert.Contains("project_name", columns);
        Assert.Contains("project_status", columns);
    }

    [Fact]
    public async Task SkillMigrationKeepsMostCompleteLegacyInvocationDuplicate()
    {
        using var temporary = new TemporaryDirectory();
        var databasePath = Path.Combine(temporary.Path, "monitor.db");
        var database = new MonitorDatabase(databasePath);
        await database.InitializeAsync();
        await using (var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            var legacy = connection.CreateCommand();
            legacy.CommandText = """
                DROP INDEX ux_skill_invocation_identity;
                INSERT INTO skill_invocations(
                    session_id, rollout_path, task_id, skill_name, skill_path, version_hash,
                    confidence, started_utc, ended_utc, model, loaded_tokens, evidence_kind)
                VALUES
                    ('session', 'rollout', 'task', 'skill', 'skill.md', 'hash',
                     'medium', '2026-08-20T01:00:00Z', NULL, 'model', 0, 'verified_skill_path'),
                    ('session', 'rollout', 'task', 'skill', 'skill.md', 'hash',
                     'high', '2026-08-20T01:00:00Z', '2026-08-20T01:01:00Z', 'model', 600, 'complete_skill_read');
                """;
            await legacy.ExecuteNonQueryAsync();
        }

        await database.InitializeAsync();

        await using var verify = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
        await verify.OpenAsync();
        var selected = verify.CreateCommand();
        selected.CommandText = "SELECT COUNT(*), MAX(loaded_tokens), MAX(ended_utc IS NOT NULL) FROM skill_invocations";
        await using var reader = await selected.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1, reader.GetInt32(0));
        Assert.Equal(600, reader.GetInt64(1));
        Assert.Equal(1, reader.GetInt32(2));
    }

    [Fact]
    public async Task PersistsSnapshotsSettingsAndCheckpoints()
    {
        using var temporary = new TemporaryDirectory();
        var database = new MonitorDatabase(Path.Combine(temporary.Path, "monitor.db"));
        await database.InitializeAsync();
        var timestamp = DateTimeOffset.Parse("2026-08-26T01:00:00Z");
        var result = new RolloutFileResult(
            "rollout.jsonl",
            123,
            false,
            "session-1",
            new ParsedSessionMetadata("session-1", timestamp, "gpt-test", "desktop"),
            new ParsedTokenEvent(
                timestamp,
                new TokenUsage(1000, 800, 0, 200, 50, 1200),
                new TokenUsage(600, 500, 0, 100, 25, 700),
                10_000),
            2,
            2,
            0,
            0);

        var applied = await database.ApplyFileResultAsync(result);
        await database.SaveSettingsAsync(new MonitorSettings { ActiveWindowMinutes = 12 });

        Assert.Null(applied.Previous);
        Assert.NotNull(applied.Current);
        Assert.Equal(1200, applied.Current.TotalUsage.TotalTokens);
        Assert.Equal(7, applied.Current.EstimatedContextPercent);
        Assert.Equal("gpt-test", applied.Current.Model);
        var checkpoint = await database.GetCheckpointAsync("rollout.jsonl");
        Assert.Equal(123, checkpoint?.Offset);
        Assert.Equal(12, (await database.GetSettingsAsync()).ActiveWindowMinutes);
    }

    [Fact]
    public async Task PurgesTitlesAndClearsSessionsWithoutRewindingFiles()
    {
        using var temporary = new TemporaryDirectory();
        var database = new MonitorDatabase(Path.Combine(temporary.Path, "monitor.db"));
        await database.InitializeAsync();
        var timestamp = DateTimeOffset.UtcNow;
        await database.ApplyFileResultAsync(new RolloutFileResult(
            "rollout.jsonl", 99, false, "session-1",
            new ParsedSessionMetadata("session-1", timestamp, null, null),
            null, 1, 1, 0, 0));
        await database.ApplyEnrichmentAsync(
            [new SessionEnrichment("session-1", "Sensitive title", timestamp.AddDays(-100), null, null)],
            captureTitles: true);
        await database.SaveSessionAnnotationAsync(
            "session-1",
            new SessionAnnotation("development", "completed", 25, null, timestamp));

        await database.PurgeExpiredTitlesAsync(timestamp.AddDays(-90));
        Assert.Null((await database.GetSessionAsync("session-1"))?.Title);
        Assert.Equal("completed", (await database.GetSessionAsync("session-1"))?.Annotation.Outcome);

        await database.ClearHistoryAsync();
        Assert.Empty(await database.ListSessionsAsync());
        Assert.Equal(99, (await database.GetCheckpointAsync("rollout.jsonl"))?.Offset);

        await database.ApplyFileResultAsync(new RolloutFileResult(
            "replacement.jsonl", 10, false, "session-1",
            new ParsedSessionMetadata("session-1", timestamp, null, null),
            null, 1, 1, 0, 0));
        Assert.Equal("unmarked", (await database.GetSessionAsync("session-1"))?.Annotation.Outcome);
    }

    [Fact]
    public async Task ModelSummarySeparatesReasoningDetails()
    {
        using var temporary = new TemporaryDirectory();
        var database = new MonitorDatabase(Path.Combine(temporary.Path, "monitor.db"));
        await database.InitializeAsync();
        var timestamp = DateTimeOffset.UtcNow;
        foreach (var (id, total, effort) in new[] { ("one", 100L, "low"), ("two", 300L, "high") })
        {
            await database.ApplyFileResultAsync(new RolloutFileResult(
                $"{id}.jsonl", total, false, id,
                new ParsedSessionMetadata(id, timestamp, "gpt-test", null),
                new ParsedTokenEvent(timestamp, new TokenUsage(total, 0, 0, 0, 0, total), TokenUsage.Empty, 1000),
                2, 2, 0, 0));
            await database.ApplyEnrichmentAsync(
                [new SessionEnrichment(id, null, null, "gpt-test", effort)],
                captureTitles: false);
        }

        var summary = Assert.Single(await database.GetModelSummariesAsync());

        Assert.Equal(2, summary.SessionCount);
        Assert.Equal(400, summary.TotalTokens);
        Assert.Equal(200, summary.MedianTokens);
        Assert.Equal(2, summary.Reasoning.Count);
    }

    [Fact]
    public async Task AValidEventRecoversAPreviouslyDegradedSession()
    {
        using var temporary = new TemporaryDirectory();
        var database = new MonitorDatabase(Path.Combine(temporary.Path, "monitor.db"));
        await database.InitializeAsync();
        var timestamp = DateTimeOffset.UtcNow;
        await database.ApplyFileResultAsync(new RolloutFileResult(
            "rollout.jsonl", 20, false, "session-1",
            new ParsedSessionMetadata("session-1", timestamp, null, null),
            null, 1, 1, 1, 0, "invalid_token_usage"));
        Assert.Equal("degraded", (await database.GetSessionAsync("session-1"))?.DataStatus);

        await database.ApplyFileResultAsync(new RolloutFileResult(
            "rollout.jsonl", 40, false, "session-1", null,
            new ParsedTokenEvent(timestamp.AddMinutes(1), new TokenUsage(10, 0, 0, 2, 0, 12), new TokenUsage(10, 0, 0, 2, 0, 12), 100),
            1, 1, 0, 0));

        var recovered = await database.GetSessionAsync("session-1");
        Assert.Equal("healthy", recovered?.DataStatus);
        Assert.Null(recovered?.ErrorCode);
    }

    [Fact]
    public async Task DisablingTitleCaptureRemovesTheTitleAndItsTimestamp()
    {
        using var temporary = new TemporaryDirectory();
        var database = new MonitorDatabase(Path.Combine(temporary.Path, "monitor.db"));
        await database.InitializeAsync();
        var timestamp = DateTimeOffset.UtcNow;
        await database.ApplyFileResultAsync(new RolloutFileResult(
            "rollout.jsonl", 10, false, "session-1",
            new ParsedSessionMetadata("session-1", timestamp, null, null), null, 1, 1, 0, 0));
        var enrichment = new SessionEnrichment("session-1", "Local title", timestamp, null, null);
        await database.ApplyEnrichmentAsync([enrichment], captureTitles: true);
        await database.ApplyEnrichmentAsync([enrichment], captureTitles: false);

        var session = await database.GetSessionAsync("session-1");
        Assert.Null(session?.Title);
        Assert.Null(session?.TitleUpdatedAt);
    }

    [Fact]
    public async Task SchedulesOneProjectMetadataRescanWhenIdentityRulesChange()
    {
        using var temporary = new TemporaryDirectory();
        var databasePath = Path.Combine(temporary.Path, "monitor.db");
        var database = new MonitorDatabase(databasePath);
        await database.InitializeAsync();
        var timestamp = DateTimeOffset.UtcNow;
        await database.ApplyFileResultAsync(new RolloutFileResult(
            "identity.jsonl",
            120,
            false,
            "identity-session",
            new ParsedSessionMetadata(
                "identity-session",
                timestamp,
                "gpt-test",
                "desktop",
                "D:\\worktrees\\1234",
                "1234",
                "pending"),
            null,
            1,
            1,
            0,
            0));

        await using (var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            var downgrade = connection.CreateCommand();
            downgrade.CommandText = "UPDATE settings SET value = '1' WHERE key = 'project_identity_version'";
            await downgrade.ExecuteNonQueryAsync();
        }

        await database.InitializeAsync();

        Assert.Null(await database.GetCheckpointAsync("identity.jsonl"));
        await using var verify = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
        await verify.OpenAsync();
        var version = verify.CreateCommand();
        version.CommandText = "SELECT value FROM settings WHERE key = 'project_identity_version'";
        Assert.Equal("2", Convert.ToString(await version.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task AggregatesDailyUsageAcrossGrowthAndCounterResetsWithoutDoubleCounting()
    {
        using var temporary = new TemporaryDirectory();
        var database = new MonitorDatabase(Path.Combine(temporary.Path, "monitor.db"));
        await database.InitializeAsync();
        var localNoon = DateTime.Today.AddHours(12);
        var today = new DateTimeOffset(localNoon, TimeZoneInfo.Local.GetUtcOffset(localNoon));
        var yesterday = today.AddDays(-1);
        ParsedTokenEvent[] events =
        [
            new(yesterday, Usage(90, 70, 10, 5), Usage(90, 70, 10, 5), 1000),
            new(yesterday.AddHours(1), Usage(135, 110, 15, 7), Usage(45, 40, 5, 2), 1000),
            new(today, Usage(225, 190, 25, 12), Usage(90, 80, 10, 5), 1000),
            new(today.AddHours(1), Usage(54, 40, 6, 3), Usage(54, 40, 6, 3), 1000)
        ];
        var result = new RolloutFileResult(
            "usage.jsonl", 500, false, "session-1",
            new ParsedSessionMetadata("session-1", yesterday, "gpt-test", "desktop"),
            events[^1], 5, 5, 0, 0, null, events);

        await database.ApplyFileResultAsync(result);
        await database.ApplyFileResultAsync(result);
        var summary = await database.GetUsageSummaryAsync(7);
        var yesterdaySummary = Assert.Single(summary.Days, day => day.Date == DateOnly.FromDateTime(yesterday.LocalDateTime));

        Assert.Equal(310, summary.AllTime.TotalTokens);
        Assert.Equal(160, summary.Today.Usage.TotalTokens);
        Assert.Equal(144, summary.Today.Usage.InputTokens);
        Assert.Equal(16, summary.Today.Usage.OutputTokens);
        Assert.Equal(120, summary.Today.Usage.CachedInputTokens);
        Assert.Equal(150, yesterdaySummary.Usage.TotalTokens);
        Assert.Equal(1, summary.Today.SessionCount);
        Assert.Equal(1, summary.TotalSessions);
        Assert.Equal(7, summary.Days.Count);
    }

    [Fact]
    public async Task IgnoresRepeatedCumulativeUsageSnapshotsAtDifferentTimes()
    {
        using var temporary = new TemporaryDirectory();
        var database = new MonitorDatabase(Path.Combine(temporary.Path, "monitor.db"));
        await database.InitializeAsync();
        var timestamp = DateTimeOffset.UtcNow;
        ParsedTokenEvent[] events =
        [
            new(timestamp, Usage(100, 80, 20, 5), Usage(100, 80, 20, 5), 1000),
            new(timestamp.AddSeconds(1), Usage(100, 80, 20, 5), Usage(100, 80, 20, 5), 1000)
        ];

        await database.ApplyFileResultAsync(new RolloutFileResult(
            "duplicate.jsonl", 100, false, "duplicate-session",
            new ParsedSessionMetadata("duplicate-session", timestamp, "gpt-test", "desktop"),
            events[^1], 3, 3, 0, 0, null, events));

        var summary = await database.GetUsageSummaryAsync(7);
        Assert.Equal(120, summary.AllTime.TotalTokens);
    }

    [Fact]
    public async Task ReturnsAnEmptySkillUsageSummaryAfterInitialization()
    {
        using var temporary = new TemporaryDirectory();
        var database = new MonitorDatabase(Path.Combine(temporary.Path, "monitor.db"));
        await database.InitializeAsync();

        var summary = await database.GetSkillUsageSummaryAsync();

        Assert.Empty(summary.Skills);
        Assert.Equal(100m, summary.CoveragePercent);
    }

    [Fact]
    public async Task ReplacesSkillRolloutAndAggregatesExclusiveAndCollaborationUsage()
    {
        using var temporary = new TemporaryDirectory();
        var database = new MonitorDatabase(Path.Combine(temporary.Path, "monitor.db"));
        await database.InitializeAsync();
        var observedAt = DateTimeOffset.UtcNow;
        var firstPath = Path.Combine(temporary.Path, "first", "SKILL.md");
        var secondPath = Path.Combine(temporary.Path, "second", "SKILL.md");
        await database.ReplaceSkillDefinitionsAsync(
        [
            new("first", firstPath, "codex", true, "hash-1", 100, 90, 30, "o200k_base", "1", false, observedAt),
            new("second", secondPath, "agent", true, "hash-2", 120, 100, 40, "o200k_base", "1", false, observedAt)
        ]);
        var rolloutPath = Path.Combine(temporary.Path, "rollout.jsonl");
        var firstInvocation = new SkillInvocationSnapshot(
            "session", rolloutPath, "task", "first", firstPath, "hash-1", "high",
            observedAt, observedAt.AddMinutes(1), "gpt-test", 30, "full_read");
        await database.ReplaceSkillRolloutAsync(new SkillRolloutAnalysis(
            rolloutPath, 100, 10,
            [firstInvocation],
            [
                new("session", rolloutPath, "task", observedAt.AddSeconds(1), "gpt-test", "exclusive", "first", "first", "high", Usage(80, 20, 20, 5)),
                new("session", rolloutPath, "task", observedAt.AddSeconds(2), "gpt-test", "collaboration", "first+second", "first|second", "high", Usage(160, 40, 40, 10))
            ]), 1);

        var initial = await database.GetSkillUsageSummaryAsync();
        Assert.Equal(2, initial.ActiveSkillCount);
        Assert.Equal(1, initial.InvocationCount);
        Assert.Equal(100, initial.ExclusiveTokens);
        Assert.Equal(200, initial.CollaborativeTokens);
        Assert.Equal(200, Assert.Single(initial.Collaborations).Usage.TotalTokens);
        Assert.Equal(200, Assert.Single(initial.Collaborations).UnpricedTokens);
        Assert.Equal(200, Assert.Single(initial.Skills, item => item.Name == "second").CollaborativeUsage.TotalTokens);
        Assert.False(await database.SkillRolloutNeedsRebuildAsync(rolloutPath, 100, 10, 1));

        await database.ReplaceSkillDefinitionsAsync(
        [
            new("first", firstPath, "codex", true, "hash-1-updated", 110, 95, 32, "o200k_base", "1", false, observedAt.AddMinutes(2)),
            new("second", secondPath, "agent", true, "hash-2", 120, 100, 40, "o200k_base", "1", false, observedAt.AddMinutes(2))
        ]);
        var versioned = await database.GetSkillUsageSummaryAsync();
        var firstSkill = Assert.Single(versioned.Skills, item => item.Name == "first");
        Assert.Equal(32, firstSkill.BaseTokens);
        Assert.Equal(2, firstSkill.Versions.Count);

        await database.ReplaceSkillRolloutAsync(new SkillRolloutAnalysis(
            rolloutPath, 120, 11,
            [firstInvocation with { LoadedTokens = 35 }],
            [new("session", rolloutPath, "task", observedAt.AddSeconds(3), "gpt-test", "exclusive", "first", "first", "high", Usage(240, 60, 60, 15))]), 1);

        var replaced = await database.GetSkillUsageSummaryAsync();
        Assert.Equal(1, replaced.InvocationCount);
        Assert.Equal(300, replaced.ExclusiveTokens);
        Assert.Equal(0, replaced.CollaborativeTokens);
        Assert.Empty(replaced.Collaborations);
        Assert.True(await database.SkillRolloutNeedsRebuildAsync(rolloutPath, 120, 11, 2));
    }

    [Fact]
    public async Task EstimatesDailyWeeklyAndMonthlyApiEquivalentCost()
    {
        using var temporary = new TemporaryDirectory();
        var database = new MonitorDatabase(Path.Combine(temporary.Path, "monitor.db"));
        await database.InitializeAsync();
        var localNoon = DateTime.Today.AddHours(12);
        var timestamp = new DateTimeOffset(localNoon, TimeZoneInfo.Local.GetUtcOffset(localNoon));
        var tokenEvent = new ParsedTokenEvent(
            timestamp,
            Usage(80, 40, 20, 5),
            Usage(80, 40, 20, 5),
            1000);

        await database.ApplyFileResultAsync(new RolloutFileResult(
            "estimate.jsonl", 100, false, "estimate-session",
            new ParsedSessionMetadata("estimate-session", timestamp, "gpt-5.6-sol", "desktop"),
            tokenEvent, 2, 2, 0, 0, null, [tokenEvent]));

        var summary = await database.GetUsageSummaryAsync(7);
        var today = DateOnly.FromDateTime(localNoon);

        Assert.Equal(100, summary.CostEstimate.Daily.Usage.TotalTokens);
        Assert.Equal(100, summary.CostEstimate.Daily.PricedTokens);
        Assert.Equal(0, summary.CostEstimate.Daily.UnpricedTokens);
        Assert.Equal(0.000576m, summary.CostEstimate.Daily.EstimatedCostUsd);
        Assert.Equal(0.000576m, summary.CostEstimate.Weekly.EstimatedCostUsd);
        Assert.Equal(0.000576m, summary.CostEstimate.Monthly.EstimatedCostUsd);
        Assert.Equal(today, summary.CostEstimate.Daily.StartDate);
        Assert.Equal(today, summary.CostEstimate.Monthly.EndDate);
        Assert.Equal("USD", summary.CostEstimate.Currency);
    }

    [Fact]
    public async Task AnalyzesSelectedDayAgainstBaselinesAndRanksSessions()
    {
        using var temporary = new TemporaryDirectory();
        var database = new MonitorDatabase(Path.Combine(temporary.Path, "monitor.db"));
        await database.InitializeAsync();
        var localNoon = DateTime.Today.AddHours(12);
        var selectedAt = new DateTimeOffset(localNoon, TimeZoneInfo.Local.GetUtcOffset(localNoon));
        var previousAt = selectedAt.AddDays(-1);
        ParsedTokenEvent[] primaryEvents =
        [
            new(previousAt, Usage(80, 60, 20, 5), Usage(80, 60, 20, 5), 1000),
            new(selectedAt, Usage(240, 180, 60, 20), Usage(240, 180, 60, 20), 1000)
        ];
        var secondaryEvent = new ParsedTokenEvent(
            selectedAt.AddHours(1), Usage(80, 60, 20, 5), Usage(80, 60, 20, 5), 1000);

        await database.ApplyFileResultAsync(new RolloutFileResult(
            "primary.jsonl", 300, false, "primary-session",
            new ParsedSessionMetadata("primary-session", previousAt, "gpt-primary", "desktop"),
            primaryEvents[^1], 3, 3, 0, 0, null, primaryEvents));
        await database.ApplyFileResultAsync(new RolloutFileResult(
            "secondary.jsonl", 100, false, "secondary-session",
            new ParsedSessionMetadata("secondary-session", selectedAt, "gpt-secondary", "desktop"),
            secondaryEvent, 2, 2, 0, 0, null, [secondaryEvent]));
        await database.ApplyEnrichmentAsync(
            [new SessionEnrichment("primary-session", "Primary task", selectedAt, "gpt-primary", "high")],
            captureTitles: true);
        await database.SaveSessionAnnotationAsync(
            "primary-session",
            new SessionAnnotation("development", "completed", 45, null, selectedAt));
        await database.SaveSessionAnnotationAsync(
            "secondary-session",
            new SessionAnnotation("debugging", "rework", 20, "test_defect", selectedAt));

        var selectedDate = DateOnly.FromDateTime(localNoon);
        var analysis = await database.GetDailyUsageAnalysisAsync(selectedDate);

        Assert.Equal(400, analysis.Selected.Usage.TotalTokens);
        Assert.Equal(2, analysis.Selected.SessionCount);
        Assert.Equal(100, analysis.PreviousDay.Usage.TotalTokens);
        Assert.Equal(14, analysis.SevenDayAverageTokens);
        Assert.Equal(2, analysis.TopSessions.Count);
        Assert.Equal("primary-session", analysis.TopSessions[0].SessionId);
        Assert.Equal("Primary task", analysis.TopSessions[0].DisplayName);
        Assert.Equal("gpt-primary", analysis.TopSessions[0].Model);
        Assert.Equal("high", analysis.TopSessions[0].ReasoningEffort);
        Assert.Equal(300, analysis.TopSessions[0].Usage.TotalTokens);
        Assert.Equal("development", analysis.TopSessions[0].Annotation.TaskType);
        Assert.Equal("completed", analysis.TopSessions[0].Annotation.Outcome);
        Assert.Equal(2, analysis.AnnotatedSessionCount);
        Assert.Equal(1, analysis.CompletedSessionCount);
        Assert.Equal(300, analysis.CompletedTokens);
        Assert.Equal(1, analysis.ReworkSessionCount);
        Assert.Equal(100, analysis.ReworkTokens);

        var historical = await database.GetDailyUsageAnalysisAsync(selectedDate.AddDays(-1));
        Assert.Equal(1, historical.AnnotatedSessionCount);
        Assert.Equal(1, historical.CompletedSessionCount);
        Assert.Equal(100, historical.CompletedTokens);
    }

    [Fact]
    public async Task RanksDaysAndSessionsByTotalTokenUsage()
    {
        using var temporary = new TemporaryDirectory();
        var database = new MonitorDatabase(Path.Combine(temporary.Path, "monitor.db"));
        await database.InitializeAsync();
        var localNoon = DateTime.Today.AddHours(12);
        var firstDay = new DateTimeOffset(localNoon.AddDays(-1), TimeZoneInfo.Local.GetUtcOffset(localNoon.AddDays(-1)));
        var secondDay = new DateTimeOffset(localNoon, TimeZoneInfo.Local.GetUtcOffset(localNoon));
        ParsedTokenEvent[] firstEvents =
        [
            new(firstDay, Usage(240, 120, 60, 10), Usage(240, 120, 60, 10), 1000)
        ];
        ParsedTokenEvent[] secondEvents =
        [
            new(firstDay.AddHours(1), Usage(80, 40, 20, 5), Usage(80, 40, 20, 5), 1000),
            new(secondDay, Usage(400, 200, 100, 20), Usage(400, 200, 100, 20), 1000)
        ];

        await database.ApplyFileResultAsync(new RolloutFileResult(
            "first.jsonl", 100, false, "first-session",
            new ParsedSessionMetadata("first-session", firstDay, "gpt-5.6-sol", "desktop", "D:\\projects\\alpha", "alpha"),
            firstEvents[^1], 2, 2, 0, 0, null, firstEvents));
        await database.ApplyFileResultAsync(new RolloutFileResult(
            "second.jsonl", 200, false, "second-session",
            new ParsedSessionMetadata("second-session", firstDay, "gpt-5.6-terra", "desktop", "D:\\projects\\alpha", "alpha"),
            secondEvents[^1], 3, 3, 0, 0, null, secondEvents));
        await database.ApplyEnrichmentAsync(
            [new SessionEnrichment("second-session", "Highest session", secondDay, "gpt-5.6-terra", "high")],
            captureTitles: true);

        var rankings = await database.GetUsageRankingsAsync();

        Assert.Equal(DateOnly.FromDateTime(secondDay.LocalDateTime), rankings.Days[0].Date);
        Assert.Equal(500, rankings.Days[0].Usage.TotalTokens);
        Assert.Equal(400, rankings.Days[1].Usage.TotalTokens);
        Assert.Equal("second-session", rankings.Sessions[0].SessionId);
        Assert.Equal("Highest session", rankings.Sessions[0].DisplayName);
        Assert.Equal(500, rankings.Sessions[0].Usage.TotalTokens);
        Assert.Equal(secondDay, rankings.Sessions[0].LastActivityAt);
        Assert.Equal(300, rankings.Sessions[1].Usage.TotalTokens);
        var project = Assert.Single(rankings.Projects);
        Assert.Equal("alpha", project.DisplayName);
        Assert.Equal("D:\\projects\\alpha", project.ProjectPath);
        Assert.Equal(2, project.SessionCount);
        Assert.Equal(2, project.ModelCount);
        Assert.Equal(800, project.Usage.TotalTokens);
        Assert.True(project.EstimatedCostUsd > 0);
        Assert.Equal(0, project.UnpricedTokens);
    }

    [Fact]
    public async Task AggregatesEveryPositiveSessionIntoExactlyOneDailyProject()
    {
        using var temporary = new TemporaryDirectory();
        var database = new MonitorDatabase(Path.Combine(temporary.Path, "monitor.db"));
        await database.InitializeAsync();
        var localNoon = DateTime.Today.AddHours(12);
        var selectedAt = new DateTimeOffset(localNoon, TimeZoneInfo.Local.GetUtcOffset(localNoon));
        var previousAt = selectedAt.AddDays(-1);

        async Task ApplyAsync(
            string sessionId,
            string path,
            string? projectPath,
            string? projectName,
            string projectStatus,
            IReadOnlyList<ParsedTokenEvent> events)
        {
            await database.ApplyFileResultAsync(new RolloutFileResult(
                path,
                100,
                false,
                sessionId,
                new ParsedSessionMetadata(
                    sessionId,
                    events[0].Timestamp,
                    "gpt-test",
                    "desktop",
                    projectPath,
                    projectName,
                    projectStatus),
                events[^1],
                events.Count + 1,
                events.Count + 1,
                0,
                0,
                null,
                events));
        }

        ParsedTokenEvent[] firstEvents =
        [
            new(previousAt, Usage(80, 60, 20, 5), Usage(80, 60, 20, 5), 1000),
            new(selectedAt, Usage(240, 160, 60, 15), Usage(160, 100, 40, 10), 1000)
        ];
        ParsedTokenEvent[] secondEvents =
        [
            new(selectedAt.AddMinutes(1), Usage(80, 50, 20, 5), Usage(80, 50, 20, 5), 1000)
        ];
        ParsedTokenEvent[] sameNameDifferentRepository =
        [
            new(selectedAt.AddMinutes(2), Usage(40, 20, 10, 2), Usage(40, 20, 10, 2), 1000)
        ];
        ParsedTokenEvent[] pendingEvents =
        [
            new(selectedAt.AddMinutes(3), Usage(20, 10, 5, 1), Usage(20, 10, 5, 1), 1000)
        ];
        ParsedTokenEvent[] unidentifiedEvents =
        [
            new(selectedAt.AddMinutes(4), Usage(8, 4, 2, 1), Usage(8, 4, 2, 1), 1000)
        ];

        await ApplyAsync("alpha-one", "alpha-one.jsonl", "D:\\repos\\alpha", "alpha", "git", firstEvents);
        await ApplyAsync("alpha-two", "alpha-two.jsonl", "d:\\repos\\alpha", "alpha", "git", secondEvents);
        await ApplyAsync("other-alpha", "other-alpha.jsonl", "D:\\other\\alpha", "alpha", "git", sameNameDifferentRepository);
        await ApplyAsync("pending", "pending.jsonl", "D:\\deleted\\worktree", "worktree", "pending", pendingEvents);
        await ApplyAsync("unidentified", "unidentified.jsonl", null, null, "unidentified", unidentifiedEvents);
        await ApplyAsync("alpha-two", "alpha-two.jsonl", "d:\\repos\\alpha", "alpha", "git", secondEvents);

        var selectedDate = DateOnly.FromDateTime(localNoon);
        var breakdown = await database.GetDailyProjectUsageAsync(selectedDate);

        Assert.Equal(selectedDate, breakdown.Date);
        Assert.Equal(DateOnly.FromDateTime(previousAt.LocalDateTime), breakdown.EarliestDate);
        Assert.Equal(DateOnly.FromDateTime(DateTime.Now), breakdown.LatestDate);
        Assert.Equal(5, breakdown.SessionCount);
        Assert.Equal(4, breakdown.Projects.Count);
        Assert.Equal(385, breakdown.Usage.TotalTokens);
        Assert.Equal(308, breakdown.Usage.InputTokens);
        Assert.Equal(77, breakdown.Usage.OutputTokens);
        Assert.Equal(184, breakdown.Usage.CachedInputTokens);
        Assert.Equal(19, breakdown.Usage.ReasoningOutputTokens);
        Assert.Equal(breakdown.Usage, Sum(breakdown.Projects.Select(project => project.Usage)));
        Assert.Equal(breakdown.SessionCount, breakdown.Projects.Sum(project => project.Sessions.Count));

        var alpha = Assert.Single(breakdown.Projects, project =>
            string.Equals(project.ProjectPath, "D:\\repos\\alpha", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("git", alpha.ProjectStatus);
        Assert.Equal(2, alpha.Sessions.Count);
        Assert.Equal(300, alpha.Usage.TotalTokens);
        Assert.Equal(alpha.Usage, Sum(alpha.Sessions.Select(session => session.Usage)));
        Assert.Contains(breakdown.Projects, project => project.ProjectPath == "D:\\other\\alpha");
        Assert.Contains(breakdown.Projects, project => project.ProjectStatus == "pending");
        Assert.Contains(breakdown.Projects, project => project.ProjectStatus == "unidentified");

        var empty = await database.GetDailyProjectUsageAsync(selectedDate.AddDays(-2));
        Assert.Empty(empty.Projects);
        Assert.Equal(TokenUsage.Empty, empty.Usage);
        Assert.Equal(0, empty.SessionCount);
    }

    [Fact]
    public async Task RejectsAnnotationForUnknownSession()
    {
        using var temporary = new TemporaryDirectory();
        var database = new MonitorDatabase(Path.Combine(temporary.Path, "monitor.db"));
        await database.InitializeAsync();

        var saved = await database.SaveSessionAnnotationAsync(
            "missing",
            new SessionAnnotation("research", "partial", null, null, DateTimeOffset.UtcNow));

        Assert.False(saved);
    }

    [Fact]
    public async Task PreparesOneTimeUsageBackfillForALegacyCheckpoint()
    {
        using var temporary = new TemporaryDirectory();
        var database = new MonitorDatabase(Path.Combine(temporary.Path, "monitor.db"));
        await database.InitializeAsync();
        var timestamp = DateTimeOffset.UtcNow;
        await database.ApplyFileResultAsync(new RolloutFileResult(
            "legacy.jsonl", 80, false, "legacy-session",
            new ParsedSessionMetadata("legacy-session", timestamp, null, null),
            null, 1, 1, 0, 0));

        Assert.True(await database.PrepareUsageBackfillAsync());
        Assert.Null(await database.GetCheckpointAsync("legacy.jsonl"));
        Assert.False(await database.PrepareUsageBackfillAsync());
    }

    [Fact]
    public async Task VersionsPersistAndDerivedResetPreservesRawTokenFacts()
    {
        using var temporary = new TemporaryDirectory();
        var databasePath = Path.Combine(temporary.Path, "monitor.db");
        var database = new MonitorDatabase(databasePath);
        await database.InitializeAsync();
        var before = await database.GetDataVersionsAsync();
        var timestamp = DateTimeOffset.Parse("2026-08-31T01:00:00Z");
        await database.ApplyFileResultAsync(new RolloutFileResult(
            "rollout-version.jsonl",
            120,
            false,
            "session-version",
            new ParsedSessionMetadata("session-version", timestamp, "gpt-test", "desktop"),
            new ParsedTokenEvent(timestamp, Usage(80, 20, 20, 5), Usage(80, 20, 20, 5), 1000),
            2,
            2,
            0,
            0));
        var changed = await database.GetDataVersionsAsync();
        Assert.True(changed.Usage > before.Usage);
        await database.WriteMaterializedViewAsync("test-view", changed.Usage.ToString(), new[] { 1, 2, 3 });
        Assert.Equal(new[] { 1, 2, 3 }, await database.ReadMaterializedViewAsync<int[]>("test-view", changed.Usage.ToString()));

        await database.ResetDerivedDataAsync();

        Assert.Null(await database.GetCheckpointAsync("rollout-version.jsonl"));
        Assert.Null(await database.ReadMaterializedViewAsync<int[]>("test-view", changed.Usage.ToString()));
        await using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();
        var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM token_usage_events";
        Assert.Equal(1L, (long)(await count.ExecuteScalarAsync())!);
        var reopened = new MonitorDatabase(databasePath);
        var persisted = await reopened.GetDataVersionsAsync();
        Assert.True(persisted.Usage > changed.Usage);
    }

    private static TokenUsage Usage(long input, long cachedInput, long output, long reasoningOutput) =>
        new(input, cachedInput, 0, output, reasoningOutput, input + output);

    private static TokenUsage Sum(IEnumerable<TokenUsage> usages) => usages.Aggregate(
        TokenUsage.Empty,
        (total, usage) => new TokenUsage(
            total.InputTokens + usage.InputTokens,
            total.CachedInputTokens + usage.CachedInputTokens,
            total.CacheWriteInputTokens + usage.CacheWriteInputTokens,
            total.OutputTokens + usage.OutputTokens,
            total.ReasoningOutputTokens + usage.ReasoningOutputTokens,
            total.TotalTokens + usage.TotalTokens));
}
