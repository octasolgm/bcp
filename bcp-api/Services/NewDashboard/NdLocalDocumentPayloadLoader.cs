using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Data.Entities;
using Reguliq.Api.Models;
using Reguliq.Api.Services.LocalDocs;
using Reguliq.Api.Services.Storage;

namespace Reguliq.Api.Services.NewDashboard;

/// <summary>Builds analysis payloads from azure-di local extraction rows when available.</summary>
public sealed class NdLocalDocumentPayloadLoader(AppDbContext db, SupabaseStorageService storage)
{
    public async Task<InternalDocPayload?> TryFromAzureDiExtractionAsync(
        StoredDocument doc,
        CancellationToken ct = default)
    {
        var row = await db.NdLocalDocumentExtractions.AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.StoredDocumentId == doc.Id
                    && x.Engine == OcrEngineNames.AzureDocIntelligence
                    && x.Status == "parsed"
                    && !string.IsNullOrWhiteSpace(x.MarkdownText),
                ct);
        if (row == null) return null;

        byte[]? pdf = null;
        if (storage.IsConfigured && !string.IsNullOrWhiteSpace(doc.StoragePath))
        {
            try
            {
                pdf = await storage.DownloadAsync(doc.StoragePath, ct);
            }
            catch
            {
                /* markdown-only payload is still usable */
            }
        }

        var fileName = doc.OriginalFileName ?? doc.Title ?? "document";
        var hash = doc.FileHash?.Trim() ?? doc.Id.ToString("N");
        return new InternalDocPayload(hash, fileName, row.MarkdownText!, pdf);
    }
}
