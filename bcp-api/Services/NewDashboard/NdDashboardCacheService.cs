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

    /// <param name="ttl">How long to keep the value; the short default suits polled dashboard numbers. A longer
    /// lifetime is safe for values that only change through actions that call <see cref="Invalidate"/>, which
    /// drops every cached entry at once.</param>
    public async Task<T> GetOrCreateAsync<T>(
        string scope,
        Func<CancellationToken, Task<T>> factory,
        CancellationToken ct = default,
        TimeSpan? ttl = null)
    {
        // Cached numbers are per workspace; the key must never let one workspace read another's.
        var tenant = Reguliq.Api.Infrastructure.NewDashboard.WorkspaceScope.CurrentWorkspaceId?.ToString() ?? "all";
        var key = $"nd-dash:{Volatile.Read(ref _generation)}:{tenant}:{scope}";
        if (cache.TryGetValue(key, out T? hit) && hit is not null)
            return hit;

        var value = await factory(ct);
        cache.Set(key, value, ttl ?? Ttl);
        return value;
    }
}
