using System.Text.Json;
using CodexTokenMonitor.Core.Models;

namespace CodexTokenMonitor.Core.Parsing;

public sealed class RolloutEventParser
{
    private static ReadOnlySpan<byte> SessionMetaMarker => "\"session_meta\""u8;
    private static ReadOnlySpan<byte> TokenUsageMarker => "\"total_token_usage\""u8;

    public bool TryParse(
        ReadOnlyMemory<byte> utf8Line,
        out ParsedRolloutEvent? parsedEvent,
        out string? errorCode)
    {
        parsedEvent = null;
        errorCode = null;

        var span = utf8Line.Span;
        if (span.IndexOf(SessionMetaMarker) < 0 && span.IndexOf(TokenUsageMarker) < 0)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(utf8Line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!TryGetString(root, "type", out var eventType) ||
                !root.TryGetProperty("payload", out var payload))
            {
                errorCode = "missing_event_envelope";
                return false;
            }

            var rootTimestamp = ParseTimestamp(root, "timestamp") ?? DateTimeOffset.UtcNow;
            if (eventType == "session_meta")
            {
                if (payload.ValueKind != JsonValueKind.Object)
                {
                    errorCode = "invalid_session_metadata";
                    return false;
                }

                var sessionId = GetString(payload, "session_id") ?? GetString(payload, "id");
                if (string.IsNullOrWhiteSpace(sessionId))
                {
                    errorCode = "missing_session_id";
                    return false;
                }

                var timestamp = ParseTimestamp(payload, "timestamp") ?? rootTimestamp;
                var model = ReadNestedString(payload, "base_instructions", "provenance", "model");
                var threadSource = ReadSimpleString(payload, "thread_source");
                var project = ProjectIdentityResolver.Resolve(GetString(payload, "cwd"));
                parsedEvent = new ParsedSessionMetadata(
                    sessionId,
                    timestamp,
                    model,
                    threadSource,
                    project.Path,
                    project.Name);
                return true;
            }

            if (eventType != "event_msg")
            {
                return false;
            }

            if (payload.ValueKind != JsonValueKind.Object ||
                !payload.TryGetProperty("info", out var info) ||
                info.ValueKind != JsonValueKind.Object ||
                !info.TryGetProperty("total_token_usage", out var totalElement))
            {
                return false;
            }

            if (totalElement.ValueKind != JsonValueKind.Object ||
                !info.TryGetProperty("last_token_usage", out var lastElement) ||
                lastElement.ValueKind != JsonValueKind.Object)
            {
                errorCode = "missing_token_payload";
                return false;
            }

            if (!TryReadUsage(totalElement, out var totalUsage) ||
                !TryReadUsage(lastElement, out var lastUsage))
            {
                errorCode = "invalid_token_usage";
                return false;
            }

            var contextWindow = GetInt64(info, "model_context_window") ?? 0;
            parsedEvent = new ParsedTokenEvent(rootTimestamp, totalUsage, lastUsage, contextWindow);
            return true;
        }
        catch (JsonException)
        {
            errorCode = "invalid_json";
            return false;
        }
        catch (InvalidOperationException)
        {
            errorCode = "invalid_json_shape";
            return false;
        }
    }

    private static bool TryReadUsage(JsonElement element, out TokenUsage usage)
    {
        var input = GetInt64(element, "input_tokens");
        var output = GetInt64(element, "output_tokens");
        var total = GetInt64(element, "total_tokens");
        if (input is null || output is null || total is null || input < 0 || output < 0 || total < 0)
        {
            usage = TokenUsage.Empty;
            return false;
        }

        usage = new TokenUsage(
            input.Value,
            GetInt64(element, "cached_input_tokens") ?? 0,
            GetInt64(element, "cache_write_input_tokens") ?? 0,
            output.Value,
            GetInt64(element, "reasoning_output_tokens") ?? 0,
            total.Value);
        return true;
    }

    private static long? GetInt64(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.Number &&
        property.TryGetInt64(out var value)
            ? value
            : null;

    private static bool TryGetString(JsonElement element, string propertyName, out string? value)
    {
        value = GetString(element, propertyName);
        return value is not null;
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static string? ReadSimpleString(JsonElement element, string propertyName) =>
        GetString(element, propertyName);

    private static string? ReadNestedString(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!current.TryGetProperty(segment, out current))
            {
                return null;
            }
        }

        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }

    private static DateTimeOffset? ParseTimestamp(JsonElement element, string propertyName)
    {
        var value = GetString(element, propertyName);
        return DateTimeOffset.TryParse(value, out var timestamp) ? timestamp : null;
    }
}
