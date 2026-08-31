using CodexTokenMonitor.Core.Models;

namespace CodexTokenMonitor.Core.Pricing;

public interface IPriceSource
{
    string SourceId { get; }

    string SourceKind { get; }

    string DisplayName { get; }

    string SourceUrl { get; }

    bool Enabled { get; }

    string GateStatus { get; }

    Task<PriceFetchResult> FetchAsync(
        IReadOnlyCollection<string> observedModels,
        CancellationToken cancellationToken = default);
}
