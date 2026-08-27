using CodexTokenMonitor.Core.Models;
using CodexTokenMonitor.Core.Pricing;

namespace CodexTokenMonitor.Tests;

public sealed class UsageCostEstimatorTests
{
    [Fact]
    public void SeparatesCachedInputAndOutputWhenCalculatingCost()
    {
        var usage = new TokenUsage(1_000_000, 800_000, 0, 100_000, 25_000, 1_100_000);

        var result = UsageCostEstimator.EstimateModel("gpt-5.6-sol", usage);

        Assert.True(result.IsPriced);
        Assert.Equal(200_000, result.UncachedInputTokens);
        Assert.Equal(1_100_000, result.PricedTokens);
        Assert.Equal(3.12m, result.EstimatedCostUsd);
    }

    [Fact]
    public void LeavesUnknownModelsOutOfTheEstimatedAmount()
    {
        var usage = new TokenUsage(900, 600, 0, 100, 0, 1_000);

        var result = UsageCostEstimator.EstimatePeriod(
            new DateOnly(2026, 8, 27),
            new DateOnly(2026, 8, 27),
            [("deepseek-v4-flash", usage)]);

        Assert.Equal(0m, result.EstimatedCostUsd);
        Assert.Equal(0, result.PricedTokens);
        Assert.Equal(1_000, result.UnpricedTokens);
        Assert.False(Assert.Single(result.Models).IsPriced);
    }
}
