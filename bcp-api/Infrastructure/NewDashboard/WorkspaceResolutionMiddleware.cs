using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Reguliq.Api.Data;

namespace Reguliq.Api.Infrastructure.NewDashboard;

/// <summary>
/// Resolves the caller's workspace before any ND controller runs, so every EF query in the request is
/// filtered to it (see AppDbContext tenant filters). Runs ahead of the controllers' own auth checks,
/// which still decide whether the request is allowed at all.
///
/// Workspace users always act in their home workspace. A platform admin acts in the workspace they
/// switched into (profiles.active_tenant_id), falling back to their home workspace.
/// </summary>
public sealed class WorkspaceResolutionMiddleware(RequestDelegate next)
{
    /// <summary>HttpContext.Items key for the JWT user validated here, reused by NdControllerBase.</summary>
    public const string JwtUserItemKey = "nd:jwt-user";

    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    public static string CacheKey(Guid profileId) => $"nd:ws-scope:{profileId}";

    public async Task InvokeAsync(
        HttpContext context,
        SupabaseJwtValidator jwt,
        IMemoryCache cache,
        AppDbContext db)
    {
        WorkspaceScope.Set(null);

        if (context.Request.Path.StartsWithSegments("/nd")
            && context.Request.Headers.Authorization.FirstOrDefault() is { Length: > 0 } authHeader)
        {
            var user = await jwt.ValidateTokenAsync(authHeader, context.RequestAborted);
            if (user != null)
            {
                context.Items[JwtUserItemKey] = user;
                WorkspaceScopeState? state;
                try
                {
                    state = await ResolveAsync(user.UserId, cache, db, context.RequestAborted);
                }
                catch (Exception ex) when (ex is Npgsql.NpgsqlException or TimeoutException or InvalidOperationException
                    || ex.GetBaseException() is System.Net.Sockets.SocketException or TimeoutException)
                {
                    // Never fall through unfiltered: without a workspace the request would see every tenant.
                    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    await context.Response.WriteAsJsonAsync(new
                    {
                        success = false,
                        message = "Database temporarily unreachable. Wait a moment and try signing in again.",
                    });
                    return;
                }
                WorkspaceScope.Set(state);
            }
        }

        await next(context);
    }

    private static async Task<WorkspaceScopeState?> ResolveAsync(
        Guid userId,
        IMemoryCache cache,
        AppDbContext db,
        CancellationToken ct)
    {
        var key = CacheKey(userId);
        if (cache.TryGetValue<WorkspaceScopeState>(key, out var cached) && cached != null)
            return cached;

        var profile = await db.NdProfiles.AsNoTracking()
            .Where(p => p.Id == userId)
            .Select(p => new { p.TenantId, p.ActiveTenantId, p.IsPlatformAdmin })
            .FirstOrDefaultAsync(ct);

        // No profile yet (first sign-in creates it): no data can be owned by this user, so scope them to
        // the Default workspace rather than leaving the request unfiltered.
        var home = profile?.TenantId ?? WorkspaceScope.DefaultWorkspaceId;
        var target = home;
        if (profile is { IsPlatformAdmin: true, ActiveTenantId: Guid active })
            target = active;

        var workspace = await db.NdWorkspaces.AsNoTracking()
            .Where(w => w.Id == target)
            .Select(w => new { w.Id, w.IsActive })
            .FirstOrDefaultAsync(ct);

        if (workspace == null && target != home)
        {
            // Switched-into workspace was deleted: drop back home.
            target = home;
            workspace = await db.NdWorkspaces.AsNoTracking()
                .Where(w => w.Id == target)
                .Select(w => new { w.Id, w.IsActive })
                .FirstOrDefaultAsync(ct);
        }

        var state = new WorkspaceScopeState(
            target,
            home,
            workspace?.IsActive ?? false,
            profile?.IsPlatformAdmin ?? false);
        cache.Set(key, state, CacheTtl);
        return state;
    }
}
