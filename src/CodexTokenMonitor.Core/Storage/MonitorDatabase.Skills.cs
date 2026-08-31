using CodexTokenMonitor.Core.Models;
using CodexTokenMonitor.Core.Pricing;
using Microsoft.Data.Sqlite;

namespace CodexTokenMonitor.Core.Storage;

public sealed partial class MonitorDatabase
{
    public async Task ReplaceSkillDefinitionsAsync(
        IReadOnlyList<SkillDefinitionSnapshot> definitions,
        CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            var deactivate = connection.CreateCommand();
            deactivate.Transaction = transaction;
            deactivate.CommandText = "UPDATE skill_definitions SET is_active = 0";
            await deactivate.ExecuteNonQueryAsync(cancellationToken);
            foreach (var definition in definitions)
            {
                var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO skill_definitions(
                        name, path, source_kind, is_active, content_hash, byte_count, character_count,
                        base_tokens, tokenizer, tokenizer_version, is_estimated, observed_utc)
                    VALUES($name, $path, $source, $active, $hash, $bytes, $characters,
                        $tokens, $tokenizer, $tokenizer_version, $estimated, $observed)
                    ON CONFLICT(path) DO UPDATE SET
                        name=excluded.name, source_kind=excluded.source_kind, is_active=excluded.is_active,
                        content_hash=excluded.content_hash, byte_count=excluded.byte_count,
                        character_count=excluded.character_count, base_tokens=excluded.base_tokens,
                        tokenizer=excluded.tokenizer, tokenizer_version=excluded.tokenizer_version,
                        is_estimated=excluded.is_estimated, observed_utc=excluded.observed_utc
                    """;
                command.Parameters.AddWithValue("$name", definition.Name);
                command.Parameters.AddWithValue("$path", definition.Path);
                command.Parameters.AddWithValue("$source", definition.SourceKind);
                command.Parameters.AddWithValue("$active", definition.IsActive ? 1 : 0);
                command.Parameters.AddWithValue("$hash", definition.ContentHash);
                command.Parameters.AddWithValue("$bytes", definition.ByteCount);
                command.Parameters.AddWithValue("$characters", definition.CharacterCount);
                command.Parameters.AddWithValue("$tokens", definition.BaseTokens);
                command.Parameters.AddWithValue("$tokenizer", definition.Tokenizer);
                command.Parameters.AddWithValue("$tokenizer_version", definition.TokenizerVersion);
                command.Parameters.AddWithValue("$estimated", definition.IsEstimated ? 1 : 0);
                command.Parameters.AddWithValue("$observed", Format(definition.ObservedAt));
                await command.ExecuteNonQueryAsync(cancellationToken);

                var version = connection.CreateCommand();
                version.Transaction = transaction;
                version.CommandText = """
                    INSERT INTO skill_definition_versions(
                        path, content_hash, name, source_kind, byte_count, character_count,
                        base_tokens, tokenizer, tokenizer_version, is_estimated, observed_utc)
                    VALUES($path, $hash, $name, $source, $bytes, $characters,
                        $tokens, $tokenizer, $tokenizer_version, $estimated, $observed)
                    ON CONFLICT(path, content_hash) DO UPDATE SET
                        name=excluded.name, source_kind=excluded.source_kind,
                        byte_count=excluded.byte_count, character_count=excluded.character_count,
                        base_tokens=excluded.base_tokens, tokenizer=excluded.tokenizer,
                        tokenizer_version=excluded.tokenizer_version, is_estimated=excluded.is_estimated,
                        observed_utc=excluded.observed_utc
                    """;
                version.Parameters.AddWithValue("$path", definition.Path);
                version.Parameters.AddWithValue("$hash", definition.ContentHash);
                version.Parameters.AddWithValue("$name", definition.Name);
                version.Parameters.AddWithValue("$source", definition.SourceKind);
                version.Parameters.AddWithValue("$bytes", definition.ByteCount);
                version.Parameters.AddWithValue("$characters", definition.CharacterCount);
                version.Parameters.AddWithValue("$tokens", definition.BaseTokens);
                version.Parameters.AddWithValue("$tokenizer", definition.Tokenizer);
                version.Parameters.AddWithValue("$tokenizer_version", definition.TokenizerVersion);
                version.Parameters.AddWithValue("$estimated", definition.IsEstimated ? 1 : 0);
                version.Parameters.AddWithValue("$observed", Format(definition.ObservedAt));
                await version.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<bool> SkillRolloutNeedsRebuildAsync(
        string path,
        long length,
        long lastWriteUtcTicks,
        int parserVersion,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT length, last_write_utc_ticks, parser_version FROM skill_scan_checkpoints WHERE path=$path";
        command.Parameters.AddWithValue("$path", path);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return !await reader.ReadAsync(cancellationToken) ||
            reader.GetInt64(0) != length ||
            reader.GetInt64(1) != lastWriteUtcTicks ||
            reader.GetInt32(2) != parserVersion;
    }

    public async Task ReplaceSkillRolloutAsync(
        SkillRolloutAnalysis analysis,
        int parserVersion,
        CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            foreach (var table in new[] { "skill_invocations", "skill_attribution_events" })
            {
                var delete = connection.CreateCommand();
                delete.Transaction = transaction;
                delete.CommandText = $"DELETE FROM {table} WHERE rollout_path=$path";
                delete.Parameters.AddWithValue("$path", analysis.RolloutPath);
                await delete.ExecuteNonQueryAsync(cancellationToken);
            }

            foreach (var invocation in analysis.Invocations)
            {
                var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO skill_invocations(
                        session_id, rollout_path, task_id, skill_name, skill_path, version_hash,
                        confidence, started_utc, ended_utc, model, loaded_tokens, evidence_kind)
                    VALUES($session, $path, $task, $name, $skill_path, $hash,
                        $confidence, $started, $ended, $model, $loaded, $evidence)
                    """;
                command.Parameters.AddWithValue("$session", invocation.SessionId);
                command.Parameters.AddWithValue("$path", invocation.RolloutPath);
                command.Parameters.AddWithValue("$task", invocation.TaskId);
                command.Parameters.AddWithValue("$name", invocation.SkillName);
                command.Parameters.AddWithValue("$skill_path", invocation.SkillPath);
                command.Parameters.AddWithValue("$hash", invocation.VersionHash);
                command.Parameters.AddWithValue("$confidence", invocation.Confidence);
                command.Parameters.AddWithValue("$started", Format(invocation.StartedAt));
                command.Parameters.AddWithValue("$ended", invocation.EndedAt is null ? DBNull.Value : Format(invocation.EndedAt.Value));
                command.Parameters.AddWithValue("$model", invocation.Model);
                command.Parameters.AddWithValue("$loaded", invocation.LoadedTokens);
                command.Parameters.AddWithValue("$evidence", invocation.EvidenceKind);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            foreach (var attribution in analysis.Attributions)
            {
                var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO skill_attribution_events(
                        session_id, rollout_path, task_id, event_utc, model, bucket_type, bucket_key,
                        skill_names, confidence, input_tokens, cached_input_tokens, cache_write_input_tokens,
                        output_tokens, reasoning_output_tokens, total_tokens)
                    VALUES($session, $path, $task, $event, $model, $type, $key,
                        $names, $confidence, $input, $cached, $cache_write, $output, $reasoning, $total)
                    """;
                command.Parameters.AddWithValue("$session", attribution.SessionId);
                command.Parameters.AddWithValue("$path", attribution.RolloutPath);
                command.Parameters.AddWithValue("$task", attribution.TaskId);
                command.Parameters.AddWithValue("$event", Format(attribution.EventAt));
                command.Parameters.AddWithValue("$model", attribution.Model);
                command.Parameters.AddWithValue("$type", attribution.BucketType);
                command.Parameters.AddWithValue("$key", attribution.BucketKey);
                command.Parameters.AddWithValue("$names", attribution.SkillNames);
                command.Parameters.AddWithValue("$confidence", attribution.Confidence);
                command.Parameters.AddWithValue("$input", attribution.Usage.InputTokens);
                command.Parameters.AddWithValue("$cached", attribution.Usage.CachedInputTokens);
                command.Parameters.AddWithValue("$cache_write", attribution.Usage.CacheWriteInputTokens);
                command.Parameters.AddWithValue("$output", attribution.Usage.OutputTokens);
                command.Parameters.AddWithValue("$reasoning", attribution.Usage.ReasoningOutputTokens);
                command.Parameters.AddWithValue("$total", attribution.Usage.TotalTokens);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            var checkpoint = connection.CreateCommand();
            checkpoint.Transaction = transaction;
            checkpoint.CommandText = """
                INSERT INTO skill_scan_checkpoints(path, length, last_write_utc_ticks, parser_version, updated_utc)
                VALUES($path, $length, $ticks, $version, $updated)
                ON CONFLICT(path) DO UPDATE SET length=excluded.length, last_write_utc_ticks=excluded.last_write_utc_ticks,
                    parser_version=excluded.parser_version, updated_utc=excluded.updated_utc
                """;
            checkpoint.Parameters.AddWithValue("$path", analysis.RolloutPath);
            checkpoint.Parameters.AddWithValue("$length", analysis.Length);
            checkpoint.Parameters.AddWithValue("$ticks", analysis.LastWriteUtcTicks);
            checkpoint.Parameters.AddWithValue("$version", parserVersion);
            checkpoint.Parameters.AddWithValue("$updated", Format(DateTimeOffset.UtcNow));
            await checkpoint.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<SkillUsageSummary> GetSkillUsageSummaryAsync(
        int days = 0,
        string confidence = "high",
        string? model = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var definitions = new List<SkillDefinitionSnapshot>();
        var definitionCommand = connection.CreateCommand();
        definitionCommand.CommandText = """
            SELECT name, path, source_kind, is_active, content_hash, byte_count, character_count,
                   base_tokens, tokenizer, tokenizer_version, is_estimated, observed_utc
            FROM skill_definitions
            """;
        await using (var reader = await definitionCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                definitions.Add(new SkillDefinitionSnapshot(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3) == 1,
                    reader.GetString(4), reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7),
                    reader.GetString(8), reader.GetString(9), reader.GetInt64(10) == 1,
                    DateTimeOffset.Parse(reader.GetString(11))));
            }
        }

        var definitionVersions = new List<SkillVersionSummary>();
        var versionNames = new List<string>();
        var versionCommand = connection.CreateCommand();
        versionCommand.CommandText = """
            SELECT name, content_hash, path, base_tokens, byte_count, character_count,
                   tokenizer, tokenizer_version, is_estimated, observed_utc
            FROM skill_definition_versions
            """;
        await using (var reader = await versionCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                versionNames.Add(reader.GetString(0));
                definitionVersions.Add(new SkillVersionSummary(
                    reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetInt64(4),
                    reader.GetInt64(5), reader.GetString(6), reader.GetString(7), reader.GetInt64(8) == 1,
                    DateTimeOffset.Parse(reader.GetString(9))));
            }
        }

        var cutoff = days > 0 ? DateTimeOffset.UtcNow.AddDays(-Math.Clamp(days, 1, 3650)) : DateTimeOffset.MinValue;
        var invocations = new List<SkillInvocationSnapshot>();
        var invocationCommand = connection.CreateCommand();
        invocationCommand.CommandText = """
            SELECT session_id, rollout_path, task_id, skill_name, skill_path, version_hash, confidence,
                   started_utc, ended_utc, model, loaded_tokens, evidence_kind
            FROM skill_invocations WHERE started_utc >= $cutoff
              AND ($confidence = 'all' OR confidence = $confidence)
              AND ($model = '' OR model = $model)
            """;
        invocationCommand.Parameters.AddWithValue("$cutoff", Format(cutoff));
        invocationCommand.Parameters.AddWithValue("$confidence", confidence);
        invocationCommand.Parameters.AddWithValue("$model", model ?? "");
        await using (var reader = await invocationCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                invocations.Add(new SkillInvocationSnapshot(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                    reader.GetString(5), reader.GetString(6), DateTimeOffset.Parse(reader.GetString(7)),
                    reader.IsDBNull(8) ? null : DateTimeOffset.Parse(reader.GetString(8)), reader.GetString(9), reader.GetInt64(10), reader.GetString(11)));
            }
        }

        var attributions = new List<SkillAttributionSnapshot>();
        var attributionCommand = connection.CreateCommand();
        attributionCommand.CommandText = """
            SELECT session_id, rollout_path, task_id, event_utc, model, bucket_type, bucket_key, skill_names,
                   confidence, input_tokens, cached_input_tokens, cache_write_input_tokens,
                   output_tokens, reasoning_output_tokens, total_tokens
            FROM skill_attribution_events WHERE event_utc >= $cutoff
              AND ($confidence = 'all' OR confidence = $confidence)
              AND ($model = '' OR model = $model)
            """;
        attributionCommand.Parameters.AddWithValue("$cutoff", Format(cutoff));
        attributionCommand.Parameters.AddWithValue("$confidence", confidence);
        attributionCommand.Parameters.AddWithValue("$model", model ?? "");
        await using (var reader = await attributionCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                attributions.Add(new SkillAttributionSnapshot(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2), DateTimeOffset.Parse(reader.GetString(3)),
                    reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetString(8), ReadUsage(reader, 9)));
            }
        }

        var priceCatalog = new PriceCatalog(await ReadPriceVersionsAsync(connection, cancellationToken));
        var collaborations = attributions.Where(item => item.BucketType == "collaboration")
            .GroupBy(item => item.BucketKey)
            .Select(group =>
            {
                var events = group.ToArray();
                var cost = EstimateSkillCost(events, priceCatalog);
                return new SkillCollaborationSummary(
                    group.Key,
                    events[0].SkillNames,
                    events.Length,
                    SumSkillUsage(events.Select(item => item.Usage)),
                    cost.EstimatedCostUsd,
                    cost.UnpricedTokens,
                    events.Max(item => item.EventAt));
            })
            .OrderByDescending(item => item.Usage.TotalTokens)
            .ToArray();
        var skillNames = definitions.Select(item => item.Name).Distinct(StringComparer.OrdinalIgnoreCase);
        var rankings = skillNames.Select(name =>
        {
            var versions = definitions.Where(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
            var calls = invocations.Where(item => item.SkillName.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
            var exclusive = attributions.Where(item => item.BucketType == "exclusive" && item.BucketKey.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
            var collaborative = attributions.Where(item => item.BucketType == "collaboration" &&
                item.SkillNames.Split('|').Contains(name, StringComparer.OrdinalIgnoreCase)).ToArray();
            var cost = EstimateSkillCost(exclusive.Concat(collaborative), priceCatalog);
            var current = versions.OrderByDescending(item => item.IsActive).ThenByDescending(item => item.ObservedAt).First();
            return new SkillUsageRanking(
                name,
                versions.Any(item => item.IsActive),
                current.SourceKind,
                current.BaseTokens,
                current.IsEstimated,
                current.Tokenizer,
                calls.Length,
                calls.Length == 0 ? 0 : (long)calls.Average(item => item.LoadedTokens),
                SumSkillUsage(exclusive.Select(item => item.Usage)),
                SumSkillUsage(collaborative.Select(item => item.Usage)),
                cost.EstimatedCostUsd,
                cost.UnpricedTokens,
                calls.Length == 0 ? null : calls.Max(item => item.StartedAt),
                definitionVersions.Where((_, index) => versionNames[index].Equals(name, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(item => item.ObservedAt)
                    .ToArray());
        }).OrderByDescending(item => item.ExclusiveUsage.TotalTokens)
          .ThenByDescending(item => item.InvocationCount)
          .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
          .ToArray();

        var exclusiveTokens = attributions.Where(item => item.BucketType == "exclusive").Sum(item => item.Usage.TotalTokens);
        var collaborationTokens = attributions.Where(item => item.BucketType == "collaboration").Sum(item => item.Usage.TotalTokens);
        var unattributedTokens = attributions.Where(item => item.BucketType == "unattributed").Sum(item => item.Usage.TotalTokens);
        var attributable = exclusiveTokens + collaborationTokens;
        var denominator = attributable + unattributedTokens;
        return new SkillUsageSummary(
            definitions.Count(item => item.IsActive),
            invocations.Count,
            exclusiveTokens,
            collaborationTokens,
            unattributedTokens,
            denominator == 0 ? 100m : Math.Round(attributable * 100m / denominator, 1),
            rankings,
            collaborations,
            DateTimeOffset.UtcNow);
    }

    public async Task<SkillUsageDetail?> GetSkillUsageDetailAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        var summary = await GetSkillUsageSummaryAsync(confidence: "all", cancellationToken: cancellationToken);
        var skill = summary.Skills.FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (skill is null)
        {
            return null;
        }

        await using var connection = await OpenAsync(cancellationToken);
        var invocations = new List<SkillInvocationSnapshot>();
        var invocationCommand = connection.CreateCommand();
        invocationCommand.CommandText = """
            SELECT session_id, rollout_path, task_id, skill_name, skill_path, version_hash, confidence,
                   started_utc, ended_utc, model, loaded_tokens, evidence_kind
            FROM skill_invocations WHERE skill_name=$name ORDER BY started_utc DESC LIMIT 200
            """;
        invocationCommand.Parameters.AddWithValue("$name", name);
        await using (var reader = await invocationCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                invocations.Add(new SkillInvocationSnapshot(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                    reader.GetString(5), reader.GetString(6), DateTimeOffset.Parse(reader.GetString(7)),
                    reader.IsDBNull(8) ? null : DateTimeOffset.Parse(reader.GetString(8)), reader.GetString(9), reader.GetInt64(10), reader.GetString(11)));
            }
        }

        var events = new List<SkillAttributionSnapshot>();
        var eventCommand = connection.CreateCommand();
        eventCommand.CommandText = """
            SELECT session_id, rollout_path, task_id, event_utc, model, bucket_type, bucket_key, skill_names,
                   confidence, input_tokens, cached_input_tokens, cache_write_input_tokens,
                   output_tokens, reasoning_output_tokens, total_tokens
            FROM skill_attribution_events
            WHERE bucket_key=$name OR ('|' || skill_names || '|') LIKE ('%|' || $name || '|%')
            ORDER BY event_utc DESC LIMIT 500
            """;
        eventCommand.Parameters.AddWithValue("$name", name);
        await using (var reader = await eventCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                events.Add(new SkillAttributionSnapshot(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2), DateTimeOffset.Parse(reader.GetString(3)),
                    reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetString(8), ReadUsage(reader, 9)));
            }
        }

        return new SkillUsageDetail(skill, invocations, events);
    }

    private static TokenUsage SumSkillUsage(IEnumerable<TokenUsage> usages) => usages.Aggregate(
        TokenUsage.Empty,
        (sum, item) => new TokenUsage(
            sum.InputTokens + item.InputTokens,
            sum.CachedInputTokens + item.CachedInputTokens,
            sum.CacheWriteInputTokens + item.CacheWriteInputTokens,
            sum.OutputTokens + item.OutputTokens,
            sum.ReasoningOutputTokens + item.ReasoningOutputTokens,
            sum.TotalTokens + item.TotalTokens));

    private static UsageCostPeriod EstimateSkillCost(
        IEnumerable<SkillAttributionSnapshot> attributionEvents,
        PriceCatalog catalog)
    {
        var events = attributionEvents.ToArray();
        if (events.Length == 0)
        {
            return new UsageCostPeriod(
                DateOnly.FromDateTime(DateTime.Today),
                DateOnly.FromDateTime(DateTime.Today),
                TokenUsage.Empty,
                0,
                0,
                0m,
                []);
        }

        return UsageCostEstimator.EstimateTimedPeriod(
            DateOnly.FromDateTime(events.Min(item => item.EventAt).LocalDateTime),
            DateOnly.FromDateTime(events.Max(item => item.EventAt).LocalDateTime),
            events.Select(item => new TimedModelUsage(item.Model, item.EventAt, item.Usage)),
            catalog);
    }

    private const string SkillUsageSchemaSql = """
        CREATE TABLE IF NOT EXISTS skill_definitions(
            path TEXT PRIMARY KEY, name TEXT NOT NULL, source_kind TEXT NOT NULL, is_active INTEGER NOT NULL,
            content_hash TEXT NOT NULL, byte_count INTEGER NOT NULL, character_count INTEGER NOT NULL,
            base_tokens INTEGER NOT NULL, tokenizer TEXT NOT NULL, tokenizer_version TEXT NOT NULL,
            is_estimated INTEGER NOT NULL, observed_utc TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS ix_skill_definitions_name ON skill_definitions(name, is_active);

        CREATE TABLE IF NOT EXISTS skill_definition_versions(
            path TEXT NOT NULL, content_hash TEXT NOT NULL, name TEXT NOT NULL, source_kind TEXT NOT NULL,
            byte_count INTEGER NOT NULL, character_count INTEGER NOT NULL, base_tokens INTEGER NOT NULL,
            tokenizer TEXT NOT NULL, tokenizer_version TEXT NOT NULL, is_estimated INTEGER NOT NULL,
            observed_utc TEXT NOT NULL, PRIMARY KEY(path, content_hash));
        CREATE INDEX IF NOT EXISTS ix_skill_definition_versions_name ON skill_definition_versions(name, observed_utc);

        CREATE TABLE IF NOT EXISTS skill_invocations(
            id INTEGER PRIMARY KEY AUTOINCREMENT, session_id TEXT NOT NULL, rollout_path TEXT NOT NULL,
            task_id TEXT NOT NULL, skill_name TEXT NOT NULL, skill_path TEXT NOT NULL, version_hash TEXT NOT NULL,
            confidence TEXT NOT NULL, started_utc TEXT NOT NULL, ended_utc TEXT NULL, model TEXT NOT NULL,
            loaded_tokens INTEGER NOT NULL, evidence_kind TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS ix_skill_invocations_name_time ON skill_invocations(skill_name, started_utc);

        CREATE TABLE IF NOT EXISTS skill_attribution_events(
            id INTEGER PRIMARY KEY AUTOINCREMENT, session_id TEXT NOT NULL, rollout_path TEXT NOT NULL,
            task_id TEXT NOT NULL, event_utc TEXT NOT NULL, model TEXT NOT NULL, bucket_type TEXT NOT NULL,
            bucket_key TEXT NOT NULL, skill_names TEXT NOT NULL, confidence TEXT NOT NULL,
            input_tokens INTEGER NOT NULL, cached_input_tokens INTEGER NOT NULL,
            cache_write_input_tokens INTEGER NOT NULL, output_tokens INTEGER NOT NULL,
            reasoning_output_tokens INTEGER NOT NULL, total_tokens INTEGER NOT NULL);
        CREATE INDEX IF NOT EXISTS ix_skill_attribution_time ON skill_attribution_events(event_utc, bucket_type);

        CREATE TABLE IF NOT EXISTS skill_scan_checkpoints(
            path TEXT PRIMARY KEY, length INTEGER NOT NULL, last_write_utc_ticks INTEGER NOT NULL,
            parser_version INTEGER NOT NULL, updated_utc TEXT NOT NULL);
        """;
}
