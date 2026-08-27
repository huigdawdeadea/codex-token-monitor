using System.Buffers;

namespace CodexTokenMonitor.Core.Parsing;

public sealed record IncrementalReadResult(
    long NextOffset,
    bool OffsetWasReset,
    int CompleteLines,
    int OversizedLines);

public sealed class IncrementalJsonlReader(int maxLineBytes = 16 * 1024 * 1024)
{
    private readonly int _maxLineBytes = maxLineBytes > 0
        ? maxLineBytes
        : throw new ArgumentOutOfRangeException(nameof(maxLineBytes));

    public async Task<IncrementalReadResult> ReadAsync(
        string path,
        long requestedOffset,
        Action<ReadOnlyMemory<byte>> onCompleteLine,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(onCompleteLine);

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var offsetWasReset = requestedOffset < 0 || requestedOffset > stream.Length;
        var offset = offsetWasReset ? 0 : requestedOffset;
        stream.Seek(offset, SeekOrigin.Begin);

        var rented = ArrayPool<byte>.Shared.Rent(64 * 1024);
        using var lineBuffer = new MemoryStream();
        var nextOffset = offset;
        var absolutePosition = offset;
        var completeLines = 0;
        var oversizedLines = 0;
        var oversized = false;

        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(rented.AsMemory(0, rented.Length), cancellationToken);
                if (read == 0)
                {
                    break;
                }

                var segmentStart = 0;
                for (var index = 0; index < read; index++)
                {
                    if (rented[index] != (byte)'\n')
                    {
                        continue;
                    }

                    AppendSegment(rented, segmentStart, index - segmentStart, lineBuffer, ref oversized);
                    if (oversized)
                    {
                        oversizedLines++;
                    }
                    else
                    {
                        var line = lineBuffer.ToArray();
                        var start = completeLines == 0 && offset == 0 && HasUtf8Bom(line) ? 3 : 0;
                        var length = line.Length > start && line[^1] == (byte)'\r'
                            ? line.Length - start - 1
                            : line.Length - start;
                        onCompleteLine(line.AsMemory(start, length));
                    }

                    completeLines++;
                    nextOffset = absolutePosition + index + 1;
                    lineBuffer.SetLength(0);
                    oversized = false;
                    segmentStart = index + 1;
                }

                AppendSegment(rented, segmentStart, read - segmentStart, lineBuffer, ref oversized);
                absolutePosition += read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }

        return new IncrementalReadResult(nextOffset, offsetWasReset, completeLines, oversizedLines);
    }

    private void AppendSegment(
        byte[] source,
        int offset,
        int count,
        MemoryStream destination,
        ref bool oversized)
    {
        if (count <= 0 || oversized)
        {
            return;
        }

        if (destination.Length + count > _maxLineBytes)
        {
            destination.SetLength(0);
            oversized = true;
            return;
        }

        destination.Write(source, offset, count);
    }

    private static bool HasUtf8Bom(byte[] value) =>
        value.Length >= 3 && value[0] == 0xEF && value[1] == 0xBB && value[2] == 0xBF;
}
