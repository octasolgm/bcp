using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Reguliq.Api.Data;
using Reguliq.Api.Services.NewDashboard;
using Reguliq.Api.Services.NewDashboard.Demo;

namespace Reguliq.Api.Infrastructure.NewDashboard;

/// <summary>
/// Applies department visibility to every endpoint that opens one analysis or one internal document by id, so a link
/// cannot reach what the lists hide. Most analyses and documents are not restricted, and that answer is cached
/// briefly because the database is remote and some of these endpoints are polled.
/// </summary>
public sealed class NdDepartmentAccessFilter(
    AppDbContext db,
    SupabaseJwtValidator jwt,
    NdDemoUserDirectory demoDirectory,
    IMemoryCache cache) : IAsyncActionFilter
{
    private static readonly TimeSpan RestrictedTtl = TimeSpan.FromSeconds(30);

    public static void Forget(IMemoryCache cache, Guid id) => cache.Remove(RestrictedKey(id));

    private static string RestrictedKey(Guid id) => $"nd-dept-restricted:{id}";

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (await IsDeniedAsync(context))
        {
            context.Result = new NotFoundObjectResult(new { success = false, message = "Not found" });
            return;
        }
        await next();
    }

    private async Task<bool> IsDeniedAsync(ActionExecutingContext context)
    {
        var http = context.HttpContext;
        var ct = http.RequestAborted;
        if (!http.Request.Path.StartsWithSegments("/nd")) return false;

        var values = context.RouteData.Values;
        var controller = values["controller"] as string;
        Guid? runId = null, documentId = null;
        if (Guid.TryParse(values["runId"] as string, out var routeRunId)) runId = routeRunId;
        else if (Guid.TryParse(values["id"] as string, out var routeId))
        {
            if (controller == "AnalysisRuns") runId = routeId;
            else if (controller is "InternalDocuments" or "LocalDocuments") documentId = routeId;
        }
        var targetId = runId ?? documentId;
        if (targetId is not Guid id) return false;

        var restricted = await cache.GetOrCreateAsync(RestrictedKey(id), async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = RestrictedTtl;
            return runId != null
                ? await db.NdAnalysisRunDepartments.AsNoTracking().AnyAsync(l => l.AnalysisRunId == id, ct)
                : await db.NdInternalDocumentDepartments.AsNoTracking().AnyAsync(l => l.StoredDocumentId == id, ct);
        });
        if (!restricted) return false;

        // No valid sign-in: the action itself answers with its own 401.
        var user = http.Items[WorkspaceResolutionMiddleware.JwtUserItemKey] as JwtUser
            ?? jwt.ValidateToken(http.Request.Headers.Authorization.FirstOrDefault());
        if (user == null) return false;
        var profile = await db.NdProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == user.UserId, ct);
        if (profile == null) return false;

        var demoCtx = await NdDemoIsolationContext.ResolveAsync(demoDirectory, user, ct);
        var scope = await NdDepartmentAccess.ResolveAsync(db, profile, demoCtx, ct);
        if (scope.Unrestricted) return false;

        var visible = runId != null
            ? await db.NdAnalysisRuns.AsNoTracking().Where(r => r.Id == id).ApplyToRuns(db, scope).AnyAsync(ct)
            : await db.StoredDocuments.AsNoTracking().Where(d => d.Id == id).ApplyToDocuments(db, scope).AnyAsync(ct);
        return !visible;
    }
}
