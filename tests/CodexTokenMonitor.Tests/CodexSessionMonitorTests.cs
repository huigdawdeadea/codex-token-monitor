using System.Text;
using System.Text.Json;
using CodexTokenMonitor.Core.Metadata;
using CodexTokenMonitor.Core.Models;
using CodexTokenMonitor.Core.Monitoring;
using CodexTokenMonitor.Core.Parsing;
using CodexTokenMonitor.Core.Storage;

namespace CodexTokenMonitor.Tests;

public sealed class CodexSessionMonitorTests
{
    [Fact]
    public async Task CoalescesConcurrentRefreshRequests()
    {
        using var temporary = new TemporaryDirectory();
        var codexHome = Path.Combine(temporary.Path, ".codex");
        var database = new MonitorDatabase(Path.Combine(temporary.Path, "monitor.db"));
        await using var monitor = new CodexSessionMonitor(
            codexHome,
            database,
            new RolloutFileProcessor(new IncrementalJsonlReader(), new RolloutEventParser()),
            new CodexMetadataReader(),
            new MonitorHealthTracker());

        Parallel.For(0, 10_000, _ => monitor.RequestRefresh());
    }

    [Fact]
    public async Task ImportsThenUpdatesOnANewTokenEvent()
    {
        using var temporary = new TemporaryDirectory();
        var codexHome = Path.Combine(temporary.Path, ".codex");
        var sessions = Path.Combine(codexHome, "sessions", "2026", "08", "26");
        Directory.CreateDirectory(sessions);
        var rollout = Path.Combine(sessions, "rollout-test.jsonl");
        await File.WriteAllTextAsync(
            rollout,
            SessionMetaLine() + "\n" + TokenLine(total: 50, last: 60) + "\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var database = new MonitorDatabase(Path.Combine(temporary.Path, "monitor.db"));
        await using var monitor = new CodexSessionMonitor(
            codexHome,
            database,
            new RolloutFileProcessor(new IncrementalJsonlReader(), new RolloutEventParser()),
            new CodexMetadataReader(),
            new MonitorHealthTracker(),
            scanInterval: TimeSpan.FromMilliseconds(40),
            metadataInterval: TimeSpan.FromHours(1));
        await monitor.StartAsync();
        await WaitUntilAsync(async () => await database.GetSessionAsync("session-1") is not null);

        await File.AppendAllTextAsync(
            rollout,
            TokenLine(total: 250, last: 90) + "\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        monitor.RequestRefresh();
        await WaitUntilAsync(async () =>
            (await database.GetSessionAsync("session-1"))?.TotalUsage.TotalTokens == 250);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail("The monitor did not reach the expected state before the timeout.");
    }

    private static string SessionMetaLine() =>
        "{\"timestamp\":\"2026-08-26T01:00:00Z\",\"type\":\"session_meta\",\"payload\":{\"id\":\"session-1\",\"timestamp\":\"2026-08-26T01:00:00Z\"}}";

    private static string TokenLine(long total, long last) => JsonSerializer.Serialize(new
    {
        timestamp = "2026-08-26T01:01:00Z",
        type = "event_msg",
        payload = new
        {
            info = new
            {
                total_token_usage = Usage(total),
                last_token_usage = Usage(last),
                model_context_window = 100
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
