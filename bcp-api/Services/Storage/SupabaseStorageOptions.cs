namespace Reguliq.Api.Services.Storage;

public class SupabaseStorageOptions
{
    /// <summary>Project URL, e.g. https://xxxx.supabase.co</summary>
    public string Url { get; set; } = "";

    /// <summary>service_role key — server only; never expose to the browser.</summary>
    public string ServiceRoleKey { get; set; } = "";

    /// <summary>Private Storage bucket name.</summary>
    public string Bucket { get; set; } = "doc";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Url)
        && !string.IsNullOrWhiteSpace(ServiceRoleKey)
        && !IsPlaceholderKey(ServiceRoleKey);

    private static bool IsPlaceholderKey(string key)
    {
        var k = key.Trim();
        if (k.Length < 40) return true;
        if (k.Contains("PASTE_", StringComparison.OrdinalIgnoreCase)) return true;
        if (k.Contains("YOUR_", StringComparison.OrdinalIgnoreCase)) return true;
        if (k.Contains("SERVICE_ROLE_KEY", StringComparison.OrdinalIgnoreCase)
            && !k.StartsWith("eyJ", StringComparison.Ordinal))
            return true;
        return false;
    }
}
