using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Data.Entities;
using Reguliq.Api.Data.NewDashboard.Entities;
using Reguliq.Api.Infrastructure.NewDashboard;
using Reguliq.Api.Models;
using Reguliq.Api.Services;
using Reguliq.Api.Services.LandingAi;
using Reguliq.Api.Services.NewDashboard;
using Reguliq.Api.Services.Storage;

namespace Reguliq.Api.Controllers.NewDashboard;

[ApiController]
[Route("nd/regulation-documents")]
public class RegulationDocumentsController(
    AppDbContext db,
    SupabaseJwtValidator jwt,
    NdRegulationUploadService uploadService,
    LandingAiGovExtractService govExtract,
    GovPointsService govPoints,
    SupabaseStorageService storage,
    LandingAiCacheRepository landingCache) : NdControllerBase
{
    public record UpdateRegulationRequest(string? DepartmentId);

    public record ManualPointRequest(
        string? PointNumber,
        string? ParentPointNumber,
        string? PointTitle,
        string PointContent,
        string? PageReference);

    public record UpdateManualPointRequest(
        string? PointNumber,
        string? PointTitle,
        string? PointContent,
        string? PageReference);

    private const int StatusActive = 1;
    private const int StatusHidden = -1;

    private static Guid? ParseDepartmentId(UpdateRegulationRequest? body)
    {
        if (body == null || string.IsNullOrWhiteSpace(body.DepartmentId)) return null;
        return Guid.TryParse(body.DepartmentId, out var id) ? id : null;
    }

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] Guid? departmentId,
        [FromQuery] string? status,
        [FromQuery] bool hiddenOnly = false,
        CancellationToken ct = default)
    {
        var (profile, error) = await RequireAuthAsync(db, jwt, ct,
            "super_admin", "maker", "checker", "reviewer");
        if (error != null) return error;

        if (hiddenOnly && profile!.Role != "super_admin")
            return StatusCode(403, new { success = false, message = "Forbidden" });

        var deptNames = await LoadDepartmentNamesAsync(ct);

        List<NdRegulationDocument> ndDocs;
        try
        {
            ndDocs = await db.NdRegulationDocuments.AsNoTracking().ToListAsync(ct);
        }
        catch
        {
            ndDocs = [];
        }

        var pointCountMap = new Dictionary<Guid, int>();
        try
        {
            var pointCounts = await db.NdRegulationPoints.AsNoTracking()
                .GroupBy(p => p.RegulationDocumentId)
                .Select(g => new { g.Key, Count = g.Count() })
                .ToListAsync(ct);
            foreach (var c in pointCounts) pointCountMap[c.Key] = c.Count;
        }
        catch { /* table may not exist */ }

        var ndByStoredId = ndDocs
            .Where(d => d.Status != StatusHidden && d.StoredDocumentId.HasValue && !IsDepartmentOverlay(d))
            .GroupBy(d => d.StoredDocumentId!.Value)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(d => pointCountMap.GetValueOrDefault(d.Id))
                    .ThenByDescending(d => d.CreatedAt)
                    .First());

        var hiddenStoredIds = ndDocs
            .Where(d => d.Status == StatusHidden && d.StoredDocumentId.HasValue)
            .Select(d => d.StoredDocumentId!.Value)
            .ToHashSet();

        var legacyDocs = await db.StoredDocuments.AsNoTracking()
            .Where(d => d.DocKind == "regulation" && !d.IsHidden)
            .OrderByDescending(d => d.UpdatedAt)
            .ToListAsync(ct);

        var profileNames = await LoadProfileNamesAsync(
            db,
            ndDocs.SelectMany(d => new Guid?[] { d.CreatedBy, d.ExtractedBy })
                .Concat(legacyDocs.SelectMany(d => new Guid?[] { d.UploadedBy })),
            ct);

        var allRegStored = await db.StoredDocuments.AsNoTracking()
            .Where(d => d.DocKind == "regulation")
            .ToDictionaryAsync(d => d.Id, ct);

        var storedById = legacyDocs.ToDictionary(d => d.Id);

        var cachedHashes = await NdLegacyDataQueries.GetExtractCachedHashesAsync(
            db, legacyDocs.Select(d => d.FileHash), ct);

        var items = new List<object>();

        if (hiddenOnly)
        {
            var hiddenLegacyDocs = await db.StoredDocuments.AsNoTracking()
                .Where(d => d.DocKind == "regulation" && d.IsHidden)
                .OrderByDescending(d => d.HiddenAt ?? d.UpdatedAt)
                .ToListAsync(ct);
            var hiddenLegacyNames = await LoadProfileNamesAsync(
                db,
                hiddenLegacyDocs.SelectMany(d => new Guid?[] { d.UploadedBy, d.HiddenBy }),
                ct);
            profileNames = profileNames
                .Concat(hiddenLegacyNames)
                .GroupBy(kv => kv.Key)
                .ToDictionary(g => g.Key, g => g.First().Value);

            foreach (var d in ndDocs.Where(d => d.Status == StatusHidden && !IsDepartmentOverlay(d)).OrderByDescending(d => d.UpdatedAt))
            {
                allRegStored.TryGetValue(d.StoredDocumentId ?? Guid.Empty, out var stored);
                items.Add(BuildRegulationListItem(
                    d, stored, deptNames, pointCountMap, profileNames, isHidden: true));
            }

            foreach (var leg in hiddenLegacyDocs)
            {
                if (hiddenStoredIds.Contains(leg.Id)) continue;
                items.Add(BuildLegacyRegulationListItem(
                    leg, ndDocs, deptNames, cachedHashes, pointCountMap, profileNames, isHidden: true));
            }

            var sortedHidden = items
                .OrderByDescending(i => (DateTimeOffset?)i.GetType().GetProperty("updatedAt")?.GetValue(i)
                    ?? (DateTimeOffset)i.GetType().GetProperty("createdAt")!.GetValue(i)!)
                .ToList();
            return Ok(new { success = true, data = sortedHidden });
        }

        foreach (var leg in legacyDocs.Where(d => !d.IsHidden))
        {
            if (hiddenStoredIds.Contains(leg.Id)) continue;

            ndByStoredId.TryGetValue(leg.Id, out var overlay);
            if (overlay != null) continue;

            var deptOverlay = ndDocs.FirstOrDefault(d =>
                d.StoredDocumentId == leg.Id && d.Status != StatusHidden && IsDepartmentOverlay(d));
            var deptId = deptOverlay?.DepartmentId;
            if (departmentId.HasValue && deptId != departmentId) continue;

            var extractionStatus = NdLegacyDataQueries.LegacyRegulationExtractionStatus(leg, cachedHashes);
            var ndForLegacy = ndDocs.FirstOrDefault(d =>
                d.StoredDocumentId == leg.Id && d.Status != StatusHidden && !IsDepartmentOverlay(d) && !d.IsManual);
            var pointCount = ndForLegacy != null
                ? pointCountMap.GetValueOrDefault(ndForLegacy.Id)
                : (leg.PointCount ?? 0);
            var displayStatus = MapDisplayExtractionStatus(extractionStatus, pointCount, isManual: false);
            if (!MatchesStatusFilter(displayStatus, status)) continue;

            items.Add(BuildLegacyRegulationListItem(
                leg, ndDocs, deptNames, cachedHashes, pointCountMap, profileNames, isHidden: false));
        }

        foreach (var d in ndDocs)
        {
            if (d.Status == StatusHidden) continue;
            if (IsDepartmentOverlay(d)) continue;
            if (d.IsManual)
            {
                if (departmentId.HasValue && d.DepartmentId != departmentId) continue;
                var manualCount = pointCountMap.GetValueOrDefault(d.Id);
                items.Add(new
                {
                    id = d.Id,
                    source = "manual",
                    name = d.Name,
                    departmentId = d.DepartmentId,
                    departmentName = DeptName(deptNames, d.DepartmentId),
                    extractionStatus = "manual",
                    pointCount = manualCount,
                    extractedAt = d.ExtractedAt,
                    createdAt = d.CreatedAt,
                    updatedAt = d.UpdatedAt,
                    storedDocumentId = (Guid?)null,
                    legacyHref = (string?)null,
                    isManual = true,
                    uploadedBy = d.CreatedBy,
                    uploadedByName = ProfileName(profileNames, d.CreatedBy),
                    extractedBy = d.ExtractedBy,
                    extractedByName = ProfileName(profileNames, d.ExtractedBy),
                    originalFileName = (string?)null,
                    isHidden = false,
                });
                continue;
            }
            if (departmentId.HasValue && d.DepartmentId != departmentId) continue;

            var resolvedCount = pointCountMap.GetValueOrDefault(d.Id);
            var displayStatus = MapDisplayExtractionStatus(d.ExtractionStatus, resolvedCount, isManual: false);
            if (!MatchesStatusFilter(displayStatus, status)) continue;

            storedById.TryGetValue(d.StoredDocumentId ?? Guid.Empty, out var storedDoc);
            if (storedDoc == null && d.StoredDocumentId is Guid sid)
                allRegStored.TryGetValue(sid, out storedDoc);

            items.Add(BuildRegulationListItem(
                d, storedDoc, deptNames, pointCountMap, profileNames, isHidden: false));
        }

        var manualDoc = await EnsureManualDocumentAsync(ct);
        if (!ndDocs.Any(d => d.IsManual))
        {
            var manualCount = await db.NdRegulationPoints.CountAsync(p => p.RegulationDocumentId == manualDoc.Id, ct);
            if (!departmentId.HasValue || manualDoc.DepartmentId == departmentId)
            {
                items.Add(new
                {
                    id = manualDoc.Id,
                    source = "manual",
                    name = manualDoc.Name,
                    departmentId = manualDoc.DepartmentId,
                    departmentName = DeptName(deptNames, manualDoc.DepartmentId),
                    extractionStatus = "completed",
                    pointCount = manualCount,
                    extractedAt = manualDoc.ExtractedAt,
                    createdAt = manualDoc.CreatedAt,
                    updatedAt = manualDoc.UpdatedAt,
                    storedDocumentId = (Guid?)null,
                    legacyHref = (string?)null,
                    isManual = true,
                    uploadedBy = manualDoc.CreatedBy,
                    uploadedByName = ProfileName(profileNames, manualDoc.CreatedBy),
                    extractedBy = manualDoc.ExtractedBy,
                    extractedByName = ProfileName(profileNames, manualDoc.ExtractedBy),
                    originalFileName = (string?)null,
                    isHidden = false,
                });
            }
        }

        var sorted = items
            .OrderByDescending(i => string.Equals(
                (string?)i.GetType().GetProperty("source")!.GetValue(i), "manual", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(i => (DateTimeOffset)i.GetType().GetProperty("createdAt")!.GetValue(i)!)
            .ToList();

        return Ok(new { success = true, data = sorted });
    }

    [HttpGet("points/search")]
    public async Task<IActionResult> SearchPoints(
        [FromQuery] string q,
        [FromQuery] int limit = 80,
        CancellationToken ct = default)
    {
        var (_, error) = await RequireAuthAsync(db, jwt, ct,
            "super_admin", "maker", "checker", "reviewer");
        if (error != null) return error;

        var term = (q ?? "").Trim();
        if (term.Length < 2)
            return Ok(new { success = true, data = Array.Empty<object>(), totalMatches = 0 });

        var deptNames = await LoadDepartmentNamesAsync(ct);
        var pattern = $"%{term}%";
        var take = Math.Clamp(limit, 1, 200);

        var ndDocs = await db.NdRegulationDocuments.AsNoTracking().ToListAsync(ct);
        var hiddenStoredIds = ndDocs
            .Where(d => d.Status == StatusHidden && d.StoredDocumentId.HasValue)
            .Select(d => d.StoredDocumentId!.Value)
            .ToHashSet();

        var pointCountMap = new Dictionary<Guid, int>();
        try
        {
            var pointCounts = await db.NdRegulationPoints.AsNoTracking()
                .GroupBy(p => p.RegulationDocumentId)
                .Select(g => new { g.Key, Count = g.Count() })
                .ToListAsync(ct);
            foreach (var c in pointCounts) pointCountMap[c.Key] = c.Count;
        }
        catch { /* table may not exist */ }

        var ndByStoredId = ndDocs
            .Where(d => d.Status != StatusHidden && d.StoredDocumentId.HasValue && !IsDepartmentOverlay(d))
            .GroupBy(d => d.StoredDocumentId!.Value)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(d => pointCountMap.GetValueOrDefault(d.Id))
                    .ThenByDescending(d => d.CreatedAt)
                    .First());

        var deptByStoredId = ndDocs
            .Where(d => d.Status != StatusHidden && d.StoredDocumentId.HasValue && IsDepartmentOverlay(d))
            .GroupBy(d => d.StoredDocumentId!.Value)
            .ToDictionary(g => g.Key, g => g.First().DepartmentId);

        var hits = new List<PointSearchHit>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var markdownByStoredId = new Dictionary<Guid, string?>();

        try
        {
            var ndRows = await db.NdRegulationPoints.AsNoTracking()
                .Join(
                    db.NdRegulationDocuments.AsNoTracking()
                        .Where(d => d.Status != StatusHidden && !IsDepartmentOverlay(d)),
                    p => p.RegulationDocumentId,
                    d => d.Id,
                    (p, d) => new { Point = p, Doc = d })
                .Where(x =>
                    EF.Functions.ILike(x.Point.PointNumber, pattern)
                    || EF.Functions.ILike(x.Point.PointTitle ?? "", pattern)
                    || EF.Functions.ILike(x.Point.PointContent, pattern)
                    || EF.Functions.ILike(x.Point.PageReference ?? "", pattern))
                .OrderBy(x => x.Doc.Name)
                .ThenBy(x => x.Point.PointNumber)
                .Take(take)
                .ToListAsync(ct);

            foreach (var x in ndRows)
            {
                var key = $"{x.Doc.Id}:{x.Point.PointNumber}";
                if (!seen.Add(key)) continue;
                var ndSourceStoredId = x.Doc.StoredDocumentId ?? x.Doc.Id;
                var (pointSection, storedPageHint) = ParsePointPageReference(x.Point.PageReference);
                hits.Add(new PointSearchHit(
                    x.Doc.Id,
                    x.Doc.Name,
                    DeptName(deptNames, x.Doc.DepartmentId),
                    x.Doc.IsManual,
                    x.Point.Id,
                    x.Point.PointNumber,
                    x.Point.PointTitle,
                    SnippetForSearch(x.Point.PointContent, term),
                    x.Point.PageReference,
                    await ResolveStoredPointPdfPageAsync(
                        ndSourceStoredId,
                        x.Point.PointNumber,
                        pointSection ?? x.Point.PointNumber,
                        x.Point.PointTitle,
                        x.Point.PointContent,
                        storedPageHint,
                        markdownByStoredId,
                        ct),
                    ndSourceStoredId));
            }
        }
        catch
        {
            // regulation_points table may not exist in some environments
        }

        if (hits.Count < take)
        {
            var legacyRows = await LoadLegacyExtractSearchRowsAsync(pattern, ct);
            foreach (var row in legacyRows)
            {
                if (hiddenStoredIds.Contains(row.DocumentId)) continue;

                var docId = row.DocumentId;
                var docName = row.DocumentName;
                Guid? deptId = deptByStoredId.GetValueOrDefault(row.DocumentId);
                var isManual = false;

                if (ndByStoredId.TryGetValue(row.DocumentId, out var overlay))
                {
                    docId = overlay.Id;
                    docName = overlay.Name;
                    deptId = overlay.DepartmentId;
                    isManual = overlay.IsManual;
                }

                List<GovPoint> points;
                try
                {
                    points = GovPointsParser.ParseFromExtractJson(row.PointsJson);
                }
                catch
                {
                    continue;
                }

                var docPoints = points.OrderBy(pt => pt.PointId, StringComparer.Ordinal).ToList();
                foreach (var p in docPoints)
                {
                    if (hits.Count >= take) break;
                    if (!PointMatchesSearchTerm(p, term)) continue;

                    var key = $"{docId}:{p.PointId}";
                    if (!seen.Add(key)) continue;

                    var isAnnex = GovPointClassifier.IsAnnexPoint(p.PointId, p.Title, p.Section);
                    hits.Add(new PointSearchHit(
                        docId,
                        docName,
                        DeptName(deptNames, deptId),
                        isManual,
                        LegacyPointId(docId, p.PointId, p.Title, isAnnex),
                        p.PointId,
                        p.Title,
                        SnippetForSearch(p.Text, term),
                        p.Section,
                        await ResolveStoredPointPdfPageAsync(
                            row.DocumentId,
                            p.PointId,
                            p.Section,
                            p.Title,
                            p.Text,
                            p.PageHint,
                            markdownByStoredId,
                            ct),
                        row.DocumentId));
                }

                if (hits.Count >= take) break;
            }
        }

        var grouped = hits
            .GroupBy(h => h.DocumentId)
            .Select(g => new
            {
                documentId = g.Key,
                documentName = g.First().DocumentName,
                departmentName = g.First().DepartmentName,
                isManual = g.First().IsManual,
                storedDocumentId = g.First().SourceStoredDocumentId,
                points = g.Select(x => new
                {
                    id = x.PointId,
                    pointNumber = x.PointNumber,
                    pointTitle = x.PointTitle,
                    snippet = x.Snippet,
                    pageReference = x.PageReference,
                    pdfPage = x.PageHint,
                    storedDocumentId = x.SourceStoredDocumentId,
                }).ToList(),
            })
            .OrderBy(g => g.documentName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return Ok(new { success = true, data = grouped, totalMatches = hits.Count });
    }

    private sealed record PointSearchHit(
        Guid DocumentId,
        string DocumentName,
        string? DepartmentName,
        bool IsManual,
        Guid PointId,
        string PointNumber,
        string? PointTitle,
        string Snippet,
        string? PageReference,
        int? PageHint,
        Guid SourceStoredDocumentId);

    private sealed record LegacyExtractSearchRow(
        Guid DocumentId,
        string DocumentName,
        string PointsJson);

    private async Task<List<LegacyExtractSearchRow>> LoadLegacyExtractSearchRowsAsync(
        string pattern,
        CancellationToken ct)
    {
        try
        {
            return await db.Database.SqlQueryRaw<LegacyExtractSearchRow>(
                    """
                    SELECT sd.id AS "DocumentId",
                           sd.title AS "DocumentName",
                           ec.points_json::text AS "PointsJson"
                    FROM stored_documents sd
                    INNER JOIN landing_ai_extract_cache ec
                      ON ec.file_hash = sd.file_hash
                     AND ec.schema_key = {0}
                    WHERE sd.doc_kind = 'regulation'
                      AND ec.points_json::text ILIKE {1}
                    ORDER BY sd.title
                    """,
                    LandingAiGovExtractService.GovSchemaKey,
                    pattern)
                .ToListAsync(ct);
        }
        catch
        {
            return [];
        }
    }

    private static bool PointMatchesSearchTerm(GovPoint p, string term)
    {
        if (string.IsNullOrWhiteSpace(term)) return false;
        return ContainsIgnoreCase(p.PointId, term)
               || ContainsIgnoreCase(p.Title, term)
               || ContainsIgnoreCase(p.Text, term)
               || ContainsIgnoreCase(p.Section, term);
    }

    private static bool ContainsIgnoreCase(string? value, string term) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Contains(term, StringComparison.OrdinalIgnoreCase);

    private static bool IsNumberedClausePoint(string pointId) =>
        Regex.IsMatch(pointId.Trim().TrimEnd('.'), @"^\d+(\.\d+)+$");

    private static int? ResolvePdfPage(string? pageReference, int? pageHint)
    {
        var fromRef = ParsePdfPageFromReference(pageReference);
        if (fromRef is > 0) return fromRef;
        return pageHint is > 0 ? pageHint : null;
    }

    private async Task<string?> LoadParsedMarkdownAsync(Guid storedDocumentId, CancellationToken ct)
    {
        var stored = await db.StoredDocuments.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == storedDocumentId, ct);
        if (stored == null || string.IsNullOrWhiteSpace(stored.FileHash)) return null;

        var row = await landingCache.GetParseCacheAsync(stored.FileHash, ct);
        return row?.Markdown;
    }

    private async Task<int?> LoadStoredDocumentPageCountAsync(Guid storedDocumentId, CancellationToken ct)
    {
        var stored = await db.StoredDocuments.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == storedDocumentId, ct);
        if (stored == null) return null;
        if (stored.Pages is > 1) return stored.Pages;
        if (stored.SizeBytes is > 80_000)
            return Math.Clamp((int)Math.Round(stored.SizeBytes / 45000.0), 30, 500);
        return stored.Pages is > 0 ? stored.Pages : null;
    }

    private async Task<int?> ResolveStoredPointPdfPageAsync(
        Guid storedDocumentId,
        string pointId,
        string? section,
        string? title,
        string content,
        int? pageHint,
        Dictionary<Guid, string?> markdownCache,
        CancellationToken ct)
    {
        if (!markdownCache.TryGetValue(storedDocumentId, out var markdown))
        {
            markdown = await LoadParsedMarkdownAsync(storedDocumentId, ct);
            markdownCache[storedDocumentId] = markdown;
        }

        var markerPages = PolicyPageResolver.EstimatePageCount(markdown);
        var storedPages = await LoadStoredDocumentPageCountAsync(storedDocumentId, ct);
        if ((storedPages is null or < 15) && markdown is { Length: > 40_000 })
        {
            storedPages = Math.Clamp((int)Math.Round(markdown.Length / 4000.0), 20, 500);
        }
        int? maxPages = markerPages is > 0 && storedPages is > 0
            ? Math.Max(markerPages.Value, storedPages.Value)
            : markerPages ?? storedPages;

        if (!string.IsNullOrWhiteSpace(markdown))
        {
            var hintForResolve = IsNumberedClausePoint(pointId) ? null : pageHint;
            var resolved = PolicyPageResolver.ResolveGovPointPage(
                markdown, pointId, section, title, content, hintForResolve, maxPages);
            var refined = PolicyPageResolver.RefinePageGuess(resolved, pointId, maxPages);
            if (refined is > 0) return refined;
        }

        if (maxPages is > 10)
        {
            var byPoint = PolicyPageResolver.EstimatePageFromPointNumber(pointId, maxPages.Value);
            if (byPoint is > 0) return byPoint;
        }

        var trustedHint = pageHint is > 0 && (maxPages is null or <= 0 || pageHint <= maxPages)
            ? pageHint
            : null;
        if (trustedHint == 1 && maxPages is > 10)
            trustedHint = null;
        return PolicyPageResolver.RefinePageGuess(
            ResolvePdfPage(section, trustedHint),
            pointId,
            maxPages);
    }

    /// <summary>
    /// Best-effort PDF page for search hits when extract page_hint is 0.
    /// </summary>
    private static int? ResolveSearchPage(GovPoint point, IReadOnlyList<GovPoint> documentPoints)
    {
        var direct = ResolvePdfPage(point.Section, point.PageHint);
        if (direct is > 0) return direct;

        var sameSection = documentPoints
            .Where(p => p.PageHint is > 0 && SectionMatches(p.Section, point.Section))
            .Select(p => p.PageHint)
            .Min();
        if (sameSection is > 0) return sameSection;

        var chapter = ChapterPrefix(point.PointId, point.Section);
        if (string.IsNullOrEmpty(chapter)) return null;

        var inChapter = documentPoints
            .Where(p => p.PageHint is > 0 && ChapterPrefix(p.PointId, p.Section) == chapter)
            .Select(p => p.PageHint)
            .Min();
        return inChapter is > 0 ? inChapter : null;
    }

    private static bool SectionMatches(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a)
        && !string.IsNullOrWhiteSpace(b)
        && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string? ChapterPrefix(string pointId, string? section)
    {
        var pid = (pointId ?? "").Trim().TrimEnd('.');
        var match = Regex.Match(pid, @"^(\d+)");
        if (match.Success) return match.Groups[1].Value;

        if (!string.IsNullOrWhiteSpace(section))
        {
            match = Regex.Match(section.Trim(), @"^(\d+)");
            if (match.Success) return match.Groups[1].Value;
        }

        return null;
    }

    private static (string? Section, int? PageHint) ParsePointPageReference(string? pageReference)
    {
        if (string.IsNullOrWhiteSpace(pageReference)) return (null, null);

        var hint = ParsePdfPageFromReference(pageReference);
        var section = pageReference.Trim();
        var sep = section.IndexOf(" · p.", StringComparison.OrdinalIgnoreCase);
        if (sep >= 0)
            section = section[..sep].Trim();
        else if (hint is > 0 && Regex.IsMatch(section, @"^p\.?\s*\d+$", RegexOptions.IgnoreCase))
            section = null;

        return (section, hint);
    }

    private static int? ParsePdfPageFromReference(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return null;
        var trimmed = reference.Trim();
        var match = Regex.Match(trimmed, @"(?:page|p\.?|pp\.?)\s*(\d+)", RegexOptions.IgnoreCase);
        if (match.Success && int.TryParse(match.Groups[1].Value, out var fromLabel) && fromLabel > 0)
            return fromLabel;
        if (int.TryParse(trimmed, out var bare) && bare > 0) return bare;
        return null;
    }

    private static string SnippetForSearch(string content, string term, int maxLen = 120)
    {
        var text = (content ?? "").Trim();
        if (string.IsNullOrEmpty(text)) return "";
        var idx = text.IndexOf(term, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            return text.Length <= maxLen ? text : text[..maxLen] + "…";
        var start = Math.Max(0, idx - 40);
        var slice = text.Substring(start, Math.Min(maxLen, text.Length - start)).Trim();
        return start > 0 ? "…" + slice : slice + (text.Length > start + maxLen ? "…" : "");
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var (_, error) = await RequireAuthAsync(db, jwt, ct,
            "super_admin", "maker", "checker", "reviewer");
        if (error != null) return error;

        var deptNames = await LoadDepartmentNamesAsync(ct);

        var refreshed = await uploadService.TryRefreshExtractionStatusAsync(id, ct);
        var doc = refreshed
            ?? await db.NdRegulationDocuments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct);
        if (doc != null && !IsDepartmentOverlay(doc))
        {
            if (doc.Status == StatusHidden)
                return NotFound(new { success = false, message = "Not found" });

            var pointCount = await db.NdRegulationPoints.CountAsync(p => p.RegulationDocumentId == id, ct);
            return Ok(new
            {
                success = true,
                data = new
                {
                    id = doc.Id,
                    source = doc.IsManual ? "manual" : "nd",
                    name = doc.Name,
                    departmentId = doc.DepartmentId,
                    departmentName = DeptName(deptNames, doc.DepartmentId),
                    extractionStatus = doc.IsManual ? "completed" : doc.ExtractionStatus,
                    extractionProgressLabel = doc.ExtractionProgressLabel,
                    extractionProgressPct = doc.ExtractionProgressPct,
                    pointCount,
                    extractedAt = doc.ExtractedAt,
                    createdAt = doc.CreatedAt,
                    isManual = doc.IsManual,
                },
            });
        }

        var stored = await db.StoredDocuments.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == id && d.DocKind == "regulation", ct);
        if (stored == null) return NotFound(new { success = false, message = "Not found" });

        var overlay = await db.NdRegulationDocuments.AsNoTracking()
            .FirstOrDefaultAsync(d => d.StoredDocumentId == id, ct);
        if (overlay?.Status == StatusHidden)
            return NotFound(new { success = false, message = "Not found" });

        var cachedHashes = await NdLegacyDataQueries.GetExtractCachedHashesAsync(db, [stored.FileHash], ct);
        var extractionStatus = NdLegacyDataQueries.LegacyRegulationExtractionStatus(stored, cachedHashes);
        var legacyPointCount = overlay != null
            ? await db.NdRegulationPoints.CountAsync(p => p.RegulationDocumentId == overlay.Id, ct)
            : (stored.PointCount ?? 0);

        return Ok(new
        {
            success = true,
            data = new
            {
                id = stored.Id,
                source = "legacy",
                name = stored.Title,
                departmentId = overlay?.DepartmentId,
                departmentName = DeptName(deptNames, overlay?.DepartmentId),
                extractionStatus,
                pointCount = legacyPointCount,
                extractedAt = (DateTimeOffset?)null,
                createdAt = stored.CreatedAt,
            },
        });
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> SoftDelete(Guid id, CancellationToken ct)
    {
        var (profile, error) = await RequireAuthAsync(db, jwt, ct, "super_admin", "maker");
        if (error != null) return error;

        var ndDoc = await db.NdRegulationDocuments.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (ndDoc != null)
        {
            if (ndDoc.IsManual)
                return BadRequest(new { success = false, message = "Manual custom points cannot be hidden." });
            if (ndDoc.Status == StatusHidden)
                return Ok(new { success = true, message = "Already hidden." });

            ndDoc.Status = StatusHidden;
            ndDoc.UpdatedAt = DateTimeOffset.UtcNow;
            if (ndDoc.StoredDocumentId is Guid storedId)
            {
                var storedRow = await db.StoredDocuments.FirstOrDefaultAsync(d => d.Id == storedId, ct);
                if (storedRow != null)
                {
                    storedRow.IsHidden = true;
                    storedRow.HiddenAt = DateTimeOffset.UtcNow;
                    storedRow.HiddenBy = profile!.Id;
                    storedRow.UpdatedAt = DateTimeOffset.UtcNow;
                }
            }
            await db.SaveChangesAsync(ct);
            return Ok(new { success = true, message = "Regulation hidden from library (data kept in database)." });
        }

        var stored = await db.StoredDocuments.FirstOrDefaultAsync(d => d.Id == id && d.DocKind == "regulation", ct);
        if (stored == null)
            return NotFound(new { success = false, message = "Not found" });

        var overlay = await db.NdRegulationDocuments.FirstOrDefaultAsync(d => d.StoredDocumentId == id, ct);
        if (overlay == null)
        {
            overlay = new NdRegulationDocument
            {
                StoredDocumentId = stored.Id,
                Name = stored.Title,
                FilePath = "",
                ExtractionStatus = "completed",
                Status = StatusHidden,
                CreatedBy = profile!.Id,
            };
            db.NdRegulationDocuments.Add(overlay);
        }
        else
        {
            if (overlay.IsManual)
                return BadRequest(new { success = false, message = "Manual custom points cannot be hidden." });
            overlay.Status = StatusHidden;
            overlay.UpdatedAt = DateTimeOffset.UtcNow;
        }

        stored.IsHidden = true;
        stored.HiddenAt = DateTimeOffset.UtcNow;
        stored.HiddenBy = profile!.Id;
        stored.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
        return Ok(new { success = true, message = "Regulation hidden from library (data kept in database)." });
    }

    [HttpPost("{id:guid}/restore")]
    public async Task<IActionResult> Restore(Guid id, CancellationToken ct)
    {
        var (_, error) = await RequireAuthAsync(db, jwt, ct, "super_admin");
        if (error != null) return error;

        var ndDoc = await db.NdRegulationDocuments.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (ndDoc != null)
        {
            if (ndDoc.Status != StatusHidden)
                return Ok(new { success = true, message = "Regulation is already active." });

            ndDoc.Status = StatusActive;
            ndDoc.UpdatedAt = DateTimeOffset.UtcNow;
            if (ndDoc.StoredDocumentId is Guid storedId)
            {
                var stored = await db.StoredDocuments.FirstOrDefaultAsync(d => d.Id == storedId, ct);
                if (stored != null)
                {
                    stored.IsHidden = false;
                    stored.HiddenAt = null;
                    stored.HiddenBy = null;
                    stored.UpdatedAt = DateTimeOffset.UtcNow;
                }
            }

            await db.SaveChangesAsync(ct);
            return Ok(new { success = true, message = "Regulation restored." });
        }

        var legacyStored = await db.StoredDocuments.FirstOrDefaultAsync(
            d => d.Id == id && d.DocKind == "regulation", ct);
        if (legacyStored == null)
            return NotFound(new { success = false, message = "Not found" });

        legacyStored.IsHidden = false;
        legacyStored.HiddenAt = null;
        legacyStored.HiddenBy = null;
        legacyStored.UpdatedAt = DateTimeOffset.UtcNow;

        var overlay = await db.NdRegulationDocuments.FirstOrDefaultAsync(d => d.StoredDocumentId == id, ct);
        if (overlay != null && overlay.Status == StatusHidden)
        {
            overlay.Status = StatusActive;
            overlay.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(ct);
        return Ok(new { success = true, message = "Regulation restored." });
    }

    [HttpPut("{id:guid}")]
    [HttpPatch("{id:guid}")]
    public Task<IActionResult> Update(Guid id, [FromBody] UpdateRegulationRequest body, CancellationToken ct) =>
        SetDepartmentInternal(id, body, ct);

    [HttpPost("{id:guid}/department")]
    public Task<IActionResult> SetDepartment(Guid id, [FromBody] UpdateRegulationRequest body, CancellationToken ct) =>
        SetDepartmentInternal(id, body, ct);

    private async Task<IActionResult> SetDepartmentInternal(Guid id, UpdateRegulationRequest body, CancellationToken ct)
    {
        var (profile, error) = await RequireAuthAsync(db, jwt, ct, "super_admin", "maker");
        if (error != null) return error;

        var departmentId = ParseDepartmentId(body);
        var deptNames = await LoadDepartmentNamesAsync(ct);
        var ndDoc = await db.NdRegulationDocuments.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (ndDoc != null)
        {
            if (!departmentId.HasValue && IsDepartmentOverlay(ndDoc) && ndDoc.Status != StatusHidden)
                db.NdRegulationDocuments.Remove(ndDoc);
            else
            {
                ndDoc.DepartmentId = departmentId;
                ndDoc.UpdatedAt = DateTimeOffset.UtcNow;
            }
            await db.SaveChangesAsync(ct);
            return Ok(new
            {
                success = true,
                data = new
                {
                    id = ndDoc.Id,
                    departmentId,
                    departmentName = DeptName(deptNames, departmentId),
                },
            });
        }

        var stored = await db.StoredDocuments.FirstOrDefaultAsync(d => d.Id == id && d.DocKind == "regulation", ct);
        if (stored == null) return NotFound(new { success = false, message = "Not found" });

        var overlay = await db.NdRegulationDocuments.FirstOrDefaultAsync(d => d.StoredDocumentId == id, ct);
        if (!departmentId.HasValue)
        {
            if (overlay != null && IsDepartmentOverlay(overlay) && overlay.Status != StatusHidden)
                db.NdRegulationDocuments.Remove(overlay);
            else if (overlay != null)
            {
                overlay.DepartmentId = null;
                overlay.UpdatedAt = DateTimeOffset.UtcNow;
            }
        }
        else if (overlay == null)
        {
            overlay = new NdRegulationDocument
            {
                StoredDocumentId = stored.Id,
                Name = stored.Title,
                FilePath = "",
                DepartmentId = departmentId,
                ExtractionStatus = "pending",
                CreatedBy = profile!.Id,
            };
            db.NdRegulationDocuments.Add(overlay);
        }
        else
        {
            overlay.DepartmentId = departmentId;
            overlay.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(ct);
        return Ok(new
        {
            success = true,
            data = new
            {
                id = stored.Id,
                departmentId,
                departmentName = DeptName(deptNames, departmentId),
            },
        });
    }

    [HttpPost("upload")]
    [RequestSizeLimit(52_428_800)]
    public async Task<IActionResult> Upload(
        IFormFile file,
        [FromForm] Guid? departmentId,
        CancellationToken ct = default)
    {
        var (profile, error) = await RequireAuthAsync(db, jwt, ct, "super_admin", "maker");
        if (error != null) return error;

        if (file == null || file.Length == 0)
            return BadRequest(new { success = false, message = "No file provided." });

        await using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        var bytes = ms.ToArray();

        try
        {
            var doc = await uploadService.UploadAndExtractAsync(
                bytes, file.FileName, file.ContentType ?? "application/pdf",
                departmentId, profile!.Id, ct);

            var pointCount = await db.NdRegulationPoints.CountAsync(p => p.RegulationDocumentId == doc.Id, ct);
            return Ok(new
            {
                success = true,
                data = new
                {
                    id = doc.Id,
                    name = doc.Name,
                    departmentId = doc.DepartmentId,
                    extractionStatus = doc.ExtractionStatus,
                    pointCount,
                },
            });
        }
        catch (Exception ex)
        {
            return BadRequest(new { success = false, message = ex.Message });
        }
    }

    [HttpPost("{id:guid}/extract")]
    public async Task<IActionResult> Extract(Guid id, CancellationToken ct)
    {
        var (profile, error) = await RequireAuthAsync(db, jwt, ct, "super_admin", "maker");
        if (error != null) return error;

        try
        {
            var doc = await uploadService.ExtractByRegulationIdAsync(id, profile!.Id, ct);
            var pointCount = await db.NdRegulationPoints.CountAsync(p => p.RegulationDocumentId == doc.Id, ct);
            var responseId = doc.StoredDocumentId ?? doc.Id;
            return Accepted(new
            {
                success = true,
                message = "Extraction started.",
                data = new
                {
                    id = responseId,
                    regulationDocumentId = doc.Id,
                    extractionStatus = doc.ExtractionStatus,
                    extractionProgressLabel = doc.ExtractionProgressLabel,
                    extractionProgressPct = doc.ExtractionProgressPct,
                    pointCount,
                },
            });
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            return NotFound(new { success = false, message = ex.Message });
        }
        catch (Exception ex)
        {
            return BadRequest(new { success = false, message = ex.Message });
        }
    }

    /// <summary>Update stored point page references from parse cache only — no Landing AI credits.</summary>
    [HttpPost("{id:guid}/refresh-page-references")]
    public async Task<IActionResult> RefreshPageReferences(Guid id, CancellationToken ct)
    {
        var (profile, error) = await RequireAuthAsync(db, jwt, ct, "super_admin", "maker");
        if (error != null) return error;

        try
        {
            var updated = await uploadService.RefreshPointPageReferencesAsync(id, ct);
            return Ok(new { success = true, data = new { pointsUpdated = updated } });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { success = false, message = ex.Message });
        }
        catch (Exception)
        {
            return StatusCode(500, new { success = false, message = "Failed to refresh PDF page numbers" });
        }
    }

    [HttpPost("{id:guid}/extract/stop")]
    public async Task<IActionResult> StopExtract(Guid id, CancellationToken ct)
    {
        var (profile, error) = await RequireAuthAsync(db, jwt, ct, "super_admin", "maker");
        if (error != null) return error;

        var stopped = await uploadService.StopExtractAsync(id, ct);
        if (!stopped)
            return BadRequest(new { success = false, message = "No extraction is running for this document." });

        var doc = await uploadService.TryRefreshExtractionStatusAsync(id, ct)
            ?? await db.NdRegulationDocuments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct);
        if (doc == null)
            return NotFound(new { success = false, message = "Not found" });

        var pointCount = await db.NdRegulationPoints.CountAsync(p => p.RegulationDocumentId == doc.Id, ct);
        return Ok(new
        {
            success = true,
            message = "Extraction stopped. Saved progress can be resumed with Extract.",
            data = new
            {
                id = doc.StoredDocumentId ?? doc.Id,
                regulationDocumentId = doc.Id,
                extractionStatus = doc.ExtractionStatus,
                extractionProgressLabel = doc.ExtractionProgressLabel,
                extractionProgressPct = doc.ExtractionProgressPct,
                extractionParseChunkCompleted = doc.ExtractionParseChunkCompleted,
                pointCount,
            },
        });
    }

    [HttpGet("{id:guid}/file-url")]
    public async Task<IActionResult> FileUrl(Guid id, CancellationToken ct)
    {
        var (_, error) = await RequireAuthAsync(db, jwt, ct,
            "super_admin", "maker", "checker", "reviewer");
        if (error != null) return error;

        if (!storage.IsConfigured)
            return StatusCode(503, new { success = false, message = "Supabase Storage not configured." });

        var ndDoc = await db.NdRegulationDocuments.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == id, ct);

        Data.Entities.StoredDocument? stored = null;
        string? storagePath = null;
        string? fileName = null;

        if (ndDoc != null)
        {
            if (!string.IsNullOrWhiteSpace(ndDoc.FilePath))
                storagePath = ndDoc.FilePath;

            if (ndDoc.StoredDocumentId is Guid storedId)
            {
                stored = await db.StoredDocuments.AsNoTracking()
                    .FirstOrDefaultAsync(d => d.Id == storedId, ct);
                storagePath ??= stored?.StoragePath;
            }

            fileName = ndDoc.Name;
        }
        else
        {
            stored = await db.StoredDocuments.AsNoTracking()
                .FirstOrDefaultAsync(d => d.Id == id && d.DocKind == "regulation", ct);
            storagePath = stored?.StoragePath;
        }

        if (string.IsNullOrWhiteSpace(storagePath))
            return NotFound(new { success = false, message = "Regulation file not found." });

        fileName ??= stored?.OriginalFileName ?? stored?.Title ?? "regulation.pdf";

        var url = await storage.CreateSignedUrlAsync(storagePath, 3600, ct);
        return Ok(new
        {
            success = true,
            data = new
            {
                url,
                expiresIn = 3600,
                fileName,
            },
        });
    }

    [HttpGet("{id:guid}/points")]
    public async Task<IActionResult> Points(Guid id, CancellationToken ct)
    {
        var (_, error) = await RequireAuthAsync(db, jwt, ct,
            "super_admin", "maker", "checker", "reviewer");
        if (error != null) return error;

        var ndDoc = await db.NdRegulationDocuments.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == id, ct)
            ?? await db.NdRegulationDocuments.AsNoTracking()
                .FirstOrDefaultAsync(d => d.StoredDocumentId == id, ct);

        if (ndDoc != null)
        {
            if (ndDoc.Status == StatusHidden)
                return NotFound(new { success = false, message = "Not found" });

            var points = PointNumberSort.OrderByPointNumber(
                    await db.NdRegulationPoints.AsNoTracking()
                        .Where(p => p.RegulationDocumentId == ndDoc.Id)
                        .ToListAsync(ct),
                    p => p.PointNumber)
                .ToList();

            return Ok(new
            {
                success = true,
                data = points.Select(MapNdPoint).ToList(),
                source = "nd",
                pointCount = points.Count,
            });
        }

        var stored = ndDoc?.StoredDocumentId is Guid storedId
            ? await db.StoredDocuments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == storedId, ct)
            : await db.StoredDocuments.AsNoTracking()
                .FirstOrDefaultAsync(d => d.Id == id && d.DocKind == "regulation", ct);

        if (stored == null || string.IsNullOrWhiteSpace(stored.FileHash))
        {
            return Ok(new { success = true, data = Array.Empty<object>(), source = "none" });
        }

        var loaded = await govExtract.LoadFromDatabaseOrSeedAsync(stored.FileHash, ct);
        var linkedBuiltin =
            string.Equals(stored.FileHash, LandingAiGovExtractService.BuiltinGovFileHash, StringComparison.OrdinalIgnoreCase)
            && loaded.Source is "db-cache" or "seed";

        if (loaded.Source != "db-cache" && !linkedBuiltin)
        {
            return Ok(new { success = true, data = Array.Empty<object>(), source = "none" });
        }

        var legacyPoints = govPoints.GetAllPoints()
            .OrderBy(p => p.PointId, StringComparer.Ordinal)
            .Select(p => MapLegacyPoint(id, p))
            .ToList();

        return Ok(new
        {
            success = true,
            data = legacyPoints,
            source = loaded.Source,
            pointCount = legacyPoints.Count,
        });
    }

    [HttpPost("{id:guid}/manual-points")]
    public async Task<IActionResult> CreateManualPoint(
        Guid id,
        [FromBody] ManualPointRequest body,
        CancellationToken ct)
    {
        var (profile, error) = await RequireAuthAsync(db, jwt, ct, "super_admin", "maker");
        if (error != null) return error;

        var doc = await RequireManualDocumentAsync(id, ct);
        if (doc == null) return NotFound(new { success = false, message = "Manual regulation document not found" });

        string pointNumber;
        if (!string.IsNullOrWhiteSpace(body.PointNumber))
        {
            var validation = await ValidateManualPointAsync(doc.Id, body.PointNumber, null, ct);
            if (validation != null) return BadRequest(new { success = false, message = validation });
            pointNumber = NormalizeManualPointNumber(body.PointNumber);
        }
        else
        {
            try
            {
                pointNumber = await AllocateNextManualPointNumberAsync(doc.Id, body.ParentPointNumber, ct);
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { success = false, message = ex.Message });
            }
        }

        var point = new NdRegulationPoint
        {
            RegulationDocumentId = doc.Id,
            PointNumber = pointNumber,
            PointTitle = string.IsNullOrWhiteSpace(body.PointTitle) ? null : body.PointTitle.Trim(),
            PointContent = body.PointContent.Trim(),
            PageReference = string.IsNullOrWhiteSpace(body.PageReference) ? null : body.PageReference.Trim(),
        };
        db.NdRegulationPoints.Add(point);
        doc.UpdatedAt = DateTimeOffset.UtcNow;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            var inner = ex.InnerException?.Message ?? ex.Message;
            return StatusCode(500, new { success = false, message = $"Could not save point: {inner}" });
        }

        return Ok(new { success = true, data = MapNdPoint(point) });
    }

    [HttpPut("{id:guid}/manual-points/{pointId:guid}")]
    public async Task<IActionResult> UpdateManualPoint(
        Guid id,
        Guid pointId,
        [FromBody] UpdateManualPointRequest body,
        CancellationToken ct)
    {
        var (_, error) = await RequireAuthAsync(db, jwt, ct, "super_admin", "maker");
        if (error != null) return error;

        var doc = await RequireManualDocumentAsync(id, ct);
        if (doc == null) return NotFound(new { success = false, message = "Manual regulation document not found" });

        var point = await db.NdRegulationPoints.FirstOrDefaultAsync(
            p => p.Id == pointId && p.RegulationDocumentId == doc.Id, ct);
        if (point == null) return NotFound(new { success = false, message = "Point not found" });

        if (!string.IsNullOrWhiteSpace(body.PointNumber))
        {
            var validation = await ValidateManualPointAsync(doc.Id, body.PointNumber, point.Id, ct);
            if (validation != null) return BadRequest(new { success = false, message = validation });
            point.PointNumber = NormalizeManualPointNumber(body.PointNumber);
        }

        if (body.PointTitle != null)
            point.PointTitle = string.IsNullOrWhiteSpace(body.PointTitle) ? null : body.PointTitle.Trim();
        if (body.PointContent != null)
            point.PointContent = body.PointContent.Trim();
        if (body.PageReference != null)
            point.PageReference = string.IsNullOrWhiteSpace(body.PageReference) ? null : body.PageReference.Trim();

        doc.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return Ok(new { success = true, data = MapNdPoint(point) });
    }

    [HttpDelete("{id:guid}/manual-points/{pointId:guid}")]
    public async Task<IActionResult> DeleteManualPoint(Guid id, Guid pointId, CancellationToken ct)
    {
        var (_, error) = await RequireAuthAsync(db, jwt, ct, "super_admin", "maker");
        if (error != null) return error;

        var doc = await RequireManualDocumentAsync(id, ct);
        if (doc == null) return NotFound(new { success = false, message = "Manual regulation document not found" });

        var point = await db.NdRegulationPoints.FirstOrDefaultAsync(
            p => p.Id == pointId && p.RegulationDocumentId == doc.Id, ct);
        if (point == null) return NotFound(new { success = false, message = "Point not found" });

        var hasChildren = await db.NdRegulationPoints.AnyAsync(
            p => p.RegulationDocumentId == doc.Id
                 && p.Id != point.Id
                 && p.PointNumber.StartsWith(point.PointNumber.TrimEnd('.') + "."),
            ct);
        if (hasChildren)
            return BadRequest(new { success = false, message = "Remove child points before deleting this point." });

        db.NdRegulationPoints.Remove(point);
        doc.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return Ok(new { success = true });
    }

    private async Task<NdRegulationDocument> EnsureManualDocumentAsync(CancellationToken ct)
    {
        var existing = await db.NdRegulationDocuments.FirstOrDefaultAsync(d => d.IsManual, ct);
        if (existing != null) return existing;

        var doc = new NdRegulationDocument
        {
            Name = "Manual custom points",
            FilePath = "",
            IsManual = true,
            ExtractionStatus = "completed",
            ExtractedAt = DateTimeOffset.UtcNow,
        };
        db.NdRegulationDocuments.Add(doc);
        await db.SaveChangesAsync(ct);
        return doc;
    }

    private async Task<NdRegulationDocument?> RequireManualDocumentAsync(Guid id, CancellationToken ct)
    {
        var doc = await db.NdRegulationDocuments.FirstOrDefaultAsync(d => d.Id == id && d.IsManual, ct);
        return doc;
    }

    private static object BuildRegulationListItem(
        NdRegulationDocument d,
        StoredDocument? stored,
        IReadOnlyDictionary<Guid, string> deptNames,
        IReadOnlyDictionary<Guid, int> pointCountMap,
        IReadOnlyDictionary<Guid, string> profileNames,
        bool isHidden)
    {
        var resolvedCount = pointCountMap.GetValueOrDefault(d.Id);
        var displayStatus = MapDisplayExtractionStatus(d.ExtractionStatus, resolvedCount, isManual: false);
        var uploadedBy = d.CreatedBy ?? stored?.UploadedBy;
        return new
        {
            id = d.Id,
            source = "nd",
            name = d.Name,
            departmentId = d.DepartmentId,
            departmentName = DeptName(deptNames, d.DepartmentId),
            extractionStatus = displayStatus,
            extractionProgressLabel = ShowsExtractionProgress(displayStatus)
                ? d.ExtractionProgressLabel
                : null,
            extractionProgressPct = ShowsExtractionProgress(displayStatus)
                ? d.ExtractionProgressPct
                : null,
            extractionParseChunkCompleted = string.Equals(d.ExtractionStatus, "paused", StringComparison.OrdinalIgnoreCase)
                ? d.ExtractionParseChunkCompleted
                : null,
            pointCount = resolvedCount,
            extractedAt = d.ExtractedAt,
            createdAt = d.CreatedAt,
            updatedAt = d.UpdatedAt,
            storedDocumentId = d.StoredDocumentId,
            legacyHref = (string?)null,
            uploadedBy,
            uploadedByName = ProfileName(profileNames, uploadedBy),
            extractedBy = d.ExtractedBy,
            extractedByName = ProfileName(profileNames, d.ExtractedBy),
            originalFileName = stored?.OriginalFileName,
            convertedFromWord = stored != null && !string.IsNullOrWhiteSpace(stored.SourceStoragePath),
            sourceOriginalFileName = stored?.SourceStoragePath != null ? stored.OriginalFileName : null,
            landingAiFileName = stored != null ? Path.GetFileName(stored.StoragePath) : null,
            isHidden,
            hiddenAt = isHidden ? (DateTimeOffset?)(stored?.HiddenAt ?? d.UpdatedAt) : null,
        };
    }

    private static object BuildLegacyRegulationListItem(
        StoredDocument leg,
        IReadOnlyList<NdRegulationDocument> ndDocs,
        IReadOnlyDictionary<Guid, string> deptNames,
        IReadOnlySet<string> cachedHashes,
        IReadOnlyDictionary<Guid, int> pointCountMap,
        IReadOnlyDictionary<Guid, string> profileNames,
        bool isHidden)
    {
        var deptOverlay = ndDocs.FirstOrDefault(d =>
            d.StoredDocumentId == leg.Id && d.Status != StatusHidden && IsDepartmentOverlay(d));
        var deptId = deptOverlay?.DepartmentId;
        var extractionStatus = NdLegacyDataQueries.LegacyRegulationExtractionStatus(leg, cachedHashes);
        var ndForLegacy = ndDocs.FirstOrDefault(d =>
            d.StoredDocumentId == leg.Id && d.Status != StatusHidden && !IsDepartmentOverlay(d) && !d.IsManual);
        var pointCount = ndForLegacy != null
            ? pointCountMap.GetValueOrDefault(ndForLegacy.Id)
            : (leg.PointCount ?? 0);
        var displayStatus = MapDisplayExtractionStatus(extractionStatus, pointCount, isManual: false);
        return new
        {
            id = leg.Id,
            source = "legacy",
            name = leg.Title,
            departmentId = deptId,
            departmentName = DeptName(deptNames, deptId),
            extractionStatus = displayStatus,
            pointCount,
            extractedAt = ndForLegacy?.ExtractedAt,
            createdAt = leg.CreatedAt,
            updatedAt = leg.UpdatedAt,
            storedDocumentId = leg.Id,
            legacyHref = $"/nd/regulation-documents/{leg.Id}",
            uploadedBy = leg.UploadedBy ?? ndForLegacy?.CreatedBy,
            uploadedByName = ProfileName(profileNames, leg.UploadedBy ?? ndForLegacy?.CreatedBy),
            extractedBy = ndForLegacy?.ExtractedBy,
            extractedByName = ProfileName(profileNames, ndForLegacy?.ExtractedBy),
            originalFileName = leg.OriginalFileName,
            convertedFromWord = !string.IsNullOrWhiteSpace(leg.SourceStoragePath),
            sourceOriginalFileName = !string.IsNullOrWhiteSpace(leg.SourceStoragePath) ? leg.OriginalFileName : null,
            landingAiFileName = Path.GetFileName(leg.StoragePath),
            isHidden,
            hiddenAt = isHidden ? (DateTimeOffset?)(leg.HiddenAt ?? leg.UpdatedAt) : null,
        };
    }

    private static bool MatchesStatusFilter(string displayStatus, string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return true;
        return string.Equals(displayStatus, filter, StringComparison.OrdinalIgnoreCase);
    }

    private static string MapDisplayExtractionStatus(string rawStatus, int pointCount, bool isManual)
    {
        if (isManual) return "manual";
        if (string.Equals(rawStatus, "completed", StringComparison.OrdinalIgnoreCase) || pointCount > 0)
            return "extracted";
        if (string.Equals(rawStatus, "processing", StringComparison.OrdinalIgnoreCase))
            return "processing";
        if (string.Equals(rawStatus, "paused", StringComparison.OrdinalIgnoreCase))
            return "paused";
        if (string.Equals(rawStatus, "failed", StringComparison.OrdinalIgnoreCase))
            return "failed";
        return "pending";
    }

    private static bool ShowsExtractionProgress(string displayStatus) =>
        string.Equals(displayStatus, "processing", StringComparison.OrdinalIgnoreCase)
        || string.Equals(displayStatus, "paused", StringComparison.OrdinalIgnoreCase);

    private static int ResolvePointCount(
        NdRegulationDocument doc,
        IReadOnlyDictionary<Guid, int> pointCountMap,
        StoredDocument? stored,
        int legacySeedCount = 0)
    {
        _ = stored;
        _ = legacySeedCount;
        return pointCountMap.GetValueOrDefault(doc.Id);
    }

    private int BuiltinSeedPointCount(StoredDocument? stored)
    {
        if (stored == null || string.IsNullOrWhiteSpace(stored.FileHash)) return 0;
        if (!string.Equals(
                stored.FileHash,
                LandingAiGovExtractService.BuiltinGovFileHash,
                StringComparison.OrdinalIgnoreCase))
            return 0;
        try
        {
            return govPoints.GetAllPoints().Count;
        }
        catch
        {
            return 0;
        }
    }

    private static string NormalizePointNumberKey(string? pointNumber) =>
        (pointNumber ?? "").Trim().TrimEnd('.');

    private async Task<List<object>> BuildMergedPointListAsync(
        NdRegulationDocument ndDoc,
        Guid requestId,
        List<NdRegulationPoint> ndPoints,
        CancellationToken ct)
    {
        var stored = await ResolveStoredForNdDocAsync(ndDoc, requestId, ct);
        var legacyGov = await TryLoadLegacyGovPointsAsync(stored, ct);
        if (legacyGov == null || legacyGov.Count == 0)
            return ndPoints.Select(MapNdPoint).Cast<object>().ToList();

        var ndByNumber = ndPoints
            .GroupBy(p => NormalizePointNumberKey(p.PointNumber))
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var merged = new List<object>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mapDocId = ndDoc.Id;

        foreach (var gov in legacyGov)
        {
            var key = NormalizePointNumberKey(gov.PointId);
            if (string.IsNullOrWhiteSpace(key) || !seen.Add(key)) continue;
            if (ndByNumber.TryGetValue(key, out var nd))
                merged.Add(MapNdPoint(nd));
            else
                merged.Add(MapLegacyPoint(mapDocId, gov));
        }

        foreach (var nd in PointNumberSort.OrderByPointNumber(ndPoints, p => p.PointNumber))
        {
            var key = NormalizePointNumberKey(nd.PointNumber);
            if (string.IsNullOrWhiteSpace(key) || !seen.Add(key)) continue;
            merged.Add(MapNdPoint(nd));
        }

        return merged;
    }

    private async Task<StoredDocument?> ResolveStoredForNdDocAsync(
        NdRegulationDocument ndDoc,
        Guid requestId,
        CancellationToken ct)
    {
        if (ndDoc.StoredDocumentId is Guid storedId)
        {
            return await db.StoredDocuments.AsNoTracking()
                .FirstOrDefaultAsync(d => d.Id == storedId, ct);
        }

        return await db.StoredDocuments.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == requestId && d.DocKind == "regulation", ct);
    }

    private async Task<List<GovPoint>?> TryLoadLegacyGovPointsAsync(
        StoredDocument? stored,
        CancellationToken ct)
    {
        if (stored == null || string.IsNullOrWhiteSpace(stored.FileHash)) return null;

        var loaded = await govExtract.LoadFromDatabaseOrSeedAsync(stored.FileHash, ct);
        var linkedBuiltin =
            string.Equals(stored.FileHash, LandingAiGovExtractService.BuiltinGovFileHash, StringComparison.OrdinalIgnoreCase)
            && loaded.Source is "db-cache" or "seed";

        if (loaded.Source != "db-cache" && !linkedBuiltin)
            return null;

        return govPoints.GetAllPoints()
            .OrderBy(p => p.PointId, StringComparer.Ordinal)
            .ToList();
    }

    private static int CountPointsInExtractionResult(string? extractionResult)
    {
        if (string.IsNullOrWhiteSpace(extractionResult)) return 0;
        try
        {
            using var json = JsonDocument.Parse(extractionResult);
            if (json.RootElement.TryGetProperty("points", out var points)
                && points.ValueKind == JsonValueKind.Array)
                return points.GetArrayLength();
        }
        catch { /* ignore malformed json */ }
        return 0;
    }

    private async Task<string> AllocateNextManualPointNumberAsync(
        Guid docId,
        string? parentPointNumber,
        CancellationToken ct)
    {
        var numbers = await db.NdRegulationPoints.AsNoTracking()
            .Where(p => p.RegulationDocumentId == docId)
            .Select(p => p.PointNumber)
            .ToListAsync(ct);

        if (string.IsNullOrWhiteSpace(parentPointNumber))
        {
            var nextMain = numbers
                .Select(n => int.TryParse(NormalizeManualPointNumber(n).Split('.')[0], out var v) ? v : 0)
                .DefaultIfEmpty(0)
                .Max() + 1;
            return nextMain.ToString();
        }

        var parent = NormalizeManualPointNumber(parentPointNumber);
        var parentDepth = parent.Split('.').Length;
        if (parentDepth >= 3)
            throw new InvalidOperationException("Maximum nesting depth is 3 levels (e.g. 2.1.1).");

        if (!numbers.Any(n => NormalizeManualPointNumber(n) == parent))
            throw new InvalidOperationException($"Parent point {parent} must exist before adding a sub-point.");

        var siblingNext = numbers
            .Where(n => GetManualParentNumber(n) == parent)
            .Select(n =>
            {
                var parts = NormalizeManualPointNumber(n).Split('.');
                return int.TryParse(parts[^1], out var v) ? v : 0;
            })
            .DefaultIfEmpty(0)
            .Max() + 1;

        return $"{parent}.{siblingNext}";
    }

    private static string NormalizeManualPointNumber(string pointNumber)
    {
        return pointNumber.Trim().TrimEnd('.');
    }

    private static bool IsValidManualPointNumber(string pointNumber, out string? error)
    {
        error = null;
        var normalized = NormalizeManualPointNumber(pointNumber);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            error = "Point number is required.";
            return false;
        }

        if (!System.Text.RegularExpressions.Regex.IsMatch(normalized, @"^\d+(\.\d+){0,2}$"))
        {
            error = "Point number must be like 1, 2.1, or 2.1.1 (up to 3 levels).";
            return false;
        }

        return true;
    }

    private static string? GetManualParentNumber(string pointNumber)
    {
        var normalized = NormalizeManualPointNumber(pointNumber);
        var parts = normalized.Split('.');
        if (parts.Length <= 1) return null;
        return string.Join('.', parts[..^1]);
    }

    private async Task<string?> ValidateManualPointAsync(
        Guid docId,
        string pointNumber,
        Guid? excludePointId,
        CancellationToken ct)
    {
        if (!IsValidManualPointNumber(pointNumber, out var formatError))
            return formatError;

        var normalized = NormalizeManualPointNumber(pointNumber);
        var duplicate = await db.NdRegulationPoints.AnyAsync(
            p => p.RegulationDocumentId == docId
                 && p.PointNumber == normalized
                 && (!excludePointId.HasValue || p.Id != excludePointId.Value),
            ct);
        if (duplicate) return $"Point number {normalized} already exists.";

        var parent = GetManualParentNumber(normalized);
        if (parent != null)
        {
            var parentExists = await db.NdRegulationPoints.AnyAsync(
                p => p.RegulationDocumentId == docId && p.PointNumber == parent, ct);
            if (!parentExists)
                return $"Parent point {parent} must exist before adding {normalized}.";
        }

        return null;
    }

    private static object MapNdPoint(NdRegulationPoint p) => new
    {
        id = p.Id,
        pointNumber = p.PointNumber,
        pointTitle = p.PointTitle,
        pointContent = p.PointContent,
        pageReference = p.PageReference,
        isIntroductionPoint = p.IsIntroductionPoint,
        isAnnexPoint = p.IsAnnexPoint,
    };

    private static object MapLegacyPoint(Guid documentId, GovPoint p)
    {
        var isAnnex = GovPointClassifier.IsAnnexPoint(p.PointId, p.Title, p.Section);
        var isIntro = GovPointClassifier.IsIntroductionPoint(
            p.PointId, p.Title, p.Text, p.Section, null);
        return new
        {
            id = LegacyPointId(documentId, p.PointId, p.Title, isAnnex),
            pointNumber = p.PointId,
            pointTitle = p.Title,
            pointContent = p.Text,
            pageReference = p.Section,
            isIntroductionPoint = isIntro,
            isAnnexPoint = isAnnex,
        };
    }

    private static Guid LegacyPointId(Guid documentId, string pointNumber, string? title = null, bool isAnnex = false) =>
        NdLibraryPointPersistence.LegacyPointId(documentId, pointNumber, title, isAnnex);

    private async Task<Data.Entities.StoredDocument?> ResolveStoredDocumentAsync(Guid id, CancellationToken ct)
    {
        var ndDoc = await db.NdRegulationDocuments.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == id, ct)
            ?? await db.NdRegulationDocuments.AsNoTracking()
                .FirstOrDefaultAsync(d => d.StoredDocumentId == id, ct);

        if (ndDoc?.StoredDocumentId is Guid storedId)
        {
            return await db.StoredDocuments.AsNoTracking()
                .FirstOrDefaultAsync(d => d.Id == storedId, ct);
        }

        return await db.StoredDocuments.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == id && d.DocKind == "regulation", ct);
    }

    private async Task<Dictionary<Guid, string>> LoadDepartmentNamesAsync(CancellationToken ct)
    {
        try
        {
            return await db.NdDepartments.AsNoTracking()
                .ToDictionaryAsync(d => d.Id, d => d.Name, ct);
        }
        catch
        {
            return [];
        }
    }

    private static string? DeptName(IReadOnlyDictionary<Guid, string> names, Guid? id) =>
        id.HasValue && names.TryGetValue(id.Value, out var name) ? name : null;

    private static bool IsNdUpload(NdRegulationDocument d) =>
        !string.IsNullOrWhiteSpace(d.FilePath) &&
        d.FilePath.Contains("regulations/nd", StringComparison.OrdinalIgnoreCase);

    private static bool IsDepartmentOverlay(NdRegulationDocument d) =>
        d.StoredDocumentId.HasValue && string.IsNullOrWhiteSpace(d.FilePath);
}
