using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Reguliq.Api.Data;
using Reguliq.Api.Infrastructure.NewDashboard;

namespace Reguliq.Api.Services.NewDashboard.Demo;

/// <summary>Maps Supabase auth emails and profile names to profile IDs for demo/real data isolation.</summary>
public sealed class NdDemoUserDirectory(
    IMemoryCache cache,
    IOptions<SupabaseJwtOptions> jwtOptions,
    IOptions<NdDemoIsolationOptions> demoOptions,
    IHttpClientFactory httpClientFactory,
    IServiceScopeFactory scopeFactory)
{
    private const string CacheKey = "nd:demo-profile-ids";
    private static readonly TimeSpan AuthDirectoryTimeout = TimeSpan.FromSeconds(6);

    public void InvalidateProfileCache() => cache.Remove(CacheKey);

    public bool IsEnabled => NdDemoIsolationHelper.ResolveEnabled(demoOptions.Value);

    public async Task<HashSet<Guid>> GetDemoProfileIdsAsync(CancellationToken ct = default)
    {
        if (!IsEnabled)
            return new HashSet<Guid>();

        return await cache.GetOrCreateAsync(CacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
            return await FetchDemoProfileIdsAsync(ct);
        }) ?? new HashSet<Guid>();
    }

    public async Task<bool> IsDemoProfileAsync(Guid profileId, CancellationToken ct = default) =>
        (await GetDemoProfileIdsAsync(ct)).Contains(profileId);

    public async Task<bool> ShouldSimulateForProfilesAsync(
        Guid? actingProfileId,
        Guid? resourceOwnerId,
        CancellationToken ct = default)
    {
        if (!IsEnabled) return false;
        if (actingProfileId is Guid actor && await IsDemoProfileAsync(actor, ct)) return true;
        return resourceOwnerId is Guid owner && await IsDemoProfileAsync(owner, ct);
    }

    public async Task<NdDemoIsolationContext> ResolveContextAsync(JwtUser user, CancellationToken ct = default)
    {
        if (!IsEnabled)
            return new NdDemoIsolationContext(false, false, new HashSet<Guid>(), user);

        var demoIds = new HashSet<Guid>(await GetDemoProfileIdsAsync(ct));
        var profileFullName = await LoadProfileFullNameAsync(user.UserId, ct);
        var viewerIsDemo =
            NdDemoIsolationHelper.IsDemoUser(user)
            || NdDemoIsolationHelper.IsDemoName(profileFullName)
            || demoIds.Contains(user.UserId);
        if (viewerIsDemo)
            demoIds.Add(user.UserId);

        return new NdDemoIsolationContext(true, viewerIsDemo, demoIds, user);
    }

    /// <summary>
    /// Resolved on every authenticated request, so it is cached: the database is remote and an
    /// extra round trip here is paid by every single ND endpoint.
    /// </summary>
    private async Task<string?> LoadProfileFullNameAsync(Guid profileId, CancellationToken ct)
    {
        var key = $"nd:demo-profile-name:{profileId}";
        if (cache.TryGetValue(key, out string? cached))
            return cached;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var fullName = await db.NdProfiles.AsNoTracking()
            .Where(p => p.Id == profileId)
            .Select(p => p.FullName)
            .FirstOrDefaultAsync(ct);

        cache.Set(key, fullName, TimeSpan.FromMinutes(5));
        return fullName;
    }

    private async Task<HashSet<Guid>> FetchDemoProfileIdsAsync(CancellationToken ct)
    {
        var demoIds = await FetchDemoIdsFromAuthAsync(ct);
        await MergeDemoIdsFromProfilesAsync(demoIds, ct);
        await MergeDescendantDemoProfilesAsync(demoIds, ct);
        return demoIds;
    }

    /// <summary>
    /// Profiles invited/created by a demo admin (CreatedBy chain) — use simulation, not live AI.
    /// The parent/child links are fetched once and walked in memory: the database is remote, so
    /// looping a query until the set stops growing used to cost a round trip per level.
    /// </summary>
    private async Task MergeDescendantDemoProfilesAsync(HashSet<Guid> demoIds, CancellationToken ct)
    {
        if (demoIds.Count == 0) return;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var links = await db.NdProfiles.AsNoTracking()
            .Where(p => p.CreatedBy != null)
            .Select(p => new { p.Id, ParentId = p.CreatedBy!.Value })
            .ToListAsync(ct);

        var childrenByParent = links
            .GroupBy(l => l.ParentId)
            .ToDictionary(g => g.Key, g => g.Select(l => l.Id).ToList());

        var pending = new Queue<Guid>(demoIds);
        while (pending.Count > 0)
        {
            if (!childrenByParent.TryGetValue(pending.Dequeue(), out var children)) continue;
            foreach (var child in children)
            {
                if (demoIds.Add(child)) pending.Enqueue(child);
            }
        }
    }

    /// <summary>
    /// Best-effort: sign-in blocks on this, so a slow or failing auth admin API must degrade to the
    /// profile-name fallback rather than stall the request.
    /// </summary>
    private async Task<HashSet<Guid>> FetchDemoIdsFromAuthAsync(CancellationToken ct)
    {
        var demoIds = new HashSet<Guid>();
        var opts = jwtOptions.Value;
        if (string.IsNullOrWhiteSpace(opts.Url) || string.IsNullOrWhiteSpace(opts.ServiceRoleKey))
            return demoIds;

        try
        {
            var client = httpClientFactory.CreateClient();
            client.Timeout = AuthDirectoryTimeout;
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", opts.ServiceRoleKey);
            client.DefaultRequestHeaders.TryAddWithoutValidation("apikey", opts.ServiceRoleKey);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(AuthDirectoryTimeout);

            var res = await client.GetAsync(
                $"{opts.Url.TrimEnd('/')}/auth/v1/admin/users?per_page=1000",
                timeoutCts.Token);
            if (!res.IsSuccessStatusCode)
                return demoIds;

            var json = await res.Content.ReadAsStringAsync(timeoutCts.Token);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("users", out var usersEl) || usersEl.ValueKind != JsonValueKind.Array)
                return demoIds;

            foreach (var u in usersEl.EnumerateArray())
            {
                if (!u.TryGetProperty("id", out var idEl) || !Guid.TryParse(idEl.GetString(), out var id))
                    continue;
                var email = u.TryGetProperty("email", out var emailEl) ? emailEl.GetString() : null;
                if (NdDemoIsolationHelper.IsDemoEmail(email))
                    demoIds.Add(id);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
        {
            return demoIds;
        }

        return demoIds;
    }

  /// <summary>Fallback when auth admin API is unavailable — match profiles whose name contains "demo".</summary>
    private async Task MergeDemoIdsFromProfilesAsync(HashSet<Guid> demoIds, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var profileIds = await db.NdProfiles.AsNoTracking()
            .Where(p => EF.Functions.ILike(p.FullName, "%demo%"))
            .Select(p => p.Id)
            .ToListAsync(ct);
        foreach (var id in profileIds)
            demoIds.Add(id);
    }
}
