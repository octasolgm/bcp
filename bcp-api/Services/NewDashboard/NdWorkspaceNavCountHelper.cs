using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Data.NewDashboard.Entities;
using Reguliq.Api.Services.NewDashboard.Demo;

namespace Reguliq.Api.Services.NewDashboard;

/// <summary>Sidebar document counts aligned with library list endpoints.</summary>
public static class NdWorkspaceNavCountHelper
{
    private const int StatusHidden = -1;

    /// <summary>Visible regulation library cards — matches GET /nd/regulation-documents (no filters).</summary>
    public static async Task<int> CountVisibleRegulationDocumentsAsync(
        AppDbContext db,
        NdDemoIsolationContext demoCtx,
        CancellationToken ct)
    {
        var ndDocs = await NdDemoDataFilters.ApplyToRegulationDocuments(
                db.NdRegulationDocuments.AsNoTracking(),
                demoCtx)
            .ToListAsync(ct);

        var ndByStoredId = ndDocs
            .Where(d =>
                d.Status != StatusHidden
                && d.StoredDocumentId.HasValue
                && !NdDemoDataFilters.IsRegulationDepartmentOverlay(d))
            .GroupBy(d => d.StoredDocumentId!.Value)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(d => d.CreatedAt).First());

        var ndForLegacyByStoredId = ndDocs
            .Where(d =>
                d.Status != StatusHidden
                && d.StoredDocumentId.HasValue
                && !NdDemoDataFilters.IsRegulationDepartmentOverlay(d)
                && !d.IsManual)
            .GroupBy(d => d.StoredDocumentId!.Value)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(d => d.CreatedAt).First());

        List<Guid> legacyStoredIds;
        try
        {
            legacyStoredIds = await NdDemoDataFilters.ApplyToStoredDocuments(
                    db.StoredDocuments.AsNoTracking()
                        .Where(d => d.DocKind == "regulation" && !d.IsHidden),
                    demoCtx)
                .Select(d => d.Id)
                .ToListAsync(ct);
        }
        catch
        {
            legacyStoredIds = [];
        }

        if (legacyStoredIds.Count > 0)
        {
            var linkedNd = await db.NdRegulationDocuments.AsNoTracking()
                .Where(d =>
                    d.StoredDocumentId != null
                    && legacyStoredIds.Contains(d.StoredDocumentId.Value)
                    && d.Status != StatusHidden
                    && !d.IsManual)
                .ToListAsync(ct);

            foreach (var linked in linkedNd)
            {
                if (NdDemoDataFilters.IsRegulationDepartmentOverlay(linked)) continue;
                if (ndDocs.Any(d => d.Id == linked.Id)) continue;
                ndDocs.Add(linked);
                if (linked.StoredDocumentId is Guid sid)
                    ndForLegacyByStoredId[sid] = linked;
            }
        }

        var hiddenStoredIds = ndDocs
            .Where(d => d.Status == StatusHidden && d.StoredDocumentId.HasValue)
            .Select(d => d.StoredDocumentId!.Value)
            .ToHashSet();

        var manualsWithPoints = await ManualIdsWithActivePointsAsync(
            db,
            ndDocs.Where(d => d.IsManual).Select(d => d.Id),
            ct);

        var count = 0;

        foreach (var storedId in legacyStoredIds)
        {
            if (hiddenStoredIds.Contains(storedId)) continue;
            if (ndForLegacyByStoredId.ContainsKey(storedId)) continue;
            count++;
        }

        foreach (var d in ndDocs)
        {
            if (d.Status == StatusHidden) continue;
            if (NdDemoDataFilters.IsRegulationDepartmentOverlay(d)) continue;
            if (d.IsManual)
            {
                if (manualsWithPoints.Contains(d.Id)) count++;
                continue;
            }

            if (d.StoredDocumentId is Guid storedKey
                && ndByStoredId.TryGetValue(storedKey, out var canonicalNd)
                && canonicalNd.Id != d.Id)
                continue;

            count++;
        }

        if (!ndDocs.Any(d => d.IsManual))
        {
            var manualDoc = await db.NdRegulationDocuments.AsNoTracking()
                .FirstOrDefaultAsync(d => d.IsManual && d.Status != StatusHidden, ct);
            if (manualDoc != null)
            {
                var hasPoints = await db.NdRegulationPoints.AsNoTracking()
                    .AnyAsync(p => p.RegulationDocumentId == manualDoc.Id, ct);
                if (hasPoints)
                    count++;
            }
        }

        return count;
    }

    /// <summary>A manual regulation counts as a library document only after it has at least one active point.</summary>
    public static async Task<HashSet<Guid>> ManualIdsWithActivePointsAsync(
        AppDbContext db,
        IEnumerable<Guid> manualDocumentIds,
        CancellationToken ct)
    {
        var ids = manualDocumentIds.Distinct().ToList();
        if (ids.Count == 0) return [];

        var withPoints = await db.NdRegulationPoints.AsNoTracking()
            .Where(p => ids.Contains(p.RegulationDocumentId))
            .Select(p => p.RegulationDocumentId)
            .Distinct()
            .ToListAsync(ct);
        return withPoints.ToHashSet();
    }
}
