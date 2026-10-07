using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Data.NewDashboard.Entities;
using Reguliq.Api.Infrastructure.NewDashboard;
using Reguliq.Api.Services.NewDashboard;

namespace Reguliq.Api.Controllers.NewDashboard;

/// <summary>
/// Clause evals: saved reference results for single clauses (versioned, one current version per clause), and
/// the comparison of an analysis's clauses against them. Comparison is local and rule-based, no AI call.
/// Platform super admins only, like prompts and the AI call log the evals are built from.
/// </summary>
[ApiController]
[Route("nd/evals")]
public class EvalsController(
    AppDbContext db,
    SupabaseJwtValidator jwt,
    NdAnalysisEvalService evals) : NdControllerBase
{
    private static readonly Dictionary<Guid, string> NoNames = [];

    public sealed record SaveClausesRequest(Guid RunId, List<string> ClauseNos, string? Notes, bool? SetCurrent);

    public sealed record CompareRequest(Guid RunId, List<string> ClauseNos, List<Guid> EvalIds);

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var (_, error) = await RequirePlatformAdminAsync(db, jwt, ct);
        if (error != null) return error;

        var rows = await db.NdClauseEvals.AsNoTracking().ToListAsync(ct);
        var names = await CreatorNamesAsync(rows, ct);
        var ordered = rows
            .OrderBy(e => e.RegulationName ?? "", StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.ClauseNo, NdAnalysisEvalService.ClauseNoComparer.Instance)
            .ThenByDescending(e => e.VersionNumber);
        return Ok(new { success = true, data = ordered.Select(e => Summary(e, names)) });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var (_, error) = await RequirePlatformAdminAsync(db, jwt, ct);
        if (error != null) return error;

        var eval = await db.NdClauseEvals.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, ct);
        if (eval == null) return NotFound(new { success = false, message = "Eval not found." });
        var names = await CreatorNamesAsync([eval], ct);
        return Ok(new
        {
            success = true,
            data = new
            {
                eval = Summary(eval, names),
                promptVersions = NdAnalysisEvalService.ReadPromptVersions(eval),
                result = NdAnalysisEvalService.ReadClause(eval),
            },
        });
    }

    /// <summary>Saves the chosen clauses of an analysis as clause evals (new version each, current by default).</summary>
    [HttpPost]
    public async Task<IActionResult> Save([FromBody] SaveClausesRequest body, CancellationToken ct)
    {
        var (profile, error) = await RequirePlatformAdminAsync(db, jwt, ct);
        if (error != null) return error;
        if (body.ClauseNos is not { Count: > 0 })
            return BadRequest(new { success = false, message = "Choose at least one clause to save." });

        var run = await db.NdAnalysisRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == body.RunId, ct);
        if (run == null || run.Status == "deleted")
            return NotFound(new { success = false, message = "Analysis not found." });

        var (saved, skipped) = await evals.SaveClausesAsync(
            body.RunId, body.ClauseNos, body.Notes, body.SetCurrent ?? true, profile!.Id, ct);
        var message = saved.Count == 0
            ? "Nothing saved: the chosen clauses have no AI result yet."
            : $"Saved {string.Join(", ", saved.Select(s => $"{s.ClauseNo} v{s.VersionNumber}"))}"
                + (skipped.Count > 0 ? $"; skipped {string.Join(", ", skipped)} (no AI result)" : "") + ".";
        return Ok(new { success = saved.Count > 0, message, data = saved.Select(e => Summary(e, NoNames)) });
    }

    [HttpPost("{id:guid}/set-current")]
    public async Task<IActionResult> SetCurrent(Guid id, CancellationToken ct)
    {
        var (_, error) = await RequirePlatformAdminAsync(db, jwt, ct);
        if (error != null) return error;
        return await evals.SetCurrentAsync(id, ct)
            ? Ok(new { success = true })
            : NotFound(new { success = false, message = "Eval not found." });
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var (_, error) = await RequirePlatformAdminAsync(db, jwt, ct);
        if (error != null) return error;
        return await evals.DeleteAsync(id, ct)
            ? Ok(new { success = true })
            : NotFound(new { success = false, message = "Eval not found." });
    }

    /// <summary>An analysis's clauses (left column of the compare view) and the AI setup that produced them.</summary>
    [HttpGet("runs/{runId:guid}")]
    public async Task<IActionResult> RunClauses(Guid runId, CancellationToken ct)
    {
        var (_, error) = await RequirePlatformAdminAsync(db, jwt, ct);
        if (error != null) return error;

        var snap = await evals.SnapshotRunAsync(runId, ct);
        if (snap == null || snap.Run.Status == "deleted")
            return NotFound(new { success = false, message = "Analysis not found." });

        return Ok(new
        {
            success = true,
            data = new
            {
                setup = NdAnalysisEvalService.DescribeSetup(snap),
                clauses = snap.Clauses.Select(c => new
                {
                    c.ClauseNo,
                    c.ClauseTitle,
                    c.RegulationName,
                    clauseKey = NdAnalysisEvalService.ClauseKeyOf(c),
                    c.OverallStatus,
                    c.Confidence,
                    gapCount = c.Gaps.Count,
                    c.Model,
                    c.PipelineVersion,
                    promptVersions = NdAnalysisEvalService.DescribePromptVersions(c.PromptVersions),
                    c.FindingStatus,
                }),
            },
        });
    }

    /// <summary>
    /// Compares the chosen clauses of an analysis with the chosen clause evals. Pairs by clause identity
    /// (same regulation and clause number), falling back to the clause number alone. Local and rule-based:
    /// verdicts compared directly, gaps by word overlap. No AI call, no cost.
    /// </summary>
    [HttpPost("compare")]
    public async Task<IActionResult> Compare([FromBody] CompareRequest body, CancellationToken ct)
    {
        var (_, error) = await RequirePlatformAdminAsync(db, jwt, ct);
        if (error != null) return error;

        var snap = await evals.SnapshotRunAsync(body.RunId, ct);
        if (snap == null || snap.Run.Status == "deleted")
            return NotFound(new { success = false, message = "Analysis not found." });

        var wanted = (body.ClauseNos ?? []).Select(NdAnalysisEvalService.NumberKey).ToHashSet(StringComparer.Ordinal);
        var runClauses = snap.Clauses.Where(c => wanted.Contains(NdAnalysisEvalService.NumberKey(c.ClauseNo))).ToList();
        var evalIds = body.EvalIds ?? [];
        var evalRows = await db.NdClauseEvals.AsNoTracking().Where(e => evalIds.Contains(e.Id)).ToListAsync(ct);
        if (runClauses.Count == 0 && evalRows.Count == 0)
            return BadRequest(new { success = false, message = "Choose clauses on both sides to compare." });

        var pending = evalRows.ToList();
        var pairs = new List<NdAnalysisEvalService.ComparePair>();
        foreach (var r in runClauses)
        {
            var key = NdAnalysisEvalService.ClauseKeyOf(r);
            var match = pending.FirstOrDefault(e => e.ClauseKey == key)
                ?? pending.FirstOrDefault(e => NdAnalysisEvalService.NumberKey(e.ClauseNo) == NdAnalysisEvalService.NumberKey(r.ClauseNo));
            if (match != null) pending.Remove(match);
            pairs.Add(new NdAnalysisEvalService.ComparePair(
                match == null ? null : NdAnalysisEvalService.ReadClause(match), r, match?.Id, match?.VersionNumber));
        }
        foreach (var e in pending)
            pairs.Add(new NdAnalysisEvalService.ComparePair(NdAnalysisEvalService.ReadClause(e), null, e.Id, e.VersionNumber));

        var (summary, rows) = NdAnalysisEvalService.Compare(pairs);
        return Ok(new
        {
            success = true,
            data = new
            {
                method = "local",
                run = new { id = snap.Run.Id, name = snap.Run.Name, setup = NdAnalysisEvalService.DescribeSetup(snap) },
                summary,
                clauses = rows,
            },
        });
    }

    private async Task<Dictionary<Guid, string>> CreatorNamesAsync(IEnumerable<NdClauseEval> rows, CancellationToken ct)
    {
        var ids = rows.Where(r => r.CreatedBy.HasValue).Select(r => r.CreatedBy!.Value).Distinct().ToList();
        if (ids.Count == 0) return [];
        return await db.NdProfiles.AsNoTracking()
            .Where(p => ids.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.FullName, ct);
    }

    private static object Summary(NdClauseEval e, IReadOnlyDictionary<Guid, string> names)
    {
        var prompts = NdAnalysisEvalService.ReadPromptVersions(e);
        return new
        {
            id = e.Id,
            clauseNo = e.ClauseNo,
            clauseKey = e.ClauseKey,
            clauseTitle = e.ClauseTitle,
            regulationName = e.RegulationName,
            versionNumber = e.VersionNumber,
            isCurrent = e.IsCurrent,
            notes = e.Notes,
            sourceRunId = e.SourceRunId,
            sourceRunName = e.SourceRunName,
            llmModel = e.LlmModel,
            pipelineVersion = e.PipelineVersion,
            promptVersions = NdAnalysisEvalService.DescribePromptVersions(
                prompts.Select(p => new NdAnalysisEvalService.PromptVersionRef(p.PromptKey, p.VersionNumber, p.Label))),
            overallStatus = e.OverallStatus,
            gapCount = e.GapCount,
            createdAt = e.CreatedAt,
            createdByName = e.CreatedBy is Guid by && names.TryGetValue(by, out var n) ? n : null,
        };
    }
}
