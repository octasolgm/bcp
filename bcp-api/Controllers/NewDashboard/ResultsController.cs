using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Data.NewDashboard.Entities;
using Reguliq.Api.Infrastructure.NewDashboard;
using Reguliq.Api.Services.NewDashboard;

namespace Reguliq.Api.Controllers.NewDashboard;

[ApiController]
[Route("nd/results")]
public class ResultsController(
    AppDbContext db,
    SupabaseJwtValidator jwt,
    NdRegulationPointPageService pointPages,
    IServiceScopeFactory scopeFactory,
    ILogger<ResultsController> logger) : NdControllerBase
{
    public record UpdateActionPlanRequest(string Content, int? RevertToVersion);

    [HttpGet("{runId:guid}")]
    public async Task<IActionResult> Get(Guid runId, CancellationToken ct)
    {
        var (profile, error) = await RequireAuthAsync(db, jwt, ct,
            "super_admin", "maker", "checker", "reviewer");
        if (error != null) return error;

        var run = await db.NdAnalysisRuns
            .Include(r => r.Points)
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run == null) return NotFound();

        if (profile!.Role == "maker" && run.CreatedBy != profile.Id)
            return StatusCode(403);

        if (AnalysisWorkflowEngine.IsRegulFamily(run.WorkflowEngine))
            StartBackgroundTemplateSync(runId, profile.Id);

        var creator = run.CreatedBy.HasValue
            ? await db.NdProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == run.CreatedBy, ct)
            : null;

        var reviews = await db.NdAnalysisReviews.AsNoTracking()
            .Where(r => r.AnalysisRunId == runId)
            .OrderBy(r => r.CreatedAt)
            .ToListAsync(ct);

        var pointIds = run.Points.Select(p => p.Id).ToList();
        var comments = await db.NdAnalysisPointComments.AsNoTracking()
            .Where(c => pointIds.Contains(c.AnalysisPointId))
            .ToListAsync(ct);

        var actionItemReviews = await db.NdActionPlanItemReviews.AsNoTracking()
            .Where(r => pointIds.Contains(r.AnalysisPointId))
            .OrderByDescending(r => r.SortOrder)
            .ThenByDescending(r => r.CreatedAt)
            .ToListAsync(ct);

        var actionPlans = await db.NdAnalysisActionPlans.AsNoTracking()
            .Where(p => p.AnalysisRunId == runId)
            .OrderBy(p => p.GapIndex).ThenBy(p => p.SortOrder).ThenBy(p => p.CreatedAt)
            .ToListAsync(ct);
        var actionPlanIds = actionPlans.Select(p => p.Id).ToList();
        var actionPlanReviews = actionPlanIds.Count == 0
            ? []
            : await db.NdAnalysisActionPlanReviews.AsNoTracking()
                .Where(r => actionPlanIds.Contains(r.ActionPlanId))
                .OrderBy(r => r.CreatedAt)
                .ToListAsync(ct);
        var actionPlanAssignees = actionPlanIds.Count == 0
            ? []
            : await db.NdAnalysisActionPlanAssignees.AsNoTracking()
                .Where(a => actionPlanIds.Contains(a.ActionPlanId))
                .OrderBy(a => a.SortOrder)
                .ToListAsync(ct);
        var actionPlanPeople = await LoadProfileNamesAsync(
            db,
            actionPlans.SelectMany(p => new[] { p.CreatedBy, p.UpdatedBy, p.ResponsibilityUserId })
                .Concat(actionPlanReviews.Select(r => r.ReviewerId)),
            ct);
        var actionPlanDeptIds = actionPlans
            .Where(p => p.ResponsibilityDepartmentId.HasValue)
            .Select(p => p.ResponsibilityDepartmentId!.Value).Distinct().ToList();
        var actionPlanDepartments = actionPlanDeptIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await db.NdDepartments.AsNoTracking()
                .Where(d => actionPlanDeptIds.Contains(d.Id))
                .ToDictionaryAsync(d => d.Id, d => d.Name, ct);

        var tempReviewComments = await db.NdTempPointReviewComments.AsNoTracking()
            .Where(c => pointIds.Contains(c.AnalysisPointId))
            .OrderBy(c => c.CreatedAt)
            .ToListAsync(ct);

        var tempCommentAuthorIds = tempReviewComments
            .Where(c => c.CommentedBy.HasValue)
            .Select(c => c.CommentedBy!.Value)
            .Distinct()
            .ToList();
        var tempCommentAuthors = tempCommentAuthorIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await db.NdProfiles.AsNoTracking()
                .Where(p => tempCommentAuthorIds.Contains(p.Id))
                .ToDictionaryAsync(p => p.Id, p => p.FullName, ct);

        var history = await db.NdAnalysisStatusHistories.AsNoTracking()
            .Where(h => h.AnalysisRunId == runId)
            .OrderBy(h => h.CreatedAt)
            .ToListAsync(ct);

        var attachments = await db.NdAnalysisPointAttachments.AsNoTracking()
            .Where(a => pointIds.Contains(a.AnalysisPointId))
            .OrderBy(a => a.CreatedAt)
            .ToListAsync(ct);

        var runRegDocIds = ParseSelectedRegulationDocIds(run.SelectedRegulationDocIds);
        var enrichedPoints = run.Points.Select(p => new
        {
            p.Id,
            regulationPointId = p.RegulationPointId,
            pointSnapshot = p.PointSnapshot,
            landingAiStatus = p.LandingAiStatus,
            landingAiResult = p.LandingAiResult,
            landingAiError = p.LandingAiError,
            googleAiStatus = p.GoogleAiStatus,
            googleAiResult = p.GoogleAiResult,
            googleAiError = p.GoogleAiError,
            dualVerifyStatus = p.DualVerifyStatus,
            finalStatus = p.FinalStatus,
            finalStatusSource = p.FinalStatusSource,
            aiFinalStatus = p.AiFinalStatus,
            finalActionPlan = p.FinalActionPlan,
            originalAiActionPlan = p.OriginalAiActionPlan,
        }).ToList();

        var qualitativeRow = AnalysisWorkflowEngine.IsRegulFamily(run.WorkflowEngine)
            ? await db.NdRegulQualitativeAssessments.AsNoTracking()
                .FirstOrDefaultAsync(q => q.AnalysisRunId == runId, ct)
            : null;

        object? regulQualitativeAssessment = null;
        if (qualitativeRow != null)
        {
            object? parsedResult = null;
            if (!string.IsNullOrWhiteSpace(qualitativeRow.ResultJson))
            {
                try
                {
                    parsedResult = JsonSerializer.Deserialize<object>(qualitativeRow.ResultJson);
                }
                catch
                {
                    parsedResult = qualitativeRow.ResultJson;
                }
            }

            regulQualitativeAssessment = new
            {
                status = qualitativeRow.Status,
                result = parsedResult,
                errorMessage = qualitativeRow.ErrorMessage,
            };
        }

        return Ok(new
        {
            success = true,
            data = new
            {
                run = new
                {
                    run.Id,
                    run.Name,
                    run.Status,
                    run.WorkflowEngine,
                    run.RegulClausesConfirmedAt,
                    run.TotalPointsCount,
                    run.ProcessedPointsCount,
                    run.DualVerifyFailedCount,
                    createdByName = creator?.FullName,
                    run.CreatedAt,
                },
                points = enrichedPoints,
                pointAttachments = attachments.Select(a => new
                {
                    id = a.Id,
                    analysisPointId = a.AnalysisPointId,
                    actionIndex = a.ActionIndex,
                    storedDocumentId = a.StoredDocumentId,
                    fileName = a.FileName,
                    createdAt = a.CreatedAt,
                }),
                reviews = reviews.Select(r => new
                {
                    r.Id,
                    reviewerRole = r.ReviewerRole,
                    action = r.Action,
                    overallComment = r.OverallComment,
                    reviewStatus = r.ReviewStatus,
                    priority = r.Priority,
                    responsibility = r.Responsibility,
                    dueDate = FormatDueDateResponse(r.DueDate),
                    createdAt = r.CreatedAt,
                }),
                comments,
                actionItemReviews = actionItemReviews.Select(r => new
                {
                    r.Id,
                    analysisPointId = r.AnalysisPointId,
                    analysisReviewId = r.AnalysisReviewId,
                    actionIndex = r.ActionIndex,
                    status = r.Status,
                    comment = r.Comment,
                    responsibility = r.Responsibility,
                    dueDate = FormatDueDateResponse(r.DueDate),
                    priority = r.Priority,
                    sortOrder = r.SortOrder,
                    r.CreatedAt,
                }),
                actionPlans = actionPlans.Select(p => new
                {
                    id = p.Id,
                    analysisRunId = p.AnalysisRunId,
                    analysisPointId = p.AnalysisPointId,
                    gapIndex = p.GapIndex,
                    actionPlan = p.ActionPlan,
                    status = p.Status,
                    priority = p.Priority,
                    priorityScore = ActionPlanPriorities.TierFromScore(p.PriorityScore) == ActionPlanPriorities.Normalize(p.Priority)
                        ? ActionPlanPriorities.ClampScore(p.PriorityScore)
                        : ActionPlanPriorities.ScoreFromTier(p.Priority),
                    targetDate = FormatDueDateResponse(p.TargetDate),
                    responsibilityType = p.ResponsibilityType,
                    responsibilityDepartmentId = p.ResponsibilityDepartmentId,
                    responsibilityUserId = p.ResponsibilityUserId,
                    responsibilityName = p.ResponsibilityType == ActionPlanResponsibilityTypes.User
                        ? ProfileName(actionPlanPeople, p.ResponsibilityUserId) ?? p.ResponsibilityLabel
                        : p.ResponsibilityDepartmentId is Guid did && actionPlanDepartments.TryGetValue(did, out var deptName)
                            ? deptName
                            : p.ResponsibilityLabel,
                    assignees = actionPlanAssignees
                        .Where(a => a.ActionPlanId == p.Id)
                        .Select(ActionPlansController.MapAssignee),
                    comment = p.Comment,
                    sortOrder = p.SortOrder,
                    resolvedAt = FormatDueDateResponse(p.ResolvedAt),
                    createdBy = p.CreatedBy,
                    createdByName = ProfileName(actionPlanPeople, p.CreatedBy),
                    updatedByName = ProfileName(actionPlanPeople, p.UpdatedBy),
                    createdAt = p.CreatedAt,
                    updatedAt = p.UpdatedAt,
                    reviewCount = actionPlanReviews.Count(r => r.ActionPlanId == p.Id),
                    reviews = actionPlanReviews.Where(r => r.ActionPlanId == p.Id).Select(r => new
                    {
                        id = r.Id,
                        actionPlanId = r.ActionPlanId,
                        analysisPointId = r.AnalysisPointId,
                        comment = r.Comment,
                        reviewerId = r.ReviewerId,
                        reviewerName = ProfileName(actionPlanPeople, r.ReviewerId),
                        reviewerRole = r.ReviewerRole,
                        createdAt = r.CreatedAt,
                        updatedAt = r.UpdatedAt,
                    }),
                }),
                tempReviewComments = tempReviewComments.Select(c => new
                {
                    c.Id,
                    analysisPointId = c.AnalysisPointId,
                    comment = c.Comment,
                    commentedBy = c.CommentedBy,
                    commentedByName = c.CommentedBy.HasValue && tempCommentAuthors.TryGetValue(c.CommentedBy.Value, out var name)
                        ? name
                        : null,
                    c.CreatedAt,
                }),
                statusHistory = history,
                regulQualitativeAssessment,
            },
        });
    }

    /// <summary>
    /// Refreshes a Regul demo run from its template without blocking the response. The work must
    /// run on its own DI scope: sharing the request-scoped <see cref="AppDbContext"/> would race
    /// with the queries below and then hit a disposed context once the request completes.
    /// </summary>
    private void StartBackgroundTemplateSync(Guid runId, Guid? profileId)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var seed = scope.ServiceProvider.GetRequiredService<DemoAnalysisSeedService>();
                await seed.SyncRegulDemoRunFromTemplateAsync(
                    runId, profileId, preserveWorkflowStatus: true, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Background demo template sync failed for run {RunId}", runId);
            }
        });
    }

    public record UpdateClauseStatusRequest(string? FinalStatus);

    /// <summary>
    /// Sets a clause's compliance verdict by hand. Passing null hands the clause back to
    /// the AI verdict and to the rule that flips it once every action is resolved.
    /// </summary>
    [HttpPut("{runId:guid}/points/{pointId:guid}/status")]
    public async Task<IActionResult> UpdateClauseStatus(
        Guid runId,
        Guid pointId,
        [FromBody] UpdateClauseStatusRequest body,
        CancellationToken ct)
    {
        var (_, error) = await RequireAuthAsync(db, jwt, ct, "super_admin", "maker", "checker", "reviewer");
        if (error != null) return error;

        var point = await db.NdAnalysisPoints
            .FirstOrDefaultAsync(p => p.Id == pointId && p.AnalysisRunId == runId, ct);
        if (point == null) return NotFound(new { success = false, message = "Clause not found for this run." });

        if (string.IsNullOrWhiteSpace(body.FinalStatus))
        {
            NdClauseStatusResolver.ClearManual(point);
            await db.SaveChangesAsync(ct);
            await NdClauseStatusResolver.RecomputeAsync(db, [pointId], ct);
        }
        else
        {
            var status = ClauseStatuses.Normalize(body.FinalStatus);
            if (status == null)
                return BadRequest(new { success = false, message = "Unknown compliance status." });

            NdClauseStatusResolver.ApplyManual(point, status);
            await db.SaveChangesAsync(ct);
        }

        return Ok(new
        {
            success = true,
            data = new
            {
                finalStatus = point.FinalStatus,
                finalStatusSource = point.FinalStatusSource,
                aiFinalStatus = point.AiFinalStatus,
            },
        });
    }

    [HttpPut("{runId:guid}/action-plan/{pointId:guid}")]
    public async Task<IActionResult> UpdateActionPlan(
        Guid runId,
        Guid pointId,
        [FromBody] UpdateActionPlanRequest body,
        CancellationToken ct)
    {
        var (profile, error) = await RequireAuthAsync(db, jwt, ct, "super_admin", "maker");
        if (error != null) return error;

        var run = await db.NdAnalysisRuns.FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run == null) return NotFound();
        if (profile!.Role == "maker" && run.CreatedBy != profile.Id)
            return StatusCode(403);
        if (run.Status is "submitted_for_review" or "checker_approved" or "reviewer_approved")
            return BadRequest(new { success = false, message = "Cannot edit while in review." });

        var point = await db.NdAnalysisPoints.FirstOrDefaultAsync(p => p.Id == pointId && p.AnalysisRunId == runId, ct);
        if (point == null) return NotFound();

        var prev = await db.NdActionPlanHistories
            .Where(h => h.AnalysisPointId == pointId && h.IsCurrent)
            .ToListAsync(ct);
        foreach (var h in prev) h.IsCurrent = false;

        var maxVersion = await db.NdActionPlanHistories
            .Where(h => h.AnalysisPointId == pointId)
            .MaxAsync(h => (int?)h.VersionNumber, ct) ?? 0;

        string changeType;
        int? revertedTo = null;
        if (body.RevertToVersion.HasValue)
        {
            changeType = "maker_reverted_to_version";
            revertedTo = body.RevertToVersion;
        }
        else
        {
            changeType = "maker_edit";
        }

        point.FinalActionPlan = body.Content;
        point.UpdatedAt = DateTimeOffset.UtcNow;

        db.NdActionPlanHistories.Add(new NdActionPlanHistory
        {
            AnalysisPointId = pointId,
            ActionPlanContent = body.Content,
            VersionNumber = maxVersion + 1,
            ChangeType = changeType,
            RevertedToVersion = revertedTo,
            ChangedBy = profile.Id,
            IsCurrent = true,
        });

        await db.SaveChangesAsync(ct);
        return Ok(new { success = true });
    }

    [HttpGet("{runId:guid}/action-plan-history/{pointId:guid}")]
    public async Task<IActionResult> ActionPlanHistory(Guid runId, Guid pointId, CancellationToken ct)
    {
        var (_, error) = await RequireAuthAsync(db, jwt, ct,
            "super_admin", "maker", "checker", "reviewer");
        if (error != null) return error;

        var history = await db.NdActionPlanHistories.AsNoTracking()
            .Where(h => h.AnalysisPointId == pointId)
            .OrderByDescending(h => h.VersionNumber)
            .ToListAsync(ct);

        var changerIds = history.Where(h => h.ChangedBy.HasValue).Select(h => h.ChangedBy!.Value).Distinct().ToList();
        var changers = await db.NdProfiles.AsNoTracking()
            .Where(p => changerIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.FullName, ct);

        return Ok(new
        {
            success = true,
            data = history.Select(h => new
            {
                h.Id,
                h.VersionNumber,
                h.ActionPlanContent,
                h.ChangeType,
                h.RevertedToVersion,
                h.IsCurrent,
                h.CreatedAt,
                changedByName = h.ChangedBy.HasValue && changers.TryGetValue(h.ChangedBy.Value, out var n) ? n : null,
            }),
        });
    }
}
