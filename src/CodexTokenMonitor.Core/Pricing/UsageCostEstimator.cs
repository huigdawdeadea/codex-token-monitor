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
            normalized.Equals(candidate.Model, StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith(candidate.Model + "-", StringComparison.OrdinalIgnoreCase));
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
            true);
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
