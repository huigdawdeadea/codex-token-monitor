using System.Text.Json;
using CodexTokenMonitor.Core.Models;
using CodexTokenMonitor.Core.Pricing;

namespace CodexTokenMonitor.Core.Parsing;

public sealed class SessionQuestionDetailReader(RolloutEventParser tokenParser)
{
    private const int PromptPreviewLength = 280;

    public async Task<SessionQuestionHistory> ReadAsync(
        string sessionId,
        string rolloutPath,
        string fallbackModel,
        int limit = 200,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 200);
        if (!File.Exists(rolloutPath))
        {
            return new SessionQuestionHistory(sessionId, [], 0, false, false);
        }

        var retained = new Queue<SessionQuestionDetail>(limit);
        PendingQuestion? current = null;
        var totalQuestions = 0;
        await using var stream = new FileStream(
            rolloutPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024,
            useAsync: true);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var isTokenEvent = line.Contains("\"total_token_usage\"", StringComparison.Ordinal);
            var isTurnContext = line.Contains("\"turn_context\"", StringComparison.Ordinal);
            var isUserMessage = line.Contains("\"role\":\"user\"", StringComparison.Ordinal);
            if (!isTokenEvent && !isTurnContext && !isUserMessage)
            {
                continue;
            }

            if (isTokenEvent &&
                tokenParser.TryParse(System.Text.Encoding.UTF8.GetBytes(line), out var parsedEvent, out _) &&
                parsedEvent is ParsedTokenEvent tokenEvent)
            {
                current ??= new PendingQuestion(null, tokenEvent.Timestamp, fallbackModel);
                current.AddUsage(tokenEvent.LastUsage);
                continue;
            }

            if (!isTurnContext && !isUserMessage)
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var eventType = GetString(root, "type");
                if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (eventType == "turn_context")
                {
                    FinalizeCurrent();
                    current = new PendingQuestion(
                        GetString(payload, "turn_id"),
                        ParseTimestamp(root) ?? DateTimeOffset.UtcNow,
                        GetString(payload, "model") ?? fallbackModel);
                    continue;
                }

                if (eventType == "response_item" &&
                    GetString(payload, "type") == "message" &&
                    GetString(payload, "role") == "user" &&
                    current is not null)
                {
                    current.AddPrompt(ReadUserText(payload));
                }
            }
            catch (JsonException)
            {
                // A partially written line is ignored; the next refresh reads the completed source file.
            }
        }

        FinalizeCurrent();
        return new SessionQuestionHistory(
            sessionId,
            retained.ToArray(),
            totalQuestions,
            totalQuestions > limit,
            true);

        void FinalizeCurrent()
        {
            if (current is null || !current.HasPrompt && current.ModelCallCount == 0)
            {
                current = null;
                return;
            }

            totalQuestions++;
            var detail = current.ToDetail(totalQuestions);
            if (retained.Count == limit)
            {
                retained.Dequeue();
            }

            retained.Enqueue(detail);
            current = null;
        }
    }

    private static string ReadUserText(JsonElement payload)
    {
        if (!payload.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            return "";
        }

        var parts = new List<string>();
        foreach (var item in content.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object &&
                GetString(item, "type") == "input_text" &&
                GetString(item, "text") is { } text &&
                !string.IsNullOrWhiteSpace(text))
            {
                parts.Add(text);
            }
        }

        return string.Join(" ", parts);
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static DateTimeOffset? ParseTimestamp(JsonElement root) =>
        DateTimeOffset.TryParse(GetString(root, "timestamp"), out var timestamp) ? timestamp : null;

    private sealed class PendingQuestion(string? turnId, DateTimeOffset startedAt, string model)
    {
        private readonly List<string> _promptParts = [];

        public int ModelCallCount { get; private set; }
        public TokenUsage Usage { get; private set; } = TokenUsage.Empty;
        public bool HasPrompt => _promptParts.Count > 0;

        public void AddPrompt(string prompt)
        {
            if (!string.IsNullOrWhiteSpace(prompt))
            {
                _promptParts.Add(prompt);
            }
        }

        public void AddUsage(TokenUsage usage)
        {
            ModelCallCount++;
            Usage = new TokenUsage(
                Usage.InputTokens + usage.InputTokens,
                Usage.CachedInputTokens + usage.CachedInputTokens,
                Usage.CacheWriteInputTokens + usage.CacheWriteInputTokens,
                Usage.OutputTokens + usage.OutputTokens,
                Usage.ReasoningOutputTokens + usage.ReasoningOutputTokens,
                Usage.TotalTokens + usage.TotalTokens);
        }

        public SessionQuestionDetail ToDetail(int sequence)
        {
            var normalizedPrompt = string.Join(
                " ",
                string.Join(" ", _promptParts).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            var truncated = normalizedPrompt.Length > PromptPreviewLength;
            var preview = string.IsNullOrWhiteSpace(normalizedPrompt)
                ? $"第 {sequence} 次调用"
                : truncated ? normalizedPrompt[..PromptPreviewLength] + "..." : normalizedPrompt;
            var cost = UsageCostEstimator.EstimateModel(model, Usage);
            return new SessionQuestionDetail(
                sequence,
                turnId,
                startedAt,
                preview,
                truncated,
                model,
                ModelCallCount,
                Usage,
                cost.EstimatedCostUsd,
                cost.IsPriced);
        }
    }
}
