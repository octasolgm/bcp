using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Reguliq.Api.Data;
using Reguliq.Api.Data.NewDashboard.Entities;
using Reguliq.Api.Infrastructure.NewDashboard;
using Reguliq.Api.Services.NewDashboard;
using Reguliq.Api.Services.NewDashboard.Demo;

namespace Reguliq.Api.Controllers.NewDashboard;

/// <summary>
/// Cross-run action plan rollups for the overview page, plus the responsibility
/// picker (departments and users, isolated per demo/real tenant).
/// </summary>
[ApiController]
[Route("nd/action-plans")]
public class ActionPlanInsightsController(
    AppDbContext db,
    SupabaseJwtValidator jwt,
    IOptions<SupabaseJwtOptions> jwtOptions,
    IHttpClientFactory httpClientFactory,
    NdDemoUserDirectory demoDirectory) : NdControllerBase
{
    private static readonly string[] AllRoles = ["super_admin", "maker", "checker", "reviewer"];

    /// <summary>Counts by priority across every run the viewer can see.</summary>
    [HttpGet("summary")]
    public async Task<IActionResult> Summary(CancellationToken ct)
    {
        var (profile, user, error) = await RequireAuthWithUserAsync(db, jwt, ct, AllRoles);
        if (error != null) return error;

        var runIds = await VisibleRunIdsAsync(profile!, user, ct);
        var plans = await LoadPlansAsync(runIds, ct);

        return Ok(new
        {
            success = true,
            data = new
            {
                total = plans.Count,
                pending = plans.Count(p => p.Status == ActionPlanStatuses.Pending),
                resolved = plans.Count(p => p.Status == ActionPlanStatuses.Resolved),
                overdue = plans.Count(IsOverdue),
                byPriority = new[] { ActionPlanPriorities.High, ActionPlanPriorities.Medium, ActionPlanPriorities.Low }
                    .Select(priority => new
                    {
                        priority,
                        total = plans.Count(p => p.Priority == priority),
                        pending = plans.Count(p => p.Priority == priority && p.Status == ActionPlanStatuses.Pending),
                        resolved = plans.Count(p => p.Priority == priority && p.Status == ActionPlanStatuses.Resolved),
                        overdue = plans.Count(p => p.Priority == priority && IsOverdue(p)),
                        runCount = plans.Where(p => p.Priority == priority).Select(p => p.AnalysisRunId).Distinct().Count(),
                    }),
            },
        });
    }

    /// <summary>Runs that hold action plans of a given priority, with per-run counts.</summary>
    [HttpGet("by-priority/{priority}")]
    public async Task<IActionResult> ByPriority(string priority, [FromQuery] string? status, CancellationToken ct)
    {
        var (profile, user, error) = await RequireAuthWithUserAsync(db, jwt, ct, AllRoles);
        if (error != null) return error;

        var normalized = ActionPlanPriorities.Normalize(priority);
        var runIds = await VisibleRunIdsAsync(profile!, user, ct);
        var plans = (await LoadPlansAsync(runIds, ct))
            .Where(p => p.Priority == normalized)
            .ToList();

        if (!string.IsNullOrWhiteSpace(status))
        {
            var wanted = ActionPlanStatuses.Normalize(status);
            plans = plans.Where(p => p.Status == wanted).ToList();
        }

        var matchedRunIds = plans.Select(p => p.AnalysisRunId).Distinct().ToList();
        var runs = await db.NdAnalysisRuns.AsNoTracking()
            .Where(r => matchedRunIds.Contains(r.Id))
            .ToListAsync(ct);
        var creatorNames = await LoadProfileNamesAsync(db, runs.Select(r => r.CreatedBy), ct);

        var rows = runs.Select(run =>
        {
            var runPlans = plans.Where(p => p.AnalysisRunId == run.Id).ToList();
            return new
            {
                runId = run.Id,
                runName = run.Name,
                runStatus = run.Status,
                workflowEngine = run.WorkflowEngine,
                createdAt = run.CreatedAt,
                createdByName = ProfileName(creatorNames, run.CreatedBy),
                actionPlanCount = runPlans.Count,
                pendingCount = runPlans.Count(p => p.Status == ActionPlanStatuses.Pending),
                resolvedCount = runPlans.Count(p => p.Status == ActionPlanStatuses.Resolved),
                overdueCount = runPlans.Count(IsOverdue),
                gapCount = runPlans.Select(p => p.AnalysisPointId).Distinct().Count(),
                nextTargetDate = FormatDueDateResponse(
                    runPlans.Where(p => p.TargetDate.HasValue && p.Status == ActionPlanStatuses.Pending)
                        .Select(p => p.TargetDate!.Value)
                        .DefaultIfEmpty()
                        .Min() is var min && min == default ? null : min),
                pointIds = runPlans.Select(p => p.AnalysisPointId).Distinct(),
            };
        })
        .OrderByDescending(r => r.actionPlanCount)
        .ThenByDescending(r => r.createdAt)
        .ToList();

        return Ok(new { success = true, data = new { priority = normalized, runs = rows, total = plans.Count } });
    }

    /// <summary>
    /// Every action assigned to the signed-in user, directly or through their department,
    /// with pending/resolved/overdue tallies for the inbox header.
    /// </summary>
    [HttpGet("inbox")]
    public async Task<IActionResult> Inbox([FromQuery] string? status, CancellationToken ct)
    {
        var (profile, user, error) = await RequireAuthWithUserAsync(db, jwt, ct, AllRoles);
        if (error != null) return error;

        var ctx = await NdDemoIsolationContext.ResolveAsync(demoDirectory, user, ct);
        var runIds = await NdDemoDataFilters
            .ApplyToAnalysisRuns(db.NdAnalysisRuns.AsNoTracking().Where(r => r.DeletedAt == null && r.Status != "deleted"), ctx)
            .Select(r => r.Id)
            .ToListAsync(ct);

        var plans = runIds.Count == 0
            ? []
            : await NdActionPlanInbox
                .AssignedTo(db, db.NdAnalysisActionPlans.AsNoTracking().Where(p => runIds.Contains(p.AnalysisRunId)), profile!.Id, profile.DepartmentId)
                .ToListAsync(ct);

        var counts = new
        {
            total = plans.Count,
            pending = plans.Count(p => p.Status == ActionPlanStatuses.Pending),
            resolved = plans.Count(p => p.Status == ActionPlanStatuses.Resolved),
            overdue = plans.Count(IsOverdue),
        };

        var filtered = string.IsNullOrWhiteSpace(status) || status.Trim().ToLowerInvariant() == "all"
            ? plans
            : status.Trim().ToLowerInvariant() == "overdue"
                ? plans.Where(IsOverdue).ToList()
                : plans.Where(p => p.Status == ActionPlanStatuses.Normalize(status)).ToList();

        var planIds = filtered.Select(p => p.Id).ToList();
        var pointIds = filtered.Select(p => p.AnalysisPointId).Distinct().ToList();
        var planRunIds = filtered.Select(p => p.AnalysisRunId).Distinct().ToList();

        var runs = await db.NdAnalysisRuns.AsNoTracking()
            .Where(r => planRunIds.Contains(r.Id))
            .Select(r => new { r.Id, r.Name, r.Status, r.WorkflowEngine })
            .ToDictionaryAsync(r => r.Id, ct);

        var snapshots = await db.NdAnalysisPoints.AsNoTracking()
            .Where(p => pointIds.Contains(p.Id))
            .Select(p => new { p.Id, p.PointSnapshot })
            .ToDictionaryAsync(p => p.Id, p => p.PointSnapshot, ct);

        var assignees = planIds.Count == 0
            ? []
            : await db.NdAnalysisActionPlanAssignees.AsNoTracking()
                .Where(a => planIds.Contains(a.ActionPlanId))
                .OrderBy(a => a.SortOrder)
                .ToListAsync(ct);

        var departmentName = profile!.DepartmentId is Guid myDept
            ? await db.NdDepartments.AsNoTracking().Where(d => d.Id == myDept).Select(d => d.Name).FirstOrDefaultAsync(ct)
            : null;

        var items = filtered
            .OrderBy(p => p.Status == ActionPlanStatuses.Resolved)
            .ThenBy(p => p.TargetDate ?? DateTimeOffset.MaxValue)
            .ThenByDescending(p => p.PriorityScore)
            .Select(p =>
            {
                var mine = assignees.Where(a => a.ActionPlanId == p.Id).ToList();
                var run = runs.GetValueOrDefault(p.AnalysisRunId);
                var clause = ClauseLabel(snapshots.GetValueOrDefault(p.AnalysisPointId));
                var direct = p.ResponsibilityUserId == profile.Id || mine.Any(a => a.UserId == profile.Id);
                return new
                {
                    id = p.Id,
                    analysisRunId = p.AnalysisRunId,
                    analysisPointId = p.AnalysisPointId,
                    gapIndex = p.GapIndex,
                    runName = run?.Name ?? "Analysis",
                    runStatus = run?.Status,
                    workflowEngine = run?.WorkflowEngine,
                    clauseNo = clause.No,
                    clauseTitle = clause.Title,
                    actionPlan = p.ActionPlan,
                    status = p.Status,
                    priority = p.Priority,
                    priorityScore = p.PriorityScore,
                    targetDate = FormatDueDateResponse(p.TargetDate),
                    overdue = IsOverdue(p),
                    resolvedAt = FormatDueDateResponse(p.ResolvedAt),
                    updatedAt = p.UpdatedAt,
                    assignedDirectly = direct,
                    assignedVia = direct ? "you" : departmentName ?? "your department",
                    owners = mine.Select(a => new { type = a.AssigneeType, label = a.Label }),
                };
            })
            .ToList();

        return Ok(new
        {
            success = true,
            data = new
            {
                counts,
                departmentId = profile.DepartmentId,
                departmentName,
                items,
            },
        });
    }

    /// <summary>Departments and users a plan can be assigned to. Demo viewers only see demo accounts.</summary>
    [HttpGet("responsibility-options")]
    public async Task<IActionResult> ResponsibilityOptions(CancellationToken ct)
    {
        var (_, user, error) = await RequireAuthWithUserAsync(db, jwt, ct, AllRoles);
        if (error != null) return error;

        var ctx = await NdDemoIsolationContext.ResolveAsync(demoDirectory, user, ct);

        var departments = await NdDemoDataFilters
            .ApplyToDepartments(db.NdDepartments.AsNoTracking().Where(d => d.IsActive), ctx)
            .OrderBy(d => d.Name)
            .Select(d => new { id = d.Id, name = d.Name })
            .ToListAsync(ct);

        var emails = await FetchAuthEmailsAsync(ct);
        var profiles = await db.NdProfiles.AsNoTracking()
            .Where(p => p.IsActive)
            .OrderBy(p => p.FullName)
            .ToListAsync(ct);
        profiles = NdDemoDataFilters.FilterProfiles(profiles, ctx, emails);

        return Ok(new
        {
            success = true,
            data = new
            {
                departments,
                users = profiles.Select(p => new
                {
                    id = p.Id,
                    fullName = p.FullName,
                    email = emails.TryGetValue(p.Id, out var e) ? e : null,
                    role = p.Role,
                }),
            },
        });
    }

    // ----------------------------------------------------------- helpers

    private static (string No, string Title) ClauseLabel(string? snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot)) return ("", "");
        try
        {
            using var doc = JsonDocument.Parse(snapshot);
            var root = doc.RootElement;
            string Read(params string[] keys)
            {
                foreach (var key in keys)
                    if (root.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.String)
                        return el.GetString() ?? "";
                return "";
            }
            return (Read("pointNumber", "point_number"), Read("pointTitle", "point_title", "title"));
        }
        catch
        {
            return ("", "");
        }
    }

    private static bool IsOverdue(NdAnalysisActionPlan p) =>
        p.Status == ActionPlanStatuses.Pending
        && p.TargetDate.HasValue
        && p.TargetDate.Value < DateTimeOffset.UtcNow;

    private async Task<List<Guid>> VisibleRunIdsAsync(NdProfile profile, JwtUser user, CancellationToken ct)
    {
        var ctx = await NdDemoIsolationContext.ResolveAsync(demoDirectory, user, ct);
        var query = NdDemoDataFilters.ApplyToAnalysisRuns(
            db.NdAnalysisRuns.AsNoTracking().Where(r => r.DeletedAt == null),
            ctx);

        if (string.Equals(profile.Role, "maker", StringComparison.OrdinalIgnoreCase))
            query = query.Where(r => r.CreatedBy == profile.Id);

        return await query.Select(r => r.Id).ToListAsync(ct);
    }

    private async Task<List<NdAnalysisActionPlan>> LoadPlansAsync(List<Guid> runIds, CancellationToken ct)
    {
        if (runIds.Count == 0) return [];
        return await db.NdAnalysisActionPlans.AsNoTracking()
            .Where(p => runIds.Contains(p.AnalysisRunId))
            .ToListAsync(ct);
    }

    private async Task<Dictionary<Guid, string?>> FetchAuthEmailsAsync(CancellationToken ct)
    {
        var map = new Dictionary<Guid, string?>();
        var opts = jwtOptions.Value;
        if (string.IsNullOrWhiteSpace(opts.Url) || string.IsNullOrWhiteSpace(opts.ServiceRoleKey))
            return map;

        var client = httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", opts.ServiceRoleKey);
        client.DefaultRequestHeaders.TryAddWithoutValidation("apikey", opts.ServiceRoleKey);

        var res = await client.GetAsync($"{opts.Url.TrimEnd('/')}/auth/v1/admin/users?per_page=1000", ct);
        if (!res.IsSuccessStatusCode) return map;

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.TryGetProperty("users", out var usersEl) || usersEl.ValueKind != JsonValueKind.Array)
            return map;

        foreach (var u in usersEl.EnumerateArray())
        {
            if (u.TryGetProperty("id", out var idEl) && Guid.TryParse(idEl.GetString(), out var id))
                map[id] = u.TryGetProperty("email", out var emailEl) ? emailEl.GetString() : null;
        }

        return map;
    }
}
