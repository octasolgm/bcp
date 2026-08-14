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
    NdDemoUserDirectory demoDirectory,
    IServiceScopeFactory scopeFactory) : NdControllerBase
{
    private const string DeletedStatus = "deleted";

    /// <summary>
    /// Runs a counter batch on its own DbContext so the independent badge queries can overlap.
    /// A DbContext is not thread-safe, hence the dedicated scope per batch; the payoff is that
    /// this endpoint costs about one database round trip instead of one per badge, which matters
    /// because the database is remote.
    /// </summary>
    private async Task<T> InParallelScopeAsync<T>(
        Func<AppDbContext, CancellationToken, Task<T>> work,
        CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var scopedDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await work(scopedDb, ct);
    }

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

            // The database is remote (~200ms per round trip), so the independent counters are
            // batched and overlapped rather than issued one badge at a time.
            IQueryable<Data.NewDashboard.Entities.NdAnalysisRun> RunsFor(AppDbContext ctx)
            {
                var q = NdDemoDataFilters.ApplyToAnalysisRuns(ctx.NdAnalysisRuns.AsNoTracking(), demoCtx);
                return mineOnly ? q.Where(r => r.CreatedBy == profile.Id) : q;
            }

            var runCountsTask = RunsFor(db)
                .GroupBy(_ => 1)
                .Select(g => new
                {
                    All = g.Count(r => r.Status != DeletedStatus),
                    Correction = g.Count(r => r.Status == "pulled_back"),
                    Checker = g.Count(r => r.Status == "submitted_for_review"),
                    Reviewer = g.Count(r => r.Status == "checker_approved"),
                    Deleted = g.Count(r => r.Status == DeletedStatus),
                })
                .FirstOrDefaultAsync(innerCt);

            var inProgressTask = InParallelScopeAsync(async (sdb, sct) =>
                await RunsFor(sdb)
                    .Where(r =>
                        r.Status != DeletedStatus
                        && (r.Status == "running"
                            || r.Status == "processing"
                            || (r.TotalPointsCount > 0 && r.ProcessedPointsCount < r.TotalPointsCount)))
                    .Select(r => new
                    {
                        r.Status,
                        r.TotalPointsCount,
                        r.ProcessedPointsCount,
                        r.DualVerifyFailedCount,
                        r.UpdatedAt,
                    })
                    .ToListAsync(sct), innerCt);

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
                var isSuperAdmin = role == "super_admin";

                // One grouped pass over stored documents covers the active and both deleted bins.
                var storedDocsTask = InParallelScopeAsync(async (sdb, sct) =>
                    await NdDemoDataFilters.ApplyToStoredDocuments(sdb.StoredDocuments.AsNoTracking(), demoCtx)
                        .GroupBy(_ => 1)
                        .Select(g => new
                        {
                            Internal = g.Count(d =>
                                (d.DocKind == "document" || d.DocKind == "internal") && !d.IsHidden),
                            InternalDeleted = g.Count(d =>
                                (d.DocKind == "document" || d.DocKind == "internal") && d.IsHidden),
                            RegulationDeleted = g.Count(d => d.DocKind == "regulation" && d.IsHidden),
                        })
                        .FirstOrDefaultAsync(sct), innerCt);

                var docsAndLibrariesTask = InParallelScopeAsync(async (sdb, sct) =>
                {
                    var regs = await NdDemoDataFilters.ApplyToRegulationDocuments(
                            sdb.NdRegulationDocuments.AsNoTracking()
                                .Where(d =>
                                    d.Status != -1
                                    && (d.IsManual
                                        || !d.StoredDocumentId.HasValue
                                        || !string.IsNullOrWhiteSpace(d.FilePath))),
                            demoCtx)
                        .CountAsync(sct);
                    var libs = await NdDemoDataFilters.ApplyToLibraries(
                            sdb.NdLibraries.AsNoTracking(), demoCtx)
                        .CountAsync(sct);
                    return (Regulations: regs, Libraries: libs);
                }, innerCt);

                var demoProfileIds = demoCtx.Enabled
                    ? await demoDirectory.GetDemoProfileIdsAsync(innerCt)
                    : null;

                var adminTask = isSuperAdmin
                    ? InParallelScopeAsync(async (sdb, sct) =>
                    {
                        var usersQuery = sdb.NdProfiles.AsNoTracking();
                        if (demoProfileIds != null)
                        {
                            usersQuery = demoCtx.ViewerIsDemo
                                ? usersQuery.Where(p => demoProfileIds.Contains(p.Id))
                                : usersQuery.Where(p => !demoProfileIds.Contains(p.Id));
                        }

                        var users = await usersQuery.CountAsync(sct);
                        var depts = await NdDemoDataFilters.ApplyToDepartments(
                                sdb.NdDepartments.AsNoTracking(), demoCtx)
                            .CountAsync(sct);
                        return (Users: users, Departments: depts);
                    }, innerCt)
                    : Task.FromResult((Users: 0, Departments: 0));

                await Task.WhenAll(storedDocsTask, docsAndLibrariesTask, adminTask);

                var storedCounts = await storedDocsTask;
                var (regulationCount, libraryCount) = await docsAndLibrariesTask;
                var (userCount, departmentCount) = await adminTask;

                internalDocuments = storedCounts?.Internal ?? 0;
                regulationDocuments = regulationCount;
                libraries = libraryCount;

                if (isSuperAdmin)
                {
                    internalDocumentsDeleted = storedCounts?.InternalDeleted ?? 0;
                    regulationDocumentsDeleted = storedCounts?.RegulationDeleted ?? 0;
                    adminUsers = userCount;
                    adminDepartments = departmentCount;
                }
            }

            // Inbox badge: pending actions owned by this user or their department, across
            // every run they can see (not just their own runs).
            var inboxPending = await InParallelScopeAsync(async (sdb, sct) =>
            {
                var visibleRuns = NdDemoDataFilters
                    .ApplyToAnalysisRuns(sdb.NdAnalysisRuns.AsNoTracking().Where(r => r.Status != DeletedStatus), demoCtx)
                    .Select(r => r.Id);
                return await NdActionPlanInbox
                    .AssignedTo(
                        sdb,
                        sdb.NdAnalysisActionPlans.AsNoTracking()
                            .Where(p => p.Status == "pending" && visibleRuns.Contains(p.AnalysisRunId)),
                        profile.Id,
                        profile.DepartmentId)
                    .CountAsync(sct);
            }, innerCt);

            var runCounts = await runCountsTask;
            var inProgressCandidates = await inProgressTask;

            var analysisRunsAll = runCounts?.All ?? 0;
            var analysisRunsCorrection = runCounts?.Correction ?? 0;
            var analysisRunsInProgress = inProgressCandidates.Count(r => NdRunActivityHelper.IsProcessingRun(
                r.Status,
                r.TotalPointsCount,
                r.ProcessedPointsCount,
                r.DualVerifyFailedCount,
                r.UpdatedAt));

            if (role == "super_admin")
            {
                deletedAnalysisRuns = runCounts?.Deleted ?? 0;
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
                inboxPending,
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
