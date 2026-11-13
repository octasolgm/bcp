namespace Reguliq.Api.Infrastructure;

/// <summary>
/// Resolves a stored file's Content-Type from its file extension against a safe allow-list, instead of
/// trusting the client-sent Content-Type. Unknown or risky types (e.g. .html, .svg, .js) fall back to
/// application/octet-stream, which browsers download rather than render - so a file mislabelled by the
/// uploader can't be served back as active content (stored XSS).
/// </summary>
public static class SafeContentType
{
    public const string Fallback = "application/octet-stream";

    private static readonly Dictionary<string, string> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = "application/pdf",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".bmp"] = "image/bmp",
        [".tif"] = "image/tiff",
        [".tiff"] = "image/tiff",
        [".txt"] = "text/plain; charset=utf-8",
        [".csv"] = "text/csv; charset=utf-8",
        [".doc"] = "application/msword",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xls"] = "application/vnd.ms-excel",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".ppt"] = "application/vnd.ms-powerpoint",
        [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        [".json"] = "application/json",
        [".zip"] = "application/zip",
    };

    /// <summary>Safe Content-Type for an uploaded file, based only on its (trusted-server-side) extension.</summary>
    public static string FromFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return Fallback;
        var ext = Path.GetExtension(fileName);
        return ByExtension.TryGetValue(ext, out var type) ? type : Fallback;
    }
}
