using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using CodexTokenMonitor.Core.Models;

namespace CodexTokenMonitor.Core.Pricing;

public sealed class OpenAiOfficialPriceSource : IPriceSource, IDisposable
{
    public const string DefaultUrl = "https://developers.openai.com/api/docs/pricing.md";
    private const int MaximumResponseBytes = 2 * 1024 * 1024;
    private const string ParserVersionValue = "openai-pricing-markdown-v1";
    private readonly HttpClient _client;
    private readonly bool _ownsClient;

    public OpenAiOfficialPriceSource(bool enabled = true, HttpClient? client = null)
    {
        Enabled = enabled;
        _ownsClient = client is null;
        _client = client ?? CreateClient();
    }

    public string SourceId => "openai-official";

    public string SourceKind => PriceSourceKinds.Official;

    public string DisplayName => "OpenAI 官方价格";

    public string SourceUrl => DefaultUrl;

    public bool Enabled { get; }

    public string GateStatus => Enabled
        ? "verified_official_markdown_table_v1"
        : "disabled_by_operator";

    public async Task<PriceFetchResult> FetchAsync(
        IReadOnlyCollection<string> observedModels,
        CancellationToken cancellationToken = default)
    {
        if (!Enabled)
        {
            throw new InvalidOperationException("The OpenAI official price source is disabled.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, SourceUrl);
        request.Headers.UserAgent.ParseAdd("CodexTokenMonitor/1.0");
        using var response = await _client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new HttpRequestException(
                $"The official price source returned HTTP {(int)response.StatusCode}.",
                null,
                response.StatusCode);
        }

        var finalUri = response.RequestMessage?.RequestUri;
        if (finalUri is null || !finalUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !finalUri.Host.Equals("developers.openai.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The official price source redirected outside the approved host.");
        }

        if (response.Content.Headers.ContentLength is > MaximumResponseBytes)
        {
            throw new InvalidDataException("The official price source response exceeded the size limit.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);
        if (buffer.Length > MaximumResponseBytes)
        {
            throw new InvalidDataException("The official price source response exceeded the size limit.");
        }

        var content = buffer.ToArray();
        var retrievedAt = DateTimeOffset.UtcNow;
        var contentHash = Convert.ToHexString(SHA256.HashData(content));
        var revision = response.Headers.ETag?.Tag;
        var prices = Parse(content, observedModels, retrievedAt, contentHash, revision);
        return new PriceFetchResult(SourceId, retrievedAt, contentHash, revision, prices);
    }

    public IReadOnlyList<PriceCandidate> Parse(
        ReadOnlySpan<byte> utf8Markdown,
        IReadOnlyCollection<string> observedModels,
        DateTimeOffset retrievedAt,
        string contentHash,
        string? sourceRevision)
    {
        var markdown = Encoding.UTF8.GetString(utf8Markdown);
        var requested = observedModels
            .Where(model => !string.IsNullOrWhiteSpace(model))
            .Select(model => model.Trim().ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (requested.Count == 0)
        {
            return [];
        }

        var standardSectionStart = markdown.IndexOf("### Standard pricing data", StringComparison.OrdinalIgnoreCase);
        if (standardSectionStart < 0)
        {
            throw new InvalidDataException("The official price page did not contain standard pricing data.");
        }

        var section = markdown[standardSectionStart..];
        var nextHeading = section.IndexOf("\n### ", StringComparison.Ordinal);
        if (nextHeading >= 0)
        {
            section = section[..nextHeading];
        }

        var candidates = new Dictionary<string, PriceCandidate>(StringComparer.OrdinalIgnoreCase);
        var sawTable = false;
        foreach (var rawLine in section.Split('\n'))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith('|') || !line.EndsWith('|'))
            {
                continue;
            }

            var cells = line[1..^1]
                .Split('|')
                .Select(cell => cell.Trim())
                .ToArray();
            if (cells.Length >= 5 && cells[0].Equals("Model", StringComparison.OrdinalIgnoreCase))
            {
                sawTable = true;
                continue;
            }

            if (!sawTable || cells.Length < 5 || cells.All(cell => cell.All(character => character is '-' or ':' or ' ')))
            {
                continue;
            }

            var model = NormalizeModel(cells[0]);
            if (!requested.Contains(model))
            {
                continue;
            }

            var candidate = new PriceCandidate(
                model,
                ParsePerMillion(cells[1]),
                ParsePerMillion(cells[2]),
                ParsePerMillion(cells[4]),
                retrievedAt,
                retrievedAt,
                SourceKind,
                SourceId,
                DisplayName,
                SourceUrl,
                contentHash,
                sourceRevision,
                ParserVersionValue);
            if (candidates.TryGetValue(model, out var existing))
            {
                if (existing.InputUsdPerMillion != candidate.InputUsdPerMillion ||
                    existing.CachedInputUsdPerMillion != candidate.CachedInputUsdPerMillion ||
                    existing.OutputUsdPerMillion != candidate.OutputUsdPerMillion)
                {
                    throw new InvalidDataException($"Conflicting official prices were returned for '{model}'.");
                }

                continue;
            }

            candidates.Add(model, candidate);
        }

        if (!sawTable)
        {
            throw new InvalidDataException("The official price page did not contain a standard pricing table.");
        }

        var specializedStart = markdown.IndexOf("Specialized models", StringComparison.OrdinalIgnoreCase);
        var fastModeStart = specializedStart < 0
            ? -1
            : markdown.IndexOf("Fast mode", specializedStart, StringComparison.OrdinalIgnoreCase);
        if (specializedStart >= 0)
        {
            var specialized = fastModeStart > specializedStart
                ? markdown[specializedStart..fastModeStart]
                : markdown[specializedStart..];
            var specializedTableStarted = false;
            foreach (var rawLine in specialized.Split('\n'))
            {
                var line = rawLine.Trim();
                if (!line.StartsWith('|') || !line.EndsWith('|'))
                {
                    continue;
                }

                var cells = line[1..^1]
                    .Split('|')
                    .Select(cell => cell.Trim())
                    .ToArray();
                if (cells.Length >= 5 &&
                    cells[0].Equals("Category", StringComparison.OrdinalIgnoreCase) &&
                    cells[1].Equals("Model", StringComparison.OrdinalIgnoreCase))
                {
                    specializedTableStarted = true;
                    continue;
                }

                if (!specializedTableStarted || cells.Length < 5 ||
                    cells.All(cell => cell.All(character => character is '-' or ':' or ' ')))
                {
                    continue;
                }

                var model = NormalizeModel(cells[1]);
                if (!requested.Contains(model))
                {
                    continue;
                }

                var candidate = new PriceCandidate(
                    model,
                    ParsePerMillion(cells[2]),
                    ParsePerMillion(cells[3]),
                    ParsePerMillion(cells[4]),
                    retrievedAt,
                    retrievedAt,
                    SourceKind,
                    SourceId,
                    DisplayName,
                    SourceUrl,
                    contentHash,
                    sourceRevision,
                    ParserVersionValue);
                if (candidates.TryGetValue(model, out var existing))
                {
                    if (existing.InputUsdPerMillion != candidate.InputUsdPerMillion ||
                        existing.CachedInputUsdPerMillion != candidate.CachedInputUsdPerMillion ||
                        existing.OutputUsdPerMillion != candidate.OutputUsdPerMillion)
                    {
                        throw new InvalidDataException($"Conflicting official prices were returned for '{model}'.");
                    }

                    continue;
                }

                candidates.Add(model, candidate);
            }
        }

        return candidates.Values
            .OrderBy(candidate => candidate.Model, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _client.Dispose();
        }
    }

    private static HttpClient CreateClient()
    {
        return new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        })
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
    }

    private static string NormalizeModel(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        var annotation = normalized.IndexOf(" (", StringComparison.Ordinal);
        return annotation >= 0 ? normalized[..annotation] : normalized;
    }

    private static decimal? ParsePerMillion(string value)
    {
        var normalized = value.Trim();
        if (normalized is "-" or "")
        {
            return null;
        }

        normalized = normalized.TrimStart('$').Replace(",", "", StringComparison.Ordinal);
        if (!decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out var price) || price <= 0m)
        {
            throw new InvalidDataException($"The official price value '{value}' was not a positive USD amount.");
        }

        return price;
    }
}
