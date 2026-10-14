namespace CaseManagement.Api.Services;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Primitives;

/// <summary>
/// Short-lived cache for administrator configuration (field metadata, lookups, case types…), which is
/// read on almost every request but changes rarely. Every configuration write calls
/// <see cref="InvalidateAll"/>, so an administrator's change is visible on the very next read; the expiry
/// is only an upper bound on staleness across multiple application instances.
/// </summary>
public interface IConfigCache
{
    Task<T> GetOrCreateAsync<T>(string key, Func<Task<T>> factory);
    void InvalidateAll();
}

public sealed class ConfigCache : IConfigCache
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(2);

    private readonly IMemoryCache _cache;
    private CancellationTokenSource _generation = new();
    private readonly object _lock = new();

    public ConfigCache(IMemoryCache cache) => _cache = cache;

    public async Task<T> GetOrCreateAsync<T>(string key, Func<Task<T>> factory)
    {
        var cacheKey = "config:" + key;
        if (_cache.TryGetValue(cacheKey, out T? hit) && hit != null) return hit;

        CancellationChangeToken token;
        lock (_lock) token = new CancellationChangeToken(_generation.Token);

        var value = await factory();
        _cache.Set(cacheKey, value, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = MaxAge }.AddExpirationToken(token));
        return value;
    }

    public void InvalidateAll()
    {
        CancellationTokenSource old;
        lock (_lock)
        {
            old = _generation;
            _generation = new CancellationTokenSource();
        }
        old.Cancel();
        old.Dispose();
    }
}
