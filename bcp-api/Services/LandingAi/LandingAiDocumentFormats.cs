namespace Reguliq.Api.Services.LandingAi;

/// <summary>MIME types and helpers for Landing AI ADE parse uploads (PDF, Word, etc.).</summary>
public static class LandingAiDocumentFormats
{
    public static bool IsWordDocument(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext is ".docx" or ".doc";
    }

    public static bool IsSupportedNdUpload(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext is ".pdf" or ".docx" or ".doc";
    }

    public static bool IsPdf(string fileName, byte[]? bytes = null)
    {
        if (Path.GetExtension(fileName).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            return true;
        if (bytes is { Length: >= 4 })
        {
            return bytes[0] == 0x25 && bytes[1] == 0x50 && bytes[2] == 0x44 && bytes[3] == 0x46;
        }

        return false;
    }

    /// <summary>Content-Type for ADE <c>/v1/ade/parse</c> multipart upload.</summary>
    public static string ContentTypeForFileName(string fileName)
    {
        return Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".pdf" => "application/pdf",
            ".doc" => "application/msword",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            _ => "application/octet-stream",
        };
    }

    public static string DetectStoredFileType(string fileName) =>
        IsWordDocument(fileName) ? "DOC" : IsPdf(fileName) ? "PDF" : "DOC";
}
