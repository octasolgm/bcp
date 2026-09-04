namespace Reguliq.Api.Services.LocalDocs;

/// <summary>Which file types the local (non-AI) parse pipeline accepts. Reject anything else at upload time.</summary>
public static class SupportedDocumentTypes
{
    public static readonly IReadOnlySet<string> AllowedExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".pdf", ".docx" };

    public static bool IsSupported(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return false;
        var ext = Path.GetExtension(fileName);
        return AllowedExtensions.Contains(ext);
    }

    public static string DescribeAllowed() => string.Join(", ", AllowedExtensions);
}
