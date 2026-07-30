using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Data.NewDashboard.Entities;
using Reguliq.Api.Services.LandingAi;

namespace Reguliq.Api.Services.NewDashboard;

public sealed class NdRegulationPointRepairService(
    AppDbContext db,
    LandingAiCacheRepository cache,
    ILogger<NdRegulationPointRepairService> logger)
{
    public sealed record RepairResult(
        int BeforeCount,
        int AfterCount,
        int SoftDeleted,
        int DuplicateGroups,
        int JunkRemoved,
        int PagesRefreshed);

    public async Task<RepairResult> RepairDocumentAsync(Guid regulationDocumentId, CancellationToken ct = default)
    {
        var doc = await db.NdRegulationDocuments.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == regulationDocumentId, ct);
        if (doc == null)
            throw new InvalidOperationException("Regulation document not found.");
        if (doc.IsManual)
            throw new InvalidOperationException("Repair is not available for manual point documents.");

        var points = await db.NdRegulationPoints
            .Where(p => p.RegulationDocumentId == regulationDocumentId)
            .ToListAsync(ct);
        if (points.Count == 0)
            return new RepairResult(0, 0, 0, 0, 0, 0);

        var plan = GovPointExtractNormalizer.PlanRepair(
            points,
            p => p.Id,
            p => p.PointNumber,
            p => p.PointTitle,
            p => p.PointContent,
            p => ParsePageFromReference(p.PageReference));

        var now = DateTimeOffset.UtcNow;
        foreach (var id in plan.SoftDeleteIds)
        {
            var row = points.First(p => p.Id == id);
            row.Status = NdRegulationPointStatus.Removed;
        }

        foreach (var (id, newNumber) in plan.RenumberTo)
        {
            var row = points.First(p => p.Id == id);
            row.PointNumber = newNumber;
        }

        var pagesRefreshed = await RefreshPageReferencesAsync(doc, points.Where(p => plan.KeepIds.Contains(p.Id)).ToList(), ct);

        doc = await db.NdRegulationDocuments.FirstAsync(d => d.Id == regulationDocumentId, ct);
        doc.UpdatedAt = now;
        await db.SaveChangesAsync(ct);

        var afterCount = points.Count(p => p.Status == NdRegulationPointStatus.Active);
        logger.LogInformation(
            "Repaired regulation points for {DocId}: {Before} → {After} (soft-deleted {Removed}, dup groups {Dup}, junk {Junk})",
            regulationDocumentId,
            points.Count,
            afterCount,
            plan.SoftDeleteIds.Count,
            plan.DuplicateGroups,
            plan.JunkRemoved);

        return new RepairResult(
            points.Count,
            afterCount,
            plan.SoftDeleteIds.Count,
            plan.DuplicateGroups,
            plan.JunkRemoved,
            pagesRefreshed);
    }

    private async Task<int> RefreshPageReferencesAsync(
        NdRegulationDocument doc,
        IReadOnlyList<NdRegulationPoint> keepPoints,
        CancellationToken ct)
    {
        if (keepPoints.Count == 0) return 0;

        string? markdown = doc.ExtractionMarkdown;
        string? fileHash = null;

        if (doc.StoredDocumentId is Guid storedId)
        {
            var stored = await db.StoredDocuments.AsNoTracking()
                .FirstOrDefaultAsync(d => d.Id == storedId, ct);
            fileHash = stored?.FileHash;
            if (string.IsNullOrWhiteSpace(markdown) && !string.IsNullOrWhiteSpace(stored?.ExtractionCacheKey))
            {
                var cached = await cache.GetParseCacheAsync(stored!.ExtractionCacheKey!, ct);
                markdown = cached?.Markdown;
            }
        }

        if (string.IsNullOrWhiteSpace(markdown) && !string.IsNullOrWhiteSpace(fileHash))
        {
            var cached = await cache.GetParseCacheAsync(fileHash, ct);
            markdown = cached?.Markdown;
        }

        if (string.IsNullOrWhiteSpace(markdown)) return 0;

        int? pdfPageCount = PolicyPageResolver.EstimatePageCount(markdown);
        var refreshed = 0;

        foreach (var point in keepPoints)
        {
            var pageHint = ParsePageFromReference(point.PageReference);
            var resolved = PolicyPageResolver.ResolveGovPointPage(
                markdown,
                point.PointNumber,
                ExtractSectionFromReference(point.PageReference),
                point.PointTitle,
                point.PointContent,
                pageHint,
                pdfPageCount);
            resolved = PolicyPageResolver.RefinePageGuess(resolved, point.PointNumber, pdfPageCount);
            var formatted = FormatPointPageReference(
                ExtractSectionFromReference(point.PageReference) ?? point.PointNumber,
                resolved);
            if (formatted != point.PageReference)
            {
                point.PageReference = formatted;
                refreshed++;
            }
        }

        return refreshed;
    }

    private static int? ParsePageFromReference(string? pageReference)
    {
        if (string.IsNullOrWhiteSpace(pageReference)) return null;
        var m = System.Text.RegularExpressions.Regex.Match(pageReference, @"p\.\s*(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success && int.TryParse(m.Groups[1].Value, out var page) ? page : null;
    }

    private static string? ExtractSectionFromReference(string? pageReference)
    {
        if (string.IsNullOrWhiteSpace(pageReference)) return null;
        var idx = pageReference.IndexOf(" · p.", StringComparison.OrdinalIgnoreCase);
        return idx > 0 ? pageReference[..idx].Trim() : pageReference.Trim();
    }

    private static string? FormatPointPageReference(string? section, int? pdfPage)
    {
        var sec = section?.Trim();
        if (pdfPage is > 0)
            return string.IsNullOrWhiteSpace(sec) ? $"p. {pdfPage}" : $"{sec} · p. {pdfPage}";
        return sec;
    }
}
