using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Data.Entities;
using Reguliq.Api.Data.NewDashboard.Entities;
using Reguliq.Api.Services.LandingAi;
using Reguliq.Api.Services.Pdf;

namespace Reguliq.Api.Services.NewDashboard;

public sealed class NdRegulationPointRepairService(
    AppDbContext db,
    LandingAiCacheRepository cache,
    NdDocumentPageReferenceResolver pageResolver,
    ILogger<NdRegulationPointRepairService> logger)
{
    public sealed record RepairResult(
        int BeforeCount,
        int AfterCount,
        int SoftDeleted,
        int DuplicateGroups,
        int JunkRemoved,
        int PagesRefreshed);

    /// <summary>Scan parse markdown for numbered headings missing from the library and add them.</summary>
    public async Task<int> RecoverMissingPointsAsync(Guid regulationDocumentId, CancellationToken ct = default)
    {
        var doc = await db.NdRegulationDocuments.FirstOrDefaultAsync(d => d.Id == regulationDocumentId, ct)
            ?? await db.NdRegulationDocuments.FirstOrDefaultAsync(d => d.StoredDocumentId == regulationDocumentId, ct)
            ?? throw new InvalidOperationException("Regulation document not found.");

        if (doc.IsManual) return 0;

        var points = await db.NdRegulationPoints
            .Where(p => p.RegulationDocumentId == doc.Id && p.Status == NdRegulationPointStatus.Active)
            .ToListAsync(ct);

        var markdown = doc.ExtractionMarkdown;
        if (string.IsNullOrWhiteSpace(markdown) && doc.StoredDocumentId is Guid storedId)
        {
            var stored = await db.StoredDocuments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == storedId, ct);
            if (!string.IsNullOrWhiteSpace(stored?.ExtractionCacheKey))
                markdown = (await cache.GetParseCacheAsync(stored.ExtractionCacheKey, ct))?.Markdown;
            if (string.IsNullOrWhiteSpace(markdown) && !string.IsNullOrWhiteSpace(stored?.FileHash))
                markdown = (await cache.GetParseCacheAsync(stored.FileHash, ct))?.Markdown;
        }

        if (string.IsNullOrWhiteSpace(markdown)) return 0;

        var govPoints = points.Select(p => new Models.GovPoint(
            p.PointNumber,
            p.PointTitle,
            p.PointContent,
            p.PageReference,
            ParsePageFromReference(p.PageReference),
            "mandatory")).ToList();

        var merged = GovPointMarkdownRecovery.MergeMissing(govPoints, markdown);
        if (merged.Count <= govPoints.Count) return 0;

        var existing = new HashSet<string>(
            points.Select(p => GovPointExtractNormalizer.NormalizePointNumberKey(p.PointNumber)),
            StringComparer.OrdinalIgnoreCase);
        var added = 0;

        foreach (var point in merged)
        {
            var key = GovPointExtractNormalizer.NormalizePointNumberKey(point.PointId);
            if (existing.Contains(key)) continue;

            int? resolvedPage = null;
            if (doc.StoredDocumentId is Guid sid)
            {
                var stored = await db.StoredDocuments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == sid, ct);
                if (stored is not null)
                {
                    resolvedPage = await pageResolver.ResolveSectionPageAsync(
                        stored,
                        markdown,
                        point.PointId,
                        point.Title,
                        point.Text,
                        ct);
                }
            }

            db.NdRegulationPoints.Add(new NdRegulationPoint
            {
                RegulationDocumentId = doc.Id,
                PointNumber = point.PointId,
                PointTitle = point.Title,
                PointContent = point.Text,
                PageReference = FormatPointPageReference(point.Section, resolvedPage),
                IsIntroductionPoint = GovPointClassifier.IsIntroductionPoint(
                    point.PointId, point.Title, point.Text, point.Section, point.PointType),
                IsAnnexPoint = GovPointClassifier.IsAnnexPoint(point.PointId, point.Title, point.Section),
            });
            existing.Add(key);
            added++;
        }

        if (added > 0)
        {
            doc.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "Recovered {Count} missing regulation point(s) from markdown for doc {DocId}",
                added,
                doc.Id);
        }

        return added;
    }

    /// <summary>Recompute point page references from native PDF text (preferred) or parse cache — no Landing AI credits.</summary>
    public async Task<int> RefreshPagesAsync(Guid regulationDocumentId, CancellationToken ct = default)
    {
        var doc = await db.NdRegulationDocuments.FirstOrDefaultAsync(d => d.Id == regulationDocumentId, ct)
            ?? await db.NdRegulationDocuments.FirstOrDefaultAsync(d => d.StoredDocumentId == regulationDocumentId, ct)
            ?? throw new InvalidOperationException("Regulation document not found.");

        var points = await db.NdRegulationPoints
            .Where(p => p.RegulationDocumentId == doc.Id && p.Status == NdRegulationPointStatus.Active)
            .ToListAsync(ct);
        if (points.Count == 0)
            return 0;

        var refreshed = await RefreshPageReferencesAsync(doc, points, ct);
        doc.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return refreshed;
    }

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

        StoredDocument? stored = null;
        if (doc.StoredDocumentId is Guid storedId)
            stored = await db.StoredDocuments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == storedId, ct);

        var markdown = doc.ExtractionMarkdown;
        if (string.IsNullOrWhiteSpace(markdown) && stored is not null)
        {
            if (!string.IsNullOrWhiteSpace(stored.ExtractionCacheKey))
                markdown = (await cache.GetParseCacheAsync(stored.ExtractionCacheKey, ct))?.Markdown;
            if (string.IsNullOrWhiteSpace(markdown) && !string.IsNullOrWhiteSpace(stored.FileHash))
                markdown = (await cache.GetParseCacheAsync(stored.FileHash, ct))?.Markdown;
        }

        if (stored is null || string.IsNullOrWhiteSpace(markdown))
            return 0;

        var refreshed = 0;

        foreach (var point in keepPoints)
        {
            var resolved = await pageResolver.ResolveSectionPageAsync(
                stored,
                markdown,
                point.PointNumber,
                point.PointTitle,
                point.PointContent,
                ct);

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
