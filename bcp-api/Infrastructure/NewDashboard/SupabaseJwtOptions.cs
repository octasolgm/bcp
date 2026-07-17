namespace Reguliq.Api.Infrastructure.NewDashboard;

public class SupabaseJwtOptions
{
    public string JwtSecret { get; set; } = "";
    public string Url { get; set; } = "";
    public string ServiceRoleKey { get; set; } = "";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(JwtSecret);

    /// <summary>Supabase user JWT issuer, e.g. https://project.supabase.co/auth/v1</summary>
    public string ExpectedIssuer =>
        string.IsNullOrWhiteSpace(Url) ? "" : $"{Url.TrimEnd('/')}/auth/v1";
}
