using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using CodexTokenMonitor.Core.Models;

namespace CodexTokenMonitor.Core.Pricing;

public sealed class LiteLlmPriceSource : IPriceSource, IDisposable
{
    public const string DefaultUrl =
        "https://raw.githubusercontent.com/BerriAI/litellm/main/model_prices_and_context_window.json";
    private const int MaximumResponseBytes = 16 * 1024 * 1024;
    private const string ParserVersionValue = "litellm-json-v1-unverified";
    private readonly HttpClient _client;
    private readonly bool _ownsClient;

    public LiteLlmPriceSource(bool enabled = false, HttpClient? client = null)
    {
        Enabled = enabled;
        _ownsClient = client is null;
        _client = client ?? CreateClient();
    }

    public string SourceId => "litellm";

    public string SourceKind => PriceSourceKinds.ThirdPartyReference;

    public string DisplayName => "LiteLLM 参考价";

    public string SourceUrl => DefaultUrl;

    public bool Enabled { get; }

    public string GateStatus => Enabled
        ? "operator_override_unverified"
        : "disabled_pending_live_schema_and_license_verification";

    public async Task<PriceFetchResult> FetchAsync(
        IReadOnlyCollection<string> observedModels,
        CancellationToken cancellationToken = default)
    {
        if (!Enabled)
        {
            throw new InvalidOperationException("The LiteLLM source is disabled until its live schema and license are verified.");
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
                $"The price source returned HTTP {(int)response.StatusCode}.",
                null,
                response.StatusCode);
        }

        var finalUri = response.RequestMessage?.RequestUri;
        if (finalUri is null || !finalUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !finalUri.Host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The price source redirected outside the approved host.");
        }

        if (response.Content.Headers.ContentLength is > MaximumResponseBytes)
        {
            throw new InvalidDataException("The price source response exceeded the size limit.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);
        if (buffer.Length > MaximumResponseBytes)
        {
            throw new InvalidDataException("The price source response exceeded the size limit.");
        }

        var content = buffer.ToArray();
        var retrievedAt = DateTimeOffset.UtcNow;
        var contentHash = Convert.ToHexString(SHA256.HashData(content));
        var revision = response.Headers.ETag?.Tag;
        var prices = Parse(content, observedModels, retrievedAt, contentHash, revision);
        return new PriceFetchResult(SourceId, retrievedAt, contentHash, revision, prices);
    }

    public IReadOnlyList<PriceCandidate> Parse(
        ReadOnlySpan<byte> utf8Json,
        IReadOnlyCollection<string> observedModels,
        DateTimeOffset retrievedAt,
        string contentHash,
        string? sourceRevision)
    {
        using var document = JsonDocument.Parse(utf8Json.ToArray());
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("The LiteLLM price registry must be a JSON object.");
        }

        var requested = observedModels
            .Where(IsPublicOpenAiModel)
            .Select(model => model.Trim().ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = new Dictionary<string, PriceCandidate>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            var normalizedKey = NormalizeRegistryKey(property.Name);
            if (!requested.Contains(normalizedKey) || property.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var input = ReadPerMillion(property.Value, "input_cost_per_token");
            var cached = ReadPerMillion(property.Value, "cache_read_input_token_cost")
                ?? ReadPerMillion(property.Value, "cache_read_input_cost_per_token");
            var output = ReadPerMillion(property.Value, "output_cost_per_token");
            if (input is null && cached is null && output is null)
            {
                continue;
            }

            var candidate = new PriceCandidate(
                normalizedKey,
                input,
                cached,
                output,
                retrievedAt,
                retrievedAt,
                SourceKind,
                SourceId,
                DisplayName,
                SourceUrl,
                contentHash,
                sourceRevision,
                ParserVersionValue);
            if (candidates.TryGetValue(normalizedKey, out var existing))
            {
                if (existing.InputUsdPerMillion != candidate.InputUsdPerMillion ||
                    existing.CachedInputUsdPerMillion != candidate.CachedInputUsdPerMillion ||
                    existing.OutputUsdPerMillion != candidate.OutputUsdPerMillion)
                {
                    throw new InvalidDataException($"Conflicting duplicate prices were returned for '{normalizedKey}'.");
                }

                continue;
            }

            candidates.Add(normalizedKey, candidate);
        }

        return candidates.Values.OrderBy(candidate => candidate.Model, StringComparer.OrdinalIgnoreCase).ToArray();
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
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
    }

    private static bool IsPublicOpenAiModel(string model)
    {
        var normalized = model.Trim().ToLowerInvariant();
        return normalized.StartsWith("gpt-", StringComparison.Ordinal) ||
            normalized.StartsWith("o1", StringComparison.Ordinal) ||
            normalized.StartsWith("o3", StringComparison.Ordinal) ||
            normalized.StartsWith("o4", StringComparison.Ordinal);
    }

    private static string NormalizeRegistryKey(string key)
    {
        var normalized = key.Trim().ToLowerInvariant();
        return normalized.StartsWith("openai/", StringComparison.Ordinal)
            ? normalized["openai/".Length..]
            : normalized;
    }

    private static decimal? ReadPerMillion(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        decimal perToken;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var numeric))
        {
            perToken = numeric;
        }
        else if (value.ValueKind == JsonValueKind.String &&
            decimal.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out numeric))
        {
            perToken = numeric;
        }
        else
        {
            throw new InvalidDataException($"Price field '{name}' was not numeric.");
        }

        if (perToken <= 0m)
        {
            throw new InvalidDataException($"Price field '{name}' must be positive.");
        }

        return perToken * 1_000_000m;
    }
}
