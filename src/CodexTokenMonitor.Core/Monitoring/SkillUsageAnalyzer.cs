using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodexTokenMonitor.Core.Models;
using CodexTokenMonitor.Core.Parsing;

namespace CodexTokenMonitor.Core.Monitoring;

public sealed partial class SkillUsageAnalyzer(
    SkillTokenCounter tokenCounter,
    RolloutEventParser tokenParser,
    IncrementalJsonlReader? incrementalReader = null)
{
    public const int ParserVersion = 3;
    private readonly IncrementalJsonlReader _incrementalReader = incrementalReader ?? new IncrementalJsonlReader();
    private static readonly JsonSerializerOptions StateJsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<SkillDefinitionSnapshot>> ScanDefinitionsAsync(
        string codexHome,
        CancellationToken cancellationToken = default)
    {
        var profile = Directory.GetParent(codexHome)?.FullName ?? codexHome;
        var availablePaths = await ReadLatestAvailableSkillPathsAsync(codexHome, cancellationToken);
        var hasAvailabilitySnapshot = availablePaths.Count > 0;
        var roots = new[]
        {
            (Path.Combine(codexHome, "skills"), "codex", true),
            (Path.Combine(profile, ".agents", "skills"), "agent", true),
            (Path.Combine(codexHome, "plugins", "cache"), "plugin", false),
            (Path.Combine(codexHome, "disabled-skills"), "disabled", false),
            (Path.Combine(codexHome, "skill-archive"), "archive", false)
        };
        var observedAt = DateTimeOffset.UtcNow;
        var snapshots = new List<SkillDefinitionSnapshot>();
        foreach (var (root, source, active) in roots)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var path in Directory.EnumerateFiles(root, "SKILL.md", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var content = await File.ReadAllTextAsync(path, cancellationToken);
                    var count = tokenCounter.Count(content);
                    snapshots.Add(new SkillDefinitionSnapshot(
                        ReadSkillName(content) ?? Directory.GetParent(path)?.Name ?? "unknown",
                        Path.GetFullPath(path),
                        source,
                        hasAvailabilitySnapshot ? availablePaths.Contains(NormalizePath(path)) : active,
                        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))),
                        Encoding.UTF8.GetByteCount(content),
                        content.Length,
                        count.Tokens,
                        count.Tokenizer,
                        count.Version,
                        count.IsEstimated,
                        observedAt));
                }
                catch (IOException)
                {
                    // A plugin cache can rotate while it is being scanned.
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }

        return snapshots
            .GroupBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    private static async Task<HashSet<string>> ReadLatestAvailableSkillPathsAsync(
        string codexHome,
        CancellationToken cancellationToken)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sessionsRoot = Path.Combine(codexHome, "sessions");
        if (!Directory.Exists(sessionsRoot))
        {
            return result;
        }

        foreach (var rolloutPath in Directory.EnumerateFiles(sessionsRoot, "rollout-*.jsonl", SearchOption.AllDirectories)
            .OrderByDescending(path => File.GetLastWriteTimeUtc(path))
            .Take(12))
        {
            string? latestInstructions = null;
            try
            {
                await using var stream = new FileStream(rolloutPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                while (!reader.EndOfStream)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var line = await reader.ReadLineAsync(cancellationToken);
                    if (line is null || !line.Contains("<skills_instructions>", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    using var document = JsonDocument.Parse(line);
                    if (!document.RootElement.TryGetProperty("payload", out var payload) ||
                        !payload.TryGetProperty("content", out var content))
                    {
                        continue;
                    }

                    var text = ReadText(content);
                    if (text.Contains("<skills_instructions>", StringComparison.Ordinal))
                    {
                        latestInstructions = text;
                        break;
                    }
                }
            }
            catch (JsonException)
            {
            }
            catch (IOException)
            {
            }

            if (latestInstructions is null)
            {
                continue;
            }

            var roots = SkillRootRegex().Matches(latestInstructions)
                .ToDictionary(match => match.Groups[1].Value, match => match.Groups[2].Value, StringComparer.OrdinalIgnoreCase);
            foreach (Match match in SkillFileRegex().Matches(latestInstructions))
            {
                var value = match.Groups[1].Value.Trim();
                var separator = value.IndexOf('/');
                if (separator > 0 && roots.TryGetValue(value[..separator], out var root))
                {
                    value = Path.Combine(root, value[(separator + 1)..].Replace('/', Path.DirectorySeparatorChar));
                }

                if (Path.IsPathFullyQualified(value))
                {
                    result.Add(NormalizePath(value));
                }
            }

            if (result.Count > 0)
            {
                return result;
            }
        }

        return result;
    }

    public async Task<SkillRolloutAnalysis> AnalyzeRolloutAsync(
        string rolloutPath,
        IReadOnlyList<SkillDefinitionSnapshot> definitions,
        CancellationToken cancellationToken = default)
    {
        var delta = await AnalyzeRolloutIncrementAsync(rolloutPath, definitions, null, cancellationToken);
        return new SkillRolloutAnalysis(
            delta.RolloutPath,
            delta.Length,
            delta.LastWriteUtcTicks,
            delta.Invocations,
            delta.Attributions);
    }

    public async Task<SkillRolloutDelta> AnalyzeRolloutIncrementAsync(
        string rolloutPath,
        IReadOnlyList<SkillDefinitionSnapshot> definitions,
        SkillScanCheckpoint? checkpoint,
        CancellationToken cancellationToken = default)
    {
        var definitionsByPath = definitions.ToDictionary(
            item => NormalizePath(item.Path),
            StringComparer.OrdinalIgnoreCase);
        var state = ReadState(checkpoint?.StateJson);
        var changedInvocations = new Dictionary<string, InvocationBuilder>(StringComparer.Ordinal);
        var attributions = new List<SkillAttributionSnapshot>();
        var pendingCalls = state.PendingCalls.ToDictionary(
            item => item.CallId,
            item => new PendingSkillRead(
                item.SkillPath,
                item.ReferenceSkillName is not null && state.Active.TryGetValue(item.ReferenceSkillName, out var target)
                    ? target
                    : null),
            StringComparer.Ordinal);
        var requestedOffset = checkpoint is null || checkpoint.ParserVersion != ParserVersion ? 0 : checkpoint.Offset;
        var replaceExisting = checkpoint is null || checkpoint.ParserVersion != ParserVersion;
        var read = await _incrementalReader.ReadAsync(
            rolloutPath,
            requestedOffset,
            line => ProcessLine(line),
            cancellationToken);
        replaceExisting |= read.OffsetWasReset;
        var info = new FileInfo(rolloutPath);
        state.PendingCalls = pendingCalls.Select(item => new PendingCallState(
            item.Key,
            item.Value.SkillPath,
            item.Value.ReferenceTarget?.SkillName)).ToList();
        return new SkillRolloutDelta(
            rolloutPath,
            read.NextOffset,
            info.Length,
            info.LastWriteTimeUtc.Ticks,
            JsonSerializer.Serialize(state.ToPersisted(), StateJsonOptions),
            replaceExisting,
            read.CompleteLines,
            read.OversizedLines,
            changedInvocations.Values.Select(item => item.ToSnapshot()).ToArray(),
            attributions);

        void ProcessLine(ReadOnlyMemory<byte> line)
        {
            if (line.IsEmpty)
            {
                return;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (!root.TryGetProperty("type", out var typeElement) ||
                    !root.TryGetProperty("payload", out var payload))
                {
                    return;
                }

                var type = typeElement.GetString();
                var payloadType = GetString(payload, "type");
                var timestamp = ParseTimestamp(root);
                if (type == "session_meta")
                {
                    state.SessionId = GetString(payload, "session_id") ?? GetString(payload, "id") ?? state.SessionId;
                    return;
                }

                if (type == "turn_context")
                {
                    EndTask(timestamp);
                    state.CurrentTask = GetString(payload, "turn_id") ?? state.CurrentTask;
                    state.CurrentModel = GetString(payload, "model") ?? state.CurrentModel;
                    state.PreviousTotal = null;
                    return;
                }

                if (type == "event_msg" && payloadType == "task_started")
                {
                    EndTask(timestamp);
                    state.CurrentTask = GetString(payload, "turn_id") ?? state.CurrentTask;
                    state.PreviousTotal = null;
                    return;
                }

                if (type == "event_msg" && payloadType == "task_complete")
                {
                    EndTask(timestamp);
                    state.PreviousTotal = null;
                    return;
                }

                if (type == "response_item" && payloadType == "custom_tool_call")
                {
                    var callId = GetString(payload, "call_id");
                    var input = GetString(payload, "input");
                    var path = FindKnownSkillPath(input, definitionsByPath.Keys);
                    if (callId is not null && path is not null)
                    {
                        pendingCalls[callId] = new PendingSkillRead(path, null);
                        state.CandidateSkillTask = true;
                    }
                    else if (callId is not null && FindActiveReferenceTarget(input, state.Active.Values) is { } target)
                    {
                        pendingCalls[callId] = new PendingSkillRead(null, target);
                    }

                    return;
                }

                if (type == "response_item" && payloadType == "custom_tool_call_output")
                {
                    var callId = GetString(payload, "call_id");
                    if (callId is not null && pendingCalls.Remove(callId, out var pendingRead))
                    {
                        var output = ReadToolOutputContent(payload.TryGetProperty("output", out var value) ? value : default);
                        if (pendingRead.SkillPath is not null &&
                            definitionsByPath.TryGetValue(pendingRead.SkillPath, out var definition))
                        {
                            var highConfidence = output.Contains("name:", StringComparison.OrdinalIgnoreCase) &&
                                output.Contains(definition.Name, StringComparison.OrdinalIgnoreCase);
                            var builder = new InvocationBuilder(
                                state.SessionId,
                                rolloutPath,
                                state.CurrentTask,
                                definition,
                                highConfidence ? "high" : "medium",
                                timestamp,
                                state.CurrentModel,
                                highConfidence ? definition.BaseTokens : 0,
                                highConfidence ? "complete_skill_read" : "verified_skill_path");
                            state.Active[definition.Name] = builder;
                            TrackChanged(builder);
                        }
                        else if (pendingRead.ReferenceTarget is not null && output.Length > 0)
                        {
                            pendingRead.ReferenceTarget.AddLoadedTokens(tokenCounter.Count(output).Tokens);
                            TrackChanged(pendingRead.ReferenceTarget);
                        }
                    }

                    return;
                }

                if (line.Span.IndexOf("\"total_token_usage\""u8) >= 0 &&
                    tokenParser.TryParse(line, out var parsed, out _) &&
                    parsed is ParsedTokenEvent tokenEvent)
                {
                    if (state.PreviousTotal == tokenEvent.TotalUsage)
                    {
                        return;
                    }

                    var monotonic = state.PreviousTotal is null || IsMonotonic(state.PreviousTotal, tokenEvent.TotalUsage);
                    state.PreviousTotal = tokenEvent.TotalUsage;
                    if (state.Active.Count == 0 && !state.CandidateSkillTask)
                    {
                        return;
                    }

                    var names = state.Active.Keys.Order(StringComparer.OrdinalIgnoreCase).ToArray();
                    var bucketType = names.Length switch
                    {
                        0 => "unattributed",
                        1 => "exclusive",
                        _ => "collaboration"
                    };
                    var bucketKey = names.Length == 0 ? "unattributed" : string.Join(" + ", names);
                    var confidence = !monotonic || state.Active.Values.Any(item => item.Confidence != "high")
                        ? "medium"
                        : "high";
                    attributions.Add(new SkillAttributionSnapshot(
                        state.SessionId,
                        rolloutPath,
                        state.CurrentTask,
                        tokenEvent.Timestamp,
                        state.CurrentModel,
                        bucketType,
                        bucketKey,
                        string.Join("|", names),
                        confidence,
                        tokenEvent.LastUsage));
                }
            }
            catch (JsonException)
            {
                // A malformed complete line is ignored without losing the following append checkpoint.
            }
        }

        void EndTask(DateTimeOffset endedAt)
        {
            foreach (var invocation in state.Active.Values)
            {
                invocation.EndedAt ??= endedAt;
                TrackChanged(invocation);
            }

            state.Active.Clear();
            pendingCalls.Clear();
            state.CandidateSkillTask = false;
        }

        void TrackChanged(InvocationBuilder invocation) => changedInvocations[invocation.Identity] = invocation;
    }

    private static ParserState ReadState(string? json)
    {
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                var persisted = JsonSerializer.Deserialize<PersistedParserState>(json, StateJsonOptions);
                if (persisted is not null)
                {
                    return new ParserState(persisted);
                }
            }
            catch (JsonException)
            {
            }
        }

        return new ParserState();
    }

    private static bool IsMonotonic(TokenUsage previous, TokenUsage current) =>
        current.InputTokens >= previous.InputTokens &&
        current.OutputTokens >= previous.OutputTokens &&
        current.TotalTokens >= previous.TotalTokens;

    private static string? FindKnownSkillPath(string? input, IEnumerable<string> knownPaths)
    {
        if (string.IsNullOrWhiteSpace(input) || !input.Contains("SKILL.md", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var normalizedInput = input.Replace("/", "\\", StringComparison.Ordinal);
        return knownPaths.FirstOrDefault(path =>
            normalizedInput.Contains(path, StringComparison.OrdinalIgnoreCase) ||
            normalizedInput.Contains(path.Replace("\\", "\\\\", StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase));
    }

    private static InvocationBuilder? FindActiveReferenceTarget(
        string? input,
        IEnumerable<InvocationBuilder> activeInvocations)
    {
        if (string.IsNullOrWhiteSpace(input) ||
            (!input.Contains("Get-Content", StringComparison.OrdinalIgnoreCase) &&
             !input.Contains("ReadAllText", StringComparison.OrdinalIgnoreCase) &&
             !input.Contains("read_file", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var normalizedInput = input.Replace('/', '\\');
        foreach (var invocation in activeInvocations)
        {
            var directory = Path.GetDirectoryName(invocation.SkillPath);
            if (directory is null ||
                normalizedInput.Contains(invocation.SkillPath, StringComparison.OrdinalIgnoreCase) ||
                normalizedInput.Contains(invocation.SkillPath.Replace("\\", "\\\\", StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (normalizedInput.Contains(directory + "\\", StringComparison.OrdinalIgnoreCase) ||
                normalizedInput.Contains((directory + "\\").Replace("\\", "\\\\", StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase))
            {
                return invocation;
            }
        }

        return null;
    }

    private static string ReadText(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            return element.GetString() ?? "";
        }

        if (element.ValueKind is not (JsonValueKind.Array or JsonValueKind.Object))
        {
            return "";
        }

        var builder = new StringBuilder();
        foreach (var child in element.ValueKind == JsonValueKind.Array
            ? element.EnumerateArray()
            : element.EnumerateObject().Select(property => property.Value))
        {
            builder.Append(ReadText(child));
        }

        return builder.ToString();
    }

    private static string ReadToolOutputContent(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            return element.GetString() ?? "";
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            return string.Concat(element.EnumerateArray().Select(ReadToolOutputContent));
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return "";
        }

        foreach (var propertyName in new[] { "text", "output", "content" })
        {
            if (element.TryGetProperty(propertyName, out var value))
            {
                return ReadToolOutputContent(value);
            }
        }

        return ReadText(element);
    }

    private static DateTimeOffset ParseTimestamp(JsonElement root) =>
        DateTimeOffset.TryParse(GetString(root, "timestamp"), out var value) ? value : DateTimeOffset.UtcNow;

    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string NormalizePath(string path) => Path.GetFullPath(path).Replace('/', '\\');

    private static string? ReadSkillName(string content)
    {
        var match = SkillNameRegex().Match(content);
        return match.Success ? match.Groups[1].Value.Trim().Trim('"', '\'') : null;
    }

    [GeneratedRegex("(?m)^name:\\s*(.+?)\\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex SkillNameRegex();

    [GeneratedRegex("(?m)^- `?(r\\d+)`? = `?([^`\\r\\n]+)`?\\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex SkillRootRegex();

    [GeneratedRegex("\\(file:\\s*([^\\)]+SKILL\\.md)\\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SkillFileRegex();

    private sealed record PendingSkillRead(string? SkillPath, InvocationBuilder? ReferenceTarget);

    private sealed record PendingCallState(string CallId, string? SkillPath, string? ReferenceSkillName);

    private sealed record PersistedParserState(
        string SessionId,
        string CurrentTask,
        string CurrentModel,
        bool CandidateSkillTask,
        TokenUsage? PreviousTotal,
        IReadOnlyList<SkillInvocationSnapshot> Active,
        IReadOnlyList<PendingCallState> PendingCalls);

    private sealed class ParserState
    {
        public ParserState()
        {
        }

        public ParserState(PersistedParserState state)
        {
            SessionId = state.SessionId;
            CurrentTask = state.CurrentTask;
            CurrentModel = state.CurrentModel;
            CandidateSkillTask = state.CandidateSkillTask;
            PreviousTotal = state.PreviousTotal;
            Active = state.Active.ToDictionary(item => item.SkillName, item => new InvocationBuilder(item), StringComparer.OrdinalIgnoreCase);
            PendingCalls = state.PendingCalls.ToList();
        }

        public string SessionId { get; set; } = "unknown";
        public string CurrentTask { get; set; } = "unknown";
        public string CurrentModel { get; set; } = "unknown";
        public bool CandidateSkillTask { get; set; }
        public TokenUsage? PreviousTotal { get; set; }
        public Dictionary<string, InvocationBuilder> Active { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public List<PendingCallState> PendingCalls { get; set; } = [];

        public PersistedParserState ToPersisted() => new(
            SessionId,
            CurrentTask,
            CurrentModel,
            CandidateSkillTask,
            PreviousTotal,
            Active.Values.Select(item => item.ToSnapshot()).ToArray(),
            PendingCalls);
    }

    private sealed class InvocationBuilder
    {
        private readonly string _sessionId;
        private readonly string _rolloutPath;
        private readonly string _taskId;
        private readonly SkillDefinitionSnapshot _definition;
        private readonly DateTimeOffset _startedAt;
        private readonly string _model;
        private long _loadedTokens;
        private readonly string _evidenceKind;

        public InvocationBuilder(
            string sessionId,
            string rolloutPath,
            string taskId,
            SkillDefinitionSnapshot definition,
            string confidence,
            DateTimeOffset startedAt,
            string model,
            long loadedTokens,
            string evidenceKind)
        {
            _sessionId = sessionId;
            _rolloutPath = rolloutPath;
            _taskId = taskId;
            _definition = definition;
            Confidence = confidence;
            _startedAt = startedAt;
            _model = model;
            _loadedTokens = loadedTokens;
            _evidenceKind = evidenceKind;
        }

        public InvocationBuilder(SkillInvocationSnapshot snapshot)
        {
            _sessionId = snapshot.SessionId;
            _rolloutPath = snapshot.RolloutPath;
            _taskId = snapshot.TaskId;
            _definition = new SkillDefinitionSnapshot(
                snapshot.SkillName,
                snapshot.SkillPath,
                "persisted",
                true,
                snapshot.VersionHash,
                0,
                0,
                snapshot.LoadedTokens,
                "persisted",
                "persisted",
                false,
                snapshot.StartedAt);
            Confidence = snapshot.Confidence;
            _startedAt = snapshot.StartedAt;
            _model = snapshot.Model;
            _loadedTokens = snapshot.LoadedTokens;
            _evidenceKind = snapshot.EvidenceKind;
            EndedAt = snapshot.EndedAt;
        }

        public string Confidence { get; }
        public string SkillName => _definition.Name;
        public string SkillPath => _definition.Path;
        public string Identity => string.Join("|", _rolloutPath, _taskId, _definition.Name, _startedAt.ToString("O"));
        public DateTimeOffset? EndedAt { get; set; }

        public void AddLoadedTokens(long tokens) => _loadedTokens += Math.Max(tokens, 0);

        public SkillInvocationSnapshot ToSnapshot() => new(
            _sessionId,
            _rolloutPath,
            _taskId,
            _definition.Name,
            _definition.Path,
            _definition.ContentHash,
            Confidence,
            _startedAt,
            EndedAt,
            _model,
            _loadedTokens,
            _evidenceKind);
    }
}
