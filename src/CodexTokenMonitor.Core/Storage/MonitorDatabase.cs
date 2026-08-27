using System.Globalization;
using System.Text.Json;
using CodexTokenMonitor.Core.Models;
using CodexTokenMonitor.Core.Pricing;
using Microsoft.Data.Sqlite;

namespace CodexTokenMonitor.Core.Storage;

public sealed class MonitorDatabase(string databasePath)
{
    private readonly string _databasePath = Path.GetFullPath(databasePath);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public string DatabasePath => _databasePath;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            var command = connection.CreateCommand();
            command.CommandText = SchemaSql;
            await command.ExecuteNonQueryAsync(cancellationToken);
            await EnsureSessionProjectSchemaAsync(connection, cancellationToken);
            await EnsureTokenUsageEventsSchemaAsync(connection, cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<FileCheckpoint?> GetCheckpointAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT offset, session_id FROM file_checkpoints WHERE path = $path";
        command.Parameters.AddWithValue("$path", path);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new FileCheckpoint(path, reader.GetInt64(0), GetNullableString(reader, 1))
            : null;
    }

    public async Task<(SessionSnapshot? Previous, SessionSnapshot? Current)> ApplyFileResultAsync(
        RolloutFileResult result,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(result.SessionId))
        {
            return (null, null);
        }

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            var previous = await GetSessionAsync(connection, transaction, result.SessionId, cancellationToken);
            var current = Merge(previous, result);
            await UpsertSessionAsync(connection, transaction, current, cancellationToken);
            await InsertTokenEventsAsync(connection, transaction, result, cancellationToken);
            await UpsertCheckpointAsync(connection, transaction, result, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return (previous, current);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<bool> PrepareUsageBackfillAsync(CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            var check = connection.CreateCommand();
            check.CommandText = """
                SELECT CASE
                    WHEN EXISTS(SELECT 1 FROM sessions)
                     AND EXISTS(SELECT 1 FROM file_checkpoints)
                     AND NOT EXISTS(SELECT 1 FROM token_usage_events WHERE last_total_tokens > 0)
                    THEN 1 ELSE 0
                END
                """;
            var required = Convert.ToInt64(await check.ExecuteScalarAsync(cancellationToken)) == 1;
            if (!required)
            {
                return false;
            }

            var reset = connection.CreateCommand();
            reset.CommandText = "DELETE FROM token_usage_events; DELETE FROM file_checkpoints;";
            await reset.ExecuteNonQueryAsync(cancellationToken);
            return true;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<SessionSnapshot?> GetSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var session = await GetSessionAsync(connection, null, sessionId, cancellationToken);
        if (session is null)
        {
            return null;
        }

        return session with
        {
            Annotation = await ReadAnnotationAsync(connection, sessionId, cancellationToken)
        };
    }

    public async Task<IReadOnlyList<SessionSnapshot>> ListSessionsAsync(
        int limit = 500,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 5000);
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SessionColumns} FROM sessions ORDER BY last_activity_utc DESC LIMIT $limit";
        command.Parameters.AddWithValue("$limit", limit);
        var sessions = new List<SessionSnapshot>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                sessions.Add(ReadSession(reader));
            }
        }

        var annotations = await ReadAnnotationsAsync(connection, cancellationToken);
        return sessions
            .Select(session => session with
            {
                Annotation = annotations.GetValueOrDefault(session.SessionId, SessionAnnotation.Unmarked)
            })
            .ToArray();
    }

    public async Task ApplyEnrichmentAsync(
        IReadOnlyCollection<SessionEnrichment> enrichments,
        bool captureTitles,
        CancellationToken cancellationToken = default)
    {
        if (enrichments.Count == 0)
        {
            return;
        }

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            foreach (var enrichment in enrichments)
            {
                var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    UPDATE sessions SET
                        title = CASE WHEN $capture_titles = 1 THEN COALESCE($title, title) ELSE NULL END,
                        title_updated_utc = CASE
                            WHEN $capture_titles = 0 THEN NULL
                            WHEN $title IS NOT NULL THEN $title_updated_utc
                            ELSE title_updated_utc
                        END,
                        model = COALESCE($model, model),
                        reasoning_effort = COALESCE($reasoning_effort, reasoning_effort)
                    WHERE session_id = $session_id
                    """;
                command.Parameters.AddWithValue("$capture_titles", captureTitles ? 1 : 0);
                AddNullable(command, "$title", captureTitles ? enrichment.Title : null);
                AddNullable(command, "$title_updated_utc", enrichment.TitleUpdatedAt is null ? null : Format(enrichment.TitleUpdatedAt.Value));
                AddNullable(command, "$model", enrichment.Model);
                AddNullable(command, "$reasoning_effort", enrichment.ReasoningEffort);
                command.Parameters.AddWithValue("$session_id", enrichment.SessionId);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task SetMutedAsync(
        string sessionId,
        bool muted,
        CancellationToken cancellationToken = default)
    {
        await ExecuteWriteAsync(
            "UPDATE sessions SET is_muted = $muted WHERE session_id = $session_id",
            command =>
            {
                command.Parameters.AddWithValue("$muted", muted ? 1 : 0);
                command.Parameters.AddWithValue("$session_id", sessionId);
            },
            cancellationToken);
    }

    public Task DeleteAllTitlesAsync(CancellationToken cancellationToken = default) =>
        ExecuteWriteAsync(
            "UPDATE sessions SET title = NULL, title_updated_utc = NULL",
            null,
            cancellationToken);

    public Task PurgeExpiredTitlesAsync(
        DateTimeOffset cutoff,
        CancellationToken cancellationToken = default) =>
        ExecuteWriteAsync(
            "UPDATE sessions SET title = NULL, title_updated_utc = NULL WHERE title_updated_utc < $cutoff",
            command => command.Parameters.AddWithValue("$cutoff", Format(cutoff)),
            cancellationToken);

    public Task ClearHistoryAsync(CancellationToken cancellationToken = default) =>
        ExecuteWriteAsync(
            "DELETE FROM session_annotations; DELETE FROM token_usage_events; DELETE FROM sessions;",
            null,
            cancellationToken);

    public async Task<bool> SaveSessionAnnotationAsync(
        string sessionId,
        SessionAnnotation annotation,
        CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            var existsCommand = connection.CreateCommand();
            existsCommand.CommandText = "SELECT 1 FROM sessions WHERE session_id = $session_id";
            existsCommand.Parameters.AddWithValue("$session_id", sessionId);
            if (await existsCommand.ExecuteScalarAsync(cancellationToken) is null)
            {
                return false;
            }

            var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO session_annotations(
                    session_id, task_type, outcome, human_minutes, rework_reason, updated_utc)
                VALUES(
                    $session_id, $task_type, $outcome, $human_minutes, $rework_reason, $updated_utc)
                ON CONFLICT(session_id) DO UPDATE SET
                    task_type = excluded.task_type,
                    outcome = excluded.outcome,
                    human_minutes = excluded.human_minutes,
                    rework_reason = excluded.rework_reason,
                    updated_utc = excluded.updated_utc
                """;
            command.Parameters.AddWithValue("$session_id", sessionId);
            command.Parameters.AddWithValue("$task_type", annotation.TaskType);
            command.Parameters.AddWithValue("$outcome", annotation.Outcome);
            AddNullable(command, "$human_minutes", annotation.HumanMinutes);
            AddNullable(command, "$rework_reason", annotation.ReworkReason);
            command.Parameters.AddWithValue("$updated_utc", Format(annotation.UpdatedAt ?? DateTimeOffset.UtcNow));
            await command.ExecuteNonQueryAsync(cancellationToken);
            return true;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<MonitorSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key = 'monitor'";
        var value = await command.ExecuteScalarAsync(cancellationToken) as string;
        if (string.IsNullOrWhiteSpace(value))
        {
            return new MonitorSettings();
        }

        try
        {
            return (_jsonOptions is not null
                ? JsonSerializer.Deserialize<MonitorSettings>(value, _jsonOptions)
                : null)?.Normalize() ?? new MonitorSettings();
        }
        catch (JsonException)
        {
            return new MonitorSettings();
        }
    }

    public Task SaveSettingsAsync(
        MonitorSettings settings,
        CancellationToken cancellationToken = default)
    {
        var normalized = settings.Normalize();
        var json = JsonSerializer.Serialize(normalized, _jsonOptions);
        return ExecuteWriteAsync(
            "INSERT INTO settings(key, value) VALUES('monitor', $value) ON CONFLICT(key) DO UPDATE SET value = excluded.value",
            command => command.Parameters.AddWithValue("$value", json),
            cancellationToken);
    }

    public async Task<IReadOnlyList<ModelSummary>> GetModelSummariesAsync(
        CancellationToken cancellationToken = default)
    {
        var sessions = await ListSessionsAsync(5000, cancellationToken);
        return sessions
            .GroupBy(session => string.IsNullOrWhiteSpace(session.Model) ? "unknown" : session.Model)
            .Select(group => new ModelSummary(
                group.Key,
                group.Count(),
                group.Sum(session => session.TotalUsage.TotalTokens),
                Median(group.Select(session => session.TotalUsage.TotalTokens)),
                group.Sum(session => session.TotalUsage.InputTokens),
                group.Sum(session => session.TotalUsage.OutputTokens),
                group.Sum(session => session.TotalUsage.CachedInputTokens),
                group.Sum(session => session.TotalUsage.ReasoningOutputTokens),
                group.GroupBy(session => string.IsNullOrWhiteSpace(session.ReasoningEffort) ? "unknown" : session.ReasoningEffort)
                    .Select(reasoning => new ReasoningSummary(
                        reasoning.Key,
                        reasoning.Count(),
                        reasoning.Sum(session => session.TotalUsage.TotalTokens),
                        Median(reasoning.Select(session => session.TotalUsage.TotalTokens))))
                    .OrderByDescending(item => item.TotalTokens)
                    .ToArray()))
            .OrderByDescending(item => item.TotalTokens)
            .ToArray();
    }

    public async Task<UsageSummary> GetUsageSummaryAsync(
        int days = 14,
        CancellationToken cancellationToken = default)
    {
        days = Math.Clamp(days, 7, 90);
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = DailyUsageSql;
        var recordedDays = new Dictionary<DateOnly, DailyUsageSummary>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var date = DateOnly.ParseExact(reader.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture);
                recordedDays[date] = new DailyUsageSummary(
                    date,
                    new TokenUsage(
                        reader.GetInt64(1),
                        reader.GetInt64(2),
                        reader.GetInt64(3),
                        reader.GetInt64(4),
                        reader.GetInt64(5),
                        reader.GetInt64(6)),
                    reader.GetInt32(7));
            }
        }

        var calculatedAt = DateTimeOffset.Now;
        var today = DateOnly.FromDateTime(calculatedAt.DateTime);
        var visibleDays = Enumerable.Range(0, days)
            .Select(offset => today.AddDays(offset - days + 1))
            .Select(date => recordedDays.TryGetValue(date, out var summary)
                ? summary
                : new DailyUsageSummary(date, TokenUsage.Empty, 0))
            .ToArray();
        var allTime = SumUsage(recordedDays.Values.Select(item => item.Usage));
        var totalSessionsCommand = connection.CreateCommand();
        totalSessionsCommand.CommandText = "SELECT COUNT(DISTINCT session_id) FROM token_usage_events";
        var totalSessions = Convert.ToInt32(await totalSessionsCommand.ExecuteScalarAsync(cancellationToken));
        var costEstimate = await BuildUsageCostEstimateAsync(connection, today, calculatedAt, cancellationToken);

        return new UsageSummary(
            recordedDays.TryGetValue(today, out var todaySummary)
                ? todaySummary
                : new DailyUsageSummary(today, TokenUsage.Empty, 0),
            allTime,
            totalSessions,
            visibleDays,
            TimeZoneInfo.Local.Id,
            costEstimate);
    }

    public async Task<DailyUsageAnalysis> GetDailyUsageAnalysisAsync(
        DateOnly date,
        int top = 5,
        CancellationToken cancellationToken = default)
    {
        top = Math.Clamp(top, 1, 20);
        await using var connection = await OpenAsync(cancellationToken);
        var selected = await ReadDailyUsageAsync(connection, date, cancellationToken);
        var previousDay = await ReadDailyUsageAsync(connection, date.AddDays(-1), cancellationToken);

        var baselineCommand = connection.CreateCommand();
        baselineCommand.CommandText = """
            SELECT COALESCE(SUM(last_total_tokens), 0)
            FROM token_usage_events
            WHERE date(event_utc, 'localtime') >= $start_date
              AND date(event_utc, 'localtime') < $selected_date
            """;
        baselineCommand.Parameters.AddWithValue("$start_date", FormatDate(date.AddDays(-7)));
        baselineCommand.Parameters.AddWithValue("$selected_date", FormatDate(date));
        var baselineTotal = Convert.ToInt64(await baselineCommand.ExecuteScalarAsync(cancellationToken));
        var sevenDayAverage = (long)Math.Round(baselineTotal / 7d, MidpointRounding.AwayFromZero);

        var topCommand = connection.CreateCommand();
        topCommand.CommandText = """
            SELECT
                events.session_id,
                CASE
                    WHEN TRIM(COALESCE(sessions.title, '')) <> '' THEN sessions.title
                    ELSE SUBSTR(events.session_id, 1, 8)
                END AS display_name,
                COALESCE(sessions.model, 'unknown'),
                COALESCE(sessions.reasoning_effort, 'unknown'),
                SUM(events.last_input_tokens),
                SUM(events.last_cached_input_tokens),
                SUM(events.last_cache_write_input_tokens),
                SUM(events.last_output_tokens),
                SUM(events.last_reasoning_output_tokens),
                SUM(events.last_total_tokens),
                MAX(events.event_utc),
                COALESCE(annotations.task_type, 'unclassified'),
                COALESCE(annotations.outcome, 'unmarked'),
                annotations.human_minutes,
                annotations.rework_reason,
                annotations.updated_utc
            FROM token_usage_events AS events
            LEFT JOIN sessions ON sessions.session_id = events.session_id
            LEFT JOIN session_annotations AS annotations ON annotations.session_id = events.session_id
            WHERE date(events.event_utc, 'localtime') = $selected_date
            GROUP BY events.session_id
            HAVING SUM(events.last_total_tokens) > 0
            ORDER BY SUM(events.last_total_tokens) DESC, MAX(events.event_utc) DESC
            LIMIT $limit
            """;
        topCommand.Parameters.AddWithValue("$selected_date", FormatDate(date));
        topCommand.Parameters.AddWithValue("$limit", top);
        var topSessions = new List<DailySessionUsage>();
        await using (var reader = await topCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                topSessions.Add(new DailySessionUsage(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    ReadUsage(reader, 4),
                    ParseDate(reader.GetString(10)),
                    ReadAnnotation(reader, 11)));
            }
        }

        var resultCommand = connection.CreateCommand();
        resultCommand.CommandText = """
            WITH per_session AS (
                SELECT session_id, SUM(last_total_tokens) AS total_tokens
                FROM token_usage_events
                WHERE date(event_utc, 'localtime') = $selected_date
                GROUP BY session_id
                HAVING SUM(last_total_tokens) > 0
            )
            SELECT
                COALESCE(SUM(CASE WHEN COALESCE(annotations.outcome, 'unmarked') <> 'unmarked' THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN annotations.outcome = 'completed' THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN annotations.outcome = 'completed' THEN per_session.total_tokens ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN annotations.outcome = 'rework' THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN annotations.outcome = 'rework' THEN per_session.total_tokens ELSE 0 END), 0)
            FROM per_session
            LEFT JOIN session_annotations AS annotations ON annotations.session_id = per_session.session_id
            """;
        resultCommand.Parameters.AddWithValue("$selected_date", FormatDate(date));
        await using var resultReader = await resultCommand.ExecuteReaderAsync(cancellationToken);
        await resultReader.ReadAsync(cancellationToken);

        return new DailyUsageAnalysis(
            selected,
            previousDay,
            sevenDayAverage,
            topSessions,
            resultReader.GetInt32(0),
            resultReader.GetInt32(1),
            resultReader.GetInt64(2),
            resultReader.GetInt32(3),
            resultReader.GetInt64(4));
    }

    public async Task<UsageRankings> GetUsageRankingsAsync(
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 10, 500);
        await using var connection = await OpenAsync(cancellationToken);

        var dayCommand = connection.CreateCommand();
        dayCommand.CommandText = """
            SELECT
                date(event_utc, 'localtime') AS local_date,
                SUM(last_input_tokens),
                SUM(last_cached_input_tokens),
                SUM(last_cache_write_input_tokens),
                SUM(last_output_tokens),
                SUM(last_reasoning_output_tokens),
                SUM(last_total_tokens),
                COUNT(DISTINCT CASE WHEN last_total_tokens > 0 THEN session_id END)
            FROM token_usage_events
            WHERE local_date IS NOT NULL
            GROUP BY local_date
            HAVING SUM(last_total_tokens) > 0
            ORDER BY SUM(last_total_tokens) DESC, local_date DESC
            LIMIT $limit
            """;
        dayCommand.Parameters.AddWithValue("$limit", limit);
        var days = new List<DailyUsageSummary>();
        await using (var reader = await dayCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                days.Add(new DailyUsageSummary(
                    DateOnly.ParseExact(reader.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                    ReadUsage(reader, 1),
                    reader.GetInt32(7)));
            }
        }

        var sessionCommand = connection.CreateCommand();
        sessionCommand.CommandText = """
            SELECT
                sessions.session_id,
                CASE
                    WHEN TRIM(COALESCE(sessions.title, '')) <> '' THEN sessions.title
                    ELSE SUBSTR(sessions.session_id, 1, 8)
                END AS display_name,
                COALESCE(sessions.model, 'unknown'),
                COALESCE(sessions.reasoning_effort, 'unknown'),
                sessions.total_input_tokens,
                sessions.total_cached_input_tokens,
                sessions.total_cache_write_input_tokens,
                sessions.total_output_tokens,
                sessions.total_reasoning_output_tokens,
                sessions.total_tokens,
                sessions.last_activity_utc,
                COALESCE(annotations.task_type, 'unclassified'),
                COALESCE(annotations.outcome, 'unmarked'),
                annotations.human_minutes,
                annotations.rework_reason,
                annotations.updated_utc
            FROM sessions
            LEFT JOIN session_annotations AS annotations ON annotations.session_id = sessions.session_id
            WHERE sessions.total_tokens > 0
            ORDER BY sessions.total_tokens DESC, sessions.last_activity_utc DESC
            LIMIT $limit
            """;
        sessionCommand.Parameters.AddWithValue("$limit", limit);
        var sessions = new List<SessionUsageRanking>();
        await using (var reader = await sessionCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                sessions.Add(new SessionUsageRanking(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    ReadUsage(reader, 4),
                    ParseDate(reader.GetString(10)),
                    ReadAnnotation(reader, 11)));
            }
        }

        var projectCommand = connection.CreateCommand();
        projectCommand.CommandText = """
            SELECT
                sessions.project_path,
                sessions.project_name,
                COALESCE(NULLIF(TRIM(sessions.model), ''), 'unknown'),
                sessions.total_input_tokens,
                sessions.total_cached_input_tokens,
                sessions.total_cache_write_input_tokens,
                sessions.total_output_tokens,
                sessions.total_reasoning_output_tokens,
                sessions.total_tokens,
                sessions.last_activity_utc
            FROM sessions
            WHERE sessions.total_tokens > 0
            """;
        var projectRows = new List<ProjectSessionRow>();
        await using (var reader = await projectCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                projectRows.Add(new ProjectSessionRow(
                    GetNullableString(reader, 0),
                    GetNullableString(reader, 1),
                    reader.GetString(2),
                    ReadUsage(reader, 3),
                    ParseDate(reader.GetString(9))));
            }
        }

        var projects = projectRows
            .GroupBy(
                row => row.ProjectPath ?? "",
                StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var usage = SumUsage(group.Select(row => row.Usage));
                var modelCosts = group
                    .GroupBy(row => row.Model, StringComparer.OrdinalIgnoreCase)
                    .Select(modelGroup => UsageCostEstimator.EstimateModel(
                        modelGroup.Key,
                        SumUsage(modelGroup.Select(row => row.Usage))))
                    .ToArray();
                return new ProjectUsageRanking(
                    group.Select(row => row.ProjectName).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name))
                        ?? "未识别项目",
                    string.IsNullOrWhiteSpace(group.Key) ? null : group.Key,
                    group.Count(),
                    group.Select(row => row.Model).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    usage,
                    modelCosts.Sum(cost => cost.EstimatedCostUsd),
                    modelCosts.Where(cost => !cost.IsPriced).Sum(cost => cost.Usage.TotalTokens),
                    group.Max(row => row.LastActivityAt));
            })
            .OrderByDescending(project => project.Usage.TotalTokens)
            .ThenByDescending(project => project.LastActivityAt)
            .Take(limit)
            .ToArray();

        return new UsageRankings(days, sessions, projects, TimeZoneInfo.Local.Id);
    }

    private async Task ExecuteWriteAsync(
        string sql,
        Action<SqliteCommand>? configure,
        CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            var command = connection.CreateCommand();
            command.CommandText = sql;
            configure?.Invoke(command);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static SessionSnapshot Merge(SessionSnapshot? previous, RolloutFileResult result)
    {
        var now = DateTimeOffset.UtcNow;
        var metadata = result.Metadata;
        var token = result.LatestTokenEvent;
        var createdAt = metadata?.Timestamp ?? previous?.CreatedAt;
        var lastActivity = token?.Timestamp ?? previous?.LastActivityAt ?? metadata?.Timestamp ?? now;
        var hasErrors = result.ParseErrors > 0 || result.OversizedLines > 0;
        var recognized = result.RecognizedEvents > 0;

        return new SessionSnapshot
        {
            SessionId = result.SessionId!,
            RolloutPath = result.Path,
            CreatedAt = createdAt,
            LastActivityAt = lastActivity,
            Title = previous?.Title,
            TitleUpdatedAt = previous?.TitleUpdatedAt,
            Model = metadata?.Model ?? previous?.Model,
            ReasoningEffort = previous?.ReasoningEffort,
            ProjectPath = metadata?.ProjectPath ?? previous?.ProjectPath,
            ProjectName = metadata?.ProjectName ?? previous?.ProjectName,
            TotalUsage = token?.TotalUsage ?? previous?.TotalUsage ?? TokenUsage.Empty,
            LastUsage = token?.LastUsage ?? previous?.LastUsage ?? TokenUsage.Empty,
            ModelContextWindow = token?.ModelContextWindow ?? previous?.ModelContextWindow ?? 0,
            IsMuted = previous?.IsMuted ?? false,
            DataStatus = hasErrors ? "degraded" : recognized ? "healthy" : previous?.DataStatus ?? "healthy",
            ErrorCode = hasErrors
                ? result.ParseErrors > 0 ? result.LastErrorCode ?? "rollout_parse_error" : "oversized_jsonl_line"
                : recognized ? null : previous?.ErrorCode,
            LastSuccessAt = recognized ? now : previous?.LastSuccessAt ?? now
        };
    }

    private static async Task UpsertCheckpointAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        RolloutFileResult result,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO file_checkpoints(path, offset, session_id, updated_utc)
            VALUES($path, $offset, $session_id, $updated_utc)
            ON CONFLICT(path) DO UPDATE SET
                offset = excluded.offset,
                session_id = excluded.session_id,
                updated_utc = excluded.updated_utc
            """;
        command.Parameters.AddWithValue("$path", result.Path);
        command.Parameters.AddWithValue("$offset", result.NextOffset);
        AddNullable(command, "$session_id", result.SessionId);
        command.Parameters.AddWithValue("$updated_utc", Format(DateTimeOffset.UtcNow));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpsertSessionAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        SessionSnapshot session,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = $"""
            INSERT INTO sessions({SessionInsertColumns})
            VALUES({SessionInsertValues})
            ON CONFLICT(session_id) DO UPDATE SET
                rollout_path = excluded.rollout_path,
                created_utc = excluded.created_utc,
                last_activity_utc = excluded.last_activity_utc,
                title = excluded.title,
                title_updated_utc = excluded.title_updated_utc,
                model = excluded.model,
                reasoning_effort = excluded.reasoning_effort,
                project_path = excluded.project_path,
                project_name = excluded.project_name,
                total_input_tokens = excluded.total_input_tokens,
                total_cached_input_tokens = excluded.total_cached_input_tokens,
                total_cache_write_input_tokens = excluded.total_cache_write_input_tokens,
                total_output_tokens = excluded.total_output_tokens,
                total_reasoning_output_tokens = excluded.total_reasoning_output_tokens,
                total_tokens = excluded.total_tokens,
                last_input_tokens = excluded.last_input_tokens,
                last_cached_input_tokens = excluded.last_cached_input_tokens,
                last_cache_write_input_tokens = excluded.last_cache_write_input_tokens,
                last_output_tokens = excluded.last_output_tokens,
                last_reasoning_output_tokens = excluded.last_reasoning_output_tokens,
                last_total_tokens = excluded.last_total_tokens,
                model_context_window = excluded.model_context_window,
                is_muted = excluded.is_muted,
                data_status = excluded.data_status,
                error_code = excluded.error_code,
                last_success_utc = excluded.last_success_utc
            """;
        AddSessionParameters(command, session);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<SessionSnapshot?> GetSessionAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction? transaction,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction?)transaction;
        command.CommandText = $"SELECT {SessionColumns} FROM sessions WHERE session_id = $session_id";
        command.Parameters.AddWithValue("$session_id", sessionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadSession(reader) : null;
    }

    private static SessionSnapshot ReadSession(SqliteDataReader reader) => new()
    {
        SessionId = reader.GetString(0),
        RolloutPath = reader.GetString(1),
        CreatedAt = ParseNullableDate(reader, 2),
        LastActivityAt = ParseDate(reader.GetString(3)),
        Title = GetNullableString(reader, 4),
        TitleUpdatedAt = ParseNullableDate(reader, 5),
        Model = GetNullableString(reader, 6),
        ReasoningEffort = GetNullableString(reader, 7),
        ProjectPath = GetNullableString(reader, 8),
        ProjectName = GetNullableString(reader, 9),
        TotalUsage = new TokenUsage(
            reader.GetInt64(10), reader.GetInt64(11), reader.GetInt64(12),
            reader.GetInt64(13), reader.GetInt64(14), reader.GetInt64(15)),
        LastUsage = new TokenUsage(
            reader.GetInt64(16), reader.GetInt64(17), reader.GetInt64(18),
            reader.GetInt64(19), reader.GetInt64(20), reader.GetInt64(21)),
        ModelContextWindow = reader.GetInt64(22),
        IsMuted = reader.GetInt64(23) == 1,
        DataStatus = reader.GetString(24),
        ErrorCode = GetNullableString(reader, 25),
        LastSuccessAt = ParseDate(reader.GetString(26))
    };

    private static async Task<Dictionary<string, SessionAnnotation>> ReadAnnotationsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT session_id, task_type, outcome, human_minutes, rework_reason, updated_utc
            FROM session_annotations
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var annotations = new Dictionary<string, SessionAnnotation>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken))
        {
            annotations[reader.GetString(0)] = ReadAnnotation(reader, 1);
        }

        return annotations;
    }

    private static async Task<SessionAnnotation> ReadAnnotationAsync(
        SqliteConnection connection,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT task_type, outcome, human_minutes, rework_reason, updated_utc
            FROM session_annotations
            WHERE session_id = $session_id
            """;
        command.Parameters.AddWithValue("$session_id", sessionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadAnnotation(reader, 0)
            : SessionAnnotation.Unmarked;
    }

    private static SessionAnnotation ReadAnnotation(SqliteDataReader reader, int startOrdinal) => new(
        reader.GetString(startOrdinal),
        reader.GetString(startOrdinal + 1),
        reader.IsDBNull(startOrdinal + 2) ? null : reader.GetInt32(startOrdinal + 2),
        GetNullableString(reader, startOrdinal + 3),
        ParseNullableDate(reader, startOrdinal + 4));

    private static void AddSessionParameters(SqliteCommand command, SessionSnapshot session)
    {
        command.Parameters.AddWithValue("$session_id", session.SessionId);
        command.Parameters.AddWithValue("$rollout_path", session.RolloutPath);
        AddNullable(command, "$created_utc", session.CreatedAt is null ? null : Format(session.CreatedAt.Value));
        command.Parameters.AddWithValue("$last_activity_utc", Format(session.LastActivityAt));
        AddNullable(command, "$title", session.Title);
        AddNullable(command, "$title_updated_utc", session.TitleUpdatedAt is null ? null : Format(session.TitleUpdatedAt.Value));
        AddNullable(command, "$model", session.Model);
        AddNullable(command, "$reasoning_effort", session.ReasoningEffort);
        AddNullable(command, "$project_path", session.ProjectPath);
        AddNullable(command, "$project_name", session.ProjectName);
        AddUsageParameters(command, "total", session.TotalUsage);
        AddUsageParameters(command, "last", session.LastUsage);
        command.Parameters.AddWithValue("$model_context_window", session.ModelContextWindow);
        command.Parameters.AddWithValue("$is_muted", session.IsMuted ? 1 : 0);
        command.Parameters.AddWithValue("$data_status", session.DataStatus);
        AddNullable(command, "$error_code", session.ErrorCode);
        command.Parameters.AddWithValue("$last_success_utc", Format(session.LastSuccessAt));
    }

    private static void AddUsageParameters(SqliteCommand command, string prefix, TokenUsage usage)
    {
        command.Parameters.AddWithValue($"${prefix}_input_tokens", usage.InputTokens);
        command.Parameters.AddWithValue($"${prefix}_cached_input_tokens", usage.CachedInputTokens);
        command.Parameters.AddWithValue($"${prefix}_cache_write_input_tokens", usage.CacheWriteInputTokens);
        command.Parameters.AddWithValue($"${prefix}_output_tokens", usage.OutputTokens);
        command.Parameters.AddWithValue($"${prefix}_reasoning_output_tokens", usage.ReasoningOutputTokens);
        command.Parameters.AddWithValue($"${prefix}_tokens", usage.TotalTokens);
    }

    private static void AddNullable(SqliteCommand command, string parameterName, object? value) =>
        command.Parameters.AddWithValue(parameterName, value ?? DBNull.Value);

    private static string? GetNullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static DateTimeOffset ParseDate(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static DateTimeOffset? ParseNullableDate(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : ParseDate(reader.GetString(ordinal));

    private static TokenUsage ReadUsage(SqliteDataReader reader, int startOrdinal) => new(
        reader.GetInt64(startOrdinal),
        reader.GetInt64(startOrdinal + 1),
        reader.GetInt64(startOrdinal + 2),
        reader.GetInt64(startOrdinal + 3),
        reader.GetInt64(startOrdinal + 4),
        reader.GetInt64(startOrdinal + 5));

    private static async Task<DailyUsageSummary> ReadDailyUsageAsync(
        SqliteConnection connection,
        DateOnly date,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                COALESCE(SUM(last_input_tokens), 0),
                COALESCE(SUM(last_cached_input_tokens), 0),
                COALESCE(SUM(last_cache_write_input_tokens), 0),
                COALESCE(SUM(last_output_tokens), 0),
                COALESCE(SUM(last_reasoning_output_tokens), 0),
                COALESCE(SUM(last_total_tokens), 0),
                COUNT(DISTINCT CASE WHEN last_total_tokens > 0 THEN session_id END)
            FROM token_usage_events
            WHERE date(event_utc, 'localtime') = $date
            """;
        command.Parameters.AddWithValue("$date", FormatDate(date));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return new DailyUsageSummary(date, ReadUsage(reader, 0), reader.GetInt32(6));
    }

    private static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static string FormatDate(DateOnly value) =>
        value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static long Median(IEnumerable<long> values)
    {
        var ordered = values.Order().ToArray();
        if (ordered.Length == 0)
        {
            return 0;
        }

        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 1
            ? ordered[middle]
            : (ordered[middle - 1] / 2) + (ordered[middle] / 2);
    }

    private static TokenUsage SumUsage(IEnumerable<TokenUsage> values) => values.Aggregate(
        TokenUsage.Empty,
        (sum, value) => new TokenUsage(
            sum.InputTokens + value.InputTokens,
            sum.CachedInputTokens + value.CachedInputTokens,
            sum.CacheWriteInputTokens + value.CacheWriteInputTokens,
            sum.OutputTokens + value.OutputTokens,
            sum.ReasoningOutputTokens + value.ReasoningOutputTokens,
            sum.TotalTokens + value.TotalTokens));

    private static async Task<UsageCostEstimate> BuildUsageCostEstimateAsync(
        SqliteConnection connection,
        DateOnly today,
        DateTimeOffset calculatedAt,
        CancellationToken cancellationToken)
    {
        var daysSinceMonday = ((int)today.DayOfWeek + 6) % 7;
        var weekStart = today.AddDays(-daysSinceMonday);
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var dailyUsage = await ReadModelUsageAsync(connection, today, today, cancellationToken);
        var weeklyUsage = await ReadModelUsageAsync(connection, weekStart, today, cancellationToken);
        var monthlyUsage = await ReadModelUsageAsync(connection, monthStart, today, cancellationToken);

        return new UsageCostEstimate(
            UsageCostEstimator.EstimatePeriod(today, today, dailyUsage),
            UsageCostEstimator.EstimatePeriod(weekStart, today, weeklyUsage),
            UsageCostEstimator.EstimatePeriod(monthStart, today, monthlyUsage),
            UsageCostEstimator.Rates,
            "USD",
            UsageCostEstimator.PricingAsOf,
            calculatedAt);
    }

    private static async Task<IReadOnlyList<(string Model, TokenUsage Usage)>> ReadModelUsageAsync(
        SqliteConnection connection,
        DateOnly startDate,
        DateOnly endDate,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                COALESCE(NULLIF(TRIM(sessions.model), ''), 'unknown'),
                SUM(events.last_input_tokens),
                SUM(events.last_cached_input_tokens),
                SUM(events.last_cache_write_input_tokens),
                SUM(events.last_output_tokens),
                SUM(events.last_reasoning_output_tokens),
                SUM(events.last_total_tokens)
            FROM token_usage_events AS events
            LEFT JOIN sessions ON sessions.session_id = events.session_id
            WHERE date(events.event_utc, 'localtime') >= $start_date
              AND date(events.event_utc, 'localtime') <= $end_date
            GROUP BY COALESCE(NULLIF(TRIM(sessions.model), ''), 'unknown')
            HAVING SUM(events.last_total_tokens) > 0
            """;
        command.Parameters.AddWithValue("$start_date", FormatDate(startDate));
        command.Parameters.AddWithValue("$end_date", FormatDate(endDate));
        var result = new List<(string Model, TokenUsage Usage)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add((reader.GetString(0), ReadUsage(reader, 1)));
        }

        return result;
    }

    private static async Task InsertTokenEventsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RolloutFileResult result,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ParsedTokenEvent> events = result.TokenEvents ??
            (result.LatestTokenEvent is null ? [] : [result.LatestTokenEvent]);
        foreach (var tokenEvent in events)
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT OR IGNORE INTO token_usage_events(
                    session_id, rollout_path, event_utc,
                    input_tokens, cached_input_tokens, cache_write_input_tokens,
                    output_tokens, reasoning_output_tokens, total_tokens,
                    last_input_tokens, last_cached_input_tokens, last_cache_write_input_tokens,
                    last_output_tokens, last_reasoning_output_tokens, last_total_tokens)
                VALUES(
                    $session_id, $rollout_path, $event_utc,
                    $event_input_tokens, $event_cached_input_tokens, $event_cache_write_input_tokens,
                    $event_output_tokens, $event_reasoning_output_tokens, $event_tokens,
                    $last_input_tokens, $last_cached_input_tokens, $last_cache_write_input_tokens,
                    $last_output_tokens, $last_reasoning_output_tokens, $last_tokens)
                """;
            command.Parameters.AddWithValue("$session_id", result.SessionId!);
            command.Parameters.AddWithValue("$rollout_path", result.Path);
            command.Parameters.AddWithValue("$event_utc", Format(tokenEvent.Timestamp));
            AddUsageParameters(command, "event", tokenEvent.TotalUsage);
            AddUsageParameters(command, "last", tokenEvent.LastUsage);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task EnsureTokenUsageEventsSchemaAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var inspect = connection.CreateCommand();
        inspect.CommandText = "PRAGMA table_info(token_usage_events)";
        await using (var reader = await inspect.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                columns.Add(reader.GetString(1));
            }
        }

        if (columns.Count > 0 && !columns.Contains("last_total_tokens"))
        {
            var rebuild = connection.CreateCommand();
            rebuild.CommandText = "DROP TABLE token_usage_events";
            await rebuild.ExecuteNonQueryAsync(cancellationToken);
        }

        var create = connection.CreateCommand();
        create.CommandText = TokenUsageEventsSchemaSql;
        await create.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureSessionProjectSchemaAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var inspect = connection.CreateCommand();
        inspect.CommandText = "PRAGMA table_info(sessions)";
        await using (var reader = await inspect.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                columns.Add(reader.GetString(1));
            }
        }

        var addedProjectColumns = false;
        if (!columns.Contains("project_path"))
        {
            var addPath = connection.CreateCommand();
            addPath.CommandText = "ALTER TABLE sessions ADD COLUMN project_path TEXT NULL";
            await addPath.ExecuteNonQueryAsync(cancellationToken);
            addedProjectColumns = true;
        }

        if (!columns.Contains("project_name"))
        {
            var addName = connection.CreateCommand();
            addName.CommandText = "ALTER TABLE sessions ADD COLUMN project_name TEXT NULL";
            await addName.ExecuteNonQueryAsync(cancellationToken);
            addedProjectColumns = true;
        }

        if (addedProjectColumns)
        {
            var rescan = connection.CreateCommand();
            rescan.CommandText = "DELETE FROM file_checkpoints WHERE session_id IS NOT NULL";
            await rescan.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private sealed record ProjectSessionRow(
        string? ProjectPath,
        string? ProjectName,
        string Model,
        TokenUsage Usage,
        DateTimeOffset LastActivityAt);

    private const string SessionColumns = """
        session_id, rollout_path, created_utc, last_activity_utc, title, title_updated_utc,
        model, reasoning_effort, project_path, project_name,
        total_input_tokens, total_cached_input_tokens, total_cache_write_input_tokens,
        total_output_tokens, total_reasoning_output_tokens, total_tokens,
        last_input_tokens, last_cached_input_tokens, last_cache_write_input_tokens,
        last_output_tokens, last_reasoning_output_tokens, last_total_tokens,
        model_context_window, is_muted, data_status, error_code, last_success_utc
        """;

    private const string SessionInsertColumns = SessionColumns;

    private const string SessionInsertValues = """
        $session_id, $rollout_path, $created_utc, $last_activity_utc, $title, $title_updated_utc,
        $model, $reasoning_effort, $project_path, $project_name,
        $total_input_tokens, $total_cached_input_tokens, $total_cache_write_input_tokens,
        $total_output_tokens, $total_reasoning_output_tokens, $total_tokens,
        $last_input_tokens, $last_cached_input_tokens, $last_cache_write_input_tokens,
        $last_output_tokens, $last_reasoning_output_tokens, $last_tokens,
        $model_context_window, $is_muted, $data_status, $error_code, $last_success_utc
        """;

    private const string SchemaSql = """
        PRAGMA journal_mode = WAL;
        PRAGMA foreign_keys = ON;

        CREATE TABLE IF NOT EXISTS sessions(
            session_id TEXT PRIMARY KEY,
            rollout_path TEXT NOT NULL,
            created_utc TEXT NULL,
            last_activity_utc TEXT NOT NULL,
            title TEXT NULL,
            title_updated_utc TEXT NULL,
            model TEXT NULL,
            reasoning_effort TEXT NULL,
            project_path TEXT NULL,
            project_name TEXT NULL,
            total_input_tokens INTEGER NOT NULL,
            total_cached_input_tokens INTEGER NOT NULL,
            total_cache_write_input_tokens INTEGER NOT NULL,
            total_output_tokens INTEGER NOT NULL,
            total_reasoning_output_tokens INTEGER NOT NULL,
            total_tokens INTEGER NOT NULL,
            last_input_tokens INTEGER NOT NULL,
            last_cached_input_tokens INTEGER NOT NULL,
            last_cache_write_input_tokens INTEGER NOT NULL,
            last_output_tokens INTEGER NOT NULL,
            last_reasoning_output_tokens INTEGER NOT NULL,
            last_total_tokens INTEGER NOT NULL,
            model_context_window INTEGER NOT NULL,
            is_muted INTEGER NOT NULL DEFAULT 0,
            data_status TEXT NOT NULL,
            error_code TEXT NULL,
            last_success_utc TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_sessions_last_activity
            ON sessions(last_activity_utc DESC);

        CREATE TABLE IF NOT EXISTS session_annotations(
            session_id TEXT PRIMARY KEY,
            task_type TEXT NOT NULL CHECK(task_type IN (
                'unclassified', 'development', 'debugging', 'research',
                'documentation', 'planning', 'other')),
            outcome TEXT NOT NULL CHECK(outcome IN (
                'unmarked', 'completed', 'partial', 'rework', 'failed', 'abandoned')),
            human_minutes INTEGER NULL CHECK(human_minutes IS NULL OR human_minutes BETWEEN 0 AND 10080),
            rework_reason TEXT NULL CHECK(rework_reason IS NULL OR rework_reason IN (
                'requirement_change', 'model_misunderstanding', 'test_defect',
                'environment_issue', 'other')),
            updated_utc TEXT NOT NULL,
            FOREIGN KEY(session_id) REFERENCES sessions(session_id) ON DELETE CASCADE
        );

        CREATE TABLE IF NOT EXISTS file_checkpoints(
            path TEXT PRIMARY KEY,
            offset INTEGER NOT NULL,
            session_id TEXT NULL,
            updated_utc TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS settings(
            key TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );

        """;

    private const string TokenUsageEventsSchemaSql = """
        CREATE TABLE IF NOT EXISTS token_usage_events(
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            session_id TEXT NOT NULL,
            rollout_path TEXT NOT NULL,
            event_utc TEXT NOT NULL,
            input_tokens INTEGER NOT NULL,
            cached_input_tokens INTEGER NOT NULL,
            cache_write_input_tokens INTEGER NOT NULL,
            output_tokens INTEGER NOT NULL,
            reasoning_output_tokens INTEGER NOT NULL,
            total_tokens INTEGER NOT NULL,
            last_input_tokens INTEGER NOT NULL,
            last_cached_input_tokens INTEGER NOT NULL,
            last_cache_write_input_tokens INTEGER NOT NULL,
            last_output_tokens INTEGER NOT NULL,
            last_reasoning_output_tokens INTEGER NOT NULL,
            last_total_tokens INTEGER NOT NULL,
            UNIQUE(
                session_id, event_utc, input_tokens, cached_input_tokens,
                cache_write_input_tokens, output_tokens, reasoning_output_tokens, total_tokens,
                last_input_tokens, last_cached_input_tokens, last_cache_write_input_tokens,
                last_output_tokens, last_reasoning_output_tokens, last_total_tokens)
        );

        CREATE INDEX IF NOT EXISTS ix_token_usage_events_session_time
            ON token_usage_events(session_id, event_utc, id);
        """;

    private const string DailyUsageSql = """
        SELECT
            date(event_utc, 'localtime') AS local_date,
            SUM(last_input_tokens),
            SUM(last_cached_input_tokens),
            SUM(last_cache_write_input_tokens),
            SUM(last_output_tokens),
            SUM(last_reasoning_output_tokens),
            SUM(last_total_tokens),
            COUNT(DISTINCT CASE WHEN last_total_tokens > 0 THEN session_id END)
        FROM token_usage_events
        WHERE local_date IS NOT NULL
        GROUP BY local_date
        ORDER BY local_date
        """;
}
