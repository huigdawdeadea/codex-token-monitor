using CodexTokenMonitor.Core.Models;

namespace CodexTokenMonitor.Core.Pricing;

public static class UsageCostEstimator
{
    private const decimal TokensPerMillion = 1_000_000m;

    public static DateOnly PricingAsOf { get; } = new(2026, 8, 27);

    public static IReadOnlyList<ModelPriceRate> Rates { get; } =
    [
        new("gpt-5.6-sol", 4m, 0.4m, 20m),
        new("gpt-5.6-terra", 2m, 0.2m, 12m),
        new("gpt-5.6-luna", 0.2m, 0.02m, 1.2m),
        new("gpt-5.5", 5m, 0.5m, 30m),
        new("gpt-5.4", 2.5m, 0.25m, 15m)
    ];

    public static UsageCostPeriod EstimatePeriod(
        DateOnly startDate,
        DateOnly endDate,
        IEnumerable<(string Model, TokenUsage Usage)> modelUsage)
    {
        var models = modelUsage
            .Select(item => EstimateModel(item.Model, item.Usage))
            .OrderByDescending(item => item.EstimatedCostUsd)
            .ThenByDescending(item => item.Usage.TotalTokens)
            .ToArray();

        return new UsageCostPeriod(
            startDate,
            endDate,
            SumUsage(models.Select(item => item.Usage)),
            models.Sum(item => item.PricedTokens),
            models.Where(item => !item.IsPriced).Sum(item => item.Usage.TotalTokens),
            models.Sum(item => item.EstimatedCostUsd),
            models);
    }

    public static ModelUsageCost EstimateModel(string model, TokenUsage usage)
    {
        var normalized = string.IsNullOrWhiteSpace(model) ? "unknown" : model.Trim().ToLowerInvariant();
        var rate = Rates.FirstOrDefault(candidate =>
            normalized.Equals(candidate.Model, StringComparison.OrdinalIgnoreCase));
        if (rate is null)
        {
            return new ModelUsageCost(normalized, usage, 0, 0, 0m, false);
        }

        var cachedInput = Math.Clamp(usage.CachedInputTokens, 0, usage.InputTokens);
        var uncachedInput = Math.Max(usage.InputTokens - cachedInput, 0);
        var pricedTokens = usage.TotalTokens;
        var cost = uncachedInput / TokensPerMillion * rate.InputUsdPerMillion
            + cachedInput / TokensPerMillion * rate.CachedInputUsdPerMillion
            + usage.OutputTokens / TokensPerMillion * rate.OutputUsdPerMillion;

        return new ModelUsageCost(
            normalized,
            usage,
            uncachedInput,
            pricedTokens,
            decimal.Round(cost, 6, MidpointRounding.AwayFromZero),
            true,
            [new PriceCoverageSummary(PriceSourceKinds.BuiltInDefault, pricedTokens, decimal.Round(cost, 6, MidpointRounding.AwayFromZero))]);
    }

    public static UsageCostPeriod EstimateTimedPeriod(
        DateOnly startDate,
        DateOnly endDate,
        IEnumerable<TimedModelUsage> events,
        PriceCatalog catalog)
    {
        var accumulators = new Dictionary<string, ModelCostAccumulator>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in events)
        {
            var model = NormalizeModel(item.Model);
            if (!accumulators.TryGetValue(model, out var accumulator))
            {
                accumulator = new ModelCostAccumulator(model);
                accumulators.Add(model, accumulator);
            }

            accumulator.Add(item, catalog);
        }

        var models = accumulators.Values
            .Select(item => item.Build())
            .OrderByDescending(item => item.EstimatedCostUsd)
            .ThenByDescending(item => item.Usage.TotalTokens)
            .ToArray();
        var coverage = MergeCoverage(models.SelectMany(item => item.Coverage ?? []));
        var usage = SumUsage(models.Select(item => item.Usage));
        var pricedTokens = models.Sum(item => item.PricedTokens);
        var official = coverage.FirstOrDefault(item => item.SourceKind == PriceSourceKinds.Official);

        return new UsageCostPeriod(
            startDate,
            endDate,
            usage,
            pricedTokens,
            Math.Max(usage.TotalTokens - pricedTokens, 0),
            decimal.Round(models.Sum(item => item.EstimatedCostUsd), 6, MidpointRounding.AwayFromZero),
            models,
            coverage,
            official?.Tokens ?? 0,
            official?.EstimatedCostUsd ?? 0m);
    }

    private static string NormalizeModel(string model) =>
        string.IsNullOrWhiteSpace(model) ? "unknown" : model.Trim().ToLowerInvariant();

    private static IReadOnlyList<PriceCoverageSummary> MergeCoverage(IEnumerable<PriceCoverageSummary> coverage) =>
        coverage
            .GroupBy(item => item.SourceKind, StringComparer.Ordinal)
            .Select(group => new PriceCoverageSummary(
                group.Key,
                group.Sum(item => item.Tokens),
                decimal.Round(group.Sum(item => item.EstimatedCostUsd), 6, MidpointRounding.AwayFromZero)))
            .OrderBy(item => PriceSourceKinds.Priority(item.SourceKind))
            .ToArray();

    private sealed class ModelCostAccumulator(string model)
    {
        private TokenUsage _usage = TokenUsage.Empty;
        private readonly Dictionary<string, (long Tokens, decimal Cost)> _coverage = new(StringComparer.Ordinal);
        private long _pricedTokens;
        private long _uncachedInputTokens;
        private decimal _cost;
        private bool _hasBackfill;

        public void Add(TimedModelUsage item, PriceCatalog catalog)
        {
            _usage = SumUsage([_usage, item.Usage]);
            var cachedInput = Math.Clamp(item.Usage.CachedInputTokens, 0, item.Usage.InputTokens);
            var uncachedInput = Math.Max(item.Usage.InputTokens - cachedInput, 0);
            _uncachedInputTokens += uncachedInput;
            AddComponent(uncachedInput, catalog.Resolve(model, PriceComponent.Input, item.EventAt));
            AddComponent(cachedInput, catalog.Resolve(model, PriceComponent.CachedInput, item.EventAt));
            AddComponent(item.Usage.OutputTokens, catalog.Resolve(model, PriceComponent.Output, item.EventAt));
        }

        public ModelUsageCost Build() => new(
            model,
            _usage,
            _uncachedInputTokens,
            _pricedTokens,
            decimal.Round(_cost, 6, MidpointRounding.AwayFromZero),
            _pricedTokens > 0,
            _coverage
                .Select(item => new PriceCoverageSummary(
                    item.Key,
                    item.Value.Tokens,
                    decimal.Round(item.Value.Cost, 6, MidpointRounding.AwayFromZero)))
                .OrderBy(item => PriceSourceKinds.Priority(item.SourceKind))
                .ToArray(),
            _hasBackfill);

        private void AddComponent(long tokens, ResolvedPriceComponent? price)
        {
            if (tokens <= 0 || price is null)
            {
                return;
            }

            var cost = tokens / TokensPerMillion * price.UsdPerMillion;
            _pricedTokens += tokens;
            _cost += cost;
            _hasBackfill |= price.IsBackfilled;
            var current = _coverage.GetValueOrDefault(price.SourceKind);
            _coverage[price.SourceKind] = (current.Tokens + tokens, current.Cost + cost);
        }
    }

    private static TokenUsage SumUsage(IEnumerable<TokenUsage> values) => values.Aggregate(
        TokenUsage.Empty,
        (sum, value) => new TokenUsage(
            sum.InputTokens + value.InputTokens,
            sum.CachedInputTokens + value.CachedInputTokens,
            sum.CacheWriteInputTokens + value.CacheWriteInputTokens,
            sum.OutputTokens + value.OutputTokens,
            sum.ReasoningOutputTokens + value.ReasoningOutputTokens,
            sum.TotalTokens + value.TotalTokens));
}
