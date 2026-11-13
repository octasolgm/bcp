using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace Reguliq.Api.Infrastructure;

/// <summary>
/// Application-layer rate limiting. A generous global per-client window is a flood / denial-of-service
/// backstop that normal use never reaches; a strict <see cref="AuthPolicy"/> window guards unauthenticated
/// sign-in style endpoints (password reset / magic link) against brute force and abuse.
///
/// Requests are partitioned by client: the first X-Forwarded-For hop when present (the real caller behind
/// Azure's proxy), otherwise the connection IP. The limits below are deliberate starting points - tune them
/// per environment (offices behind a shared NAT share one key, so raise the global limit or partition by
/// authenticated user if that becomes a problem).
/// </summary>
public static class RateLimiterSetup
{
    /// <summary>Named policy for sign-in / password-reset endpoints. Apply with [EnableRateLimiting].</summary>
    public const string AuthPolicy = "auth";

    public static void AddBcpRateLimiting(IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            // Over-limit requests get 429 Too Many Requests rather than being queued.
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            // Global backstop: generous, so a legitimate dashboard session is never throttled, but a
            // request flood from one client is capped.
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
                RateLimitPartition.GetSlidingWindowLimiter(
                    ClientKey(ctx),
                    _ => new SlidingWindowRateLimiterOptions
                    {
                        PermitLimit = 600,
                        Window = TimeSpan.FromMinutes(1),
                        SegmentsPerWindow = 6,
                        QueueLimit = 0,
                    }));

            // Strict window for sign-in style endpoints: stops password / one-time-code brute force and
            // magic-link spam from a single client.
            options.AddPolicy(AuthPolicy, ctx =>
                RateLimitPartition.GetFixedWindowLimiter(
                    ClientKey(ctx),
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 10,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                    }));
        });
    }

    private static string ClientKey(HttpContext ctx)
    {
        var forwarded = ctx.Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(forwarded))
            return forwarded.Split(',')[0].Trim();
        return ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }
}
