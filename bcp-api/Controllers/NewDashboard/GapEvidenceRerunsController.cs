using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Data.NewDashboard.Entities;
using Reguliq.Api.Infrastructure.NewDashboard;
using Reguliq.Api.Services.NewDashboard;
using Reguliq.Api.Services.NewDashboard.Ai;
using Reguliq.Api.Services.NewDashboard.Demo;

namespace Reguliq.Api.Controllers.NewDashboard;

/// <summary>
/// Re-check open gaps against uploaded gap evidence — for the whole report ("Rerun all gaps") or a
/// single clause / gap. Starts a tracked job and exposes its progress for the report page to poll.
/// </summary>
[ApiController]
[Route("nd/results/{runId:guid}/gap-evidence-reruns")]
public class GapEvidenceRerunsController(
    AppDbContext db,
    SupabaseJwtValidator jwt,
    NdGapEvidenceRerunService reruns,
    NdDemoUserDirectory demoDirectory,
    NdDemoInterceptionService demoIntercept,
    NdAiCreditService aiCredits) : NdControllerBase
{
    public record GapRequest(int Index, string? Text);

    public record PointRequest(Guid PointId, int? GapIndex, List<GapRequest>? Gaps);

    public record StartRequest(string? Scope, List<PointRequest>? Points);

    private static readonly string[] Roles = ["super_admin", "maker", "checker", "reviewer"];

    [HttpPost]
    public async Task<IActionResult> Start(Guid runId, [FromBody] StartRequest? body, CancellationToken ct)
    {
        var (profile, user, error) = await RequireAuthWithUserAsync(db, jwt, ct, Roles);
        if (error != null) return error;

        var run = await db.NdAnalysisRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run == null || run.Status == "deleted")
            return NotFound(new { success = false, message = "Analysis run not found." });
        if (profile.Role == "maker" && run.CreatedBy != profile.Id) return StatusCode(403);

        var requested = (body?.Points ?? [])
            .Select(p => new NdGapEvidenceRerunService.PointInput(
                p.PointId,
                p.GapIndex is > 0 ? p.GapIndex : null,
                p.Gaps?.Select(g => new NdGapEvidenceRerunService.GapInput(g.Index, g.Text)).ToList()))
            .ToList();

        // Demo accounts keep their fixed simulation — no parsing, no AI.
        var demoCtx = await NdDemoIsolationContext.ResolveAsync(demoDirectory, user, ct);
        var demoOwned = run.CreatedBy != null && await demoDirectory.IsDemoProfileAsync(run.CreatedBy.Value, ct);
        if (demoCtx.ViewerIsDemo || demoOwned)
        {
            var singlePoint = requested.Count == 1 ? requested[0].PointId : (Guid?)null;
            var label = await LatestEvidenceLabelAsync(runId, singlePoint, ct);
            var updated = await demoIntercept.SimulateEvidenceRerunAsync(runId, singlePoint, label, profile.Id, ct);
            return Ok(new
            {
                success = true,
                message = updated == 0 ? "No open gaps to re-run" : $"Re-ran {updated} gap(s) against \"{label}\"",
                data = new { demo = true, updated },
            });
        }

        if (!AnalysisWorkflowEngine.IsRegulFamily(run.WorkflowEngine))
            return BadRequest(new
            {
                success = false,
                code = "unsupported_engine",
                message = "Evidence re-checks are available for analyses run on the Regul workflow.",
            });

        var workspaceId = WorkspaceScope.CurrentWorkspaceId ?? WorkspaceScope.DefaultWorkspaceId;
        if ((await aiCredits.GetSummaryAsync(workspaceId, ct)).IsExhausted)
            return StatusCode(402, new
            {
                success = false,
                code = "ai_credits_exhausted",
                message = "This workspace has used all of its AI credits. Ask your administrator to add credits, then try again.",
            });

        if (requested.Count == 0)
        {
            var openIds = await db.NdAnalysisPoints.AsNoTracking()
                .Where(p => p.AnalysisRunId == runId
                    && (p.FinalStatus == ClauseStatuses.NonCompliant || p.FinalStatus == ClauseStatuses.Partial))
                .Select(p => p.Id)
                .ToListAsync(ct);
            requested = openIds.Select(id => new NdGapEvidenceRerunService.PointInput(id, null, null)).ToList();
        }
        if (requested.Count == 0)
            return BadRequest(new { success = false, message = "No open gaps to re-check on this report." });

        var scope = string.Equals(body?.Scope, GapEvidenceRerunScopes.Clause, StringComparison.OrdinalIgnoreCase)
            ? GapEvidenceRerunScopes.Clause
            : GapEvidenceRerunScopes.Report;
        var result = await reruns.CreateAsync(run, requested, scope, profile.Id, ct);
        if (result.Rerun == null)
            return BadRequest(new { success = false, message = result.Error ?? "Could not start the re-check." });

        if (!result.AlreadyRunning)
            reruns.StartInBackground(result.Rerun.Id, run.TenantId, run.Id, profile.Id);

        return Ok(new
        {
            success = true,
            message = result.AlreadyRunning
                ? "A re-check is already running for this report — showing its progress."
                : $"Re-checking {result.Rerun.TotalPoints} clause(s) against the uploaded evidence.",
            alreadyRunning = result.AlreadyRunning,
            data = await reruns.BuildRerunDtoAsync(result.Rerun, ct),
        });
    }

    [HttpGet("latest")]
    public async Task<IActionResult> Latest(Guid runId, CancellationToken ct)
    {
        var access = await AuthorizeRunAsync(runId, ct);
        if (access != null) return access;

        var latest = await reruns.LatestAsync(runId, ct);
        return Ok(new { success = true, data = latest == null ? null : await reruns.BuildRerunDtoAsync(latest, ct) });
    }

    [HttpGet("{rerunId:guid}")]
    public async Task<IActionResult> Get(Guid runId, Guid rerunId, CancellationToken ct)
    {
        var access = await AuthorizeRunAsync(runId, ct);
        if (access != null) return access;

        var rerun = await db.NdGapEvidenceReruns.FirstOrDefaultAsync(r => r.Id == rerunId && r.AnalysisRunId == runId, ct);
        if (rerun == null) return NotFound(new { success = false, message = "Re-check not found." });
        await reruns.MarkInterruptedIfStaleAsync(rerun, ct);
        return Ok(new { success = true, data = await reruns.BuildRerunDtoAsync(rerun, ct) });
    }

    private async Task<IActionResult?> AuthorizeRunAsync(Guid runId, CancellationToken ct)
    {
        var (profile, _, error) = await RequireAuthWithUserAsync(db, jwt, ct, Roles);
        if (error != null) return error;
        var run = await db.NdAnalysisRuns.AsNoTracking()
            .Where(r => r.Id == runId)
            .Select(r => new { r.CreatedBy })
            .FirstOrDefaultAsync(ct);
        if (run == null) return NotFound(new { success = false, message = "Analysis run not found." });
        if (profile.Role == "maker" && run.CreatedBy != profile.Id) return StatusCode(403);
        return null;
    }

    private async Task<string> LatestEvidenceLabelAsync(Guid runId, Guid? pointId, CancellationToken ct)
    {
        var name = await db.NdAnalysisPointAttachments.AsNoTracking()
            .Where(a => db.NdAnalysisPoints.Any(p => p.Id == a.AnalysisPointId && p.AnalysisRunId == runId)
                && (pointId == null || a.AnalysisPointId == pointId))
            .OrderByDescending(a => a.CreatedAt)
            .Select(a => a.FileName)
            .FirstOrDefaultAsync(ct);
        return string.IsNullOrWhiteSpace(name) ? "uploaded evidence document" : name;
    }
}
