using Microsoft.AspNetCore.Authorization;

namespace Reguliq.Api.Infrastructure.NewDashboard;

/// <summary>
/// Default-deny safety net for the ND API. Any request under <c>/nd</c> must carry a validated user
/// (set by <see cref="WorkspaceResolutionMiddleware"/>) unless its endpoint is explicitly marked
/// <see cref="AllowAnonymousAttribute"/>. It runs immediately after the workspace middleware.
///
/// This does NOT replace the controllers' own role and workspace checks; those still decide what an
/// authenticated caller may do. It guarantees that an ND endpoint which forgets to call a guard still
/// cannot be reached unauthenticated - closing the "authorization is opt-in" gap without rewiring the
/// existing manual auth model. Scope is limited to <c>/nd</c>; legacy routes keep LegacyApiAccessAttribute.
/// </summary>
public sealed class NdRequireAuthenticatedMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        // Only guard the ND surface; let CORS preflight through untouched.
        if (!context.Request.Path.StartsWithSegments("/nd")
            || HttpMethods.IsOptions(context.Request.Method))
        {
            await next(context);
            return;
        }

        var endpoint = context.GetEndpoint();
        var allowAnonymous = endpoint?.Metadata.GetMetadata<IAllowAnonymous>() != null;
        var authenticated = context.Items.ContainsKey(WorkspaceResolutionMiddleware.JwtUserItemKey);

        if (!allowAnonymous && !authenticated)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { success = false, message = "Unauthorized" });
            return;
        }

        await next(context);
    }
}
