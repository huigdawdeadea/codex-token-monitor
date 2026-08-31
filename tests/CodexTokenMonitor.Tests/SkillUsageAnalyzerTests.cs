using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexTokenMonitor.Core.Models;
using CodexTokenMonitor.Core.Monitoring;
using CodexTokenMonitor.Core.Parsing;

namespace CodexTokenMonitor.Tests;

public sealed class SkillUsageAnalyzerTests
{
    [Fact]
    public async Task AttributesUniqueModelCallsToExclusiveAndCollaborationBuckets()
    {
        using var temporary = new TemporaryDirectory();
        var firstPath = Path.Combine(temporary.Path, "first", "SKILL.md");
        var secondPath = Path.Combine(temporary.Path, "second", "SKILL.md");
        Directory.CreateDirectory(Path.GetDirectoryName(firstPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(secondPath)!);
        var firstContent = "---\nname: first\n---\nFirst instructions.";
        var secondContent = "---\nname: second\n---\nSecond instructions.";
        var referenceContent = "Reference material loaded after the skill entrypoint.";
        var referencePath = Path.Combine(temporary.Path, "first", "references", "guide.md");
        Directory.CreateDirectory(Path.GetDirectoryName(referencePath)!);
        await File.WriteAllTextAsync(firstPath, firstContent);
        await File.WriteAllTextAsync(secondPath, secondContent);
        await File.WriteAllTextAsync(referencePath, referenceContent);
        var counter = new SkillTokenCounter();
        var definitions = new[]
        {
            Definition("first", firstPath, firstContent, counter),
            Definition("second", secondPath, secondContent, counter)
        };
        var rollout = Path.Combine(temporary.Path, "rollout.jsonl");
        var lines = new[]
        {
            Line("session_meta", new { id = "session-1" }),
            Line("turn_context", new { turn_id = "task-1", model = "gpt-5.6-sol" }),
            Line("response_item", new { type = "custom_tool_call", call_id = "call-1", input = $"Get-Content -Raw -LiteralPath '{firstPath}'" }),
            Line("response_item", new { type = "custom_tool_call_output", call_id = "call-1", output = new[] { new { type = "input_text", text = firstContent } } }),
            Line("response_item", new { type = "custom_tool_call", call_id = "call-reference", input = $"Get-Content -Raw -LiteralPath '{referencePath}'" }),
            Line("response_item", new { type = "custom_tool_call_output", call_id = "call-reference", output = new[] { new { type = "input_text", text = referenceContent } } }),
            TokenLine("2026-08-30T01:00:01Z", 100, 100),
            TokenLine("2026-08-30T01:00:02Z", 100, 100),
            Line("response_item", new { type = "custom_tool_call", call_id = "call-2", input = $"Get-Content -Raw -LiteralPath '{secondPath}'" }),
            Line("response_item", new { type = "custom_tool_call_output", call_id = "call-2", output = new[] { new { type = "input_text", text = secondContent } } }),
            TokenLine("2026-08-30T01:00:03Z", 250, 150),
            Line("event_msg", new { type = "task_complete", turn_id = "task-1" })
        };
        await File.WriteAllLinesAsync(rollout, lines);

        var analyzer = new SkillUsageAnalyzer(counter, new RolloutEventParser());
        var result = await analyzer.AnalyzeRolloutAsync(rollout, definitions);

        Assert.Equal(2, result.Invocations.Count);
        Assert.All(result.Invocations, item => Assert.Equal("high", item.Confidence));
        Assert.Equal(
            definitions[0].BaseTokens + counter.Count(referenceContent).Tokens,
            Assert.Single(result.Invocations, item => item.SkillName == "first").LoadedTokens);
        Assert.Equal(
            definitions[1].BaseTokens,
            Assert.Single(result.Invocations, item => item.SkillName == "second").LoadedTokens);
        Assert.Equal(2, result.Attributions.Count);
        Assert.Equal("exclusive", result.Attributions[0].BucketType);
        Assert.Equal("first", result.Attributions[0].BucketKey);
        Assert.Equal(100, result.Attributions[0].Usage.TotalTokens);
        Assert.Equal("collaboration", result.Attributions[1].BucketType);
        Assert.Equal("first + second", result.Attributions[1].BucketKey);
        Assert.Equal(150, result.Attributions[1].Usage.TotalTokens);
    }

    private static SkillDefinitionSnapshot Definition(string name, string path, string content, SkillTokenCounter counter)
    {
        var count = counter.Count(content);
        return new SkillDefinitionSnapshot(
            name, path, "test", true,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))),
            Encoding.UTF8.GetByteCount(content), content.Length, count.Tokens,
            count.Tokenizer, count.Version, count.IsEstimated, DateTimeOffset.UtcNow);
    }

    private static string Line(string type, object payload) => JsonSerializer.Serialize(new
    {
        timestamp = "2026-08-30T01:00:00Z",
        type,
        payload
    });

    private static string TokenLine(string timestamp, long total, long last) => JsonSerializer.Serialize(new
    {
        timestamp,
        type = "event_msg",
        payload = new
        {
            type = "token_count",
            info = new
            {
                total_token_usage = Usage(total),
                last_token_usage = Usage(last),
                model_context_window = 1000
            }
        }
    });

    private static object Usage(long total) => new
    {
        input_tokens = total,
        cached_input_tokens = 0,
        cache_write_input_tokens = 0,
        output_tokens = 0,
        reasoning_output_tokens = 0,
        total_tokens = total
    };
}
