using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Data.NewDashboard.Entities;

namespace Reguliq.Api.Services.NewDashboard;

/// <summary>Persists Regul-specific stop state (phase, forward findings, points).</summary>
public static class NdRegulRunStopHelper
{
    public static async Task ApplyAsync(AppDbContext db, NdAnalysisRun run, CancellationToken ct)
    {
        if (!AnalysisWorkflowEngine.IsRegulFamily(run.WorkflowEngine))
            return;

        run.RegulPipelinePhase = "done";
        run.UpdatedAt = DateTimeOffset.UtcNow;

        foreach (var point in run.Points)
        {
            if (point.LandingAiStatus is "pending" or "running")
            {
                point.LandingAiStatus = "cancelled";
                point.LandingAiError = "Stopped by user";
                point.DualVerifyStatus = "skipped";
                point.UpdatedAt = DateTimeOffset.UtcNow;
            }
            else if (point.DualVerifyStatus is "pending" or "running")
            {
                point.DualVerifyStatus = "cancelled";
                if (point.GoogleAiStatus is "running" or "pending")
                    point.GoogleAiStatus = "cancelled";
                point.UpdatedAt = DateTimeOffset.UtcNow;
            }
        }

        var pendingFindings = await db.NdRegulForwardFindings
            .Where(f => f.AnalysisRunId == run.Id && f.Status == "pending")
            .ToListAsync(ct);
        foreach (var finding in pendingFindings)
        {
            finding.Status = "cancelled";
            finding.ErrorMessage = "Stopped by user";
            finding.UpdatedAt = DateTimeOffset.UtcNow;
        }
    }
}
