namespace Reguliq.Api.Services.NewDashboard;

/// <summary>
/// Per-upload Landing AI cache keys — separate from content SHA-256 so re-uploading the same
/// PDF bytes creates a fresh parse/extract pipeline for each stored document (regulation or internal).
/// </summary>
public static class NdRegulationCacheKeys
{
    public const string Prefix = "nd-reg:";

    public static string ForStoredDocument(Guid storedDocumentId) =>
        $"{Prefix}{storedDocumentId:N}";

    public static string ForRegulationDocument(Guid regulationDocumentId) =>
        $"{Prefix}doc-{regulationDocumentId:N}";
}
