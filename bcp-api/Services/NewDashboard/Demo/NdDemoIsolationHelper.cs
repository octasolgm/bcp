using Microsoft.AspNetCore.Mvc;
using Reguliq.Api.Infrastructure.NewDashboard;

namespace Reguliq.Api.Services.NewDashboard.Demo;

public static class NdDemoIsolationHelper
{
    public static bool IsDemoEmail(string? email) =>
        !string.IsNullOrWhiteSpace(email)
        && email.Contains("demo", StringComparison.OrdinalIgnoreCase);

    public static bool IsDemoName(string? fullName) =>
        !string.IsNullOrWhiteSpace(fullName)
        && fullName.Contains("demo", StringComparison.OrdinalIgnoreCase);

    public static bool IsDemoUser(JwtUser? user) =>
        user != null && IsDemoEmail(user.Email);

    /// <summary>
    /// Demo viewer or any resource owned by a demo profile — never call live AI.
    /// </summary>
    public static bool ShouldSimulateAi(NdDemoIsolationContext ctx, Guid? resourceOwnerId)
    {
        if (!ctx.Enabled) return false;
        if (ctx.ViewerIsDemo) return true;
        return resourceOwnerId is Guid owner && ctx.DemoProfileIds.Contains(owner);
    }

    /// <summary>Service-layer check: acting user or resource owner is a demo profile.</summary>
    public static async Task<bool> ShouldSimulateAiAsync(
        NdDemoUserDirectory directory,
        Guid? actingUserId,
        Guid? resourceOwnerId,
        CancellationToken ct = default) =>
        await directory.ShouldSimulateForProfilesAsync(actingUserId, resourceOwnerId, ct);

    /// <summary>Returns 403 when demo isolation is active and the viewer is a demo user (no AI credits).</summary>
    public static ObjectResult? ForbidDemoAiOperations(
        NdDemoIsolationContext ctx,
        string? message = null)
    {
        if (!ctx.Enabled || !ctx.ViewerIsDemo) return null;
        return new ObjectResult(new
        {
            success = false,
            message = message ?? "Demo accounts use simulated data only; AI processing is not available.",
        })
        {
            StatusCode = 403,
        };
    }

    /// <summary>Blocks live AI on runs created by demo profiles (even when a super admin triggers the action).</summary>
    public static async Task<ObjectResult?> ForbidLiveAiOnDemoOwnedRunAsync(
        NdDemoUserDirectory directory,
        Guid? runCreatedBy,
        CancellationToken ct = default)
    {
        if (!directory.IsEnabled || runCreatedBy is not Guid profileId) return null;
        if (!await directory.IsDemoProfileAsync(profileId, ct)) return null;
        return new ObjectResult(new
        {
            success = false,
            message = "Demo analysis runs use saved data only — live AI is disabled. Use Start to replay seeded results.",
        })
        {
            StatusCode = 403,
        };
    }

    public static bool ResolveEnabled(NdDemoIsolationOptions options)
    {
        var env = Environment.GetEnvironmentVariable("DEMO_MODE_ENABLED");
        if (!string.IsNullOrWhiteSpace(env) && bool.TryParse(env, out var parsed))
            return parsed;
        return options.DemoModeEnabled;
    }
}

public sealed record NdDemoIsolationContext(
    bool Enabled,
    bool ViewerIsDemo,
    HashSet<Guid> DemoProfileIds,
    JwtUser User)
{
    public static async Task<NdDemoIsolationContext> ResolveAsync(
        NdDemoUserDirectory directory,
        JwtUser user,
        CancellationToken ct)
    {
        return await directory.ResolveContextAsync(user, ct);
    }
}
