using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Data.Entities;
using Reguliq.Api.Data.NewDashboard.Entities;
using Reguliq.Api.Infrastructure.NewDashboard;
using Reguliq.Api.Services.NewDashboard.Demo;

namespace Reguliq.Api.Services.NewDashboard;

/// <summary>
/// What one user may see through their departments. Admins and demo accounts are unrestricted, so the demo stays a
/// fixed simulation. Documents filed under no department and analyses shared with no department stay visible to all.
/// </summary>
public sealed record NdDepartmentScope(bool Unrestricted, Guid ProfileId, List<Guid> DocumentDepartmentIds, List<Guid> RunDepartmentIds)
{
    public static NdDepartmentScope All(Guid profileId) => new(true, profileId, [], []);
}

public static class NdDepartmentAccess
{
    /// <summary>The user's departments: membership rows plus the profile's main department.</summary>
    public static async Task<List<Guid>> UserDepartmentIdsAsync(AppDbContext db, NdProfile profile, CancellationToken ct)
    {
        var ids = await db.NdProfileDepartments.AsNoTracking()
            .Where(l => l.ProfileId == profile.Id)
            .Select(l => l.DepartmentId)
            .ToListAsync(ct);
        if (IsHomeHere(profile) && profile.DepartmentId is Guid main && !ids.Contains(main)) ids.Add(main);
        return ids;
    }

    /// <summary>The main department on a profile belongs to the home workspace, so it is left alone elsewhere.</summary>
    private static bool IsHomeHere(NdProfile profile) =>
        WorkspaceScope.CurrentWorkspaceId is not Guid current || current == profile.TenantId;

    public static async Task<NdDepartmentScope> ResolveAsync(
        AppDbContext db, NdProfile profile, NdDemoIsolationContext demoCtx, CancellationToken ct)
    {
        if (profile.Role == "super_admin" || demoCtx.ViewerIsDemo) return NdDepartmentScope.All(profile.Id);

        var own = await UserDepartmentIdsAsync(db, profile, ct);
        if (own.Count == 0) return new NdDepartmentScope(false, profile.Id, [], []);

        var tree = await db.NdDepartments.AsNoTracking().Select(d => new { d.Id, d.ParentId }).ToListAsync(ct);
        var below = new HashSet<Guid>(own);
        var above = new HashSet<Guid>(own);
        var frontier = new List<Guid>(own);
        while (frontier.Count > 0)
        {
            var current = frontier;
            frontier = tree.Where(d => d.ParentId is Guid p && current.Contains(p) && below.Add(d.Id)).Select(d => d.Id).ToList();
        }
        frontier = new List<Guid>(own);
        while (frontier.Count > 0)
        {
            var current = frontier;
            frontier = tree.Where(d => current.Contains(d.Id) && d.ParentId != null && above.Add(d.ParentId.Value))
                .Select(d => d.ParentId!.Value).ToList();
        }

        // Documents: the user's departments and everything below them. Analyses: sharing with a department also
        // reaches its units and teams, and the heads above a shared unit, so the whole branch can read the report.
        return new NdDepartmentScope(false, profile.Id, below.ToList(), below.Union(above).ToList());
    }

    public static IQueryable<NdAnalysisRun> ApplyToRuns(this IQueryable<NdAnalysisRun> query, AppDbContext db, NdDepartmentScope scope)
    {
        if (scope.Unrestricted) return query;
        var me = scope.ProfileId;
        var ids = scope.RunDepartmentIds;
        return query.Where(r => r.CreatedBy == me
            || !db.NdAnalysisRunDepartments.Any(l => l.AnalysisRunId == r.Id)
            || db.NdAnalysisRunDepartments.Any(l => l.AnalysisRunId == r.Id && ids.Contains(l.DepartmentId)));
    }

    public static IQueryable<StoredDocument> ApplyToDocuments(this IQueryable<StoredDocument> query, AppDbContext db, NdDepartmentScope scope)
    {
        if (scope.Unrestricted) return query;
        var me = scope.ProfileId;
        var ids = scope.DocumentDepartmentIds;
        return query.Where(d => d.UploadedBy == me
            || !db.NdInternalDocumentDepartments.Any(l => l.StoredDocumentId == d.Id)
            || db.NdInternalDocumentDepartments.Any(l => l.StoredDocumentId == d.Id && ids.Contains(l.DepartmentId)));
    }

    /// <summary>Replaces a user's departments and keeps the profile's main department one of them.</summary>
    public static async Task SetUserDepartmentsAsync(AppDbContext db, NdProfile tracked, IReadOnlyCollection<Guid> wanted, CancellationToken ct)
    {
        var current = await db.NdProfileDepartments.Where(l => l.ProfileId == tracked.Id).ToListAsync(ct);
        db.NdProfileDepartments.RemoveRange(current.Where(l => !wanted.Contains(l.DepartmentId)));
        foreach (var id in wanted.Where(w => current.All(l => l.DepartmentId != w)))
            db.NdProfileDepartments.Add(new NdProfileDepartment { ProfileId = tracked.Id, DepartmentId = id });
        if (IsHomeHere(tracked) && (tracked.DepartmentId is not Guid main || !wanted.Contains(main)))
            tracked.DepartmentId = wanted.Count > 0 ? wanted.First() : null;
        tracked.UpdatedAt = DateTimeOffset.UtcNow;
    }
}
