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
using Reguliq.Api.Services.NewDashboard.Demo;
using Reguliq.Api.Services.Storage;

namespace Reguliq.Api.Controllers.NewDashboard;

[ApiController]
[Route("nd/regulation-documents")]
public class RegulationDocumentsController(
    AppDbContext db,
    SupabaseJwtValidator jwt,
    NdRegulationUploadService uploadService,
    NdRegulationPointRepairService pointRepair,
    LandingAiGovExtractService govExtract,
    GovPointsService govPoints,
    SupabaseStorageService storage,
    LandingAiCacheRepository landingCache,
    NdDemoUserDirectory demoDirectory,
    NdDemoInterceptionService demoInterception,
    DemoAnalysisSeedService demoSeed) : NdControllerBase
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

    private sealed class LegacyRegulationListRow
    {
        public Guid Id { get; init; }
        public string Title { get; init; } = "";
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset UpdatedAt { get; init; }
        public Guid? UploadedBy { get; init; }
        public string OriginalFileName { get; init; } = "";
        public string? SourceStoragePath { get; init; }
        public string StoragePath { get; init; } = "";
        public string? FileHash { get; init; }
        public int? PointCount { get; init; }
        public DateTimeOffset? HiddenAt { get; init; }
        public Guid? HiddenBy { get; init; }
    }

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
        var (profile, user, error) = await RequireAuthWithUserAsync(db, jwt, ct,
            "super_admin", "maker", "checker", "reviewer");
        if (error != null) return error;

        var demoCtx = await NdDemoIsolationContext.ResolveAsync(demoDirectory, user, ct);

        if (hiddenOnly && profile!.Role != "super_admin")
            return StatusCode(403, new { success = false, message = "Forbidden" });

        var deptNamesTask = LoadDepartmentNamesAsync(demoCtx, ct);
        var ndDocsTask = NdDemoDataFilters.ApplyToRegulationDocuments(
                db.NdRegulationDocuments.AsNoTracking(),
                demoCtx)
            .ToListAsync(ct);

        Dictionary<Guid, string> deptNames;
        List<NdRegulationDocument> ndDocs;
        try
        {
            await Task.WhenAll(deptNamesTask, ndDocsTask);
            deptNames = await deptNamesTask;
            ndDocs = await ndDocsTask;
        }
        catch
        {
            deptNames = await deptNamesTask;
            ndDocs = [];
        }

        var ndDocIds = ndDocs.Select(d => d.Id).ToList();
        var manualByDocId = ndDocs.ToDictionary(d => d.Id, d => d.IsManual);
        var pointCountMap = new Dictionary<Guid, int>();
        if (ndDocIds.Count > 0)
        {
            try
            {
                pointCountMap = await NdRegulationPointCanonicalFilter.BuildCanonicalCountMapAsync(
                    db, ndDocIds, manualByDocId, ct);
            }
            catch { /* table may not exist */ }
        }

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

        var overlayStoredIds = ndByStoredId.Keys.ToList();
        var deptOverlayByStoredId = ndDocs
            .Where(d => d.Status != StatusHidden && d.StoredDocumentId.HasValue && IsDepartmentOverlay(d))
            .GroupBy(d => d.StoredDocumentId!.Value)
            .ToDictionary(g => g.Key, g => g.First());

        var ndForLegacyByStoredId = ndDocs
            .Where(d => d.Status != StatusHidden && d.StoredDocumentId.HasValue && !IsDepartmentOverlay(d) && !d.IsManual)
            .GroupBy(d => d.StoredDocumentId!.Value)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(d => pointCountMap.GetValueOrDefault(d.Id))
                    .ThenByDescending(d => d.CreatedAt)
                    .First());

        List<LegacyRegulationListRow> legacyRows = [];
        List<LegacyRegulationListRow> hiddenLegacyRows = [];
        if (!hiddenOnly)
        {
            legacyRows = await LoadLegacyRegulationListRowsAsync(
                db, demoCtx, overlayStoredIds, hiddenOnly: false, ct);
        }
        else
        {
            hiddenLegacyRows = await LoadLegacyRegulationListRowsAsync(
                db, demoCtx, overlayStoredIds, hiddenOnly: true, ct);
        }

        // Legacy stored rows can exist while their NdRegulationDocument was missing from the
        // demo-filtered ndDocs query (e.g. super admin list). Merge overlays so list id/status
        // match POST /extract (regulation doc id, not stored doc id).
        var legacyStoredIds = legacyRows.Concat(hiddenLegacyRows).Select(l => l.Id).Distinct().ToList();
        if (legacyStoredIds.Count > 0)
        {
            var             linkedNd = await db.NdRegulationDocuments.AsNoTracking()
                .Where(d => d.StoredDocumentId != null
                    && legacyStoredIds.Contains(d.StoredDocumentId.Value)
                    && d.Status != StatusHidden
                    && !d.IsManual)
                .ToListAsync(ct);
            // Do not demo-filter linked overlays: if the stored file row is visible in this list,
            // surface the linked NdRegulationDocument status (fixes super-admin list showing parsed/0).

            var addedNdIds = new List<Guid>();
            foreach (var linked in linkedNd)
            {
                if (IsDepartmentOverlay(linked)) continue;
                if (ndDocs.Any(d => d.Id == linked.Id)) continue;
                ndDocs.Add(linked);
                addedNdIds.Add(linked.Id);
            }

            if (addedNdIds.Count > 0)
            {
                try
                {
                    var extraCounts = await NdRegulationPointCanonicalFilter.BuildCanonicalCountMapAsync(
                        db,
                        addedNdIds,
                        addedNdIds.ToDictionary(id => id, _ => false),
                        ct);
                    foreach (var kv in extraCounts)
                        pointCountMap[kv.Key] = kv.Value;
                }
                catch { /* table may not exist */ }
            }

            ndByStoredId = ndDocs
                .Where(d => d.Status != StatusHidden && d.StoredDocumentId.HasValue && !IsDepartmentOverlay(d))
                .GroupBy(d => d.StoredDocumentId!.Value)
                .ToDictionary(
                    g => g.Key,
                    g => g.OrderByDescending(d => pointCountMap.GetValueOrDefault(d.Id))
                        .ThenByDescending(d => d.CreatedAt)
                        .First());

            ndForLegacyByStoredId = ndDocs
                .Where(d => d.Status != StatusHidden && d.StoredDocumentId.HasValue && !IsDepartmentOverlay(d) && !d.IsManual)
                .GroupBy(d => d.StoredDocumentId!.Value)
                .ToDictionary(
                    g => g.Key,
                    g => g.OrderByDescending(d => pointCountMap.GetValueOrDefault(d.Id))
                        .ThenByDescending(d => d.CreatedAt)
                        .First());
        }

        var neededStoredIds = ndDocs
            .Where(d => d.StoredDocumentId.HasValue)
            .Select(d => d.StoredDocumentId!.Value)
            .Concat(legacyRows.Select(l => l.Id))
            .Concat(hiddenLegacyRows.Select(l => l.Id))
            .Distinct()
            .ToList();

        var allRegStored = neededStoredIds.Count == 0
            ? new Dictionary<Guid, StoredDocument>()
            : await db.StoredDocuments.AsNoTracking()
                .Where(d => d.DocKind == "regulation" && neededStoredIds.Contains(d.Id))
                .ToDictionaryAsync(d => d.Id, ct);

        var profileNames = await LoadProfileNamesAsync(
            db,
            ndDocs.SelectMany(d => new Guid?[] { d.CreatedBy, d.ExtractedBy })
                .Concat(legacyRows.Select(d => d.UploadedBy))
                .Concat(hiddenLegacyRows.SelectMany(d => new Guid?[] { d.UploadedBy, d.HiddenBy })),
            ct);

        var cachedHashes = await NdLegacyDataQueries.GetExtractCachedHashesAsync(
            db, legacyRows.Select(d => d.FileHash), ct);

        var items = new List<object>();

        if (hiddenOnly)
        {
            foreach (var d in ndDocs.Where(d => d.Status == StatusHidden && !IsDepartmentOverlay(d)).OrderByDescending(d => d.UpdatedAt))
            {
                allRegStored.TryGetValue(d.StoredDocumentId ?? Guid.Empty, out var stored);
                items.Add(BuildRegulationListItem(
                    d, stored, deptNames, pointCountMap, profileNames, isHidden: true));
            }

            foreach (var leg in hiddenLegacyRows)
            {
                if (hiddenStoredIds.Contains(leg.Id)) continue;
                if (ndForLegacyByStoredId.ContainsKey(leg.Id)) continue;
                items.Add(BuildLegacyRegulationListItem(
                    leg,
                    deptOverlayByStoredId.TryGetValue(leg.Id, out var deptOv) ? deptOv.DepartmentId : null,
                    ndForLegacyByStoredId.GetValueOrDefault(leg.Id),
                    deptNames,
                    cachedHashes,
                    pointCountMap,
                    profileNames,
                    isHidden: true,
                    allRegStored.GetValueOrDefault(leg.Id)));
            }

            var sortedHidden = items
                .OrderByDescending(i => (DateTimeOffset?)i.GetType().GetProperty("updatedAt")?.GetValue(i)
                    ?? (DateTimeOffset)i.GetType().GetProperty("createdAt")!.GetValue(i)!)
                .ToList();
            return Ok(new { success = true, data = sortedHidden });
        }

        foreach (var leg in legacyRows)
        {
            if (hiddenStoredIds.Contains(leg.Id)) continue;

            var deptId = deptOverlayByStoredId.TryGetValue(leg.Id, out var deptOv) ? deptOv.DepartmentId : null;
            if (departmentId.HasValue && deptId != departmentId) continue;

            var ndForLegacy = ndForLegacyByStoredId.GetValueOrDefault(leg.Id);
            if (ndForLegacy != null)
                continue;

            var extractionStatus = NdLegacyDataQueries.LegacyRegulationExtractionStatus(
                leg.PointCount, leg.FileHash, cachedHashes);
            var pointCount = ndForLegacy != null
                ? pointCountMap.GetValueOrDefault(ndForLegacy.Id)
                : (leg.PointCount ?? 0);
            var displayStatus = MapDisplayExtractionStatus(extractionStatus, pointCount, isManual: false);
            if (!MatchesStatusFilter(displayStatus, status)) continue;

            items.Add(BuildLegacyRegulationListItem(
                leg,
                deptId,
                ndForLegacy,
                deptNames,
                cachedHashes,
                pointCountMap,
                profileNames,
                isHidden: false,
                allRegStored.GetValueOrDefault(leg.Id)));
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

            if (d.StoredDocumentId is Guid storedKey
                && ndByStoredId.TryGetValue(storedKey, out var canonicalNd)
                && canonicalNd.Id != d.Id)
                continue;

            var resolvedCount = pointCountMap.GetValueOrDefault(d.Id);
            var displayStatus = MapDisplayExtractionStatus(d.ExtractionStatus, resolvedCount, isManual: false);
            if (!MatchesStatusFilter(displayStatus, status)) continue;

            StoredDocument? storedDoc = null;
            if (d.StoredDocumentId is Guid sid)
                allRegStored.TryGetValue(sid, out storedDoc);

            items.Add(BuildRegulationListItem(
                d, storedDoc, deptNames, pointCountMap, profileNames, isHidden: false));
        }

        if (!ndDocs.Any(d => d.IsManual))
        {
            var manualDoc = await db.NdRegulationDocuments.AsNoTracking()
                .FirstOrDefaultAsync(d => d.IsManual && d.Status != StatusHidden, ct);
            if (manualDoc == null && profile!.Role is "maker" or "super_admin")
                manualDoc = await EnsureManualDocumentAsync(ct);
            if (manualDoc != null)
            {
                var manualCount = pointCountMap.GetValueOrDefault(manualDoc.Id);
                if (manualCount == 0)
                    manualCount = await db.NdRegulationPoints.CountAsync(
                        p => p.RegulationDocumentId == manualDoc.Id, ct);
                if (!departmentId.HasValue || manualDoc.DepartmentId == departmentId)
                {
                    items.Add(new
                    {
                        id = manualDoc.Id,
                        source = "manual",
                        name = manualDoc.Name,
                        departmentId = manualDoc.DepartmentId,
                        departmentName = DeptName(deptNames, manualDoc.DepartmentId),
                        extractionStatus = "manual",
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
        var (_, user, error) = await RequireAuthWithUserAsync(db, jwt, ct,
            "super_admin", "maker", "checker", "reviewer");
        if (error != null) return error;

        var demoCtx = await NdDemoIsolationContext.ResolveAsync(demoDirectory, user, ct);

        var term = (q ?? "").Trim();
        if (term.Length < 2)
            return Ok(new { success = true, data = Array.Empty<object>(), totalMatches = 0 });

        var deptNames = await LoadDepartmentNamesAsync(demoCtx, ct);
        var pattern = $"%{term}%";
        var take = Math.Clamp(limit, 1, 200);

        List<NdRegulationDocument> ndDocs;
        try
        {
            ndDocs = await NdDemoDataFilters.ApplyToRegulationDocuments(
                    db.NdRegulationDocuments.AsNoTracking(),
                    demoCtx)
                .ToListAsync(ct);
        }
        catch
        {
            ndDocs = [];
        }

        var hiddenStoredIds = ndDocs
            .Where(d => d.Status == StatusHidden && d.StoredDocumentId.HasValue)
            .Select(d => d.StoredDocumentId!.Value)
            .ToHashSet();

        var ndDocIds = ndDocs.Select(d => d.Id).ToList();
        var manualByDocId = ndDocs.ToDictionary(d => d.Id, d => d.IsManual);
        var pointCountMap = new Dictionary<Guid, int>();
        if (ndDocIds.Count > 0)
        {
            try
            {
                pointCountMap = await NdRegulationPointCanonicalFilter.BuildCanonicalCountMapAsync(
                    db, ndDocIds, manualByDocId, ct);
            }
            catch { /* table may not exist */ }
        }

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
        var (_, user, error) = await RequireAuthWithUserAsync(db, jwt, ct,
            "super_admin", "maker", "checker", "reviewer");
        if (error != null) return error;

        var demoCtx = await NdDemoIsolationContext.ResolveAsync(demoDirectory, user, ct);

        var deptNames = await LoadDepartmentNamesAsync(demoCtx, ct);

        var refreshed = await uploadService.TryRefreshExtractionStatusAsync(id, ct);
        var doc = refreshed
            ?? await db.NdRegulationDocuments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct)
            ?? await db.NdRegulationDocuments.AsNoTracking()
                .FirstOrDefaultAsync(d => d.StoredDocumentId == id, ct);
        if (doc != null && !IsDepartmentOverlay(doc))
        {
            if (doc.Status == StatusHidden)
                return NotFound(new { success = false, message = "Not found" });

            var pointCount = await CountActiveRegulationPointsAsync(db, doc.Id, doc.IsManual, ct);
            var rawStatus = doc.IsManual ? "completed" : (doc.ExtractionStatus ?? "pending");
            // Stale "processing" after API restart / finished demo clone — clear so UI stops spinning.
            if (!doc.IsManual
                && pointCount > 0
                && string.Equals(rawStatus, "processing", StringComparison.OrdinalIgnoreCase)
                && !NdDemoInterceptionService.IsRegulationDemoJobRunning(doc.Id))
            {
                var tracked = await db.NdRegulationDocuments.FirstOrDefaultAsync(d => d.Id == doc.Id, ct);
                if (tracked != null
                    && string.Equals(tracked.ExtractionStatus, "processing", StringComparison.OrdinalIgnoreCase)
                    && !NdDemoInterceptionService.IsRegulationDemoJobRunning(doc.Id))
                {
                    tracked.ExtractionStatus = "completed";
                    tracked.ExtractionProgressLabel = null;
                    tracked.ExtractionProgressPct = null;
                    tracked.ExtractedAt ??= DateTimeOffset.UtcNow;
                    tracked.UpdatedAt = DateTimeOffset.UtcNow;
                    await db.SaveChangesAsync(ct);
                    doc = tracked;
                    rawStatus = "completed";
                }
            }
            var displayStatus = MapDisplayExtractionStatus(rawStatus, pointCount, doc.IsManual);
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
                    extractionStatus = displayStatus,
                    extractionProgressLabel = ShowsExtractionProgress(displayStatus)
                        ? doc.ExtractionProgressLabel
                        : null,
                    extractionProgressPct = ShowsExtractionProgress(displayStatus)
                        ? doc.ExtractionProgressPct
                        : null,
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
            ? await NdRegulationPointCanonicalFilter.CountCanonicalForDocumentAsync(
                db, overlay.Id, overlay.IsManual, ct)
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
        var (profile, user, error) = await RequireAuthWithUserAsync(db, jwt, ct, "super_admin", "maker");
        if (error != null) return error;

        var demoCtx = await NdDemoIsolationContext.ResolveAsync(demoDirectory, user, ct);

        var departmentId = ParseDepartmentId(body);
        if (departmentId.HasValue)
        {
            var dept = await db.NdDepartments.AsNoTracking()
                .FirstOrDefaultAsync(d => d.Id == departmentId.Value, ct);
            if (dept == null || !NdDemoDataFilters.CanAccessCreatedBy(dept.CreatedBy, demoCtx))
                return NotFound(new { success = false, message = "Department not found." });
        }

        var deptNames = await LoadDepartmentNamesAsync(demoCtx, ct);
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
        var (profile, user, error) = await RequireAuthWithUserAsync(db, jwt, ct, "super_admin", "maker");
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

            // Upload only stores the file as pending — parse/extract are separate steps for all users.
            // Demo point clone runs on Extract (not here) so upload returns quickly.
            var pointCount = await NdRegulationPointCanonicalFilter.CountCanonicalForDocumentAsync(
                db, doc.Id, isManual: false, ct);
            var displayStatus = MapDisplayExtractionStatus(doc.ExtractionStatus, pointCount, isManual: false);
            return Ok(new
            {
                success = true,
                data = new
                {
                    id = doc.Id,
                    name = doc.Name,
                    departmentId = doc.DepartmentId,
                    extractionStatus = displayStatus,
                    extractionProgressLabel = ShowsExtractionProgress(displayStatus)
                        ? doc.ExtractionProgressLabel
                        : null,
                    extractionProgressPct = ShowsExtractionProgress(displayStatus)
                        ? doc.ExtractionProgressPct
                        : null,
                    pointCount,
                },
            });
        }
        catch (Exception ex)
        {
            return BadRequest(new { success = false, message = ex.Message });
        }
    }

    [HttpPost("{id:guid}/points/repair")]
    public async Task<IActionResult> RepairPoints(Guid id, CancellationToken ct)
    {
        var (_, error) = await RequireAuthAsync(db, jwt, ct, "super_admin", "maker");
        if (error != null) return error;

        var ndDoc = await db.NdRegulationDocuments.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == id, ct)
            ?? await db.NdRegulationDocuments.AsNoTracking()
                .FirstOrDefaultAsync(d => d.StoredDocumentId == id, ct);
        if (ndDoc == null)
            return NotFound(new { success = false, message = "Regulation document not found." });
        if (ndDoc.IsManual)
            return BadRequest(new { success = false, message = "Repair is not available for manual documents." });

        try
        {
            var result = await pointRepair.RepairDocumentAsync(ndDoc.Id, ct);
            return Ok(new
            {
                success = true,
                message = result.Recovered > 0
                    ? $"Repaired points: {result.BeforeCount} → {result.AfterCount} active ({result.SoftDeleted} soft-deleted, {result.Recovered} recovered from markdown)."
                    : $"Repaired points: {result.BeforeCount} → {result.AfterCount} active ({result.SoftDeleted} soft-deleted).",
                data = new
                {
                    regulationDocumentId = ndDoc.Id,
                    pointCount = result.AfterCount,
                    repair = result,
                },
            });
        }
        catch (Exception ex)
        {
            return BadRequest(new { success = false, message = ex.Message });
        }
    }

    [HttpPost("{id:guid}/parse")]
    public async Task<IActionResult> Parse(Guid id, CancellationToken ct)
    {
        var (profile, user, error) = await RequireAuthWithUserAsync(db, jwt, ct, "super_admin", "maker");
        if (error != null) return error;

        var demoCtx = await NdDemoIsolationContext.ResolveAsync(demoDirectory, user, ct);

        try
        {
            var regDoc = await db.NdRegulationDocuments.FirstOrDefaultAsync(d => d.Id == id, ct)
                ?? await db.NdRegulationDocuments.FirstOrDefaultAsync(d => d.StoredDocumentId == id, ct);
            if (regDoc == null)
                return NotFound(new { success = false, message = "Regulation document not found." });

            if (demoInterception.CanMutateRegulationDocument(regDoc, demoCtx))
            {
                var rawStatus = (regDoc.ExtractionStatus ?? "").Trim().ToLowerInvariant();
                if (rawStatus is "processing")
                {
                    var processingPointCount = await NdRegulationPointCanonicalFilter.CountCanonicalForDocumentAsync(
                        db, regDoc.Id, isManual: false, ct);
                    return Ok(new
                    {
                        success = true,
                        data = new
                        {
                            id = regDoc.Id,
                            regulationDocumentId = regDoc.Id,
                            storedDocumentId = regDoc.StoredDocumentId,
                            extractionStatus = "processing",
                            extractionProgressLabel = regDoc.ExtractionProgressLabel,
                            extractionProgressPct = regDoc.ExtractionProgressPct,
                            pointCount = processingPointCount,
                        },
                    });
                }

                if (rawStatus is "parsed" or "completed")
                {
                    var existingPointCount = await NdRegulationPointCanonicalFilter.CountCanonicalForDocumentAsync(
                        db, regDoc.Id, isManual: false, ct);
                    var existingDisplay = MapDisplayExtractionStatus(regDoc.ExtractionStatus, existingPointCount, isManual: false);
                    return Ok(new
                    {
                        success = true,
                        data = new
                        {
                            id = regDoc.Id,
                            regulationDocumentId = regDoc.Id,
                            storedDocumentId = regDoc.StoredDocumentId,
                            extractionStatus = existingDisplay,
                            pointCount = existingPointCount,
                        },
                    });
                }

                regDoc.ExtractionStatus = "processing";
                regDoc.ExtractionProgressLabel = "Parsing document…";
                regDoc.ExtractionProgressPct = 8;
                regDoc.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);

                await demoInterception.SimulateRegulationParseAsync(db, regDoc, profile!.Id, demoCtx, ct);
            }
            else
            {
                // Production — live Landing AI parse (unchanged).
                await uploadService.ParseByRegulationIdAsync(id, profile!.Id, ct);
            }

            regDoc = await db.NdRegulationDocuments.AsNoTracking()
                .FirstOrDefaultAsync(d => d.Id == id, ct)
                ?? await db.NdRegulationDocuments.AsNoTracking()
                    .FirstOrDefaultAsync(d => d.StoredDocumentId == id, ct);
            if (regDoc == null)
                return NotFound(new { success = false, message = "Regulation document not found." });

            var pointCount = await NdRegulationPointCanonicalFilter.CountCanonicalForDocumentAsync(
                db, regDoc.Id, isManual: false, ct);
            var displayStatus = MapDisplayExtractionStatus(regDoc.ExtractionStatus, pointCount, isManual: false);

            return Ok(new
            {
                success = true,
                data = new
                {
                    id = regDoc.Id,
                    regulationDocumentId = regDoc.Id,
                    storedDocumentId = regDoc.StoredDocumentId,
                    extractionStatus = displayStatus,
                    extractionProgressLabel = ShowsExtractionProgress(displayStatus)
                        ? regDoc.ExtractionProgressLabel
                        : null,
                    extractionProgressPct = ShowsExtractionProgress(displayStatus)
                        ? regDoc.ExtractionProgressPct
                        : null,
                    pointCount,
                },
            });
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            return NotFound(new { success = false, message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { success = false, message = ex.Message });
        }
        catch (Exception ex)
        {
            return BadRequest(new { success = false, message = ex.Message });
        }
    }

    [HttpPost("{id:guid}/extract")]
    public async Task<IActionResult> Extract(Guid id, CancellationToken ct)
    {
        var (profile, user, error) = await RequireAuthWithUserAsync(db, jwt, ct, "super_admin", "maker");
        if (error != null) return error;

        var demoCtx = await NdDemoIsolationContext.ResolveAsync(demoDirectory, user, ct);

        try
        {
            var regDoc = await db.NdRegulationDocuments.FirstOrDefaultAsync(d => d.Id == id, ct)
                ?? await db.NdRegulationDocuments.FirstOrDefaultAsync(d => d.StoredDocumentId == id, ct);
            if (regDoc == null)
                return NotFound(new { success = false, message = "Regulation document not found." });

            var isDemoSimulated = demoInterception.CanMutateRegulationDocument(regDoc, demoCtx);
            NdRegulationDocument doc;
            if (isDemoSimulated)
            {
                var rawStatus = (regDoc.ExtractionStatus ?? "").Trim().ToLowerInvariant();
                // Demo clones templates — if upload never ran Parse, do parse+extract in one step.
                if (rawStatus is "pending" or "failed" or "")
                {
                    regDoc.ExtractionStatus = "processing";
                    regDoc.ExtractionProgressLabel = "Parsing document…";
                    regDoc.ExtractionProgressPct = 8;
                    regDoc.UpdatedAt = DateTimeOffset.UtcNow;
                    await db.SaveChangesAsync(ct);

                    await demoInterception.SimulateRegulationParseAsync(db, regDoc, profile!.Id, demoCtx, ct);
                    await db.Entry(regDoc).ReloadAsync(ct);
                    rawStatus = (regDoc.ExtractionStatus ?? "").Trim().ToLowerInvariant();
                }

                if (rawStatus is "processing")
                {
                    if (NdDemoInterceptionService.IsRegulationDemoJobRunning(regDoc.Id))
                    {
                        var activeCount = await NdRegulationPointCanonicalFilter.CountCanonicalForDocumentAsync(
                            db, regDoc.Id, isManual: false, ct);
                        return Ok(new
                        {
                            success = true,
                            message = "Extraction in progress.",
                            data = new
                            {
                                id = regDoc.Id,
                                regulationDocumentId = regDoc.Id,
                                storedDocumentId = regDoc.StoredDocumentId,
                                extractionStatus = "processing",
                                extractionProgressLabel = regDoc.ExtractionProgressLabel,
                                extractionProgressPct = regDoc.ExtractionProgressPct,
                                pointCount = activeCount,
                            },
                        });
                    }
                    // Stale processing from a crashed/restarted run — continue below and re-run extract.
                }

                regDoc.ExtractionStatus = "processing";
                regDoc.ExtractionProgressLabel = "Extracting regulation points…";
                regDoc.ExtractionProgressPct = 10;
                regDoc.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);

                try
                {
                    await demoInterception.SimulateRegulationExtractAsync(db, regDoc, profile!.Id, demoCtx, ct);
                }
                catch (Exception ex)
                {
                    regDoc.ExtractionStatus = "failed";
                    regDoc.ExtractionProgressLabel = ex.Message;
                    regDoc.ExtractionProgressPct = null;
                    regDoc.UpdatedAt = DateTimeOffset.UtcNow;
                    await db.SaveChangesAsync(ct);
                    throw;
                }

                doc = await db.NdRegulationDocuments.AsNoTracking()
                    .FirstAsync(d => d.Id == regDoc.Id, ct);
            }
            else
            {
                // Production — live Landing AI extract (unchanged).
                doc = await uploadService.ExtractByRegulationIdAsync(id, profile!.Id, ct);
            }
            var pointCount = await NdRegulationPointCanonicalFilter.CountCanonicalForDocumentAsync(
                db, doc.Id, isManual: false, ct);
            var displayStatus = MapDisplayExtractionStatus(doc.ExtractionStatus, pointCount, isManual: false);
            var stillProcessing = string.Equals(displayStatus, "processing", StringComparison.OrdinalIgnoreCase);
            var payload = new
            {
                success = true,
                message = stillProcessing
                    ? "Extraction started."
                    : $"Extraction complete — {pointCount} points.",
                data = new
                {
                    id = doc.Id,
                    regulationDocumentId = doc.Id,
                    storedDocumentId = doc.StoredDocumentId,
                    extractionStatus = displayStatus,
                    extractionProgressLabel = ShowsExtractionProgress(displayStatus)
                        ? doc.ExtractionProgressLabel
                        : null,
                    extractionProgressPct = ShowsExtractionProgress(displayStatus)
                        ? doc.ExtractionProgressPct
                        : null,
                    pointCount,
                },
            };

            // Demo and completed jobs return 200; production async jobs return 202 while processing.
            if (isDemoSimulated || !stillProcessing)
                return Ok(payload);

            return Accepted(payload);
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

    /// <summary>Update stored point page references from native PDF text (preferred) or parse cache — no Landing AI credits.</summary>
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

        var pointCount = await NdRegulationPointCanonicalFilter.CountCanonicalForDocumentAsync(
            db, doc.Id, doc.IsManual, ct);
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

    [HttpGet("{id:guid}/export/points")]
    public async Task<IActionResult> ExportPoints(Guid id, CancellationToken ct)
    {
        var (_, error) = await RequireAuthAsync(db, jwt, ct,
            "super_admin", "maker", "checker", "reviewer");
        if (error != null) return error;

        var export = await BuildPointsExportTextAsync(id, ct);
        if (export == null)
            return NotFound(new { success = false, message = "Regulation document not found." });

        var fileName = $"{NdDocumentExportHelper.SafeExportBaseName(export.DocumentName, "regulation")}-points.json";
        return File(NdDocumentExportHelper.Utf8Bytes(export.Text), "application/json; charset=utf-8", fileName);
    }

    [HttpGet("{id:guid}/export/file")]
    public async Task<IActionResult> ExportFile(Guid id, CancellationToken ct)
    {
        var (_, error) = await RequireAuthAsync(db, jwt, ct,
            "super_admin", "maker", "checker", "reviewer");
        if (error != null) return error;

        if (!storage.IsConfigured)
            return StatusCode(503, new { success = false, message = "Supabase Storage not configured." });

        var resolved = await ResolveRegulationFileAsync(id, ct);
        if (resolved == null)
            return NotFound(new { success = false, message = "Regulation file not found." });

        var bytes = await storage.DownloadAsync(resolved.StoragePath, ct);
        var fileName = Path.GetFileName(resolved.FileName ?? "regulation.pdf");
        var contentType = fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
            ? "application/pdf"
            : "application/octet-stream";
        return File(bytes, contentType, fileName);
    }

    [HttpGet("{id:guid}/points")]
    public async Task<IActionResult> Points(
        Guid id,
        [FromQuery] bool lite,
        [FromQuery] bool demoScope,
        CancellationToken ct)
    {
        var (profile, user, error) = await RequireAuthWithUserAsync(db, jwt, ct,
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

            if (!ndDoc.IsManual)
                await pointRepair.EnsureCbuaeSection5LandingAiPatchAsync(ndDoc.Id, ct);

            // Heal stuck "parsed/pending/processing" when points already exist and no job is running.
            if (!ndDoc.IsManual)
            {
                var healCount = await NdRegulationPointCanonicalFilter.CountCanonicalForDocumentAsync(
                    db, ndDoc.Id, isManual: false, ct);
                var healStatus = (ndDoc.ExtractionStatus ?? "").Trim().ToLowerInvariant();
                var staleProcessing = healStatus is "processing"
                    && healCount > 0
                    && !NdDemoInterceptionService.IsRegulationDemoJobRunning(ndDoc.Id);
                if (healCount > 0 && (healStatus is "parsed" or "pending" or "" || staleProcessing))
                {
                    var trackedHeal = await db.NdRegulationDocuments.FirstOrDefaultAsync(d => d.Id == ndDoc.Id, ct);
                    if (trackedHeal != null)
                    {
                        trackedHeal.ExtractionStatus = "completed";
                        trackedHeal.ExtractionProgressLabel = null;
                        trackedHeal.ExtractionProgressPct = null;
                        trackedHeal.ExtractedAt ??= DateTimeOffset.UtcNow;
                        trackedHeal.UpdatedAt = DateTimeOffset.UtcNow;
                        await db.SaveChangesAsync(ct);
                        ndDoc = trackedHeal;
                    }
                }
            }

            // Demo-owned docs: seed only when empty (never wipe+reclone on View — that made Loading points hang).
            {
                var demoCtx = await NdDemoIsolationContext.ResolveAsync(demoDirectory, user, ct);
                if (NdDemoDataFilters.IsDemoOwned(ndDoc.CreatedBy, demoCtx))
                {
                    var existingForSeed = await NdRegulationPointCanonicalFilter.CountCanonicalForDocumentAsync(
                        db, ndDoc.Id, isManual: false, ct);
                    if (existingForSeed == 0)
                    {
                        var tracked = await db.NdRegulationDocuments.FirstOrDefaultAsync(d => d.Id == ndDoc.Id, ct);
                        if (tracked != null)
                        {
                            await demoInterception.TryEnsureRegulationPointsSeededAsync(
                                db, tracked, profile!.Id, demoCtx, ct);
                            ndDoc = await db.NdRegulationDocuments.AsNoTracking()
                                .FirstAsync(d => d.Id == ndDoc.Id, ct);
                        }
                    }
                }
            }

            IQueryable<NdRegulationPoint> pointQuery = db.NdRegulationPoints.AsNoTracking()
                .Where(p => p.RegulationDocumentId == ndDoc.Id && p.Status == NdRegulationPointStatus.Active);

            // CBUAE demo points are seeded with exact clause numbers — in-memory scope filter is enough.
            if (demoScope && !DemoAnalysisSeedService.IsCbuaeRegulationName(ndDoc.Name))
            {
                var (clauseNumbers, clauseTitles) = demoSeed.GetCbuaeClauseMatchTokens();
                pointQuery = pointQuery.Where(p =>
                    clauseNumbers.Contains(p.PointNumber)
                    || (p.PointTitle != null && clauseTitles.Contains(p.PointTitle)));
            }

            if (lite)
            {
                var liteEntities = await pointQuery.ToListAsync(ct);
                if (demoScope)
                {
                    liteEntities = demoSeed.FilterRegulationPointsToCbuaeDemoScope(liteEntities);
                }

                var liteCanonical = NdRegulationPointCanonicalFilter.FilterCanonical(
                    liteEntities,
                    ndDoc.IsManual);
                var litePoints = PointNumberSort.OrderByPointNumber(liteCanonical, p => p.PointNumber)
                    .Select(p => new
                    {
                        p.Id,
                        p.PointNumber,
                        p.PointTitle,
                        PointContent = p.PointContent != null && p.PointContent.Length > NdRegulApiProjection.LiteTextMax
                            ? p.PointContent.Substring(0, NdRegulApiProjection.LiteTextMax)
                            : p.PointContent,
                        p.PageReference,
                        p.IsIntroductionPoint,
                        p.IsAnnexPoint,
                    })
                    .ToList();

                return Ok(new
                {
                    success = true,
                    data = litePoints.Select(p => new
                    {
                        id = p.Id,
                        pointNumber = p.PointNumber,
                        pointTitle = p.PointTitle,
                        pointContent = p.PointContent,
                        pageReference = p.PageReference,
                        isIntroductionPoint = p.IsIntroductionPoint,
                        isAnnexPoint = p.IsAnnexPoint,
                    }).ToList(),
                    source = "nd",
                    pointCount = litePoints.Count,
                    lite = true,
                    demoScope,
                });
            }

            var loadedPoints = demoScope
                ? demoSeed.FilterRegulationPointsToCbuaeDemoScope(
                    await pointQuery.ToListAsync(ct))
                : await pointQuery.ToListAsync(ct);
            var points = PointNumberSort.OrderByPointNumber(
                    NdRegulationPointCanonicalFilter.FilterCanonical(loadedPoints, ndDoc.IsManual),
                    p => p.PointNumber)
                .ToList();

            var activePointCount = points.Count;

            return Ok(new
            {
                success = true,
                data = points.Select(MapNdPoint).ToList(),
                source = "nd",
                pointCount = activePointCount,
                returnedCount = points.Count,
                demoScope,
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

        point.Status = NdRegulationPointStatus.Removed;
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
        var rawStatus = ResolveRegulationRawStatus(d.ExtractionStatus, "pending", stored);
        var displayStatus = MapDisplayExtractionStatus(rawStatus, resolvedCount, isManual: false);
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
        LegacyRegulationListRow leg,
        Guid? deptId,
        NdRegulationDocument? ndForLegacy,
        IReadOnlyDictionary<Guid, string> deptNames,
        IReadOnlySet<string> cachedHashes,
        IReadOnlyDictionary<Guid, int> pointCountMap,
        IReadOnlyDictionary<Guid, string> profileNames,
        bool isHidden,
        StoredDocument? storedDoc = null)
    {
        var legacyStatus = NdLegacyDataQueries.LegacyRegulationExtractionStatus(
            leg.PointCount, leg.FileHash, cachedHashes);
        var pointCount = ndForLegacy != null
            ? pointCountMap.GetValueOrDefault(ndForLegacy.Id)
            : (leg.PointCount ?? 0);
        var rawStatus = ResolveRegulationRawStatus(ndForLegacy?.ExtractionStatus, legacyStatus, storedDoc);
        var displayStatus = MapDisplayExtractionStatus(rawStatus, pointCount, isManual: false);
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

    private static async Task<List<LegacyRegulationListRow>> LoadLegacyRegulationListRowsAsync(
        AppDbContext db,
        NdDemoIsolationContext demoCtx,
        IReadOnlyCollection<Guid> excludeStoredIds,
        bool hiddenOnly,
        CancellationToken ct)
    {
        try
        {
            var query = db.StoredDocuments.AsNoTracking()
                .Where(d => d.DocKind == "regulation" && d.IsHidden == hiddenOnly);
            if (excludeStoredIds.Count > 0)
                query = query.Where(d => !excludeStoredIds.Contains(d.Id));

            query = NdDemoDataFilters.ApplyToStoredDocuments(query, demoCtx);

            if (hiddenOnly)
                query = query.OrderByDescending(d => d.HiddenAt ?? d.UpdatedAt);
            else
                query = query.OrderByDescending(d => d.UpdatedAt);

            return await query
                .Select(d => new LegacyRegulationListRow
                {
                    Id = d.Id,
                    Title = d.Title,
                    CreatedAt = d.CreatedAt,
                    UpdatedAt = d.UpdatedAt,
                    UploadedBy = d.UploadedBy,
                    OriginalFileName = d.OriginalFileName,
                    SourceStoragePath = d.SourceStoragePath,
                    StoragePath = d.StoragePath,
                    FileHash = d.FileHash,
                    PointCount = d.PointCount,
                    HiddenAt = d.HiddenAt,
                    HiddenBy = d.HiddenBy,
                })
                .ToListAsync(ct);
        }
        catch
        {
            return [];
        }
    }

    private static bool MatchesStatusFilter(string displayStatus, string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return true;
        return string.Equals(displayStatus, filter, StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveRegulationRawStatus(
        string? ndExtractionStatus,
        string legacyFallbackStatus,
        StoredDocument? stored)
    {
        if (!string.IsNullOrWhiteSpace(ndExtractionStatus))
            return ndExtractionStatus.Trim();

        if (stored != null
            && string.Equals(stored.ParseStatus, "parsed", StringComparison.OrdinalIgnoreCase)
            && string.Equals(legacyFallbackStatus, "pending", StringComparison.OrdinalIgnoreCase))
            return "parsed";

        return legacyFallbackStatus;
    }

    private static async Task<int> CountActiveRegulationPointsAsync(
        AppDbContext dbCtx,
        Guid regulationDocumentId,
        bool isManual,
        CancellationToken ct) =>
        await NdRegulationPointCanonicalFilter.CountCanonicalForDocumentAsync(
            dbCtx, regulationDocumentId, isManual, ct);

    private static string MapDisplayExtractionStatus(string rawStatus, int pointCount, bool isManual)
    {
        if (isManual) return "manual";
        // Active jobs must show processing even when stale points remain from a prior extract.
        if (string.Equals(rawStatus, "processing", StringComparison.OrdinalIgnoreCase))
            return "processing";
        if (pointCount > 0) return "extracted";
        if (string.Equals(rawStatus, "parsed", StringComparison.OrdinalIgnoreCase))
            return "parsed";
        if (string.Equals(rawStatus, "paused", StringComparison.OrdinalIgnoreCase))
            return "paused";
        if (string.Equals(rawStatus, "failed", StringComparison.OrdinalIgnoreCase))
            return "failed";
        if (string.Equals(rawStatus, "completed", StringComparison.OrdinalIgnoreCase))
            return pointCount > 0 ? "extracted" : "failed";
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

    private async Task<Dictionary<Guid, string>> LoadDepartmentNamesAsync(
        NdDemoIsolationContext demoCtx,
        CancellationToken ct)
    {
        try
        {
            return await NdDemoDataFilters.ApplyToDepartments(
                    db.NdDepartments.AsNoTracking(), demoCtx)
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

    private sealed record RegulationFileExport(string StoragePath, string FileName);

    private sealed record PointsExportBundle(
        Guid DocumentId,
        string DocumentName,
        string? OriginalFileName,
        Guid? StoredDocumentId,
        string Source,
        int PointCount,
        List<object> Points);

    private sealed record PointsExportText(string DocumentName, string Text);

    private async Task<PointsExportText?> BuildPointsExportTextAsync(Guid id, CancellationToken ct)
    {
        var bundle = await BuildPointsExportBundleAsync(id, ct);
        if (bundle == null)
            return null;

        string? cacheKey = null;
        string? fileHash = null;
        var fileName = bundle.OriginalFileName ?? bundle.DocumentName;

        if (bundle.StoredDocumentId is Guid storedId)
        {
            var stored = await db.StoredDocuments.AsNoTracking()
                .FirstOrDefaultAsync(d => d.Id == storedId, ct);
            if (stored != null)
            {
                fileHash = stored.FileHash;
                fileName = stored.OriginalFileName ?? stored.Title ?? fileName;
                cacheKey = string.IsNullOrWhiteSpace(stored.ExtractionCacheKey)
                    ? NdRegulationCacheKeys.ForStoredDocument(stored.Id)
                    : stored.ExtractionCacheKey.Trim();
            }
        }

        cacheKey ??= NdRegulationCacheKeys.ForRegulationDocument(bundle.DocumentId);

        var cachedJson = await landingCache.ResolveExtractPointsJsonAsync(
            cacheKey,
            fileHash,
            LandingAiGovExtractService.GovSchemaKey,
            "nd-export",
            ct);

        var comparablePoints = !string.IsNullOrWhiteSpace(cachedJson)
            ? ParseComparablePointsFromExtractJson(cachedJson)
            : bundle.Points;

        var payload = new
        {
            success = true,
            cached = !string.IsNullOrWhiteSpace(cachedJson),
            fileName,
            fileHash = fileHash ?? "",
            schemaKey = LandingAiGovExtractService.GovSchemaKey,
            pointCount = comparablePoints.Count,
            points = comparablePoints,
        };
        var text = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });

        return new PointsExportText(bundle.DocumentName, text);
    }

    private static List<object> ParseComparablePointsFromExtractJson(string cachedJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(cachedJson);
            if (!doc.RootElement.TryGetProperty("points", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return [];

            var list = new List<object>();
            foreach (var pt in arr.EnumerateArray())
            {
                var pointId = pt.TryGetProperty("point_id", out var idEl) ? idEl.GetString() ?? "" : "";
                var title = pt.TryGetProperty("title", out var titleEl) ? titleEl.GetString() : null;
                var text = pt.TryGetProperty("text", out var textEl) ? textEl.GetString() ?? "" : "";
                var section = pt.TryGetProperty("section", out var sectionEl) ? sectionEl.GetString() : null;
                var pointType = pt.TryGetProperty("point_type", out var typeEl) ? typeEl.GetString() : null;
                if (!GovPointClassifier.IsComparableForAnalysis(pointId, title, text, section, pointType))
                    continue;

                var pageHint = 0;
                if (pt.TryGetProperty("page_hint", out var pageEl) && pageEl.ValueKind == JsonValueKind.Number)
                    pageHint = pageEl.GetInt32();

                list.Add(new
                {
                    point_id = pointId,
                    title,
                    text,
                    section,
                    page_hint = pageHint,
                    point_type = string.IsNullOrWhiteSpace(pointType) ? "mandatory" : pointType,
                    for_analysis = true,
                });
            }

            return list;
        }
        catch
        {
            return [];
        }
    }

    private async Task<RegulationFileExport?> ResolveRegulationFileAsync(Guid id, CancellationToken ct)
    {
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
            fileName = stored?.OriginalFileName ?? stored?.Title;
        }

        if (string.IsNullOrWhiteSpace(storagePath))
            return null;

        if (ndDoc != null && ndDoc.StoredDocumentId is Guid sid)
        {
            var storedName = await db.StoredDocuments.AsNoTracking()
                .Where(d => d.Id == sid)
                .Select(d => d.OriginalFileName)
                .FirstOrDefaultAsync(ct);
            if (!string.IsNullOrWhiteSpace(storedName))
                fileName = storedName;
        }

        fileName ??= stored?.OriginalFileName ?? stored?.Title ?? "regulation.pdf";
        return new RegulationFileExport(storagePath, fileName);
    }

    private async Task<PointsExportBundle?> BuildPointsExportBundleAsync(Guid id, CancellationToken ct)
    {
        var ndDoc = await db.NdRegulationDocuments.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == id, ct)
            ?? await db.NdRegulationDocuments.AsNoTracking()
                .FirstOrDefaultAsync(d => d.StoredDocumentId == id, ct);

        if (ndDoc != null)
        {
            if (ndDoc.Status == StatusHidden)
                return null;

            var points = PointNumberSort.OrderByPointNumber(
                    await db.NdRegulationPoints.AsNoTracking()
                        .Where(p => p.RegulationDocumentId == ndDoc.Id && p.Status == NdRegulationPointStatus.Active)
                        .ToListAsync(ct),
                    p => p.PointNumber)
                .ToList();

            string? originalFileName = null;
            if (ndDoc.StoredDocumentId is Guid storedId)
            {
                originalFileName = await db.StoredDocuments.AsNoTracking()
                    .Where(d => d.Id == storedId)
                    .Select(d => d.OriginalFileName)
                    .FirstOrDefaultAsync(ct);
            }

            return new PointsExportBundle(
                ndDoc.Id,
                ndDoc.Name,
                originalFileName,
                ndDoc.StoredDocumentId,
                "nd",
                points.Count,
                points.Select(MapExportPoint).Where(p => p != null).Cast<object>().ToList());
        }

        var stored = await db.StoredDocuments.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == id && d.DocKind == "regulation", ct);

        if (stored == null || string.IsNullOrWhiteSpace(stored.FileHash))
            return null;

        var loaded = await govExtract.LoadFromDatabaseOrSeedAsync(stored.FileHash, ct);
        var linkedBuiltin =
            string.Equals(stored.FileHash, LandingAiGovExtractService.BuiltinGovFileHash, StringComparison.OrdinalIgnoreCase)
            && loaded.Source is "db-cache" or "seed";

        if (loaded.Source != "db-cache" && !linkedBuiltin)
            return null;

        var legacyPoints = govPoints.GetAllPoints()
            .OrderBy(p => p.PointId, StringComparer.Ordinal)
            .Select(p => MapLegacyExportPoint(id, p))
            .Where(p => p != null)
            .Cast<object>()
            .ToList();

        return new PointsExportBundle(
            id,
            stored.Title ?? stored.OriginalFileName ?? "regulation",
            stored.OriginalFileName,
            stored.Id,
            loaded.Source,
            legacyPoints.Count,
            legacyPoints);
    }

    private static object? MapExportPoint(NdRegulationPoint p)
    {
        var pageHint = ResolvePdfPage(p.PageReference, null) ?? 0;
        var pointType = ResolveExportPointType(
            p.PointNumber, p.PointTitle, p.PointContent, p.IsIntroductionPoint, p.IsAnnexPoint);
        if (!GovPointClassifier.IsComparableForAnalysis(
            p.PointNumber, p.PointTitle, p.PointContent, p.PageReference, pointType))
            return null;

        return new
        {
            point_id = p.PointNumber,
            title = p.PointTitle,
            text = p.PointContent,
            section = p.PageReference,
            page_hint = pageHint,
            point_type = pointType,
            for_analysis = true,
        };
    }

    private static object? MapLegacyExportPoint(Guid documentId, GovPoint p)
    {
        var isAnnex = GovPointClassifier.IsAnnexPoint(p.PointId, p.Title, p.Section);
        var isIntro = GovPointClassifier.IsIntroductionPoint(
            p.PointId, p.Title, p.Text, p.Section, null);
        var pageHint = ResolvePdfPage(p.Section, p.PageHint) ?? 0;
        var pointType = ResolveExportPointType(p.PointId, p.Title, p.Text, isIntro, isAnnex);
        if (!GovPointClassifier.IsComparableForAnalysis(p.PointId, p.Title, p.Text, p.Section, pointType))
            return null;

        return new
        {
            point_id = p.PointId,
            title = p.Title,
            text = p.Text,
            section = p.Section,
            page_hint = pageHint,
            point_type = pointType,
            for_analysis = true,
        };
    }

    private static string ResolveExportPointType(
        string pointNumber,
        string? title,
        string text,
        bool isIntroductionPoint,
        bool isAnnexPoint)
    {
        if (isAnnexPoint || isIntroductionPoint)
            return "informational";
        if (GovPointClassifier.IsIntroductionPoint(pointNumber, title, text, null, null))
            return "informational";

        var body = (text ?? "").Trim();
        if (body.Length < 400
            && Regex.IsMatch(body, @"\b(means|refers to|is defined as|is a technique|is an algorithm)\b", RegexOptions.IgnoreCase)
            && !Regex.IsMatch(body, @"\b(must|shall|should|required to)\b", RegexOptions.IgnoreCase))
            return "definition";

        return "mandatory";
    }
}
