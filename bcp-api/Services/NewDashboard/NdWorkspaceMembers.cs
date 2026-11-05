using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Data.NewDashboard.Entities;
using Reguliq.Api.Infrastructure.NewDashboard;

namespace Reguliq.Api.Services.NewDashboard;

/// <summary>Users who work in a business other than their home workspace, with a separate role there.</summary>
public static class NdWorkspaceMembers
{
    /// <summary>While a user acts in another business, their role and active state are the ones set for it. The
    /// main department belongs to the home workspace, so it does not apply. Only for profiles read without tracking.</summary>
    public static async Task ApplyToProfileAsync(AppDbContext db, NdProfile profile, CancellationToken ct)
    {
        var current = WorkspaceScope.CurrentWorkspaceId;
        if (current == null || current == profile.TenantId || profile.IsPlatformAdmin) return;
        var member = await db.NdWorkspaceMembers.AsNoTracking()
            .FirstOrDefaultAsync(m => m.ProfileId == profile.Id && m.WorkspaceId == current, ct);
        if (member == null) return;
        Overlay(profile, member);
    }

    private static void Overlay(NdProfile profile, NdWorkspaceMember member)
    {
        profile.Role = member.Role;
        profile.IsActive = member.IsActive;
        profile.DepartmentId = null;
        profile.Department = null;
    }

    /// <summary>Home users and members of a workspace, each with the role they have there. The list is read only;
    /// a member keeps their home TenantId, which is how callers tell the two apart.</summary>
    public static async Task<List<NdProfile>> ProfilesInWorkspaceAsync(AppDbContext db, Guid workspaceId, CancellationToken ct)
    {
        var home = await db.NdProfiles.AsNoTracking().Where(p => p.TenantId == workspaceId).ToListAsync(ct);
        var members = await db.NdWorkspaceMembers.AsNoTracking().Where(m => m.WorkspaceId == workspaceId).ToListAsync(ct);
        if (members.Count == 0) return home;

        var ids = members.Select(m => m.ProfileId).ToList();
        var guests = await db.NdProfiles.AsNoTracking().Where(p => ids.Contains(p.Id) && p.TenantId != workspaceId).ToListAsync(ct);
        foreach (var guest in guests) Overlay(guest, members.First(m => m.ProfileId == guest.Id));
        return home.Concat(guests).OrderBy(p => p.FullName).ToList();
    }

    /// <summary>Adds the user to the business, or updates the role if they are already in it. A null role keeps
    /// the role they have in their home workspace. Saved by the caller.</summary>
    public static async Task AddOrUpdateAsync(
        AppDbContext db, NdProfile profile, Guid workspaceId, string? role, Guid createdBy, CancellationToken ct)
    {
        var member = await db.NdWorkspaceMembers.FirstOrDefaultAsync(m => m.ProfileId == profile.Id && m.WorkspaceId == workspaceId, ct);
        if (member == null)
        {
            db.NdWorkspaceMembers.Add(new NdWorkspaceMember
            {
                ProfileId = profile.Id,
                WorkspaceId = workspaceId,
                Role = role ?? profile.Role,
                CreatedBy = createdBy,
            });
            return;
        }
        if (role != null) member.Role = role;
        member.IsActive = true;
        member.UpdatedAt = DateTimeOffset.UtcNow;
    }
}
