using System.Text;
using System.Text.Json;
using CodexTokenMonitor.Core.Models;
using CodexTokenMonitor.Core.Parsing;

namespace CodexTokenMonitor.Tests;

public sealed class RolloutEventParserTests
{
    private readonly RolloutEventParser _parser = new();

    [Fact]
    public void ParsesSessionMetadataFromWhitelistedFields()
    {
        var line = Encoding.UTF8.GetBytes("""
            {"timestamp":"2026-08-26T01:00:00Z","type":"session_meta","payload":{"id":"session-1","timestamp":"2026-08-26T00:59:00Z","thread_source":"desktop","base_instructions":{"provenance":{"model":"gpt-test"}},"unrelated_private_text":"must not be retained"}}
            """);

        var recognized = _parser.TryParse(line, out var parsed, out var errorCode);

        var metadata = Assert.IsType<ParsedSessionMetadata>(parsed);
        Assert.True(recognized);
        Assert.Null(errorCode);
        Assert.Equal("session-1", metadata.SessionId);
        Assert.Equal("gpt-test", metadata.Model);
        Assert.Equal("desktop", metadata.ThreadSource);
        Assert.Equal(DateTimeOffset.Parse("2026-08-26T00:59:00Z"), metadata.Timestamp);
    }

    [Fact]
    public void KeepsSessionMetadataWhenOptionalInstructionsUseAnOlderShape()
    {
        var line = Encoding.UTF8.GetBytes("{\"timestamp\":\"2026-08-26T01:00:00Z\",\"type\":\"session_meta\",\"payload\":{\"id\":\"legacy-session\",\"base_instructions\":\"legacy text\"}}");

        var recognized = _parser.TryParse(line, out var parsed, out var errorCode);

        var metadata = Assert.IsType<ParsedSessionMetadata>(parsed);
        Assert.True(recognized);
        Assert.Equal("legacy-session", metadata.SessionId);
        Assert.Null(metadata.Model);
        Assert.Null(errorCode);
    }

    [Fact]
    public void ResolvesProjectFromWorkingDirectoryAndGitRoot()
    {
        using var temporary = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(temporary.Path, ".git"));
        var child = Directory.CreateDirectory(Path.Combine(temporary.Path, "src", "feature")).FullName;
        var line = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            timestamp = "2026-08-26T01:00:00Z",
            type = "session_meta",
            payload = new { id = "project-session", cwd = child }
        }));

        var recognized = _parser.TryParse(line, out var parsed, out var errorCode);

        var metadata = Assert.IsType<ParsedSessionMetadata>(parsed);
        Assert.True(recognized);
        Assert.Null(errorCode);
        Assert.Equal(temporary.Path, metadata.ProjectPath);
        Assert.Equal(new DirectoryInfo(temporary.Path).Name, metadata.ProjectName);
        Assert.Equal("git", metadata.ProjectStatus);
    }

    [Fact]
    public void ResolvesLinkedWorktreeToItsCommonGitRepository()
    {
        using var temporary = new TemporaryDirectory();
        var repository = Directory.CreateDirectory(Path.Combine(temporary.Path, "repository"));
        var commonGitDirectory = Directory.CreateDirectory(Path.Combine(repository.FullName, ".git"));
        var worktreeGitDirectory = Directory.CreateDirectory(Path.Combine(commonGitDirectory.FullName, "worktrees", "feature"));
        File.WriteAllText(Path.Combine(worktreeGitDirectory.FullName, "commondir"), "../..");
        var worktree = Directory.CreateDirectory(Path.Combine(temporary.Path, "worktree"));
        File.WriteAllText(Path.Combine(worktree.FullName, ".git"), $"gitdir: {worktreeGitDirectory.FullName}");
        var child = Directory.CreateDirectory(Path.Combine(worktree.FullName, "src")).FullName;
        var line = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            timestamp = "2026-08-31T01:00:00Z",
            type = "session_meta",
            payload = new { id = "worktree-session", cwd = child }
        }));

        var recognized = _parser.TryParse(line, out var parsed, out var errorCode);

        var metadata = Assert.IsType<ParsedSessionMetadata>(parsed);
        Assert.True(recognized);
        Assert.Null(errorCode);
        Assert.Equal(repository.FullName, metadata.ProjectPath);
        Assert.Equal(repository.Name, metadata.ProjectName);
        Assert.Equal("git", metadata.ProjectStatus);
    }

    [Fact]
    public void DistinguishesExistingNonGitAndMissingWorkingDirectories()
    {
        using var temporary = new TemporaryDirectory();
        var existing = Directory.CreateDirectory(Path.Combine(temporary.Path, "plain"));
        var missing = Path.Combine(temporary.Path, "deleted-worktree");

        ParsedSessionMetadata Parse(string sessionId, string cwd)
        {
            var line = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                timestamp = "2026-08-31T01:00:00Z",
                type = "session_meta",
                payload = new { id = sessionId, cwd }
            }));
            Assert.True(_parser.TryParse(line, out var parsed, out var errorCode));
            Assert.Null(errorCode);
            return Assert.IsType<ParsedSessionMetadata>(parsed);
        }

        var directoryMetadata = Parse("directory-session", existing.FullName);
        var pendingMetadata = Parse("pending-session", missing);

        Assert.Equal("directory", directoryMetadata.ProjectStatus);
        Assert.Equal(existing.FullName, directoryMetadata.ProjectPath);
        Assert.Equal("pending", pendingMetadata.ProjectStatus);
        Assert.Equal(missing, pendingMetadata.ProjectPath);
    }

    [Fact]
    public void ParsesTokenUsageWithoutDoubleCountingBreakdowns()
    {
        var line = Encoding.UTF8.GetBytes("""
            {"timestamp":"2026-08-26T01:00:00Z","type":"event_msg","payload":{"info":{"total_token_usage":{"input_tokens":1000,"cached_input_tokens":800,"cache_write_input_tokens":0,"output_tokens":200,"reasoning_output_tokens":50,"total_tokens":1200},"last_token_usage":{"input_tokens":600,"cached_input_tokens":500,"cache_write_input_tokens":0,"output_tokens":100,"reasoning_output_tokens":25,"total_tokens":700},"model_context_window":10000}}}
            """);

        var recognized = _parser.TryParse(line, out var parsed, out var errorCode);

        var tokenEvent = Assert.IsType<ParsedTokenEvent>(parsed);
        Assert.True(recognized);
        Assert.Null(errorCode);
        Assert.Equal(1200, tokenEvent.TotalUsage.TotalTokens);
        Assert.Equal(1000, tokenEvent.TotalUsage.InputTokens);
        Assert.Equal(200, tokenEvent.TotalUsage.OutputTokens);
        Assert.Equal(800, tokenEvent.TotalUsage.CachedInputTokens);
        Assert.Equal(50, tokenEvent.TotalUsage.ReasoningOutputTokens);
        Assert.Equal(10000, tokenEvent.ModelContextWindow);
    }

    [Fact]
    public void TreatsNullOptionalBreakdownsAsZero()
    {
        var line = Encoding.UTF8.GetBytes("{\"timestamp\":\"2026-08-26T01:00:00Z\",\"type\":\"event_msg\",\"payload\":{\"info\":{\"total_token_usage\":{\"input_tokens\":10,\"cached_input_tokens\":null,\"output_tokens\":2,\"reasoning_output_tokens\":null,\"total_tokens\":12},\"last_token_usage\":{\"input_tokens\":10,\"output_tokens\":2,\"total_tokens\":12},\"model_context_window\":null}}}");

        var recognized = _parser.TryParse(line, out var parsed, out var errorCode);

        var tokenEvent = Assert.IsType<ParsedTokenEvent>(parsed);
        Assert.True(recognized);
        Assert.Equal(0, tokenEvent.TotalUsage.CachedInputTokens);
        Assert.Equal(0, tokenEvent.TotalUsage.ReasoningOutputTokens);
        Assert.Equal(0, tokenEvent.ModelContextWindow);
        Assert.Null(errorCode);
    }

    [Fact]
    public void IgnoresLinesWithoutMonitoredMarkers()
    {
        var line = Encoding.UTF8.GetBytes("{\"type\":\"response_item\",\"payload\":{\"content\":\"private\"}}");

        var recognized = _parser.TryParse(line, out var parsed, out var errorCode);

        Assert.False(recognized);
        Assert.Null(parsed);
        Assert.Null(errorCode);
    }

    [Fact]
    public void IgnoresMonitoredMarkersQuotedInsideAnUnrelatedEvent()
    {
        var line = Encoding.UTF8.GetBytes("{\"type\":\"response_item\",\"payload\":{\"content\":\"total_token_usage\"}}");

        var recognized = _parser.TryParse(line, out var parsed, out var errorCode);

        Assert.False(recognized);
        Assert.Null(parsed);
        Assert.Null(errorCode);
    }

    [Fact]
    public void IgnoresEventMessagesWhoseInfoIsNotATokenObject()
    {
        var line = Encoding.UTF8.GetBytes("{\"type\":\"event_msg\",\"payload\":{\"info\":\"total_token_usage\"}}");

        var recognized = _parser.TryParse(line, out var parsed, out var errorCode);

        Assert.False(recognized);
        Assert.Null(parsed);
        Assert.Null(errorCode);
    }

    [Fact]
    public void ReportsCandidateLinesWithInvalidJsonWithoutEchoingContent()
    {
        var line = Encoding.UTF8.GetBytes("{\"total_token_usage\":");

        var recognized = _parser.TryParse(line, out var parsed, out var errorCode);

        Assert.False(recognized);
        Assert.Null(parsed);
        Assert.Equal("invalid_json", errorCode);
    }

    [Fact]
    public void IgnoresCandidateMarkersInsideANonEventJsonValue()
    {
        var line = Encoding.UTF8.GetBytes("[\"total_token_usage\"]");

        var recognized = _parser.TryParse(line, out var parsed, out var errorCode);

        Assert.False(recognized);
        Assert.Null(parsed);
        Assert.Null(errorCode);
    }
}
