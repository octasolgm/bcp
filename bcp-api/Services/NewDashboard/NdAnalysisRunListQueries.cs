using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data.NewDashboard.Entities;

namespace Reguliq.Api.Services.NewDashboard;

/// <summary>
/// Slim list projection for analysis_runs — skips large JSONB columns
/// (selected_points_snapshot, selected_*_doc_ids) that slow dashboard list queries.
/// </summary>
public static class NdAnalysisRunListQueries
{
    public static IQueryable<NdAnalysisRun> SelectListColumns(this IQueryable<NdAnalysisRun> q) =>
        q.Select(r => new NdAnalysisRun
        {
            Id = r.Id,
            Name = r.Name,
            WorkflowEngine = r.WorkflowEngine,
            RegulPipelinePhase = r.RegulPipelinePhase,
            RegulLlmProvider = r.RegulLlmProvider,
            RegulLlmModel = r.RegulLlmModel,
            Status = r.Status,
            StatusBeforeDelete = r.StatusBeforeDelete,
            DeletedAt = r.DeletedAt,
            TotalPointsCount = r.TotalPointsCount,
            ProcessedPointsCount = r.ProcessedPointsCount,
            DualVerifyFailedCount = r.DualVerifyFailedCount,
            DepartmentId = r.DepartmentId,
            CreatedBy = r.CreatedBy,
            CreatedAt = r.CreatedAt,
            UpdatedAt = r.UpdatedAt,
            SubmittedToCheckerAt = r.SubmittedToCheckerAt,
            SubmittedToReviewerAt = r.SubmittedToReviewerAt,
        });
}
