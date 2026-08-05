using Reguliq.Api.Data.Entities;
using Reguliq.Api.Services.LandingAi;
using Reguliq.Api.Services.Storage;

namespace Reguliq.Api.Services.Pdf;

public sealed class PdfNativePageDocumentLoader(SupabaseStorageService storage)
{
    public async Task<PdfNativePageDocument?> TryLoadForDocumentAsync(
        StoredDocument doc,
        CancellationToken ct = default)
    {
        if (!storage.IsConfigured || string.IsNullOrWhiteSpace(doc.StoragePath))
            return null;

        var fileName = doc.OriginalFileName ?? Path.GetFileName(doc.StoragePath) ?? doc.Title ?? "document.pdf";
        try
        {
            var bytes = await storage.DownloadAsync(doc.StoragePath, ct);
            if (!LandingAiDocumentFormats.IsPdf(fileName, bytes))
                return null;

            return PdfNativePageDocument.TryCreate(bytes);
        }
        catch
        {
            return null;
        }
    }

    public async Task<PdfNativePageDocument?> TryLoadFromBytesAsync(
        byte[] bytes,
        string fileName,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!LandingAiDocumentFormats.IsPdf(fileName, bytes))
            return null;

        return PdfNativePageDocument.TryCreate(bytes);
    }
}
