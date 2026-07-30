using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Data.NewDashboard.Entities;
using Reguliq.Api.Infrastructure.NewDashboard;
using Reguliq.Api.Services.Llm;

namespace Reguliq.Api.Services.NewDashboard;

/// <summary>
/// Regul.ai-style pipeline: forward judgment → reverse coverage → optional qualitative.
/// Stores results in regul_* tables; analysis_runs remains the umbrella record.
/// </summary>
public class NdRegulAnalysisProcessor(
    AppDbContext db,
    RegulWorkflowLlmSettingsService llmSettings,
    NdAnalysisRunCancellationTracker runCancellation,
    ILogger<NdRegulAnalysisProcessor> logger)
{
    private static readonly JsonSerializerOptions SnapshotJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public async Task ProcessRunAsync(Guid runId, CancellationToken ct)
    {
        var run = await db.NdAnalysisRuns
            .Include(r => r.Points)
            .FirstOrDefaultAsync(r => r.Id == runId, ct)
            ?? throw new InvalidOperationException("Analysis run not found.");

        if (!AnalysisWorkflowEngine.IsRegulPipeline(run.WorkflowEngine))
            throw new InvalidOperationException("Run is not a Regul workflow analysis.");

        if (runCancellation.IsStopRequested(runId))
        {
            await MarkCancelledAsync(run, ct);
            return;
        }

        var llm = await llmSettings.GetConfigAsync(ct);
        run.RegulLlmProvider = llm.Provider;
        run.RegulLlmModel = llm.Model;
        run.Status = "running";
        run.RegulPipelinePhase = "forward";
        run.RegulPipelineError = null;
        run.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        try
        {
            await EnsureForwardFindingsAsync(run, ct);
            await RunForwardPhaseAsync(run, ct);
            if (runCancellation.IsStopRequested(runId))
            {
                await MarkCancelledAsync(run, ct);
                return;
            }

            run.RegulPipelinePhase = "reverse";
            run.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            await RunReversePhaseAsync(run, ct);

            if (run.EnableQualitative)
            {
                if (runCancellation.IsStopRequested(runId))
                {
                    await MarkCancelledAsync(run, ct);
                    return;
                }

                run.RegulPipelinePhase = "qualitative";
                run.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
                await RunQualitativePhaseAsync(run, ct);
            }

            run.RegulPipelinePhase = "done";
            run.Status = "completed";
            run.ProcessedPointsCount = run.TotalPointsCount;
            run.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        catch (OperationCanceledException)
        {
            await MarkCancelledAsync(run, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Regul pipeline failed for run {RunId}", runId);
            run.Status = "failed";
            run.RegulPipelineError = ex.Message;
            run.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
        }
    }

    private async Task EnsureForwardFindingsAsync(NdAnalysisRun run, CancellationToken ct)
    {
        var existing = await db.NdRegulForwardFindings
            .Where(f => f.AnalysisRunId == run.Id)
            .Select(f => f.AnalysisPointId)
            .ToListAsync(ct);

        var existingSet = existing.Where(id => id.HasValue).Select(id => id!.Value).ToHashSet();
        foreach (var point in run.Points)
        {
            if (existingSet.Contains(point.Id)) continue;

            var (clauseNo, clauseText) = ParseClauseFromSnapshot(point.PointSnapshot);
            db.NdRegulForwardFindings.Add(new NdRegulForwardFinding
            {
                AnalysisRunId = run.Id,
                AnalysisPointId = point.Id,
                ClauseNo = clauseNo,
                ClauseText = clauseText,
                Status = "pending",
            });
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task RunForwardPhaseAsync(NdAnalysisRun run, CancellationToken ct)
    {
        // Phase 2 implementation: port Regul.ai judge_clause() + verify_quotes().
        var pending = await db.NdRegulForwardFindings
            .Where(f => f.AnalysisRunId == run.Id && f.Status == "pending")
            .ToListAsync(ct);

        foreach (var finding in pending)
        {
            if (runCancellation.IsStopRequested(run.Id)) throw new OperationCanceledException();
            finding.Status = "skipped";
            finding.ErrorMessage = "Forward judgment LLM call not yet implemented.";
            finding.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "Regul forward phase placeholder completed for run {RunId} ({Count} clauses)",
            run.Id,
            pending.Count);
    }

    private Task RunReversePhaseAsync(NdAnalysisRun run, CancellationToken ct)
    {
        // Phase 3 implementation: extract_internal_sections + reverse_map_section.
        logger.LogInformation("Regul reverse phase placeholder for run {RunId}", run.Id);
        return Task.CompletedTask;
    }

    private Task RunQualitativePhaseAsync(NdAnalysisRun run, CancellationToken ct)
    {
        // Phase 4 implementation: run_qualitative_assessment.
        var row = db.NdRegulQualitativeAssessments
            .FirstOrDefault(q => q.AnalysisRunId == run.Id);
        if (row == null)
        {
            row = new NdRegulQualitativeAssessment { AnalysisRunId = run.Id };
            db.NdRegulQualitativeAssessments.Add(row);
        }

        row.Status = "skipped";
        row.ErrorMessage = "Qualitative assessment LLM call not yet implemented.";
        row.UpdatedAt = DateTimeOffset.UtcNow;
        return db.SaveChangesAsync(ct);
    }

    private async Task MarkCancelledAsync(NdAnalysisRun run, CancellationToken ct)
    {
        run.Status = "cancelled";
        run.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private static (string ClauseNo, string ClauseText) ParseClauseFromSnapshot(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return ("", "");
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            var no = root.TryGetProperty("pointNumber", out var pn) ? pn.GetString() ?? ""
                : root.TryGetProperty("point_number", out var pn2) ? pn2.GetString() ?? "" : "";
            var text = root.TryGetProperty("pointText", out var pt) ? pt.GetString() ?? ""
                : root.TryGetProperty("point_text", out var pt2) ? pt2.GetString() ?? ""
                : root.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
            return (no, text);
        }
        catch
        {
            return ("", "");
        }
    }
}
