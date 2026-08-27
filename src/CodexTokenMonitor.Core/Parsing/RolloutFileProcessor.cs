using CodexTokenMonitor.Core.Models;

namespace CodexTokenMonitor.Core.Parsing;

public sealed class RolloutFileProcessor(
    IncrementalJsonlReader reader,
    RolloutEventParser parser)
{
    public async Task<RolloutFileResult> ProcessAsync(
        string path,
        FileCheckpoint? checkpoint,
        CancellationToken cancellationToken = default)
    {
        ParsedSessionMetadata? metadata = null;
        ParsedTokenEvent? latestTokenEvent = null;
        var tokenEvents = new List<ParsedTokenEvent>();
        var sessionId = checkpoint?.SessionId;
        var recognizedEvents = 0;
        var parseErrors = 0;
        string? lastErrorCode = null;

        var readResult = await reader.ReadAsync(
            path,
            checkpoint?.Offset ?? 0,
            line =>
            {
                if (!parser.TryParse(line, out var parsedEvent, out var errorCode))
                {
                    if (errorCode is not null)
                    {
                        parseErrors++;
                        lastErrorCode = errorCode;
                    }

                    return;
                }

                recognizedEvents++;
                switch (parsedEvent)
                {
                    case ParsedSessionMetadata sessionMetadata:
                        metadata = sessionMetadata;
                        sessionId = sessionMetadata.SessionId;
                        break;
                    case ParsedTokenEvent tokenEvent:
                        latestTokenEvent = tokenEvent;
                        tokenEvents.Add(tokenEvent);
                        break;
                }
            },
            cancellationToken);

        return new RolloutFileResult(
            path,
            readResult.NextOffset,
            readResult.OffsetWasReset,
            sessionId,
            metadata,
            latestTokenEvent,
            readResult.CompleteLines,
            recognizedEvents,
            parseErrors,
            readResult.OversizedLines,
            lastErrorCode,
            tokenEvents);
    }
}
