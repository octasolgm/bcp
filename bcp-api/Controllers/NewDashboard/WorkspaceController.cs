using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Infrastructure.NewDashboard;
using Reguliq.Api.Services.NewDashboard;

namespace Reguliq.Api.Controllers.NewDashboard;

/// <summary>Single-request workspace counters for sidebar (avoids 8+ parallel DB calls).</summary>
[ApiController]
[Route("nd/workspace")]
public class WorkspaceController(
    AppDbContext db,
    SupabaseJwtValidator jwt,
    NdDashboardCacheService dashboardCache) : NdControllerBase
{
    private const string DeletedStatus = "deleted";

    [HttpGet("nav-counts")]
    public async Task<IActionResult> NavCounts(CancellationToken ct)
    {
        var (profile, error) = await RequireAuthAsync(db, jwt, ct,
            "super_admin", "maker", "checker", "reviewer");
        if (error != null) return error;

        var role = profile!.Role;
        var cacheScope = $"nav-counts:{profile.Id}:{role}";

        var data = await dashboardCache.GetOrCreateAsync(cacheScope, async innerCt =>
        {
            var mineOnly = role == "maker";

            IQueryable<Data.NewDashboard.Entities.NdAnalysisRun> runs = db.NdAnalysisRuns.AsNoTracking()
                .Where(r => r.Status != DeletedStatus);
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
            int checkerQueue = 0;
            int reviewerQueue = 0;

            if (role is "maker" or "super_admin")
            {
                internalDocuments = await db.StoredDocuments.AsNoTracking()
                    .CountAsync(
                        d => (d.DocKind == "document" || d.DocKind == "internal") && !d.IsHidden,
                        innerCt);
                regulationDocuments = await db.NdRegulationDocuments.AsNoTracking().CountAsync(innerCt);
                libraries = await db.NdLibraries.AsNoTracking().CountAsync(innerCt);
            }

            if (role == "super_admin")
            {
                var adminCounts = await (
                    from d in db.StoredDocuments.AsNoTracking()
                    group d by 1 into g
                    select new
                    {
                        InternalDeleted = g.Count(x =>
                            (x.DocKind == "document" || x.DocKind == "internal") && x.IsHidden),
                        RegulationDeleted = g.Count(x => x.DocKind == "regulation" && x.IsHidden),
                    }).FirstOrDefaultAsync(innerCt);

                internalDocumentsDeleted = adminCounts?.InternalDeleted ?? 0;
                regulationDocumentsDeleted = adminCounts?.RegulationDeleted ?? 0;

                adminUsers = await db.NdProfiles.AsNoTracking().CountAsync(innerCt);
                adminDepartments = await db.NdDepartments.AsNoTracking().CountAsync(innerCt);

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
            };
        }, ct);

        return Ok(new { success = true, data });
    }
}
