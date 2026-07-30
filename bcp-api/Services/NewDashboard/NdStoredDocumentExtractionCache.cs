using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Data.Entities;

namespace Reguliq.Api.Services.NewDashboard;

/// <summary>
/// Per-document Landing AI cache keys (by stored document id), not content SHA-256,
/// so duplicate uploads each get their own parse/extract cache and library rows.
/// </summary>
public static class NdStoredDocumentExtractionCache
{
    public static async Task<string> EnsureKeyAsync(
        AppDbContext db,
        StoredDocument stored,
        CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(stored.ExtractionCacheKey))
            return stored.ExtractionCacheKey.Trim();

        stored.ExtractionCacheKey = NdRegulationCacheKeys.ForStoredDocument(stored.Id);
        stored.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return stored.ExtractionCacheKey;
    }

    public static async Task<string> EnsureKeyByIdAsync(
        AppDbContext db,
        Guid storedDocumentId,
        CancellationToken ct = default)
    {
        var stored = await db.StoredDocuments.FirstOrDefaultAsync(d => d.Id == storedDocumentId, ct)
            ?? throw new InvalidOperationException("Stored document not found.");
        return await EnsureKeyAsync(db, stored, ct);
    }
}
