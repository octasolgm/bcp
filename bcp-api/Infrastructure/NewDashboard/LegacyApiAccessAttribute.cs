using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;

namespace Reguliq.Api.Infrastructure.NewDashboard;

public enum LegacyApiAccess
{
    /// <summary>Any signed-in, active user. Workspace filters still scope what they can see.</summary>
    SignedIn,

    /// <summary>
    /// Signed-in user acting in the Default workspace. Legacy tables (dual_verify_sessions,
    /// compliance_sessions, document_analysis_runs) carry no tenant and belong to the Default workspace only.
    /// </summary>
    DefaultWorkspace,

    /// <summary>Platform super admin only (reseeding shared data).</summary>
    PlatformAdmin,
}

/// <summary>
/// Login check for the pre-ND controllers (/documents, /landing-ai, /dual-verify-kafka, /bcpweb), which used to
/// accept anonymous calls. Put it on the controller for the default level and on an action to override it;
/// only the attribute closest to the action runs. The JWT is validated once by WorkspaceResolutionMiddleware.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class LegacyApiAccessAttribute(LegacyApiAccess access = LegacyApiAccess.DefaultWorkspace)
    : Attribute, IAsyncActionFilter
{
    public LegacyApiAccess Access { get; } = access;

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        // Controller and action attributes both run as filters; let the action-level one decide.
        var effective = context.ActionDescriptor.EndpointMetadata.OfType<LegacyApiAccessAttribute>().LastOrDefault();
        if (effective != null && !ReferenceEquals(effective, this))
        {
            await next();
            return;
        }

        var error = await CheckAsync(context.HttpContext);
        if (error != null)
        {
            context.Result = error;
            return;
        }

        await next();
    }

    private async Task<IActionResult?> CheckAsync(HttpContext http)
    {
        var user = http.Items[WorkspaceResolutionMiddleware.JwtUserItemKey] as JwtUser;
        if (user == null)
        {
            var jwt = http.RequestServices.GetRequiredService<SupabaseJwtValidator>();
            user = await jwt.ValidateTokenAsync(http.Request.Headers.Authorization.FirstOrDefault(), http.RequestAborted);
        }
        if (user == null)
            return Deny(401, "Unauthorized");

        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        var profile = await db.NdProfiles.AsNoTracking()
            .Where(p => p.Id == user.UserId)
            .Select(p => new { p.IsActive, p.Role, p.IsPlatformAdmin })
            .FirstOrDefaultAsync(http.RequestAborted);
        if (profile == null)
            return Deny(401, "Profile not found");
        if (!profile.IsActive)
            return Deny(403, "Account deactivated");

        var isPlatformAdmin = profile.IsPlatformAdmin
            && string.Equals(profile.Role, "super_admin", StringComparison.OrdinalIgnoreCase);

        // Never run unscoped: the tenant filters would return every workspace's rows.
        var scope = WorkspaceScope.State;
        if (scope == null)
            return Deny(401, "Unauthorized");
        if (!scope.WorkspaceIsActive && !isPlatformAdmin)
            return Deny(403, "Your workspace has been deactivated. Contact your administrator.");

        switch (Access)
        {
            case LegacyApiAccess.PlatformAdmin when !isPlatformAdmin:
                return Deny(403, "Only the platform super admin can do this.");
            // Seeds write shared Default-workspace data, so the platform admin must be acting there too.
            case LegacyApiAccess.PlatformAdmin or LegacyApiAccess.DefaultWorkspace when !WorkspaceScope.InDefaultWorkspace:
                return Deny(403, "This feature is not available in your workspace.");
        }

        return null;
    }

    private static ObjectResult Deny(int status, string message) =>
        new(new { success = false, message }) { StatusCode = status };
}
