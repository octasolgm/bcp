using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Reguliq.Api.Data;
using Reguliq.Api.Data.Entities;
using Reguliq.Api.Data.NewDashboard.Entities;
using Reguliq.Api.Services.Storage;

namespace Reguliq.Api.Services.NewDashboard.Demo;

public record DemoClearRequest(
    bool ClearAll = false,
    bool ClearInternalDocuments = false,
    bool ClearRegulationDocuments = false,
    bool ClearLibraries = false,
    bool ClearAnalysisRuns = false,
    bool ClearUsers = false);

public record DemoClearResult(
    int InternalDocuments,
    int RegulationDocuments,
    int Libraries,
    int AnalysisRuns,
    int UsersDeactivated,
    int DeletedAnalysisRunsPurged = 0,
    int DeletedInternalDocumentsPurged = 0,
    int DeletedRegulationDocumentsPurged = 0);

/// <summary>
/// Real-admin tools: clear demo-owned workspace data (never touches demo analysis templates)
/// and manage judgment templates used by demo simulation.
/// </summary>
public sealed class NdDemoWorkspaceService(
    AppDbContext db,
    NdDemoUserDirectory demoDirectory,
    IMemoryCache cache,
    IHostEnvironment env,
    NdDashboardCacheService dashboardCache,
    SupabaseStorageService storage,
    ILogger<NdDemoWorkspaceService> logger)
{
    private const string DeletedStatus = "deleted";
    private const int RegulationStatusHidden = -1;
    public const string Analys1Code = "analys1demo";
    public const string Analys2Code = "analys2demo";

    private const string TemplatesSeededCacheKey = "nd:demo-templates-seeded";

    /// <summary>
    /// Idempotent seed. It is called from startup and from several read paths, but the work is
    /// heavy (DDL statements plus loading every template point) and the database is remote, so
    /// repeat calls are short-circuited instead of re-running on every request.
    /// </summary>
    public async Task EnsureTemplatesSeededAsync(CancellationToken ct = default)
    {
        if (cache.TryGetValue(TemplatesSeededCacheKey, out bool done) && done)
            return;

        await EnsureTemplatesSeededCoreAsync(ct);
        cache.Set(TemplatesSeededCacheKey, true, TimeSpan.FromMinutes(30));
    }

    /// <summary>Forces the next <see cref="EnsureTemplatesSeededAsync"/> call to re-run.</summary>
    public void InvalidateTemplateSeedCache() => cache.Remove(TemplatesSeededCacheKey);

    private async Task EnsureTemplatesSeededCoreAsync(CancellationToken ct = default)
    {
        await EnsureTemplateSchemaAsync(ct);

        var analys1 = await db.NdDemoAnalysisTemplates
            .Include(t => t.Points)
            .FirstOrDefaultAsync(t => t.Code == Analys1Code, ct);

        if (analys1 == null)
        {
            analys1 = new NdDemoAnalysisTemplate
            {
                Code = Analys1Code,
                Name = "Analysis 1 — CBUAE AML",
                Description =
                    "Seeded judgments for regulation CBUAE_EN_3945_VER2 + Internal AML Manual 290626. Used by demo simulation (no live AI).",
                RegulationNameHint = "CBUAE_EN_3945",
                InternalNameHint = "290626",
                SortOrder = 1,
                IsActive = true,
            };
            db.NdDemoAnalysisTemplates.Add(analys1);
            await db.SaveChangesAsync(ct);
        }

        if (analys1.Points.Count == 0)
        {
            var rows = LoadSeedJudgmentsFromFile();
            for (var i = 0; i < rows.Count; i++)
            {
                db.NdDemoAnalysisTemplatePoints.Add(MapRowToPoint(analys1.Id, rows[i], i));
            }
            analys1.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        else
        {
            // Deliberately no rebuild when the point count differs from the seed file: adding or
            // removing clauses in Admin → Demo is a supported edit, and rebuilding would discard it.
            var seedRows = LoadSeedJudgmentsFromFile();
            if (seedRows.Count > 0)
                await SyncAnalys1PointFieldsFromSeedAsync(analys1, seedRows, ct);
        }

        var analys2 = await db.NdDemoAnalysisTemplates
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Code == Analys2Code, ct);
        if (analys2 == null)
        {
            db.NdDemoAnalysisTemplates.Add(new NdDemoAnalysisTemplate
            {
                Code = Analys2Code,
                Name = "Analysis 2 — (pending)",
                Description = "Placeholder for a second demo document pair. Add points when ready.",
                RegulationNameHint = "",
                InternalNameHint = "",
                SortOrder = 2,
                IsActive = false,
            });
            await db.SaveChangesAsync(ct);
        }
    }

    private async Task EnsureTemplateSchemaAsync(CancellationToken ct)
    {
        // Idempotent — also safe if NdIncrementalSchemaBootstrap already ran.
        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS demo_analysis_templates (
              id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
              code TEXT NOT NULL UNIQUE,
              name TEXT NOT NULL,
              description TEXT NULL,
              regulation_name_hint TEXT NOT NULL DEFAULT '',
              internal_name_hint TEXT NOT NULL DEFAULT '',
              is_active BOOLEAN NOT NULL DEFAULT true,
              sort_order INT NOT NULL DEFAULT 0,
              created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
              updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
            );
            """,
            ct);
        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS demo_analysis_template_points (
              id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
              template_id UUID NOT NULL REFERENCES demo_analysis_templates(id) ON DELETE CASCADE,
              clause_no TEXT NOT NULL DEFAULT '',
              clause_title TEXT NULL,
              design_status TEXT NOT NULL DEFAULT 'partial',
              operating_status TEXT NOT NULL DEFAULT 'partial',
              overall_status TEXT NOT NULL DEFAULT 'partial',
              confidence DOUBLE PRECISION NOT NULL DEFAULT 0,
              interpretation TEXT NOT NULL DEFAULT '',
              policy_extract_json JSONB NOT NULL DEFAULT '[]'::jsonb,
              document_reference TEXT NOT NULL DEFAULT '',
              gap_description TEXT NOT NULL DEFAULT '',
              suggested_action TEXT NOT NULL DEFAULT '',
              gap_direction TEXT NOT NULL DEFAULT '',
              sort_order INT NOT NULL DEFAULT 0,
              created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
              updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
            );
            """,
            ct);
        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE INDEX IF NOT EXISTS idx_demo_analysis_template_points_template
              ON demo_analysis_template_points (template_id, sort_order);
            """,
            ct);
    }

    public async Task<DemoClearResult> ClearDemoWorkspaceAsync(DemoClearRequest req, CancellationToken ct = default)
    {
        // An empty directory (auth admin API unavailable) must not abort the clear: demo runs are
        // also identifiable by their "[Demo]" name / simulated-run description markers.
        var demoIds = await demoDirectory.GetDemoProfileIdsAsync(ct);

        var clearInternal = req.ClearAll || req.ClearInternalDocuments;
        var clearRegs = req.ClearAll || req.ClearRegulationDocuments;
        var clearLibs = req.ClearAll || req.ClearLibraries;
        var clearRuns = req.ClearAll || req.ClearAnalysisRuns;
        var clearUsers = req.ClearAll || req.ClearUsers;
        var anyClear = clearInternal || clearRegs || clearLibs || clearRuns || clearUsers;

        var internalCount = 0;
        var regCount = 0;
        var libCount = 0;
        var runCount = 0;
        var deletedRunCount = 0;
        var deletedInternalCount = 0;
        var deletedRegCount = 0;
        var userCount = 0;
        var purgedRunIds = new HashSet<Guid>();

        if (clearRuns)
        {
            var runs = await ListDemoOwnedAnalysisRunsAsync(demoIds, ct);
            foreach (var run in runs)
            {
                if (await PermanentlyDeleteAnalysisRunAsync(run.Id, ct))
                    purgedRunIds.Add(run.Id);
            }
            runCount = purgedRunIds.Count;
        }
        else if (anyClear)
        {
            var deletedRuns = (await ListDemoOwnedAnalysisRunsAsync(demoIds, ct))
                .Where(r => r.Status == DeletedStatus)
                .ToList();
            foreach (var run in deletedRuns)
            {
                if (await PermanentlyDeleteAnalysisRunAsync(run.Id, ct))
                {
                    purgedRunIds.Add(run.Id);
                    deletedRunCount++;
                }
            }
        }

        if (clearLibs)
        {
            var libs = await db.NdLibraries
                .Where(l => l.CreatedBy != null && demoIds.Contains(l.CreatedBy.Value))
                .ToListAsync(ct);
            var libIds = libs.Select(l => l.Id).ToList();
            if (libIds.Count > 0)
            {
                var pts = await db.NdLibraryPoints.Where(p => libIds.Contains(p.LibraryId)).ToListAsync(ct);
                db.NdLibraryPoints.RemoveRange(pts);
                db.NdLibraries.RemoveRange(libs);
            }
            libCount = libs.Count;
        }

        if (clearRegs)
        {
            deletedRegCount = await PermanentlyDeleteDemoSoftDeletedRegulationDocsAsync(demoIds, ct);

            var regs = await db.NdRegulationDocuments
                .Where(d => d.CreatedBy != null && demoIds.Contains(d.CreatedBy.Value) && d.Status != RegulationStatusHidden)
                .ToListAsync(ct);
            foreach (var reg in regs)
            {
                reg.Status = RegulationStatusHidden;
                reg.UpdatedAt = DateTimeOffset.UtcNow;
                if (reg.StoredDocumentId is Guid storedId)
                {
                    var stored = await db.StoredDocuments.FirstOrDefaultAsync(d => d.Id == storedId, ct);
                    if (stored != null)
                    {
                        stored.IsHidden = true;
                        stored.HiddenAt = DateTimeOffset.UtcNow;
                        stored.UpdatedAt = DateTimeOffset.UtcNow;
                    }
                }
            }
            regCount = regs.Count;

            // Purge what we just hid as well, otherwise the deleted bin still shows the cleared docs.
            await db.SaveChangesAsync(ct);
            await PermanentlyDeleteDemoSoftDeletedRegulationDocsAsync(demoIds, ct);
        }
        else if (anyClear)
        {
            deletedRegCount = await PermanentlyDeleteDemoSoftDeletedRegulationDocsAsync(demoIds, ct);
        }

        if (clearInternal)
        {
            deletedInternalCount = await PermanentlyDeleteDemoSoftDeletedInternalDocsAsync(demoIds, ct);

            var docs = await db.StoredDocuments
                .Where(d =>
                    d.UploadedBy != null
                    && demoIds.Contains(d.UploadedBy.Value)
                    && !d.IsHidden
                    && (d.DocKind == "document" || d.DocKind == "internal"))
                .ToListAsync(ct);
            foreach (var doc in docs)
            {
                doc.IsHidden = true;
                doc.HiddenAt = DateTimeOffset.UtcNow;
                doc.UpdatedAt = DateTimeOffset.UtcNow;
            }
            internalCount = docs.Count;

            // Purge what we just hid as well, otherwise the deleted bin still shows the cleared docs.
            await db.SaveChangesAsync(ct);
            await PermanentlyDeleteDemoSoftDeletedInternalDocsAsync(demoIds, ct);
        }
        else if (anyClear)
        {
            deletedInternalCount = await PermanentlyDeleteDemoSoftDeletedInternalDocsAsync(demoIds, ct);
        }

        if (clearUsers)
        {
            // Soft-deactivate demo profiles that are not super_admin (keep Demo Admin accounts usable).
            var profiles = await db.NdProfiles
                .Where(p => demoIds.Contains(p.Id) && p.IsActive && p.Role != "super_admin")
                .ToListAsync(ct);
            foreach (var p in profiles)
            {
                p.IsActive = false;
                p.UpdatedAt = DateTimeOffset.UtcNow;
            }
            userCount = profiles.Count;
        }

        await db.SaveChangesAsync(ct);
        cache.Remove("nd:demo-profile-ids");
        dashboardCache.Invalidate();
        return new DemoClearResult(
            internalCount,
            regCount,
            libCount,
            runCount,
            userCount,
            deletedRunCount,
            deletedInternalCount,
            deletedRegCount);
    }

    private async Task<int> PermanentlyDeleteDemoSoftDeletedInternalDocsAsync(
        HashSet<Guid> demoIds,
        CancellationToken ct)
    {
        var hidden = await db.StoredDocuments
            .Where(d =>
                d.UploadedBy != null
                && demoIds.Contains(d.UploadedBy.Value)
                && d.IsHidden
                && (d.DocKind == "document" || d.DocKind == "internal"))
            .ToListAsync(ct);

        var purged = 0;
        foreach (var doc in hidden)
        {
            if (await PermanentlyDeleteStoredDocumentAsync(doc.Id, ct))
                purged++;
        }
        return purged;
    }

    private async Task<int> PermanentlyDeleteDemoSoftDeletedRegulationDocsAsync(
        HashSet<Guid> demoIds,
        CancellationToken ct)
    {
        var purged = 0;
        var hiddenNd = await db.NdRegulationDocuments
            .Where(d => d.CreatedBy != null && demoIds.Contains(d.CreatedBy.Value) && d.Status == RegulationStatusHidden)
            .ToListAsync(ct);
        foreach (var nd in hiddenNd)
        {
            if (await PermanentlyDeleteRegulationDocumentAsync(nd.Id, ct))
                purged++;
        }

        var hiddenStored = await db.StoredDocuments
            .Where(d =>
                d.UploadedBy != null
                && demoIds.Contains(d.UploadedBy.Value)
                && d.IsHidden
                && d.DocKind == "regulation")
            .ToListAsync(ct);
        foreach (var stored in hiddenStored)
        {
            if (await PermanentlyDeleteRegulationStoredDocumentAsync(stored.Id, ct))
                purged++;
        }

        return purged;
    }

    private async Task<bool> PermanentlyDeleteStoredDocumentAsync(Guid storedDocumentId, CancellationToken ct)
    {
        var doc = await db.StoredDocuments.FirstOrDefaultAsync(d => d.Id == storedDocumentId, ct);
        if (doc == null) return false;

        db.StoredDocuments.Remove(doc);
        await db.SaveChangesAsync(ct);
        return true;
    }

    private async Task<bool> PermanentlyDeleteRegulationDocumentAsync(Guid regulationDocumentId, CancellationToken ct)
    {
        var nd = await db.NdRegulationDocuments.FirstOrDefaultAsync(d => d.Id == regulationDocumentId, ct);
        if (nd == null) return false;

        var points = await db.NdRegulationPoints
            .IgnoreQueryFilters()
            .Where(p => p.RegulationDocumentId == nd.Id)
            .ToListAsync(ct);
        if (points.Count > 0)
            db.NdRegulationPoints.RemoveRange(points);

        var libPoints = await db.NdLibraryPoints
            .Where(p => p.RegulationDocumentId == nd.Id)
            .ToListAsync(ct);
        if (libPoints.Count > 0)
            db.NdLibraryPoints.RemoveRange(libPoints);

        var storedId = nd.StoredDocumentId;
        db.NdRegulationDocuments.Remove(nd);
        await db.SaveChangesAsync(ct);

        if (storedId is Guid sid)
            await PermanentlyDeleteStoredDocumentIfOrphanedAsync(sid, ct);

        return true;
    }

    private async Task<bool> PermanentlyDeleteRegulationStoredDocumentAsync(Guid storedDocumentId, CancellationToken ct)
    {
        var overlays = await db.NdRegulationDocuments
            .Where(d => d.StoredDocumentId == storedDocumentId)
            .ToListAsync(ct);
        foreach (var overlay in overlays)
        {
            if (!await PermanentlyDeleteRegulationDocumentAsync(overlay.Id, ct))
                return false;
        }

        return await PermanentlyDeleteStoredDocumentAsync(storedDocumentId, ct);
    }

    private async Task PermanentlyDeleteStoredDocumentIfOrphanedAsync(Guid storedDocumentId, CancellationToken ct)
    {
        var hasOverlay = await db.NdRegulationDocuments
            .AsNoTracking()
            .AnyAsync(d => d.StoredDocumentId == storedDocumentId, ct);
        if (!hasOverlay)
            await PermanentlyDeleteStoredDocumentAsync(storedDocumentId, ct);
    }

    private sealed record DemoOwnedRun(Guid Id, string Status);

    private static bool IsDemoOwnedAnalysisRun(NdAnalysisRun run, HashSet<Guid> demoIds) =>
        run.CreatedBy is Guid creatorId && demoIds.Contains(creatorId)
        || NdDemoDataFilters.IsDemoMarkedAnalysisRun(run);

    /// <summary>
    /// Ownership only needs id, status, creator and the name/description markers, so the large
    /// JSONB snapshot columns are left in the database rather than pulled across the wire.
    /// </summary>
    private async Task<List<DemoOwnedRun>> ListDemoOwnedAnalysisRunsAsync(
        HashSet<Guid> demoIds,
        CancellationToken ct)
    {
        var runs = await db.NdAnalysisRuns
            .AsNoTracking()
            .Select(r => new
            {
                r.Id,
                r.Status,
                r.Name,
                r.Description,
                r.CreatedBy,
            })
            .ToListAsync(ct);

        return runs
            .Where(r => IsDemoOwnedAnalysisRun(
                new NdAnalysisRun
                {
                    Name = r.Name,
                    Description = r.Description,
                    CreatedBy = r.CreatedBy,
                },
                demoIds))
            .Select(r => new DemoOwnedRun(r.Id, r.Status))
            .ToList();
    }

    /// <summary>Permanently removes a demo analysis run and dependent rows (including soft-deleted).</summary>
    public async Task<bool> PermanentlyDeleteAnalysisRunAsync(Guid runId, CancellationToken ct = default)
    {
        var run = await db.NdAnalysisRuns.FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run == null) return false;

        var pointIds = await db.NdAnalysisPoints
            .AsNoTracking()
            .Where(p => p.AnalysisRunId == runId)
            .Select(p => p.Id)
            .ToListAsync(ct);

        if (pointIds.Count > 0)
        {
            var attachments = await db.NdAnalysisPointAttachments
                .Where(a => pointIds.Contains(a.AnalysisPointId))
                .ToListAsync(ct);

            var storedIds = attachments.Select(a => a.StoredDocumentId).Distinct().ToList();
            if (attachments.Count > 0) db.NdAnalysisPointAttachments.RemoveRange(attachments);

            await PurgeDemoGapEvidenceDocsAsync(runId, storedIds, pointIds, ct);

            var histories = await db.NdActionPlanHistories
                .Where(h => pointIds.Contains(h.AnalysisPointId))
                .ToListAsync(ct);
            if (histories.Count > 0) db.NdActionPlanHistories.RemoveRange(histories);

            var comments = await db.NdAnalysisPointComments
                .Where(c => pointIds.Contains(c.AnalysisPointId))
                .ToListAsync(ct);
            if (comments.Count > 0) db.NdAnalysisPointComments.RemoveRange(comments);

            var itemReviews = await db.NdActionPlanItemReviews
                .Where(r => pointIds.Contains(r.AnalysisPointId))
                .ToListAsync(ct);
            if (itemReviews.Count > 0) db.NdActionPlanItemReviews.RemoveRange(itemReviews);

            var tempComments = await db.NdTempPointReviewComments
                .Where(c => pointIds.Contains(c.AnalysisPointId))
                .ToListAsync(ct);
            if (tempComments.Count > 0) db.NdTempPointReviewComments.RemoveRange(tempComments);

            var points = await db.NdAnalysisPoints
                .Where(p => p.AnalysisRunId == runId)
                .ToListAsync(ct);
            if (points.Count > 0) db.NdAnalysisPoints.RemoveRange(points);
        }

        var statusHistories = await db.NdAnalysisStatusHistories
            .Where(h => h.AnalysisRunId == runId)
            .ToListAsync(ct);
        if (statusHistories.Count > 0) db.NdAnalysisStatusHistories.RemoveRange(statusHistories);

        var reviews = await db.NdAnalysisReviews
            .Where(r => r.AnalysisRunId == runId)
            .ToListAsync(ct);
        if (reviews.Count > 0) db.NdAnalysisReviews.RemoveRange(reviews);

        var forwardFindings = await db.NdRegulForwardFindings
            .Where(f => f.AnalysisRunId == runId)
            .ToListAsync(ct);
        if (forwardFindings.Count > 0) db.NdRegulForwardFindings.RemoveRange(forwardFindings);

        db.NdAnalysisRuns.Remove(run);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Deletes gap-analysis files uploaded by demo users for this run. Real-user documents
    /// (including files a production admin attached while reviewing a demo run) are left in storage.
    /// </summary>
    private async Task PurgeDemoGapEvidenceDocsAsync(
        Guid runId,
        List<Guid> storedIds,
        List<Guid> runPointIds,
        CancellationToken ct)
    {
        if (storedIds.Count == 0) return;

        var demoIds = await demoDirectory.GetDemoProfileIdsAsync(ct);
        var runPrefix = $"documents/nd/gap-evidence/{runId:N}/";

        var otherRefs = await db.NdAnalysisPointAttachments.AsNoTracking()
            .Where(a => storedIds.Contains(a.StoredDocumentId) && !runPointIds.Contains(a.AnalysisPointId))
            .Select(a => a.StoredDocumentId)
            .Distinct()
            .ToListAsync(ct);
        var keptElsewhere = otherRefs.ToHashSet();

        var docs = await db.StoredDocuments
            .Where(d => storedIds.Contains(d.Id) && d.DocKind == "gap_evidence")
            .ToListAsync(ct);

        foreach (var doc in docs)
        {
            if (keptElsewhere.Contains(doc.Id)) continue;

            var uploadedByRealUser = doc.UploadedBy is Guid uploader && !demoIds.Contains(uploader);
            if (uploadedByRealUser) continue;

            var uploadedByDemo = doc.UploadedBy is Guid demoUploader && demoIds.Contains(demoUploader);
            var pathIsThisRun = !string.IsNullOrWhiteSpace(doc.StoragePath)
                && doc.StoragePath.StartsWith(runPrefix, StringComparison.OrdinalIgnoreCase);
            if (!uploadedByDemo && !(doc.UploadedBy == null && pathIsThisRun)) continue;

            try
            {
                if (storage.IsConfigured)
                {
                    if (!string.IsNullOrWhiteSpace(doc.StoragePath))
                        await storage.DeleteAsync(doc.StoragePath, ct);
                    if (!string.IsNullOrWhiteSpace(doc.SourceStoragePath))
                        await storage.DeleteAsync(doc.SourceStoragePath, ct);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not delete demo gap-evidence file {Path} for run {RunId}", doc.StoragePath, runId);
            }

            db.StoredDocuments.Remove(doc);
        }
    }

    public async Task<List<DemoAnalysisSeedService.DemoRegulJudgmentRow>> LoadJudgmentsForRunAsync(
        NdAnalysisRun run,
        CancellationToken ct = default)
    {
        await EnsureTemplatesSeededAsync(ct);
        var template = await ResolveTemplateForRunAsync(run, ct);
        if (template != null && template.Points.Count > 0)
            return template.Points.OrderBy(p => p.SortOrder).Select(MapPointToRow).ToList();

        // Fallback: active analys1demo or seed file.
        var analys1 = await db.NdDemoAnalysisTemplates
            .AsNoTracking()
            .Include(t => t.Points)
            .FirstOrDefaultAsync(t => t.Code == Analys1Code && t.IsActive, ct);
        if (analys1 is { Points.Count: > 0 })
            return analys1.Points.OrderBy(p => p.SortOrder).Select(MapPointToRow).ToList();

        return LoadSeedJudgmentsFromFile();
    }

    /// <summary>
    /// Clauses the CBUAE demo template currently defines. This is the single source of truth
    /// for how many regulation points a demo CBUAE document exposes — library, analysis and
    /// exports all derive their count from here, so editing points in Admin → Demo changes
    /// the number everywhere.
    /// </summary>
    public async Task<List<DemoAnalysisSeedService.DemoRegulJudgmentRow>> LoadCbuaeTemplateClausesAsync(
        CancellationToken ct = default)
    {
        await EnsureTemplatesSeededAsync(ct);

        var analys1 = await db.NdDemoAnalysisTemplates
            .AsNoTracking()
            .Include(t => t.Points)
            .FirstOrDefaultAsync(t => t.Code == Analys1Code, ct);

        if (analys1 is { Points.Count: > 0 })
            return analys1.Points.OrderBy(p => p.SortOrder).Select(MapPointToRow).ToList();

        return LoadSeedJudgmentsFromFile();
    }

    public async Task<NdDemoAnalysisTemplate?> ResolveTemplateForRunAsync(
        NdAnalysisRun run,
        CancellationToken ct = default)
    {
        var templates = await db.NdDemoAnalysisTemplates
            .AsNoTracking()
            .Include(t => t.Points)
            .Where(t => t.IsActive)
            .OrderBy(t => t.SortOrder)
            .ToListAsync(ct);

        if (templates.Count == 0) return null;

        var regIds = ParseGuidList(run.SelectedRegulationDocIds);
        var intIds = ParseGuidList(run.SelectedInternalDocIds);
        var regNames = new List<string>();
        var intNames = new List<string>();

        foreach (var id in regIds)
        {
            var name = await db.NdRegulationDocuments.AsNoTracking()
                .Where(d => d.Id == id)
                .Select(d => d.Name)
                .FirstOrDefaultAsync(ct);
            if (!string.IsNullOrWhiteSpace(name)) regNames.Add(name);
        }

        foreach (var id in intIds)
        {
            var doc = await db.StoredDocuments.AsNoTracking()
                .Where(d => d.Id == id)
                .Select(d => new { d.OriginalFileName, d.Title })
                .FirstOrDefaultAsync(ct);
            if (doc == null) continue;
            if (!string.IsNullOrWhiteSpace(doc.OriginalFileName)) intNames.Add(doc.OriginalFileName);
            if (!string.IsNullOrWhiteSpace(doc.Title)) intNames.Add(doc.Title!);
        }

        foreach (var t in templates)
        {
            if (string.IsNullOrWhiteSpace(t.RegulationNameHint) || string.IsNullOrWhiteSpace(t.InternalNameHint))
                continue;
            var regOk = regNames.Any(n => NameMatches(n, t.RegulationNameHint));
            var intOk = intNames.Any(n => NameMatches(n, t.InternalNameHint));
            if (regOk && intOk) return t;
        }

        return null;
    }

    private List<DemoAnalysisSeedService.DemoRegulJudgmentRow> LoadSeedJudgmentsFromFile()
    {
        var path = Path.Combine(env.ContentRootPath, "SeedData", DemoAnalysisSeedService.CbuaeAmlSeedFileName);
        if (!File.Exists(path)) return [];
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<List<DemoAnalysisSeedService.DemoRegulJudgmentRow>>(
                   json,
                   new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
               ?? [];
    }

    private static NdDemoAnalysisTemplatePoint MapRowToPoint(
        Guid templateId,
        DemoAnalysisSeedService.DemoRegulJudgmentRow row,
        int sortOrder)
    {
        var gap = NdRegulJudgmentFormatter.ResolveGapDescriptionForSeedRow(
            row.GapDescription,
            row.Interpretation,
            row.OverallStatus,
            row.DesignStatus);

        return new NdDemoAnalysisTemplatePoint
        {
            TemplateId = templateId,
            ClauseNo = row.ClauseNo ?? "",
            ClauseTitle = row.ClauseTitle,
            DesignStatus = row.DesignStatus ?? "partial",
            OperatingStatus = row.OperatingStatus ?? "partial",
            OverallStatus = row.OverallStatus ?? "partial",
            Confidence = row.Confidence,
            Interpretation = row.Interpretation ?? "",
            PolicyExtractJson = JsonSerializer.Serialize(row.PolicyExtract ?? []),
            DocumentReference = row.DocumentReference ?? "",
            GapDescription = gap,
            SuggestedAction = row.SuggestedAction ?? "",
            GapDirection = row.GapDirection ?? "",
            SortOrder = sortOrder,
        };
    }

    private async Task SyncAnalys1PointFieldsFromSeedAsync(
        NdDemoAnalysisTemplate template,
        List<DemoAnalysisSeedService.DemoRegulJudgmentRow> seedRows,
        CancellationToken ct)
    {
        var seedByClause = new Dictionary<string, DemoAnalysisSeedService.DemoRegulJudgmentRow>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var row in seedRows)
        {
            if (string.IsNullOrWhiteSpace(row.ClauseNo)) continue;
            seedByClause[row.ClauseNo.Trim()] = row;
        }

        var tracked = await db.NdDemoAnalysisTemplates
            .Include(t => t.Points)
            .FirstAsync(t => t.Id == template.Id, ct);

        // Backfill only. Assigning unconditionally rewrote all points on every call (slow against
        // a remote database) and silently reverted edits made in Admin → Demo.
        var changed = false;
        foreach (var point in tracked.Points)
        {
            var row = seedByClause.GetValueOrDefault(point.ClauseNo.Trim());
            if (row == null) continue;

            var gap = NdRegulJudgmentFormatter.ResolveGapDescriptionForSeedRow(
                row.GapDescription,
                row.Interpretation,
                row.OverallStatus,
                row.DesignStatus);
            if (!string.IsNullOrWhiteSpace(gap)
                && string.IsNullOrWhiteSpace(point.GapDescription))
            {
                point.GapDescription = gap;
                changed = true;
            }

            if (row.Confidence > 0 && point.Confidence <= 0)
            {
                point.Confidence = row.Confidence;
                changed = true;
            }
        }

        if (changed)
        {
            tracked.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }
    }

    public static DemoAnalysisSeedService.DemoRegulJudgmentRow MapPointToRow(NdDemoAnalysisTemplatePoint p)
    {
        List<string> extracts = [];
        try
        {
            extracts = JsonSerializer.Deserialize<List<string>>(p.PolicyExtractJson ?? "[]") ?? [];
        }
        catch
        {
            // ignore bad json
        }

        return new DemoAnalysisSeedService.DemoRegulJudgmentRow
        {
            ClauseNo = p.ClauseNo,
            ClauseTitle = p.ClauseTitle,
            DesignStatus = p.DesignStatus,
            OperatingStatus = p.OperatingStatus,
            OverallStatus = p.OverallStatus,
            Confidence = p.Confidence,
            Interpretation = p.Interpretation,
            PolicyExtract = extracts,
            DocumentReference = p.DocumentReference,
            GapDescription = p.GapDescription,
            SuggestedAction = p.SuggestedAction,
            GapDirection = p.GapDirection,
        };
    }

    private static bool NameMatches(string? name, string hint) =>
        !string.IsNullOrWhiteSpace(name)
        && !string.IsNullOrWhiteSpace(hint)
        && name.Contains(hint.Trim(), StringComparison.OrdinalIgnoreCase);

    private static List<Guid> ParseGuidList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            var ids = JsonSerializer.Deserialize<List<string>>(json) ?? [];
            return ids.Select(s => Guid.TryParse(s, out var g) ? g : (Guid?)null)
                .Where(g => g.HasValue)
                .Select(g => g!.Value)
                .ToList();
        }
        catch
        {
            return [];
        }
    }
}
