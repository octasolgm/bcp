using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Data.NewDashboard.Entities;
using Reguliq.Api.Infrastructure.NewDashboard;
using Reguliq.Api.Services.NewDashboard.Demo;

namespace Reguliq.Api.Controllers.NewDashboard;

public abstract class NdControllerBase : ControllerBase
{
    public record PointCommentInput(Guid AnalysisPointId, string Comment);
    public record ActionItemReviewInput(
        Guid AnalysisPointId,
        int ActionIndex,
        string Status,
        string? Comment,
        string? Responsibility,
        string? DueDate,
        string? Priority);

    public record ReviewRequest(
        string? OverallComment,
        string? ReviewStatus,
        int? Priority,
        string? Responsibility,
        string? DueDate,
        List<PointCommentInput>? PointComments,
        List<ActionItemReviewInput>? ActionItemReviews);

    protected static List<Guid> ParseSelectedRegulationDocIds(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            var ids = JsonSerializer.Deserialize<List<string>>(json) ?? [];
            var list = new List<Guid>(ids.Count);
            foreach (var s in ids)
            {
                if (Guid.TryParse(s, out var g)) list.Add(g);
            }

            return list;
        }
        catch
        {
            return [];
        }
    }

    protected static DateTimeOffset? ParseOptionalDueDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var t = raw.Trim();
        if (DateTimeOffset.TryParse(t, out var dto)) return dto;
        if (DateTime.TryParse(t, out var dt))
            return new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Local));
        if (DateOnly.TryParse(t, out var dateOnly))
            return new DateTimeOffset(dateOnly.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
        return null;
    }

    protected static string? FormatDueDateResponse(DateTimeOffset? dueDate) =>
        dueDate?.ToString("o");

    protected static void ApplyReviewMetadata(NdAnalysisReview review, ReviewRequest body)
    {
        review.OverallComment = string.IsNullOrWhiteSpace(body.OverallComment) ? null : body.OverallComment.Trim();
        review.ReviewStatus = string.IsNullOrWhiteSpace(body.ReviewStatus) ? null : body.ReviewStatus.Trim();
        review.Priority = body.Priority is >= 0 and <= 100 ? body.Priority : null;
        review.Responsibility = string.IsNullOrWhiteSpace(body.Responsibility) ? null : body.Responsibility.Trim();
        if (!string.IsNullOrWhiteSpace(body.DueDate) && DateTimeOffset.TryParse(body.DueDate.Trim(), out var parsedDue))
            review.DueDate = parsedDue;
    }

    private static readonly HashSet<string> ValidActionItemReviewStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "approve", "need_modify",
    };

    protected JwtUser? ValidateJwt(SupabaseJwtValidator jwt)
    {
        var authHeader = Request.Headers.Authorization.FirstOrDefault();
        return jwt.ValidateToken(authHeader);
    }

    protected async Task<(NdProfile Profile, IActionResult? Error)> RequireAuthAsync(
        AppDbContext db,
        SupabaseJwtValidator jwt,
        CancellationToken ct,
        params string[] allowedRoles)
    {
        var user = ValidateJwt(jwt);
        if (user == null)
            return (null!, Unauthorized(new { success = false, message = "Unauthorized" }));

        var profile = await db.NdProfiles
            .Include(p => p.Department)
            .FirstOrDefaultAsync(p => p.Id == user.UserId, ct);

        if (profile == null)
            return (null!, Unauthorized(new { success = false, message = "Profile not found" }));

        if (!profile.IsActive)
            return (null!, StatusCode(403, new { success = false, message = "Account deactivated" }));

        if (allowedRoles.Length > 0 && !allowedRoles.Contains(profile.Role, StringComparer.OrdinalIgnoreCase))
            return (null!, StatusCode(403, new { success = false, message = "Forbidden" }));

        return (profile, null);
    }

    protected async Task<(NdProfile Profile, JwtUser User, IActionResult? Error)> RequireAuthWithUserAsync(
        AppDbContext db,
        SupabaseJwtValidator jwt,
        CancellationToken ct,
        params string[] allowedRoles)
    {
        var user = ValidateJwt(jwt);
        if (user == null)
            return (null!, null!, Unauthorized(new { success = false, message = "Unauthorized" }));

        var (profile, error) = await RequireAuthAsync(db, jwt, ct, allowedRoles);
        if (error != null)
            return (null!, null!, error);

        return (profile, user, null);
    }

    protected static async Task<Dictionary<Guid, string>> LoadProfileNamesAsync(
        AppDbContext db,
        IEnumerable<Guid?> profileIds,
        CancellationToken ct)
    {
        var ids = profileIds.Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToList();
        if (ids.Count == 0) return new Dictionary<Guid, string>();

        return await db.NdProfiles.AsNoTracking()
            .Where(p => ids.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.FullName, ct);
    }

    protected static string? ProfileName(IReadOnlyDictionary<Guid, string> names, Guid? id) =>
        id is Guid g && names.TryGetValue(g, out var name) ? name : null;

    protected async Task<NdProfile> GetOrCreateProfileAsync(
        AppDbContext db,
        JwtUser user,
        string? fullName,
        string? requestedRole,
        Guid? departmentId,
        CancellationToken ct)
    {
        var profile = await db.NdProfiles
            .Include(p => p.Department)
            .FirstOrDefaultAsync(p => p.Id == user.UserId, ct);

        if (profile == null)
        {
            profile = new NdProfile
            {
                Id = user.UserId,
                FullName = fullName?.Trim() ?? user.Email?.Split('@')[0] ?? "",
                Role = NormalizeRole(requestedRole) ?? "maker",
                DepartmentId = departmentId,
                IsActive = true,
            };
            db.NdProfiles.Add(profile);
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(fullName))
                profile.FullName = fullName.Trim();
            if (departmentId.HasValue)
                profile.DepartmentId = departmentId;
            if (!string.IsNullOrWhiteSpace(requestedRole))
                profile.Role = NormalizeRole(requestedRole) ?? profile.Role;
        }

        profile.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        if (profile.DepartmentId.HasValue && profile.Department == null)
        {
            profile = await db.NdProfiles
                .Include(p => p.Department)
                .FirstAsync(p => p.Id == user.UserId, ct);
        }

        return profile;
    }

    protected static object MapProfile(
        NdProfile p,
        string? email = null,
        IReadOnlySet<Guid>? demoProfileIds = null) => new
    {
        id = p.Id,
        fullName = p.FullName,
        email,
        role = p.Role,
        departmentId = p.DepartmentId,
        departmentName = p.Department?.Name,
        isActive = p.IsActive,
        createdAt = p.CreatedAt,
        isDemo = NdDemoIsolationHelper.IsDemoEmail(email)
            || NdDemoIsolationHelper.IsDemoName(p.FullName)
            || (demoProfileIds != null && demoProfileIds.Contains(p.Id)),
    };

    protected static async Task SavePointCommentsAsync(
        AppDbContext db,
        Guid reviewId,
        List<PointCommentInput>? comments,
        Guid userId,
        CancellationToken ct)
    {
        if (comments == null) return;
        foreach (var c in comments.Where(c => !string.IsNullOrWhiteSpace(c.Comment)))
        {
            db.NdAnalysisPointComments.Add(new NdAnalysisPointComment
            {
                AnalysisPointId = c.AnalysisPointId,
                AnalysisReviewId = reviewId,
                Comment = c.Comment.Trim(),
                CommentedBy = userId,
            });
        }
        await db.SaveChangesAsync(ct);
    }

    protected static async Task SaveActionItemReviewsAsync(
        AppDbContext db,
        Guid reviewId,
        List<ActionItemReviewInput>? reviews,
        Guid userId,
        CancellationToken ct)
    {
        if (reviews == null) return;
        foreach (var r in reviews)
        {
            var status = r.Status?.Trim().ToLowerInvariant() ?? "";
            if (!ValidActionItemReviewStatuses.Contains(status)) continue;

            DateTimeOffset? dueDate = ParseOptionalDueDate(r.DueDate);

            db.NdActionPlanItemReviews.Add(new NdActionPlanItemReview
            {
                AnalysisPointId = r.AnalysisPointId,
                AnalysisReviewId = reviewId,
                ActionIndex = r.ActionIndex,
                Status = status,
                Comment = string.IsNullOrWhiteSpace(r.Comment) ? null : r.Comment.Trim(),
                Responsibility = string.IsNullOrWhiteSpace(r.Responsibility) ? null : r.Responsibility.Trim(),
                DueDate = dueDate,
                Priority = NormalizeReviewPriority(r.Priority),
                ReviewedBy = userId,
            });
        }
        await db.SaveChangesAsync(ct);
    }

    private static string? NormalizeReviewPriority(string? raw)
    {
        var t = raw?.Trim().ToLowerInvariant() ?? "";
        return t is "medium" or "higher" ? t : null;
    }

    protected static async Task RecordStatusChangeAsync(
        AppDbContext db,
        Guid runId,
        string? fromStatus,
        string toStatus,
        Guid? changedBy,
        string? comment,
        CancellationToken ct)
    {
        db.NdAnalysisStatusHistories.Add(new NdAnalysisStatusHistory
        {
            AnalysisRunId = runId,
            FromStatus = fromStatus,
            ToStatus = toStatus,
            ChangedBy = changedBy,
            Comment = comment,
        });
        await db.SaveChangesAsync(ct);
    }

    private static string? NormalizeRole(string? role)
    {
        if (string.IsNullOrWhiteSpace(role)) return null;
        var r = role.Trim().ToLowerInvariant();
        return r is "super_admin" or "maker" or "checker" or "reviewer" ? r : null;
    }
}
