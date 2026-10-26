using CodexTokenMonitor.Core.Parsing;

namespace CodexTokenMonitor.Tests;

public sealed class SessionQuestionDetailReaderTests
{
    [Fact]
    public async Task AggregatesAllModelCallsWithinEachUserQuestion()
    {
        using var temporary = new TemporaryDirectory();
        var path = Path.Combine(temporary.Path, "rollout.jsonl");
        await File.WriteAllLinesAsync(path,
        [
            """{"timestamp":"2026-08-27T01:00:00Z","type":"turn_context","payload":{"turn_id":"turn-1","model":"gpt-5.6-sol"}}""",
            """{"timestamp":"2026-08-27T01:00:01Z","type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"First   question"}]}}""",
            TokenLine("2026-08-27T01:00:02Z", 100, 80, 20, 120),
            TokenLine("2026-08-27T01:00:03Z", 50, 40, 10, 60),
            """{"timestamp":"2026-08-27T02:00:00Z","type":"turn_context","payload":{"turn_id":"turn-2","model":"gpt-5.6-terra"}}""",
            """{"timestamp":"2026-08-27T02:00:01Z","type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"Second question"}]}}"""
        ]);
        var reader = new SessionQuestionDetailReader(new RolloutEventParser());

        var result = await reader.ReadAsync("session-1", path, "unknown");

        Assert.True(result.SourceAvailable);
        Assert.Equal(2, result.TotalQuestions);
        var first = result.Questions[0];
        Assert.Equal("First question", first.PromptPreview);
        Assert.Equal("gpt-5.6-sol", first.Model);
        Assert.Equal(2, first.ModelCallCount);
        Assert.Equal(150, first.Usage.InputTokens);
        Assert.Equal(120, first.Usage.CachedInputTokens);
        Assert.Equal(30, first.Usage.OutputTokens);
        Assert.Equal(180, first.Usage.TotalTokens);
        Assert.Equal(0.005146m, first.EstimatedCostCny);
        Assert.True(first.IsCostPriced);
        Assert.Equal("Second question", result.Questions[1].PromptPreview);
        Assert.Equal(0, result.Questions[1].ModelCallCount);
    }

    [Fact]
    public async Task KeepsOnlyTheMostRecentQuestionsWhenLimited()
    {
        using var temporary = new TemporaryDirectory();
        var path = Path.Combine(temporary.Path, "rollout.jsonl");
        await File.WriteAllLinesAsync(path,
        [
            """{"timestamp":"2026-08-27T01:00:00Z","type":"turn_context","payload":{"turn_id":"turn-1","model":"gpt-5.6-sol"}}""",
            """{"timestamp":"2026-08-27T01:00:01Z","type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"First"}]}}""",
            """{"timestamp":"2026-08-27T02:00:00Z","type":"turn_context","payload":{"turn_id":"turn-2","model":"gpt-5.6-sol"}}""",
            """{"timestamp":"2026-08-27T02:00:01Z","type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"Second"}]}}"""
        ]);
        var reader = new SessionQuestionDetailReader(new RolloutEventParser());

        var result = await reader.ReadAsync("session-1", path, "unknown", limit: 1);

        Assert.True(result.IsTruncated);
        Assert.Equal(2, result.TotalQuestions);
        Assert.Equal(2, Assert.Single(result.Questions).Sequence);
    }

    [Fact]
    public async Task ReportsWhenTheSourceRolloutIsUnavailable()
    {
        var reader = new SessionQuestionDetailReader(new RolloutEventParser());

        var result = await reader.ReadAsync("session-1", "missing.jsonl", "gpt-5.6-sol");

        Assert.False(result.SourceAvailable);
        Assert.Empty(result.Questions);
    }

    private static string TokenLine(string timestamp, long input, long cached, long output, long total)
    {
        var usage = new
        {
            input_tokens = input,
            cached_input_tokens = cached,
            output_tokens = output,
            total_tokens = total
        };
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            timestamp,
            type = "event_msg",
            payload = new
            {
                info = new
                {
                    total_token_usage = usage,
                    last_token_usage = usage,
                    model_context_window = 1000
                }
            }
        });
    }
}
