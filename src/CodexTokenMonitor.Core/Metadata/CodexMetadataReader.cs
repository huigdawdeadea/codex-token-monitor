using System.Text.Json;
using CodexTokenMonitor.Core.Models;
using Microsoft.Data.Sqlite;

namespace CodexTokenMonitor.Core.Metadata;

public sealed record MetadataReadResult(
    IReadOnlyCollection<SessionEnrichment> Entries,
    string? ErrorCode);

public sealed class CodexMetadataReader
{
    public async Task<MetadataReadResult> ReadAsync(
        string codexHome,
        bool captureTitles,
        CancellationToken cancellationToken = default)
    {
        var entries = new Dictionary<string, SessionEnrichment>(StringComparer.OrdinalIgnoreCase);
        string? errorCode = null;

        if (captureTitles)
        {
            try
            {
                await ReadTitlesAsync(Path.Combine(codexHome, "session_index.jsonl"), entries, cancellationToken);
            }
            catch (IOException)
            {
                errorCode = "session_index_unavailable";
            }
            catch (JsonException)
            {
                errorCode = "session_index_invalid";
            }
        }

        try
        {
            await ReadStateDatabaseAsync(Path.Combine(codexHome, "state_5.sqlite"), entries, cancellationToken);
        }
        catch (SqliteException)
        {
            errorCode ??= "codex_state_unavailable";
        }
        catch (IOException)
        {
            errorCode ??= "codex_state_unavailable";
        }

        return new MetadataReadResult(entries.Values.ToArray(), errorCode);
    }

    private static async Task ReadTitlesAsync(
        string path,
        IDictionary<string, SessionEnrichment> entries,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return;
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var id = GetString(root, "id");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var title = GetString(root, "thread_name");
            var updatedAtText = GetString(root, "updated_at");
            DateTimeOffset? updatedAt = DateTimeOffset.TryParse(updatedAtText, out var parsed) ? parsed : null;
            entries[id] = Merge(GetExisting(entries, id), id, title, updatedAt, null, null);
        }
    }

    private static async Task ReadStateDatabaseAsync(
        string path,
        IDictionary<string, SessionEnrichment> entries,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return;
        }

        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared,
            Pooling = false
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT id, model, reasoning_effort FROM threads";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetString(0);
            var model = reader.IsDBNull(1) ? null : reader.GetString(1);
            var reasoningEffort = reader.IsDBNull(2) ? null : reader.GetString(2);
            entries[id] = Merge(GetExisting(entries, id), id, null, null, model, reasoningEffort);
        }
    }

    private static SessionEnrichment Merge(
        SessionEnrichment? existing,
        string id,
        string? title,
        DateTimeOffset? titleUpdatedAt,
        string? model,
        string? reasoningEffort) => new(
            id,
            title ?? existing?.Title,
            titleUpdatedAt ?? existing?.TitleUpdatedAt,
            model ?? existing?.Model,
            reasoningEffort ?? existing?.ReasoningEffort);

    private static SessionEnrichment? GetExisting(
        IDictionary<string, SessionEnrichment> entries,
        string id) => entries.TryGetValue(id, out var existing) ? existing : null;

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
}
