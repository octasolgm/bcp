namespace Reguliq.Api.Infrastructure.NewDashboard;

public class SupabaseJwtOptions
{
    public string JwtSecret { get; set; } = "";
    public string Url { get; set; } = "";
    public string ServiceRoleKey { get; set; } = "";

    /// <summary>
    /// Publishable (anon) key. Used only server-side, as the apikey on the sign-in / refresh / sign-out
    /// calls the API makes on the browser's behalf. The browser never receives it. Falls back to
    /// <see cref="ServiceRoleKey"/> when not set.
    /// </summary>
    public string AnonKey { get; set; } = "";

    public string ApiKey => string.IsNullOrWhiteSpace(AnonKey) ? ServiceRoleKey : AnonKey;

    /// <summary>
    /// Explicit, off-by-default opt-in for the local-only path that parses a JWT without checking its
    /// signature (used when no JwtSecret/JWKS is configured). Even when true it only applies in the
    /// Development environment off Azure App Service, so it can never accept a forged token in production.
    /// </summary>
    public bool AllowUnsignedJwtInDevelopment { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(JwtSecret);

    /// <summary>Supabase user JWT issuer, e.g. https://project.supabase.co/auth/v1</summary>
    public string ExpectedIssuer =>
        string.IsNullOrWhiteSpace(Url) ? "" : $"{Url.TrimEnd('/')}/auth/v1";
}
