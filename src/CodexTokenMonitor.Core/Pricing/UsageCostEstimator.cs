using CodexTokenMonitor.Core.Models;

namespace CodexTokenMonitor.Core.Pricing;

public static class UsageCostEstimator
{
    private const decimal TokensPerMillion = 1_000_000m;

    public static DateOnly PricingAsOf { get; } = new(2026, 10, 10);

    // 非 DeepSeek 模型的内置参考价：美元目录价 × 固定参考汇率 6.70，单位人民币元 / 百万 token。
    // DeepSeek 不使用该表，由 PriceCatalog 通过 DeepSeekPricing 按事件时间（北京时间高峰/空闲）计价。
    public static IReadOnlyList<ModelPriceRate> Rates { get; } =
    [
        new("gpt-5.6-sol", 26.8m, 2.68m, 134m),
        new("gpt-5.6-terra", 13.4m, 1.34m, 80.4m),
        new("gpt-5.6-luna", 1.34m, 0.134m, 8.04m),
        new("gpt-5.5", 33.5m, 3.35m, 201m),
        new("gpt-5.4", 16.75m, 1.675m, 100.5m)
    ];

    public static UsageCostPeriod EstimatePeriod(
        DateOnly startDate,
        DateOnly endDate,
        IEnumerable<(string Model, TokenUsage Usage)> modelUsage)
    {
        var models = modelUsage
            .Select(item => EstimateModel(item.Model, item.Usage))
            .OrderByDescending(item => item.EstimatedCostCny)
            .ThenByDescending(item => item.Usage.TotalTokens)
            .ToArray();

        return new UsageCostPeriod(
            startDate,
            endDate,
            SumUsage(models.Select(item => item.Usage)),
            models.Sum(item => item.PricedTokens),
            models.Where(item => !item.IsPriced).Sum(item => item.Usage.TotalTokens),
            models.Sum(item => item.EstimatedCostCny),
            models);
    }

    // 不带事件时间的估算只能使用静态内置价；DeepSeek 需要按事件时间判断高峰/空闲，请使用 EstimateTimedPeriod。
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
        var cost = uncachedInput / TokensPerMillion * rate.InputCnyPerMillion
            + cachedInput / TokensPerMillion * rate.CachedInputCnyPerMillion
            + usage.OutputTokens / TokensPerMillion * rate.OutputCnyPerMillion;

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
            .OrderByDescending(item => item.EstimatedCostCny)
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
            decimal.Round(models.Sum(item => item.EstimatedCostCny), 6, MidpointRounding.AwayFromZero),
            models,
            coverage,
            official?.Tokens ?? 0,
            official?.EstimatedCostCny ?? 0m);
    }

    private static string NormalizeModel(string model) =>
        string.IsNullOrWhiteSpace(model) ? "unknown" : model.Trim().ToLowerInvariant();

    private static IReadOnlyList<PriceCoverageSummary> MergeCoverage(IEnumerable<PriceCoverageSummary> coverage) =>
        coverage
            .GroupBy(item => item.SourceKind, StringComparer.Ordinal)
            .Select(group => new PriceCoverageSummary(
                group.Key,
                group.Sum(item => item.Tokens),
                decimal.Round(group.Sum(item => item.EstimatedCostCny), 6, MidpointRounding.AwayFromZero)))
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

            var cost = tokens / TokensPerMillion * price.CnyPerMillion;
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
