using System.Security.Cryptography;
using Reguliq.Api.Services.LandingAi;
using Reguliq.Api.Services.Storage;

namespace Reguliq.Api.Services.NewDashboard;

public sealed class NdStoredDocumentUploadService(SupabaseStorageService storage)
{
    public sealed record PreparedUpload(
        byte[] FileBytes,
        string StoragePath,
        string StoredFileName,
        string ContentType,
        string FileHash,
        long SizeBytes,
        string FileType,
        string OriginalFileName);

    public async Task<PreparedUpload> PrepareAsync(
        byte[] originalBytes,
        string originalFileName,
        string? contentType,
        string storagePrefix,
        CancellationToken ct)
    {
        if (originalBytes.Length == 0)
            throw new InvalidOperationException("No file provided.");

        if (!LandingAiDocumentFormats.IsSupportedNdUpload(originalFileName))
            throw new InvalidOperationException("Upload PDF or Word (.doc, .docx).");

        var titleKey = NormalizeKey(Path.GetFileNameWithoutExtension(originalFileName).Trim());
        var uploadId = Guid.NewGuid().ToString("N");
        var safeOriginal = SanitizeFileName(originalFileName);
        var objectPath = $"{storagePrefix}/{titleKey}/{uploadId}/{safeOriginal}";
        var resolvedType = string.IsNullOrWhiteSpace(contentType)
            ? LandingAiDocumentFormats.ContentTypeForFileName(originalFileName)
            : contentType;

        await using (var stream = new MemoryStream(originalBytes))
            await storage.UploadAsync(objectPath, stream, resolvedType, upsert: true, ct);

        return new PreparedUpload(
            originalBytes,
            objectPath,
            safeOriginal,
            resolvedType,
            HashBuffer(originalBytes),
            originalBytes.Length,
            LandingAiDocumentFormats.DetectStoredFileType(originalFileName),
            originalFileName);
    }

    private static string HashBuffer(byte[] data) =>
        Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static string NormalizeKey(string title)
    {
        var chars = title.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        var s = new string(chars);
        while (s.Contains("--", StringComparison.Ordinal)) s = s.Replace("--", "-", StringComparison.Ordinal);
        return s.Trim('-');
    }

    private static string SanitizeFileName(string name)
    {
        var baseName = Path.GetFileName(name);
        foreach (var c in Path.GetInvalidFileNameChars())
            baseName = baseName.Replace(c, '_');
        return baseName;
    }
}
