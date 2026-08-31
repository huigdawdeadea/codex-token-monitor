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

    [Fact]
    public void TreatsPartialTokenCoverageAsPricedWhenAReferenceAmountExists()
    {
        var catalog = new PriceCatalog([
            new ModelPriceVersion(
                1,
                "official",
                "gpt-test",
                2m,
                0.2m,
                4m,
                PriceSourceKinds.Official,
                "Official",
                "https://example.invalid/prices",
                DateTimeOffset.Parse("2026-08-01T00:00:00Z"),
                DateTimeOffset.Parse("2026-08-28T00:00:00Z"),
                "HASH",
                null,
                "test",
                false,
                DateTimeOffset.Parse("2026-08-28T00:00:00Z"))
        ]);
        var usage = new TokenUsage(1_000, 0, 0, 100, 25, 1_250);

        var result = UsageCostEstimator.EstimateTimedPeriod(
            new DateOnly(2026, 8, 28),
            new DateOnly(2026, 8, 28),
            [new TimedModelUsage("gpt-test", DateTimeOffset.Parse("2026-08-28T12:00:00Z"), usage)],
            catalog);

        var model = Assert.Single(result.Models);
        Assert.True(model.IsPriced);
        Assert.Equal(1_100, model.PricedTokens);
        Assert.Equal(150, result.UnpricedTokens);
        Assert.Equal(0.0024m, model.EstimatedCostUsd);
    }
}
