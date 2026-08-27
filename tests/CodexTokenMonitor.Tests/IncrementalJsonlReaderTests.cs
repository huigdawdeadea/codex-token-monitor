using System.Text;
using CodexTokenMonitor.Core.Parsing;

namespace CodexTokenMonitor.Tests;

public sealed class IncrementalJsonlReaderTests
{
    [Fact]
    public async Task CommitsOnlyCompleteLinesAndReplaysAPartialTail()
    {
        using var temporary = new TemporaryDirectory();
        var path = Path.Combine(temporary.Path, "rollout.jsonl");
        await File.WriteAllTextAsync(path, "first\npart", Encoding.UTF8);
        var reader = new IncrementalJsonlReader();
        var lines = new List<string>();

        var first = await reader.ReadAsync(path, 0, line => lines.Add(Encoding.UTF8.GetString(line.Span)));

        Assert.Equal(["first"], lines);
        var firstNewlineOffset = Array.IndexOf(await File.ReadAllBytesAsync(path), (byte)'\n') + 1;
        Assert.Equal(firstNewlineOffset, first.NextOffset);

        await File.AppendAllTextAsync(path, "ial\n", Encoding.UTF8);
        lines.Clear();
        var second = await reader.ReadAsync(path, first.NextOffset, line => lines.Add(Encoding.UTF8.GetString(line.Span)));

        Assert.Equal(["partial"], lines);
        Assert.Equal(new FileInfo(path).Length, second.NextOffset);
    }

    [Fact]
    public async Task ResetsAnOffsetBeyondTheCurrentFileLength()
    {
        using var temporary = new TemporaryDirectory();
        var path = Path.Combine(temporary.Path, "rollout.jsonl");
        await File.WriteAllTextAsync(path, "line\n", Encoding.UTF8);
        var lines = new List<string>();

        var result = await new IncrementalJsonlReader()
            .ReadAsync(path, 10_000, line => lines.Add(Encoding.UTF8.GetString(line.Span)));

        Assert.True(result.OffsetWasReset);
        Assert.Equal(["line"], lines);
    }

    [Fact]
    public async Task SkipsOversizedLinesWithoutKeepingTheirBytes()
    {
        using var temporary = new TemporaryDirectory();
        var path = Path.Combine(temporary.Path, "rollout.jsonl");
        await File.WriteAllTextAsync(path, "123456789\nok\n", Encoding.UTF8);
        var lines = new List<string>();

        var result = await new IncrementalJsonlReader(maxLineBytes: 4)
            .ReadAsync(path, 0, line => lines.Add(Encoding.UTF8.GetString(line.Span)));

        Assert.Equal(1, result.OversizedLines);
        Assert.Equal(["ok"], lines);
    }
}
