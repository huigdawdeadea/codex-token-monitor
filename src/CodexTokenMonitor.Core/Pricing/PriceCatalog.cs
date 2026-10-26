using CodexTokenMonitor.Core.Models;

namespace CodexTokenMonitor.Core.Pricing;

public enum PriceComponent
{
    Input,
    CachedInput,
    Output
}

public sealed record ResolvedPriceComponent(
    decimal CnyPerMillion,
    string SourceKind,
    DateTimeOffset EffectiveFrom,
    bool IsBackfilled);

public sealed class PriceCatalog
{
    /// <summary>美元目录价折算为人民币的固定参考汇率（非实时、非账单依据）。</summary>
    public const decimal FixedUsdToCnyRate = 6.70m;

    private readonly IReadOnlyList<ModelPriceVersion> _versions;
    private readonly IReadOnlyDictionary<string, ModelPriceRate> _defaults;

    public PriceCatalog(IEnumerable<ModelPriceVersion> versions)
    {
        _versions = versions
            .Where(version => version.SourceKind is PriceSourceKinds.Official or PriceSourceKinds.ThirdPartyReference)
            .OrderBy(version => version.EffectiveFrom)
            .ToArray();
        _defaults = UsageCostEstimator.Rates.ToDictionary(rate => rate.Model, StringComparer.OrdinalIgnoreCase);
    }

    public ResolvedPriceComponent? Resolve(string model, PriceComponent component, DateTimeOffset eventAt)
    {
        var normalized = string.IsNullOrWhiteSpace(model) ? "unknown" : model.Trim().ToLowerInvariant();

        // DeepSeek 使用内置官方中文价目，按事件发生时的北京时间区分高峰/空闲，优先于任何远程价格版本。
        if (DeepSeekPricing.TryResolve(normalized, component, eventAt) is { } deepSeekPrice)
        {
            return deepSeekPrice;
        }

        var matching = _versions
            .Where(version => normalized.Equals(version.Model, StringComparison.OrdinalIgnoreCase))
            .Where(version => GetRate(version, component) is not null)
            .ToArray();

        foreach (var sourceKind in new[] { PriceSourceKinds.Official, PriceSourceKinds.ThirdPartyReference })
        {
            var preceding = matching
                .Where(version => version.SourceKind == sourceKind && version.EffectiveFrom <= eventAt)
                .OrderByDescending(version => version.EffectiveFrom)
                .ThenByDescending(version => version.RetrievedAt)
                .FirstOrDefault();
            if (preceding is not null)
            {
                return new ResolvedPriceComponent(
                    GetRate(preceding, component)!.Value * FixedUsdToCnyRate,
                    sourceKind,
                    preceding.EffectiveFrom,
                    false);
            }
        }

        var following = matching
            .Where(version => version.EffectiveFrom > eventAt)
            .OrderBy(version => version.EffectiveFrom)
            .ThenBy(version => PriceSourceKinds.Priority(version.SourceKind))
            .ThenByDescending(version => version.RetrievedAt)
            .FirstOrDefault();
        if (following is not null)
        {
            return new ResolvedPriceComponent(
                GetRate(following, component)!.Value * FixedUsdToCnyRate,
                following.SourceKind,
                following.EffectiveFrom,
                true);
        }

        if (!_defaults.TryGetValue(normalized, out var builtIn))
        {
            return null;
        }

        return new ResolvedPriceComponent(
            component switch
            {
                PriceComponent.Input => builtIn.InputCnyPerMillion,
                PriceComponent.CachedInput => builtIn.CachedInputCnyPerMillion,
                PriceComponent.Output => builtIn.OutputCnyPerMillion,
                _ => throw new ArgumentOutOfRangeException(nameof(component))
            },
            PriceSourceKinds.BuiltInDefault,
            UsageCostEstimator.PricingAsOf.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            false);
    }

    public IReadOnlyList<ModelPriceRate> GetCurrentRates(DateTimeOffset now)
    {
        var models = _versions.Select(version => version.Model)
            .Concat(_defaults.Keys)
            .Concat(DeepSeekPricing.FlashAliases)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase);
        return models.Select(model => new ModelPriceRate(
                model,
                Resolve(model, PriceComponent.Input, now)?.CnyPerMillion ?? 0m,
                Resolve(model, PriceComponent.CachedInput, now)?.CnyPerMillion ?? 0m,
                Resolve(model, PriceComponent.Output, now)?.CnyPerMillion ?? 0m,
                HighestCurrentSource(model, now),
                LatestEffectiveFrom(model, now)))
            .ToArray();
    }

    private string HighestCurrentSource(string model, DateTimeOffset now) =>
        new[]
        {
            Resolve(model, PriceComponent.Input, now),
            Resolve(model, PriceComponent.CachedInput, now),
            Resolve(model, PriceComponent.Output, now)
        }
        .Where(value => value is not null)
        .Select(value => value!.SourceKind)
        .OrderBy(PriceSourceKinds.Priority)
        .FirstOrDefault() ?? PriceSourceKinds.BuiltInDefault;

    private DateTimeOffset? LatestEffectiveFrom(string model, DateTimeOffset now) =>
        new[]
        {
            Resolve(model, PriceComponent.Input, now)?.EffectiveFrom,
            Resolve(model, PriceComponent.CachedInput, now)?.EffectiveFrom,
            Resolve(model, PriceComponent.Output, now)?.EffectiveFrom
        }
        .Where(value => value is not null)
        .Max();

    private static decimal? GetRate(ModelPriceVersion version, PriceComponent component) => component switch
    {
        PriceComponent.Input => version.InputUsdPerMillion,
        PriceComponent.CachedInput => version.CachedInputUsdPerMillion,
        PriceComponent.Output => version.OutputUsdPerMillion,
        _ => throw new ArgumentOutOfRangeException(nameof(component))
    };
}
