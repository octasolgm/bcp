using Microsoft.Extensions.Caching.Memory;

namespace Reguliq.Api.Services.NewDashboard;

/// <summary>
/// Short-lived in-memory cache for dashboard overview + sidebar nav-counts.
/// Reduces repeated heavy DB scans when /nd/overview polls in parallel.
/// </summary>
public sealed class NdDashboardCacheService(IMemoryCache cache)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(20);
    private int _generation;

    public void Invalidate() => Interlocked.Increment(ref _generation);

    public async Task<T> GetOrCreateAsync<T>(
        string scope,
        Func<CancellationToken, Task<T>> factory,
        CancellationToken ct = default)
    {
        var key = $"nd-dash:{Volatile.Read(ref _generation)}:{scope}";
        if (cache.TryGetValue(key, out T? hit) && hit is not null)
            return hit;

        var value = await factory(ct);
        cache.Set(key, value, TimeSpan.FromSeconds(20));
        return value;
    }
}
