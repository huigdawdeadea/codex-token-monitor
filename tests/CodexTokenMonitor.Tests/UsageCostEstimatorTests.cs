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
        Assert.Equal(20.904m, result.EstimatedCostCny);
    }

    [Fact]
    public void LeavesUnknownModelsOutOfTheEstimatedAmount()
    {
        var usage = new TokenUsage(900, 600, 0, 100, 0, 1_000);

        var result = UsageCostEstimator.EstimatePeriod(
            new DateOnly(2026, 8, 27),
            new DateOnly(2026, 8, 27),
            [("unknown-model", usage)]);

        Assert.Equal(0m, result.EstimatedCostCny);
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
        Assert.Equal(0.01608m, model.EstimatedCostCny);
    }

    [Fact]
    public void PricesDeepSeekFlashAtBeijingPeakAndIdleHours()
    {
        var usage = new TokenUsage(1_000_000, 0, 0, 1_000_000, 0, 2_000_000);
        var catalog = new PriceCatalog([]);

        var peak = UsageCostEstimator.EstimateTimedPeriod(
            new DateOnly(2026, 8, 27),
            new DateOnly(2026, 8, 27),
            [new TimedModelUsage("deepseek-v4-flash", DateTimeOffset.Parse("2026-08-27T02:00:00Z"), usage)],
            catalog);
        var idle = UsageCostEstimator.EstimateTimedPeriod(
            new DateOnly(2026, 8, 27),
            new DateOnly(2026, 8, 27),
            [new TimedModelUsage("deepseek-v4-flash", DateTimeOffset.Parse("2026-08-27T04:00:00Z"), usage)],
            catalog);

        var peakModel = Assert.Single(peak.Models);
        Assert.True(peakModel.IsPriced);
        Assert.Equal(10m, peakModel.EstimatedCostCny);
        Assert.Equal(PriceSourceKinds.DeepSeekOfficialBuiltin, Assert.Single(peakModel.Coverage!).SourceKind);
        Assert.Equal(5m, Assert.Single(idle.Models).EstimatedCostCny);
    }

    [Fact]
    public void PricesDeepSeekFlashAsIdleOnHolidaysAndWeekends()
    {
        var usage = new TokenUsage(1_000_000, 0, 0, 1_000_000, 0, 2_000_000);
        var catalog = new PriceCatalog([]);

        var holiday = UsageCostEstimator.EstimateTimedPeriod(
            new DateOnly(2026, 10, 1),
            new DateOnly(2026, 10, 1),
            [new TimedModelUsage("deepseek-v4.1-flash", DateTimeOffset.Parse("2026-10-01T02:00:00Z"), usage)],
            catalog);
        var weekend = UsageCostEstimator.EstimateTimedPeriod(
            new DateOnly(2026, 10, 10),
            new DateOnly(2026, 10, 10),
            [new TimedModelUsage("deepseek-flash", DateTimeOffset.Parse("2026-10-10T02:00:00Z"), usage)],
            catalog);

        Assert.Equal(5m, Assert.Single(holiday.Models).EstimatedCostCny);
        Assert.Equal(5m, Assert.Single(weekend.Models).EstimatedCostCny);
    }

    [Fact]
    public void ResolvesAllDeepSeekFlashAliasesToTheSharedBuiltInRates()
    {
        var catalog = new PriceCatalog([]);
        var peak = DateTimeOffset.Parse("2026-08-27T02:00:00Z");
        var idle = DateTimeOffset.Parse("2026-08-27T04:00:00Z");

        foreach (var alias in DeepSeekPricing.FlashAliases)
        {
            var input = catalog.Resolve(alias, PriceComponent.Input, peak);
            var cached = catalog.Resolve(alias, PriceComponent.CachedInput, peak);
            var output = catalog.Resolve(alias, PriceComponent.Output, idle);

            Assert.NotNull(input);
            Assert.Equal(2m, input!.CnyPerMillion);
            Assert.Equal(PriceSourceKinds.DeepSeekOfficialBuiltin, input.SourceKind);
            Assert.Equal(0.04m, cached!.CnyPerMillion);
            Assert.Equal(4m, output!.CnyPerMillion);
        }

        var mixedCase = catalog.Resolve("DeepSeek-V4.1-Flash", PriceComponent.Output, peak);
        Assert.Equal(8m, mixedCase!.CnyPerMillion);
    }

    [Fact]
    public void LeavesDeepSeekModelsWithoutBuiltInRatesUnpriced()
    {
        var catalog = new PriceCatalog([]);
        var at = DateTimeOffset.Parse("2026-08-27T02:00:00Z");

        Assert.Null(catalog.Resolve("deepseek-v4-pro", PriceComponent.Input, at));

        var period = UsageCostEstimator.EstimateTimedPeriod(
            new DateOnly(2026, 8, 27),
            new DateOnly(2026, 8, 27),
            [new TimedModelUsage("deepseek-v4-pro", at, new TokenUsage(1_000, 0, 0, 100, 0, 1_100))],
            catalog);

        var model = Assert.Single(period.Models);
        Assert.False(model.IsPriced);
        Assert.Equal(0m, model.EstimatedCostCny);
        Assert.Equal(1_100, period.UnpricedTokens);
    }
}
