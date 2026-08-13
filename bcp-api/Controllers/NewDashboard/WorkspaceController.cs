using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Infrastructure.NewDashboard;
using Reguliq.Api.Services.NewDashboard;
using Reguliq.Api.Services.NewDashboard.Demo;

namespace Reguliq.Api.Controllers.NewDashboard;

/// <summary>Single-request workspace counters for sidebar (avoids 8+ parallel DB calls).</summary>
[ApiController]
[Route("nd/workspace")]
public class WorkspaceController(
    AppDbContext db,
    SupabaseJwtValidator jwt,
    NdDashboardCacheService dashboardCache,
    NdDemoUserDirectory demoDirectory) : NdControllerBase
{
    private const string DeletedStatus = "deleted";

    [HttpGet("nav-counts")]
    public async Task<IActionResult> NavCounts(CancellationToken ct)
    {
        var (profile, user, error) = await RequireAuthWithUserAsync(db, jwt, ct,
            "super_admin", "maker", "checker", "reviewer");
        if (error != null) return error;

        var role = profile!.Role;
        var demoCtx = await NdDemoIsolationContext.ResolveAsync(demoDirectory, user, ct);
        var cacheScope = $"nav-counts:{profile.Id}:{role}:demo={demoCtx.ViewerIsDemo}";

        var data = await dashboardCache.GetOrCreateAsync(cacheScope, async innerCt =>
        {
            var mineOnly = role == "maker";

            IQueryable<Data.NewDashboard.Entities.NdAnalysisRun> runs = NdDemoDataFilters.ApplyToAnalysisRuns(
                db.NdAnalysisRuns.AsNoTracking().Where(r => r.Status != DeletedStatus),
                demoCtx);
            if (mineOnly)
                runs = runs.Where(r => r.CreatedBy == profile.Id);

            var runCounts = await runs
                .GroupBy(_ => 1)
                .Select(g => new
                {
                    All = g.Count(),
                    Correction = g.Count(r => r.Status == "pulled_back"),
                    Checker = g.Count(r => r.Status == "submitted_for_review"),
                    Reviewer = g.Count(r => r.Status == "checker_approved"),
                })
                .FirstOrDefaultAsync(innerCt);

            var analysisRunsAll = runCounts?.All ?? 0;
            var analysisRunsCorrection = runCounts?.Correction ?? 0;

            var inProgressCandidates = await runs
                .Where(r =>
                    r.Status == "running"
                    || r.Status == "processing"
                    || (r.TotalPointsCount > 0 && r.ProcessedPointsCount < r.TotalPointsCount))
                .Select(r => new
                {
                    r.Status,
                    r.TotalPointsCount,
                    r.ProcessedPointsCount,
                    r.DualVerifyFailedCount,
                    r.UpdatedAt,
                })
                .ToListAsync(innerCt);

            var analysisRunsInProgress = inProgressCandidates.Count(r => NdRunActivityHelper.IsProcessingRun(
                r.Status,
                r.TotalPointsCount,
                r.ProcessedPointsCount,
                r.DualVerifyFailedCount,
                r.UpdatedAt));

            int internalDocuments = 0;
            int regulationDocuments = 0;
            int libraries = 0;
            int internalDocumentsDeleted = 0;
            int regulationDocumentsDeleted = 0;
            int adminUsers = 0;
            int adminDepartments = 0;
            var checkerQueue = 0;
            int reviewerQueue = 0;
            int deletedAnalysisRuns = 0;

            if (role is "maker" or "super_admin")
            {
                internalDocuments = await NdDemoDataFilters.ApplyToStoredDocuments(
                        db.StoredDocuments.AsNoTracking()
                            .Where(d => (d.DocKind == "document" || d.DocKind == "internal") && !d.IsHidden),
                        demoCtx)
                    .CountAsync(innerCt);
                regulationDocuments = await NdDemoDataFilters.ApplyToRegulationDocuments(
                        db.NdRegulationDocuments.AsNoTracking()
                            .Where(d =>
                                d.Status != -1
                                && (d.IsManual
                                    || !d.StoredDocumentId.HasValue
                                    || !string.IsNullOrWhiteSpace(d.FilePath))),
                        demoCtx)
                    .CountAsync(innerCt);
                libraries = await NdDemoDataFilters.ApplyToLibraries(
                        db.NdLibraries.AsNoTracking(),
                        demoCtx)
                    .CountAsync(innerCt);
            }

            if (role == "super_admin")
            {
                var adminCounts = await (
                    from d in NdDemoDataFilters.ApplyToStoredDocuments(db.StoredDocuments.AsNoTracking(), demoCtx)
                    group d by 1 into g
                    select new
                    {
                        InternalDeleted = g.Count(x =>
                            (x.DocKind == "document" || x.DocKind == "internal") && x.IsHidden),
                        RegulationDeleted = g.Count(x => x.DocKind == "regulation" && x.IsHidden),
                    }).FirstOrDefaultAsync(innerCt);

                internalDocumentsDeleted = adminCounts?.InternalDeleted ?? 0;
                regulationDocumentsDeleted = adminCounts?.RegulationDeleted ?? 0;

                var profiles = await db.NdProfiles.AsNoTracking().ToListAsync(innerCt);
                if (demoCtx.Enabled)
                {
                    var authEmails = await demoDirectory.GetDemoProfileIdsAsync(innerCt);
                    adminUsers = profiles.Count(p =>
                        demoCtx.ViewerIsDemo
                            ? authEmails.Contains(p.Id)
                            : !authEmails.Contains(p.Id));
                }
                else
                    adminUsers = profiles.Count;
                adminDepartments = await NdDemoDataFilters.ApplyToDepartments(
                        db.NdDepartments.AsNoTracking(), demoCtx)
                    .CountAsync(innerCt);

                deletedAnalysisRuns = await NdDemoDataFilters.ApplyToAnalysisRuns(
                        db.NdAnalysisRuns.AsNoTracking().Where(r => r.Status == DeletedStatus),
                        demoCtx)
                    .CountAsync(innerCt);

                checkerQueue = runCounts?.Checker ?? 0;
                reviewerQueue = runCounts?.Reviewer ?? 0;
            }
            else if (role == "checker")
            {
                checkerQueue = runCounts?.Checker ?? 0;
            }
            else if (role == "reviewer")
            {
                checkerQueue = runCounts?.Checker ?? 0;
                reviewerQueue = runCounts?.Reviewer ?? 0;
            }

            return new
            {
                analysisRunsAll,
                analysisRunsCorrection,
                analysisRunsInProgress,
                internalDocuments,
                regulationDocuments,
                libraries,
                internalDocumentsDeleted,
                regulationDocumentsDeleted,
                adminUsers,
                adminDepartments,
                checkerQueue,
                reviewerQueue,
                deletedAnalysisRuns,
            };
        }, ct);

        return Ok(new { success = true, data });
    }

    [HttpGet("dashboard-stats")]
    public async Task<IActionResult> DashboardStats(
        [FromQuery] bool mineOnly = false,
        CancellationToken ct = default)
    {
        var (profile, user, error) = await RequireAuthWithUserAsync(db, jwt, ct,
            "super_admin", "maker", "checker", "reviewer");
        if (error != null) return error;

        var role = profile!.Role;
        var demoCtx = await NdDemoIsolationContext.ResolveAsync(demoDirectory, user, ct);
        var effectiveMineOnly = mineOnly || role == "maker";
        var cacheScope = $"dashboard-stats:{profile.Id}:{role}:{effectiveMineOnly}:demo={demoCtx.ViewerIsDemo}";

        var stats = await dashboardCache.GetOrCreateAsync(cacheScope, async innerCt =>
            await NdRunEnrichmentHelper.LoadWorkspaceDashboardStatsAsync(
                db, effectiveMineOnly, profile.Id, innerCt, demoCtx), ct);

        return Ok(new
        {
            success = true,
            data = new
            {
                compliant = stats.Compliant,
                partial = stats.Partial,
                nonCompliant = stats.NonCompliant,
                criticalGaps = stats.CriticalGaps,
                mediumGaps = stats.MediumGaps,
                lowGaps = stats.LowGaps,
                totalRuns = stats.TotalRuns,
                lastAnalysisAt = stats.LastAnalysisAt,
            },
        });
    }
}
