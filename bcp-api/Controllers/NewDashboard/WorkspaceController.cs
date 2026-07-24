using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Infrastructure.NewDashboard;
using Reguliq.Api.Services.NewDashboard;

namespace Reguliq.Api.Controllers.NewDashboard;

/// <summary>Single-request workspace counters for sidebar (avoids 8+ parallel DB calls).</summary>
[ApiController]
[Route("nd/workspace")]
public class WorkspaceController(AppDbContext db, SupabaseJwtValidator jwt) : NdControllerBase
{
    private const string DeletedStatus = "deleted";

    [HttpGet("nav-counts")]
    public async Task<IActionResult> NavCounts(CancellationToken ct)
    {
        var (profile, error) = await RequireAuthAsync(db, jwt, ct,
            "super_admin", "maker", "checker", "reviewer");
        if (error != null) return error;

        var role = profile!.Role;
        var mineOnly = role == "maker";

        IQueryable<Data.NewDashboard.Entities.NdAnalysisRun> runs = db.NdAnalysisRuns.AsNoTracking()
            .Where(r => r.Status != DeletedStatus);
        if (mineOnly)
            runs = runs.Where(r => r.CreatedBy == profile.Id);

        var runRows = await runs
            .Select(r => new
            {
                r.Status,
                r.TotalPointsCount,
                r.ProcessedPointsCount,
                r.DualVerifyFailedCount,
                r.UpdatedAt,
            })
            .ToListAsync(ct);

        var analysisRunsAll = runRows.Count;
        var analysisRunsCorrection = runRows.Count(r =>
            string.Equals(r.Status, "pulled_back", StringComparison.OrdinalIgnoreCase));
        var analysisRunsInProgress = runRows.Count(r => NdRunActivityHelper.IsProcessingRun(
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
                    ct);
            regulationDocuments = await db.NdRegulationDocuments.AsNoTracking().CountAsync(ct);
            libraries = await db.NdLibraries.AsNoTracking().CountAsync(ct);
        }

        if (role == "super_admin")
        {
            internalDocumentsDeleted = await db.StoredDocuments.AsNoTracking()
                .CountAsync(
                    d => (d.DocKind == "document" || d.DocKind == "internal") && d.IsHidden,
                    ct);
            regulationDocumentsDeleted = await db.StoredDocuments.AsNoTracking()
                .CountAsync(d => d.DocKind == "regulation" && d.IsHidden, ct);
            adminUsers = await db.NdProfiles.AsNoTracking().CountAsync(ct);
            adminDepartments = await db.NdDepartments.AsNoTracking().CountAsync(ct);
            checkerQueue = await db.NdAnalysisRuns.AsNoTracking()
                .CountAsync(r => r.Status == "submitted_for_review", ct);
            reviewerQueue = await db.NdAnalysisRuns.AsNoTracking()
                .CountAsync(r => r.Status == "checker_approved", ct);
        }
        else if (role == "checker")
        {
            checkerQueue = await db.NdAnalysisRuns.AsNoTracking()
                .CountAsync(r => r.Status == "submitted_for_review", ct);
        }
        else if (role == "reviewer")
        {
            checkerQueue = await db.NdAnalysisRuns.AsNoTracking()
                .CountAsync(r => r.Status == "submitted_for_review", ct);
            reviewerQueue = await db.NdAnalysisRuns.AsNoTracking()
                .CountAsync(r => r.Status == "checker_approved", ct);
        }

        return Ok(new
        {
            success = true,
            data = new
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
            },
        });
    }
}
