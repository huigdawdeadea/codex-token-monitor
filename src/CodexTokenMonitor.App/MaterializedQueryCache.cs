using System.Collections.Concurrent;
using CodexTokenMonitor.Core.Storage;

namespace CodexTokenMonitor.App;

internal sealed class MaterializedQueryCache(MonitorDatabase database)
{
    private readonly ConcurrentDictionary<string, Lazy<Task<object>>> _inflight = new(StringComparer.Ordinal);

    public async Task<T> GetOrCreateAsync<T>(
        string cacheKey,
        string sourceVersion,
        Func<CancellationToken, Task<T>> factory,
        CancellationToken cancellationToken)
        where T : notnull
    {
        var versionedKey = $"{cacheKey}@{sourceVersion}";
        var lazy = _inflight.GetOrAdd(
            versionedKey,
            _ => new Lazy<Task<object>>(
                async () => await LoadOrCreateAsync(cacheKey, sourceVersion, factory),
                LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            return (T)await lazy.Value.WaitAsync(cancellationToken);
        }
        finally
        {
            if (lazy.IsValueCreated && lazy.Value.IsCompleted)
            {
                _inflight.TryRemove(new KeyValuePair<string, Lazy<Task<object>>>(versionedKey, lazy));
            }
        }
    }

    private async Task<object> LoadOrCreateAsync<T>(
        string cacheKey,
        string sourceVersion,
        Func<CancellationToken, Task<T>> factory)
        where T : notnull
    {
        var persisted = await database.ReadMaterializedViewAsync<T>(cacheKey, sourceVersion);
        if (persisted is not null)
        {
            return persisted;
        }

        var result = await factory(CancellationToken.None);
        await database.WriteMaterializedViewAsync(cacheKey, sourceVersion, result);
        return result;
    }
}
