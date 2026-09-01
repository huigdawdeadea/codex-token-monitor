using System.Data.Common;
using System.Text.Json;
using CodexTokenMonitor.Core.Models;
using Microsoft.Data.Sqlite;

namespace CodexTokenMonitor.Core.Storage;

public sealed partial class MonitorDatabase
{
    private static readonly string[] DataVersionSections =
    [
        "overview", "sessions", "models", "usage", "rankings", "skills", "settings"
    ];

    public async Task<DataVersionSnapshot> GetDataVersionsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var versions = DataVersionSections.ToDictionary(section => section, _ => 0L, StringComparer.Ordinal);
        var changedAt = DateTimeOffset.MinValue;
        var command = connection.CreateCommand();
        command.CommandText = "SELECT section, version, changed_utc FROM data_versions";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            versions[reader.GetString(0)] = reader.GetInt64(1);
            var changed = ParseDate(reader.GetString(2));
            if (changed > changedAt)
            {
                changedAt = changed;
            }
        }

        return new DataVersionSnapshot(
            versions["overview"],
            versions["sessions"],
            versions["models"],
            versions["usage"],
            versions["rankings"],
            versions["skills"],
            versions["settings"],
            changedAt == DateTimeOffset.MinValue ? DateTimeOffset.UtcNow : changedAt);
    }

    public async Task<T?> ReadMaterializedViewAsync<T>(
        string cacheKey,
        string sourceVersion,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT payload_json FROM materialized_views WHERE cache_key=$key AND source_version=$version";
        command.Parameters.AddWithValue("$key", cacheKey);
        command.Parameters.AddWithValue("$version", sourceVersion);
        var json = await command.ExecuteScalarAsync(cancellationToken) as string;
        if (string.IsNullOrWhiteSpace(json))
        {
            return default;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, _jsonOptions);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    public async Task WriteMaterializedViewAsync<T>(
        string cacheKey,
        string sourceVersion,
        T payload,
        CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(payload, _jsonOptions);
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO materialized_views(cache_key, source_version, payload_json, updated_utc)
                VALUES($key, $version, $payload, $updated)
                ON CONFLICT(cache_key) DO UPDATE SET
                    source_version=excluded.source_version,
                    payload_json=excluded.payload_json,
                    updated_utc=excluded.updated_utc
                """;
            command.Parameters.AddWithValue("$key", cacheKey);
            command.Parameters.AddWithValue("$version", sourceVersion);
            command.Parameters.AddWithValue("$payload", json);
            command.Parameters.AddWithValue("$updated", Format(DateTimeOffset.UtcNow));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task ResetDerivedDataAsync(CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                DELETE FROM file_checkpoints;
                DELETE FROM skill_invocations;
                DELETE FROM skill_attribution_events;
                DELETE FROM skill_scan_checkpoints;
                DELETE FROM materialized_views;
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
            await BumpDataVersionsAsync(connection, transaction, DataVersionSections, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task NotifyDataChangedAsync(
        IEnumerable<string> sections,
        CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await BumpDataVersionsAsync(connection, transaction, sections, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static async Task BumpDataVersionsAsync(
        SqliteConnection connection,
        DbTransaction transaction,
        IEnumerable<string> sections,
        CancellationToken cancellationToken)
    {
        var changedAt = Format(DateTimeOffset.UtcNow);
        foreach (var section in sections.Distinct(StringComparer.Ordinal))
        {
            var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT INTO data_versions(section, version, changed_utc)
                VALUES($section, 1, $changed)
                ON CONFLICT(section) DO UPDATE SET
                    version=data_versions.version + 1,
                    changed_utc=excluded.changed_utc
                """;
            command.Parameters.AddWithValue("$section", section);
            command.Parameters.AddWithValue("$changed", changedAt);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task EnsurePerformanceSchemaAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = PerformanceSchemaSql;
        await command.ExecuteNonQueryAsync(cancellationToken);

        var now = Format(DateTimeOffset.UtcNow);
        foreach (var section in DataVersionSections)
        {
            var seed = connection.CreateCommand();
            seed.CommandText = "INSERT OR IGNORE INTO data_versions(section, version, changed_utc) VALUES($section, 1, $changed)";
            seed.Parameters.AddWithValue("$section", section);
            seed.Parameters.AddWithValue("$changed", now);
            await seed.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private const string PerformanceSchemaSql = """
        CREATE TABLE IF NOT EXISTS data_versions(
            section TEXT PRIMARY KEY,
            version INTEGER NOT NULL,
            changed_utc TEXT NOT NULL);

        CREATE TABLE IF NOT EXISTS materialized_views(
            cache_key TEXT PRIMARY KEY,
            source_version TEXT NOT NULL,
            payload_json TEXT NOT NULL,
            updated_utc TEXT NOT NULL);
        """;
}
