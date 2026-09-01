using System.Globalization;
using System.Text.Json;
using CodexTokenMonitor.Core.Models;
using CodexTokenMonitor.Core.Pricing;
using Microsoft.Data.Sqlite;

namespace CodexTokenMonitor.Core.Storage;

public sealed partial class MonitorDatabase(string databasePath)
{
    private const string ProjectIdentityVersion = "2";
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
            var pricingCommand = connection.CreateCommand();
            pricingCommand.CommandText = PricingSchemaSql;
            await pricingCommand.ExecuteNonQueryAsync(cancellationToken);
            await EnsurePriceChangeCandidatesSchemaAsync(connection, cancellationToken);
            var skillCommand = connection.CreateCommand();
            skillCommand.CommandText = SkillUsageSchemaSql;
            await skillCommand.ExecuteNonQueryAsync(cancellationToken);
            await EnsureSkillIncrementalSchemaAsync(connection, cancellationToken);
            await EnsurePerformanceSchemaAsync(connection, cancellationToken);
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
            var insertedEvents = await InsertTokenEventsAsync(connection, transaction, result, cancellationToken);
            await UpsertCheckpointAsync(connection, transaction, result, cancellationToken);
            var changedSections = insertedEvents > 0
                ? new[] { "overview", "sessions", "models", "usage", "rankings" }
                : new[] { "overview", "sessions", "models" };
            await BumpDataVersionsAsync(connection, transaction, changedSections, cancellationToken);
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
            "DELETE FROM session_annotations; DELETE FROM token_usage_events; DELETE FROM sessions; DELETE FROM skill_invocations; DELETE FROM skill_attribution_events; DELETE FROM skill_scan_checkpoints;",
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

    public async Task<IReadOnlyList<string>> GetObservedModelsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT LOWER(TRIM(model))
            FROM sessions
            WHERE TRIM(COALESCE(model, '')) <> ''
              AND LOWER(TRIM(model)) <> 'unknown'
            ORDER BY LOWER(TRIM(model))
            """;
        var models = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            models.Add(reader.GetString(0));
        }

        return models;
    }

    public async Task<IReadOnlyList<ModelPriceVersion>> GetPriceVersionsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await ReadPriceVersionsAsync(connection, cancellationToken);
    }

    public async Task<IReadOnlyDictionary<string, ModelUsageCost>> GetSessionCostsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await ReadSessionCostsAsync(connection, cancellationToken);
    }

    public async Task<IReadOnlyList<PriceSourceStatus>> GetPriceSourceStatusesAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT source_id, source_kind, display_name, source_url, enabled, gate_status,
                   last_attempt_utc, last_success_utc, next_check_utc, last_error,
                   last_content_hash, last_source_revision, consecutive_failures
            FROM price_source_status
            ORDER BY CASE source_kind
                WHEN 'official' THEN 0
                WHEN 'third_party_reference' THEN 1
                ELSE 2 END, display_name
            """;
        var statuses = new List<PriceSourceStatus>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            statuses.Add(new PriceSourceStatus(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt32(4) == 1,
                reader.GetString(5),
                ParseNullableDate(reader, 6),
                ParseNullableDate(reader, 7),
                ParseNullableDate(reader, 8),
                GetNullableString(reader, 9),
                GetNullableString(reader, 10),
                GetNullableString(reader, 11),
                reader.GetInt32(12)));
        }

        return statuses;
    }

    public async Task<IReadOnlyList<QuarantinedPriceChange>> GetQuarantinedPriceChangesAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT source_id, model, effective_utc, input_usd_per_million,
                   cached_input_usd_per_million, output_usd_per_million,
                   maximum_change_ratio, matching_observations, first_observed_utc,
                   last_observed_utc, content_hash
            FROM price_change_candidates
            ORDER BY last_observed_utc DESC
            """;
        var changes = new List<QuarantinedPriceChange>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            changes.Add(new QuarantinedPriceChange(
                reader.GetString(0),
                reader.GetString(1),
                ParseDate(reader.GetString(2)),
                ReadNullableDecimal(reader, 3),
                ReadNullableDecimal(reader, 4),
                ReadNullableDecimal(reader, 5),
                decimal.Parse(reader.GetString(6), CultureInfo.InvariantCulture),
                reader.GetInt32(7),
                ParseDate(reader.GetString(8)),
                ParseDate(reader.GetString(9)),
                reader.GetString(10)));
        }

        return changes;
    }

    public async Task SavePriceSourceStatusAsync(
        PriceSourceStatus status,
        CancellationToken cancellationToken = default)
    {
        await ExecuteWriteAsync(
            """
            INSERT INTO price_source_status(
                source_id, source_kind, display_name, source_url, enabled, gate_status,
                last_attempt_utc, last_success_utc, next_check_utc, last_error,
                last_content_hash, last_source_revision, consecutive_failures)
            VALUES(
                $source_id, $source_kind, $display_name, $source_url, $enabled, $gate_status,
                $last_attempt_utc, $last_success_utc, $next_check_utc, $last_error,
                $last_content_hash, $last_source_revision, $consecutive_failures)
            ON CONFLICT(source_id) DO UPDATE SET
                source_kind = excluded.source_kind,
                display_name = excluded.display_name,
                source_url = excluded.source_url,
                enabled = excluded.enabled,
                gate_status = excluded.gate_status,
                last_attempt_utc = excluded.last_attempt_utc,
                last_success_utc = excluded.last_success_utc,
                next_check_utc = excluded.next_check_utc,
                last_error = excluded.last_error,
                last_content_hash = excluded.last_content_hash,
                last_source_revision = excluded.last_source_revision,
                consecutive_failures = excluded.consecutive_failures
            """,
            command =>
            {
                command.Parameters.AddWithValue("$source_id", status.SourceId);
                command.Parameters.AddWithValue("$source_kind", status.SourceKind);
                command.Parameters.AddWithValue("$display_name", status.DisplayName);
                command.Parameters.AddWithValue("$source_url", status.SourceUrl);
                command.Parameters.AddWithValue("$enabled", status.Enabled ? 1 : 0);
                command.Parameters.AddWithValue("$gate_status", status.GateStatus);
                AddNullable(command, "$last_attempt_utc", status.LastAttemptAt is null ? null : Format(status.LastAttemptAt.Value));
                AddNullable(command, "$last_success_utc", status.LastSuccessAt is null ? null : Format(status.LastSuccessAt.Value));
                AddNullable(command, "$next_check_utc", status.NextCheckAt is null ? null : Format(status.NextCheckAt.Value));
                AddNullable(command, "$last_error", status.LastError);
                AddNullable(command, "$last_content_hash", status.LastContentHash);
                AddNullable(command, "$last_source_revision", status.LastSourceRevision);
                command.Parameters.AddWithValue("$consecutive_failures", status.ConsecutiveFailures);
            },
            cancellationToken);
    }

    public async Task<PriceActivationResult> SaveFetchedPricesAsync(
        PriceFetchResult fetch,
        CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            var activated = 0;
            var quarantined = 0;
            var unchanged = 0;
            foreach (var price in fetch.Prices
                .OrderBy(price => price.Model, StringComparer.OrdinalIgnoreCase)
                .ThenBy(price => price.EffectiveFrom))
            {
                var latest = await ReadLatestSourcePriceAsync(
                    connection,
                    transaction,
                    price.SourceId,
                    price.Model,
                    cancellationToken);
                if (latest is not null &&
                    SameRates(latest, price) &&
                    price.EffectiveFrom >= latest.EffectiveFrom)
                {
                    unchanged++;
                    continue;
                }

                var maximumChange = latest is null ? 0m : MaximumChangeRatio(latest, price);
                var priceToActivate = price;
                if (latest is not null && maximumChange > 0.5m)
                {
                    var pendingEffectiveFrom = await FindMatchingQuarantinedEffectiveFromAsync(
                        connection,
                        transaction,
                        price,
                        cancellationToken);
                    var priceToConfirm = pendingEffectiveFrom is null
                        ? price
                        : price with { EffectiveFrom = pendingEffectiveFrom.Value };
                    var confirmation = await UpsertQuarantinedPriceAsync(
                        connection,
                        transaction,
                        priceToConfirm,
                        maximumChange,
                        cancellationToken);
                    if (confirmation.MatchingObservations < 2)
                    {
                        quarantined++;
                        continue;
                    }

                    priceToActivate = price with { EffectiveFrom = confirmation.EffectiveFrom };
                }

                await InsertPriceVersionAsync(connection, transaction, priceToActivate, cancellationToken);
                await DeleteQuarantinedPriceAsync(
                    connection,
                    transaction,
                    priceToActivate.SourceId,
                    priceToActivate.Model,
                    priceToActivate.EffectiveFrom,
                    cancellationToken);
                activated++;
            }

            await transaction.CommitAsync(cancellationToken);
            return new PriceActivationResult(activated, quarantined, unchanged);
        }
        finally
        {
            _writeGate.Release();
        }
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
                sessions.session_id,
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
                    reader.GetString(0),
                    GetNullableString(reader, 1),
                    GetNullableString(reader, 2),
                    reader.GetString(3),
                    ReadUsage(reader, 4),
                    ParseDate(reader.GetString(10))));
            }
        }

        var sessionCosts = await ReadSessionCostsAsync(connection, cancellationToken);
        var projects = projectRows
            .GroupBy(
                row => row.ProjectPath ?? "",
                StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var usage = SumUsage(group.Select(row => row.Usage));
                var modelCosts = group
                    .Select(row => sessionCosts.GetValueOrDefault(row.SessionId)
                        ?? new ModelUsageCost(row.Model, row.Usage, 0, 0, 0m, false))
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

    public async Task<DailyProjectUsageBreakdown> GetDailyProjectUsageAsync(
        DateOnly date,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                events.session_id,
                CASE
                    WHEN TRIM(COALESCE(sessions.title, '')) <> '' THEN sessions.title
                    ELSE SUBSTR(events.session_id, 1, 8)
                END AS display_name,
                COALESCE(NULLIF(TRIM(sessions.model), ''), 'unknown'),
                COALESCE(NULLIF(TRIM(sessions.reasoning_effort), ''), 'unknown'),
                sessions.project_path,
                sessions.project_name,
                COALESCE(NULLIF(TRIM(sessions.project_status), ''), 'unidentified'),
                SUM(events.last_input_tokens),
                SUM(events.last_cached_input_tokens),
                SUM(events.last_cache_write_input_tokens),
                SUM(events.last_output_tokens),
                SUM(events.last_reasoning_output_tokens),
                SUM(events.last_total_tokens),
                MAX(events.event_utc)
            FROM token_usage_events AS events
            LEFT JOIN sessions ON sessions.session_id = events.session_id
            WHERE date(events.event_utc, 'localtime') = $selected_date
            GROUP BY events.session_id
            HAVING SUM(events.last_total_tokens) > 0
            ORDER BY SUM(events.last_total_tokens) DESC, MAX(events.event_utc) DESC
            """;
        command.Parameters.AddWithValue("$selected_date", FormatDate(date));
        var rows = new List<DailyProjectSessionRow>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(new DailyProjectSessionRow(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    GetNullableString(reader, 4),
                    GetNullableString(reader, 5),
                    reader.GetString(6),
                    ReadUsage(reader, 7),
                    ParseDate(reader.GetString(13))));
            }
        }

        var earliestCommand = connection.CreateCommand();
        earliestCommand.CommandText = "SELECT MIN(date(event_utc, 'localtime')) FROM token_usage_events WHERE last_total_tokens > 0";
        var earliestValue = await earliestCommand.ExecuteScalarAsync(cancellationToken);
        var earliestDate = earliestValue is string earliestText
            ? DateOnly.ParseExact(earliestText, "yyyy-MM-dd", CultureInfo.InvariantCulture)
            : (DateOnly?)null;

        var projects = rows
            .GroupBy(row => row.ProjectPath ?? "", StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var sessions = group
                    .OrderByDescending(row => row.Usage.TotalTokens)
                    .ThenByDescending(row => row.LastEventAt)
                    .Select(row => new DailyProjectSessionUsage(
                        row.SessionId,
                        row.DisplayName,
                        row.Model,
                        row.ReasoningEffort,
                        row.Usage,
                        row.LastEventAt))
                    .ToArray();
                var status = group
                    .Select(row => row.ProjectStatus)
                    .OrderBy(ProjectStatusOrder)
                    .FirstOrDefault() ?? "unidentified";
                var path = group.Select(row => row.ProjectPath).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
                var displayName = group.Select(row => row.ProjectName).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
                    ?? (status == "pending" ? "待归并项目" : "未识别项目");
                return new DailyProjectUsage(
                    displayName,
                    path,
                    status,
                    SumUsage(sessions.Select(session => session.Usage)),
                    sessions.Max(session => session.LastEventAt),
                    sessions);
            })
            .OrderByDescending(project => project.Usage.TotalTokens)
            .ThenByDescending(project => project.LastEventAt)
            .ToArray();
        var usage = SumUsage(projects.Select(project => project.Usage));
        var today = DateOnly.FromDateTime(DateTimeOffset.Now.DateTime);

        return new DailyProjectUsageBreakdown(
            date,
            earliestDate,
            today,
            usage,
            rows.Count,
            projects,
            TimeZoneInfo.Local.Id);
    }

    private static int ProjectStatusOrder(string status) => status switch
    {
        "git" => 0,
        "directory" => 1,
        "pending" => 2,
        _ => 3
    };

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
            ProjectStatus = metadata?.ProjectStatus ?? previous?.ProjectStatus ?? "unidentified",
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
                project_status = excluded.project_status,
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
        ProjectStatus = reader.GetString(10),
        TotalUsage = new TokenUsage(
            reader.GetInt64(11), reader.GetInt64(12), reader.GetInt64(13),
            reader.GetInt64(14), reader.GetInt64(15), reader.GetInt64(16)),
        LastUsage = new TokenUsage(
            reader.GetInt64(17), reader.GetInt64(18), reader.GetInt64(19),
            reader.GetInt64(20), reader.GetInt64(21), reader.GetInt64(22)),
        ModelContextWindow = reader.GetInt64(23),
        IsMuted = reader.GetInt64(24) == 1,
        DataStatus = reader.GetString(25),
        ErrorCode = GetNullableString(reader, 26),
        LastSuccessAt = ParseDate(reader.GetString(27))
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
        command.Parameters.AddWithValue("$project_status", session.ProjectStatus);
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

    private static async Task<IReadOnlyList<ModelPriceVersion>> ReadPriceVersionsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, source_id, model, input_usd_per_million, cached_input_usd_per_million,
                   output_usd_per_million, source_kind, source_name, source_url,
                   effective_utc, retrieved_utc, content_hash, source_revision,
                   parser_version, is_backfill, created_utc
            FROM model_price_versions
            ORDER BY effective_utc, id
            """;
        var versions = new List<ModelPriceVersion>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            versions.Add(ReadPriceVersion(reader));
        }

        return versions;
    }

    private static async Task<IReadOnlyDictionary<string, ModelUsageCost>> ReadSessionCostsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT events.session_id,
                   COALESCE(NULLIF(TRIM(sessions.model), ''), 'unknown'),
                   events.event_utc,
                   events.last_input_tokens,
                   events.last_cached_input_tokens,
                   events.last_cache_write_input_tokens,
                   events.last_output_tokens,
                   events.last_reasoning_output_tokens,
                   events.last_total_tokens
            FROM token_usage_events AS events
            LEFT JOIN sessions ON sessions.session_id = events.session_id
            WHERE typeof(events.last_input_tokens) = 'integer'
              AND typeof(events.last_cached_input_tokens) = 'integer'
              AND typeof(events.last_cache_write_input_tokens) = 'integer'
              AND typeof(events.last_output_tokens) = 'integer'
              AND typeof(events.last_reasoning_output_tokens) = 'integer'
              AND typeof(events.last_total_tokens) = 'integer'
              AND events.last_total_tokens > 0
            ORDER BY events.session_id, events.event_utc, events.id
            """;
        var rows = new Dictionary<string, List<TimedModelUsage>>(StringComparer.Ordinal);
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var sessionId = reader.GetString(0);
                if (!rows.TryGetValue(sessionId, out var events))
                {
                    events = [];
                    rows.Add(sessionId, events);
                }

                events.Add(new TimedModelUsage(
                    reader.GetString(1),
                    ParseDate(reader.GetString(2)),
                    ReadUsage(reader, 3)));
            }
        }

        var catalog = new PriceCatalog(await ReadPriceVersionsAsync(connection, cancellationToken));
        return rows.ToDictionary(
            pair => pair.Key,
            pair =>
            {
                var first = pair.Value.Min(item => item.EventAt);
                var last = pair.Value.Max(item => item.EventAt);
                var period = UsageCostEstimator.EstimateTimedPeriod(
                    DateOnly.FromDateTime(first.LocalDateTime),
                    DateOnly.FromDateTime(last.LocalDateTime),
                    pair.Value,
                    catalog);
                return period.Models.Count == 1
                    ? period.Models[0]
                    : new ModelUsageCost(
                        "mixed",
                        period.Usage,
                        period.Models.Sum(model => model.UncachedInputTokens),
                        period.PricedTokens,
                        period.EstimatedCostUsd,
                        period.PricedTokens > 0,
                        period.Coverage,
                        period.Models.Any(model => model.HasBackfilledPrice));
            },
            StringComparer.Ordinal);
    }

    private static async Task<ModelPriceVersion?> ReadLatestSourcePriceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sourceId,
        string model,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, source_id, model, input_usd_per_million, cached_input_usd_per_million,
                   output_usd_per_million, source_kind, source_name, source_url,
                   effective_utc, retrieved_utc, content_hash, source_revision,
                   parser_version, is_backfill, created_utc
            FROM model_price_versions
            WHERE source_id = $source_id AND model = $model COLLATE NOCASE
            ORDER BY effective_utc DESC, retrieved_utc DESC, id DESC
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$source_id", sourceId);
        command.Parameters.AddWithValue("$model", model);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadPriceVersion(reader) : null;
    }

    private static ModelPriceVersion ReadPriceVersion(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetString(1),
        reader.GetString(2),
        ReadNullableDecimal(reader, 3),
        ReadNullableDecimal(reader, 4),
        ReadNullableDecimal(reader, 5),
        reader.GetString(6),
        reader.GetString(7),
        reader.GetString(8),
        ParseDate(reader.GetString(9)),
        ParseDate(reader.GetString(10)),
        GetNullableString(reader, 11),
        GetNullableString(reader, 12),
        reader.GetString(13),
        reader.GetInt32(14) == 1,
        ParseDate(reader.GetString(15)));

    private static async Task InsertPriceVersionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PriceCandidate price,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO model_price_versions(
                source_id, model, input_usd_per_million, cached_input_usd_per_million,
                output_usd_per_million, source_kind, source_name, source_url,
                effective_utc, retrieved_utc, content_hash, source_revision,
                parser_version, is_backfill, created_utc)
            VALUES(
                $source_id, $model, $input, $cached_input, $output, $source_kind,
                $source_name, $source_url, $effective_utc, $retrieved_utc,
                $content_hash, $source_revision, $parser_version, $is_backfill, $created_utc)
            """;
        command.Parameters.AddWithValue("$source_id", price.SourceId);
        command.Parameters.AddWithValue("$model", price.Model.Trim().ToLowerInvariant());
        AddNullable(command, "$input", FormatDecimal(price.InputUsdPerMillion));
        AddNullable(command, "$cached_input", FormatDecimal(price.CachedInputUsdPerMillion));
        AddNullable(command, "$output", FormatDecimal(price.OutputUsdPerMillion));
        command.Parameters.AddWithValue("$source_kind", price.SourceKind);
        command.Parameters.AddWithValue("$source_name", price.SourceName);
        command.Parameters.AddWithValue("$source_url", price.SourceUrl);
        command.Parameters.AddWithValue("$effective_utc", Format(price.EffectiveFrom));
        command.Parameters.AddWithValue("$retrieved_utc", Format(price.RetrievedAt));
        command.Parameters.AddWithValue("$content_hash", price.ContentHash);
        AddNullable(command, "$source_revision", price.SourceRevision);
        command.Parameters.AddWithValue("$parser_version", price.ParserVersion);
        command.Parameters.AddWithValue("$is_backfill", price.EffectiveFrom < price.RetrievedAt ? 1 : 0);
        command.Parameters.AddWithValue("$created_utc", Format(DateTimeOffset.UtcNow));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<(int MatchingObservations, DateTimeOffset EffectiveFrom)> UpsertQuarantinedPriceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PriceCandidate price,
        decimal maximumChange,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO price_change_candidates(
                source_id, model, effective_utc, input_usd_per_million,
                cached_input_usd_per_million, output_usd_per_million,
                maximum_change_ratio, matching_observations, first_observed_utc,
                last_observed_utc, content_hash)
            VALUES(
                $source_id, $model, $effective_utc, $input, $cached_input, $output,
                $maximum_change_ratio, 1, $observed_utc, $observed_utc, $content_hash)
            ON CONFLICT(source_id, model, effective_utc) DO UPDATE SET
                input_usd_per_million = excluded.input_usd_per_million,
                cached_input_usd_per_million = excluded.cached_input_usd_per_million,
                output_usd_per_million = excluded.output_usd_per_million,
                maximum_change_ratio = excluded.maximum_change_ratio,
                matching_observations = CASE
                    WHEN COALESCE(price_change_candidates.input_usd_per_million, '') = COALESCE(excluded.input_usd_per_million, '')
                     AND COALESCE(price_change_candidates.cached_input_usd_per_million, '') = COALESCE(excluded.cached_input_usd_per_million, '')
                     AND COALESCE(price_change_candidates.output_usd_per_million, '') = COALESCE(excluded.output_usd_per_million, '')
                    THEN price_change_candidates.matching_observations + 1
                    ELSE 1 END,
                first_observed_utc = CASE
                    WHEN COALESCE(price_change_candidates.input_usd_per_million, '') = COALESCE(excluded.input_usd_per_million, '')
                     AND COALESCE(price_change_candidates.cached_input_usd_per_million, '') = COALESCE(excluded.cached_input_usd_per_million, '')
                     AND COALESCE(price_change_candidates.output_usd_per_million, '') = COALESCE(excluded.output_usd_per_million, '')
                    THEN price_change_candidates.first_observed_utc
                    ELSE excluded.first_observed_utc END,
                last_observed_utc = excluded.last_observed_utc,
                content_hash = excluded.content_hash
            """;
        command.Parameters.AddWithValue("$source_id", price.SourceId);
        command.Parameters.AddWithValue("$model", price.Model.Trim().ToLowerInvariant());
        command.Parameters.AddWithValue("$effective_utc", Format(price.EffectiveFrom));
        AddNullable(command, "$input", FormatDecimal(price.InputUsdPerMillion));
        AddNullable(command, "$cached_input", FormatDecimal(price.CachedInputUsdPerMillion));
        AddNullable(command, "$output", FormatDecimal(price.OutputUsdPerMillion));
        command.Parameters.AddWithValue("$maximum_change_ratio", maximumChange.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$observed_utc", Format(price.RetrievedAt));
        command.Parameters.AddWithValue("$content_hash", price.ContentHash);
        await command.ExecuteNonQueryAsync(cancellationToken);

        var read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = "SELECT matching_observations, effective_utc FROM price_change_candidates WHERE source_id = $source_id AND model = $model AND effective_utc = $effective_utc";
        read.Parameters.AddWithValue("$source_id", price.SourceId);
        read.Parameters.AddWithValue("$model", price.Model.Trim().ToLowerInvariant());
        read.Parameters.AddWithValue("$effective_utc", Format(price.EffectiveFrom));
        await using var reader = await read.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return (reader.GetInt32(0), ParseDate(reader.GetString(1)));
    }

    private static async Task DeleteQuarantinedPriceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sourceId,
        string model,
        DateTimeOffset effectiveFrom,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM price_change_candidates WHERE source_id = $source_id AND model = $model AND effective_utc = $effective_utc";
        command.Parameters.AddWithValue("$source_id", sourceId);
        command.Parameters.AddWithValue("$model", model.Trim().ToLowerInvariant());
        command.Parameters.AddWithValue("$effective_utc", Format(effectiveFrom));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<DateTimeOffset?> FindMatchingQuarantinedEffectiveFromAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PriceCandidate price,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT effective_utc
            FROM price_change_candidates
            WHERE source_id = $source_id
              AND model = $model
              AND COALESCE(input_usd_per_million, '') = COALESCE($input, '')
              AND COALESCE(cached_input_usd_per_million, '') = COALESCE($cached_input, '')
              AND COALESCE(output_usd_per_million, '') = COALESCE($output, '')
            ORDER BY first_observed_utc
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$source_id", price.SourceId);
        command.Parameters.AddWithValue("$model", price.Model.Trim().ToLowerInvariant());
        AddNullable(command, "$input", FormatDecimal(price.InputUsdPerMillion));
        AddNullable(command, "$cached_input", FormatDecimal(price.CachedInputUsdPerMillion));
        AddNullable(command, "$output", FormatDecimal(price.OutputUsdPerMillion));
        var value = await command.ExecuteScalarAsync(cancellationToken) as string;
        return value is null ? null : ParseDate(value);
    }

    private static bool SameRates(ModelPriceVersion previous, PriceCandidate current) =>
        previous.InputUsdPerMillion == current.InputUsdPerMillion &&
        previous.CachedInputUsdPerMillion == current.CachedInputUsdPerMillion &&
        previous.OutputUsdPerMillion == current.OutputUsdPerMillion;

    private static decimal MaximumChangeRatio(ModelPriceVersion previous, PriceCandidate current) =>
        new[]
        {
            ChangeRatio(previous.InputUsdPerMillion, current.InputUsdPerMillion),
            ChangeRatio(previous.CachedInputUsdPerMillion, current.CachedInputUsdPerMillion),
            ChangeRatio(previous.OutputUsdPerMillion, current.OutputUsdPerMillion)
        }.Max();

    private static decimal ChangeRatio(decimal? previous, decimal? current)
    {
        if (previous == current)
        {
            return 0m;
        }

        if (previous is null || current is null || previous == 0m)
        {
            return 1m;
        }

        return Math.Abs(current.Value - previous.Value) / previous.Value;
    }

    private static string? FormatDecimal(decimal? value) =>
        value?.ToString(CultureInfo.InvariantCulture);

    private static decimal? ReadNullableDecimal(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal)
            ? null
            : decimal.Parse(reader.GetString(ordinal), CultureInfo.InvariantCulture);

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
        var catalog = new PriceCatalog(await ReadPriceVersionsAsync(connection, cancellationToken));
        var dailyUsage = await ReadTimedModelUsageAsync(connection, today, today, cancellationToken);
        var weeklyUsage = await ReadTimedModelUsageAsync(connection, weekStart, today, cancellationToken);
        var monthlyUsage = await ReadTimedModelUsageAsync(connection, monthStart, today, cancellationToken);
        var currentRates = catalog.GetCurrentRates(calculatedAt);
        var latestEffective = currentRates
            .Where(rate => rate.EffectiveFrom is not null)
            .Select(rate => rate.EffectiveFrom!.Value)
            .DefaultIfEmpty(UsageCostEstimator.PricingAsOf.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc))
            .Max();

        return new UsageCostEstimate(
            UsageCostEstimator.EstimateTimedPeriod(today, today, dailyUsage, catalog),
            UsageCostEstimator.EstimateTimedPeriod(weekStart, today, weeklyUsage, catalog),
            UsageCostEstimator.EstimateTimedPeriod(monthStart, today, monthlyUsage, catalog),
            currentRates,
            "USD",
            DateOnly.FromDateTime(latestEffective.UtcDateTime),
            calculatedAt);
    }

    private static async Task<IReadOnlyList<TimedModelUsage>> ReadTimedModelUsageAsync(
        SqliteConnection connection,
        DateOnly startDate,
        DateOnly endDate,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                COALESCE(NULLIF(TRIM(sessions.model), ''), 'unknown'),
                events.event_utc,
                events.last_input_tokens,
                events.last_cached_input_tokens,
                events.last_cache_write_input_tokens,
                events.last_output_tokens,
                events.last_reasoning_output_tokens,
                events.last_total_tokens
            FROM token_usage_events AS events
            LEFT JOIN sessions ON sessions.session_id = events.session_id
            WHERE date(events.event_utc, 'localtime') >= $start_date
              AND date(events.event_utc, 'localtime') <= $end_date
              AND typeof(events.last_input_tokens) = 'integer'
              AND typeof(events.last_cached_input_tokens) = 'integer'
              AND typeof(events.last_cache_write_input_tokens) = 'integer'
              AND typeof(events.last_output_tokens) = 'integer'
              AND typeof(events.last_reasoning_output_tokens) = 'integer'
              AND typeof(events.last_total_tokens) = 'integer'
              AND events.last_total_tokens > 0
            ORDER BY events.event_utc, events.id
            """;
        command.Parameters.AddWithValue("$start_date", FormatDate(startDate));
        command.Parameters.AddWithValue("$end_date", FormatDate(endDate));
        var result = new List<TimedModelUsage>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new TimedModelUsage(reader.GetString(0), ParseDate(reader.GetString(1)), ReadUsage(reader, 2)));
        }

        return result;
    }

    private static async Task<int> InsertTokenEventsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RolloutFileResult result,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ParsedTokenEvent> events = result.TokenEvents ??
            (result.LatestTokenEvent is null ? [] : [result.LatestTokenEvent]);
        TokenUsage? previousTotal = null;
        var previousCommand = connection.CreateCommand();
        previousCommand.Transaction = transaction;
        previousCommand.CommandText = """
            SELECT input_tokens, cached_input_tokens, cache_write_input_tokens,
                   output_tokens, reasoning_output_tokens, total_tokens
            FROM token_usage_events WHERE rollout_path=$path ORDER BY id DESC LIMIT 1
            """;
        previousCommand.Parameters.AddWithValue("$path", result.Path);
        await using (var previousReader = await previousCommand.ExecuteReaderAsync(cancellationToken))
        {
            if (await previousReader.ReadAsync(cancellationToken))
            {
                previousTotal = ReadUsage(previousReader, 0);
            }
        }

        var insertedEvents = 0;
        foreach (var tokenEvent in events)
        {
            if (previousTotal == tokenEvent.TotalUsage)
            {
                continue;
            }

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
            insertedEvents += await command.ExecuteNonQueryAsync(cancellationToken);
            previousTotal = tokenEvent.TotalUsage;
        }

        return insertedEvents;
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

        var requiresProjectRescan = false;
        if (!columns.Contains("project_path"))
        {
            var addPath = connection.CreateCommand();
            addPath.CommandText = "ALTER TABLE sessions ADD COLUMN project_path TEXT NULL";
            await addPath.ExecuteNonQueryAsync(cancellationToken);
            requiresProjectRescan = true;
        }

        if (!columns.Contains("project_name"))
        {
            var addName = connection.CreateCommand();
            addName.CommandText = "ALTER TABLE sessions ADD COLUMN project_name TEXT NULL";
            await addName.ExecuteNonQueryAsync(cancellationToken);
            requiresProjectRescan = true;
        }

        if (!columns.Contains("project_status"))
        {
            var addStatus = connection.CreateCommand();
            addStatus.CommandText = """
                ALTER TABLE sessions ADD COLUMN project_status TEXT NOT NULL DEFAULT 'unidentified';
                UPDATE sessions
                SET project_status = CASE
                    WHEN project_path IS NULL OR TRIM(project_path) = '' THEN 'unidentified'
                    ELSE 'pending'
                END;
                """;
            await addStatus.ExecuteNonQueryAsync(cancellationToken);
            requiresProjectRescan = true;
        }

        var versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "SELECT value FROM settings WHERE key = 'project_identity_version'";
        var currentVersion = Convert.ToString(await versionCommand.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        if (!string.Equals(currentVersion, ProjectIdentityVersion, StringComparison.Ordinal))
        {
            requiresProjectRescan = true;
        }

        if (requiresProjectRescan)
        {
            var rescan = connection.CreateCommand();
            rescan.CommandText = """
                DELETE FROM file_checkpoints WHERE session_id IS NOT NULL;
                INSERT INTO settings(key, value) VALUES('project_identity_version', $version)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value;
                """;
            rescan.Parameters.AddWithValue("$version", ProjectIdentityVersion);
            await rescan.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task EnsurePriceChangeCandidatesSchemaAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var primaryKeyColumns = new SortedDictionary<int, string>();
        var inspect = connection.CreateCommand();
        inspect.CommandText = "PRAGMA table_info(price_change_candidates)";
        await using (var reader = await inspect.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var primaryKeyPosition = reader.GetInt32(5);
                if (primaryKeyPosition > 0)
                {
                    primaryKeyColumns[primaryKeyPosition] = reader.GetString(1);
                }
            }
        }

        string[] expectedPrimaryKey = ["source_id", "model", "effective_utc"];
        if (primaryKeyColumns.Values.SequenceEqual(expectedPrimaryKey, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var rebuild = connection.CreateCommand();
        rebuild.Transaction = transaction;
        rebuild.CommandText = """
            CREATE TABLE price_change_candidates_v2(
                source_id TEXT NOT NULL,
                model TEXT NOT NULL COLLATE NOCASE,
                effective_utc TEXT NOT NULL,
                input_usd_per_million TEXT NULL,
                cached_input_usd_per_million TEXT NULL,
                output_usd_per_million TEXT NULL,
                maximum_change_ratio TEXT NOT NULL,
                matching_observations INTEGER NOT NULL,
                first_observed_utc TEXT NOT NULL,
                last_observed_utc TEXT NOT NULL,
                content_hash TEXT NOT NULL,
                PRIMARY KEY(source_id, model, effective_utc)
            );

            INSERT INTO price_change_candidates_v2(
                source_id, model, effective_utc, input_usd_per_million,
                cached_input_usd_per_million, output_usd_per_million,
                maximum_change_ratio, matching_observations, first_observed_utc,
                last_observed_utc, content_hash)
            SELECT
                source_id, model, effective_utc, input_usd_per_million,
                cached_input_usd_per_million, output_usd_per_million,
                maximum_change_ratio, matching_observations, first_observed_utc,
                last_observed_utc, content_hash
            FROM price_change_candidates;

            DROP TABLE price_change_candidates;
            ALTER TABLE price_change_candidates_v2 RENAME TO price_change_candidates;
            """;
        await rebuild.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private sealed record ProjectSessionRow(
        string SessionId,
        string? ProjectPath,
        string? ProjectName,
        string Model,
        TokenUsage Usage,
        DateTimeOffset LastActivityAt);

    private sealed record DailyProjectSessionRow(
        string SessionId,
        string DisplayName,
        string Model,
        string ReasoningEffort,
        string? ProjectPath,
        string? ProjectName,
        string ProjectStatus,
        TokenUsage Usage,
        DateTimeOffset LastEventAt);

    private const string SessionColumns = """
        session_id, rollout_path, created_utc, last_activity_utc, title, title_updated_utc,
        model, reasoning_effort, project_path, project_name, project_status,
        total_input_tokens, total_cached_input_tokens, total_cache_write_input_tokens,
        total_output_tokens, total_reasoning_output_tokens, total_tokens,
        last_input_tokens, last_cached_input_tokens, last_cache_write_input_tokens,
        last_output_tokens, last_reasoning_output_tokens, last_total_tokens,
        model_context_window, is_muted, data_status, error_code, last_success_utc
        """;

    private const string SessionInsertColumns = SessionColumns;

    private const string SessionInsertValues = """
        $session_id, $rollout_path, $created_utc, $last_activity_utc, $title, $title_updated_utc,
        $model, $reasoning_effort, $project_path, $project_name, $project_status,
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
            project_status TEXT NOT NULL DEFAULT 'unidentified',
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

    private const string PricingSchemaSql = """
        CREATE TABLE IF NOT EXISTS model_price_versions(
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            source_id TEXT NOT NULL,
            model TEXT NOT NULL COLLATE NOCASE,
            input_usd_per_million TEXT NULL,
            cached_input_usd_per_million TEXT NULL,
            output_usd_per_million TEXT NULL,
            source_kind TEXT NOT NULL CHECK(source_kind IN (
                'official', 'third_party_reference', 'built_in_default')),
            source_name TEXT NOT NULL,
            source_url TEXT NOT NULL,
            effective_utc TEXT NOT NULL,
            retrieved_utc TEXT NOT NULL,
            content_hash TEXT NOT NULL,
            source_revision TEXT NULL,
            parser_version TEXT NOT NULL,
            is_backfill INTEGER NOT NULL CHECK(is_backfill IN (0, 1)),
            created_utc TEXT NOT NULL,
            UNIQUE(source_id, model, effective_utc, content_hash)
        );

        CREATE INDEX IF NOT EXISTS ix_model_price_versions_lookup
            ON model_price_versions(model, source_kind, effective_utc DESC);

        CREATE TABLE IF NOT EXISTS price_source_status(
            source_id TEXT PRIMARY KEY,
            source_kind TEXT NOT NULL,
            display_name TEXT NOT NULL,
            source_url TEXT NOT NULL,
            enabled INTEGER NOT NULL CHECK(enabled IN (0, 1)),
            gate_status TEXT NOT NULL,
            last_attempt_utc TEXT NULL,
            last_success_utc TEXT NULL,
            next_check_utc TEXT NULL,
            last_error TEXT NULL,
            last_content_hash TEXT NULL,
            last_source_revision TEXT NULL,
            consecutive_failures INTEGER NOT NULL DEFAULT 0
        );

        CREATE TABLE IF NOT EXISTS price_change_candidates(
            source_id TEXT NOT NULL,
            model TEXT NOT NULL COLLATE NOCASE,
            effective_utc TEXT NOT NULL,
            input_usd_per_million TEXT NULL,
            cached_input_usd_per_million TEXT NULL,
            output_usd_per_million TEXT NULL,
            maximum_change_ratio TEXT NOT NULL,
            matching_observations INTEGER NOT NULL,
            first_observed_utc TEXT NOT NULL,
            last_observed_utc TEXT NOT NULL,
            content_hash TEXT NOT NULL,
            PRIMARY KEY(source_id, model, effective_utc)
        );
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
