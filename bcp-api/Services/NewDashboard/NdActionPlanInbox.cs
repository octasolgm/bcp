using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Data.NewDashboard.Entities;

namespace Reguliq.Api.Services.NewDashboard;

/// <summary>
/// Shared "is this action mine?" rule. The inbox page and the sidebar badge must agree,
/// so both compose this predicate rather than each spelling the join out.
/// </summary>
public static class NdActionPlanInbox
{
    /// <summary>
    /// Actions owned by the user directly or by their department. The legacy single
    /// <c>responsibility_*</c> columns are matched too so actions saved before the
    /// multi-owner picker still reach the right inbox.
    /// </summary>
    public static IQueryable<NdAnalysisActionPlan> AssignedTo(
        AppDbContext db,
        IQueryable<NdAnalysisActionPlan> plans,
        Guid profileId,
        Guid? departmentId) =>
        plans.Where(p =>
            p.ResponsibilityUserId == profileId
            || (departmentId != null && p.ResponsibilityDepartmentId == departmentId)
            || db.NdAnalysisActionPlanAssignees.Any(a =>
                a.ActionPlanId == p.Id
                && (a.UserId == profileId || (departmentId != null && a.DepartmentId == departmentId))));
}
