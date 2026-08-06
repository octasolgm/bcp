using Reguliq.Api.Data.NewDashboard.Entities;
using Reguliq.Api.Infrastructure.NewDashboard;

namespace Reguliq.Api.Services.NewDashboard;

/// <summary>
/// Maps Regul pipeline DB columns (legacy landing_ai_* / dual_verify_* names) to clear API fields.
/// </summary>
public static class NdRegulApiProjection
{
    public static object MapRunSummary(NdAnalysisRun run) =>
        AnalysisWorkflowEngine.IsRegulFamily(run.WorkflowEngine)
            ? new
            {
                id = run.Id,
                name = run.Name,
                status = run.Status,
                workflowEngine = run.WorkflowEngine,
                regulPipelinePhase = run.RegulPipelinePhase,
                regulClauseTotal = run.TotalPointsCount,
                regulClauseCompleted = run.LandingAiCompletedCount,
                regulClauseFailed = run.DualVerifyFailedCount,
                regulLlmProvider = run.RegulLlmProvider,
                regulLlmModel = run.RegulLlmModel,
                departmentId = run.DepartmentId,
                createdBy = run.CreatedBy,
                createdAt = run.CreatedAt,
                submittedToCheckerAt = run.SubmittedToCheckerAt,
                // Legacy aliases — prefer regul* fields above for Regul runs.
                totalPointsCount = run.TotalPointsCount,
                processedPointsCount = run.ProcessedPointsCount,
                dualVerifyFailedCount = run.DualVerifyFailedCount,
            }
            : new
            {
                id = run.Id,
                name = run.Name,
                status = run.Status,
                totalPointsCount = run.TotalPointsCount,
                processedPointsCount = run.ProcessedPointsCount,
                dualVerifyFailedCount = run.DualVerifyFailedCount,
                departmentId = run.DepartmentId,
                createdBy = run.CreatedBy,
                createdAt = run.CreatedAt,
                submittedToCheckerAt = run.SubmittedToCheckerAt,
                workflowEngine = run.WorkflowEngine,
                regulPipelinePhase = run.RegulPipelinePhase,
            };

    public static object MapRunDetail(NdAnalysisRun run, string? creatorName) =>
        AnalysisWorkflowEngine.IsRegulFamily(run.WorkflowEngine)
            ? new
            {
                id = run.Id,
                name = run.Name,
                description = run.Description,
                status = run.Status,
                libraryId = run.LibraryId,
                selectedPointsSnapshot = run.SelectedPointsSnapshot,
                selectedInternalDocIds = run.SelectedInternalDocIds,
                selectedRegulationDocIds = run.SelectedRegulationDocIds,
                workflowEngine = run.WorkflowEngine,
                enableQualitative = run.EnableQualitative,
                regulPipelinePhase = run.RegulPipelinePhase,
                regulPipelineError = run.RegulPipelineError,
                regulClausesConfirmedAt = run.RegulClausesConfirmedAt,
                regulLlmProvider = run.RegulLlmProvider,
                regulLlmModel = run.RegulLlmModel,
                regulClauseTotal = run.TotalPointsCount,
                regulClauseCompleted = run.LandingAiCompletedCount,
                regulClauseFailed = run.DualVerifyFailedCount,
                departmentId = run.DepartmentId,
                createdBy = run.CreatedBy,
                createdByName = creatorName,
                createdAt = run.CreatedAt,
                // Legacy aliases
                totalPointsCount = run.TotalPointsCount,
                processedPointsCount = run.ProcessedPointsCount,
                landingAiCompletedCount = run.LandingAiCompletedCount,
                dualVerifyCompletedCount = run.DualVerifyCompletedCount,
                dualVerifyFailedCount = run.DualVerifyFailedCount,
            }
            : new
            {
                id = run.Id,
                name = run.Name,
                description = run.Description,
                status = run.Status,
                libraryId = run.LibraryId,
                selectedPointsSnapshot = run.SelectedPointsSnapshot,
                selectedInternalDocIds = run.SelectedInternalDocIds,
                selectedRegulationDocIds = run.SelectedRegulationDocIds,
                totalPointsCount = run.TotalPointsCount,
                processedPointsCount = run.ProcessedPointsCount,
                landingAiCompletedCount = run.LandingAiCompletedCount,
                dualVerifyCompletedCount = run.DualVerifyCompletedCount,
                dualVerifyFailedCount = run.DualVerifyFailedCount,
                departmentId = run.DepartmentId,
                createdBy = run.CreatedBy,
                createdByName = creatorName,
                createdAt = run.CreatedAt,
                workflowEngine = run.WorkflowEngine,
                enableQualitative = run.EnableQualitative,
                regulLlmProvider = run.RegulLlmProvider,
                regulLlmModel = run.RegulLlmModel,
                regulPipelinePhase = run.RegulPipelinePhase,
                regulPipelineError = run.RegulPipelineError,
                regulClausesConfirmedAt = run.RegulClausesConfirmedAt,
            };

    public static object MapRunPoll(
        NdAnalysisRun run,
        int? regulReverseSectionTotal,
        int? regulReverseSectionCompleted,
        int? regulReverseSectionFailed) =>
        AnalysisWorkflowEngine.IsRegulFamily(run.WorkflowEngine)
            ? new
            {
                id = run.Id,
                status = run.Status,
                workflowEngine = run.WorkflowEngine,
                regulPipelinePhase = run.RegulPipelinePhase,
                enableQualitative = run.EnableQualitative,
                regulClauseTotal = run.TotalPointsCount,
                regulClauseCompleted = run.LandingAiCompletedCount,
                regulClauseFailed = run.DualVerifyFailedCount,
                regulReverseSectionTotal,
                regulReverseSectionCompleted,
                regulReverseSectionFailed,
                regulLlmProvider = run.RegulLlmProvider,
                regulLlmModel = run.RegulLlmModel,
                // Legacy aliases
                totalPointsCount = run.TotalPointsCount,
                processedPointsCount = run.ProcessedPointsCount,
                landingAiCompletedCount = run.LandingAiCompletedCount,
                dualVerifyCompletedCount = run.DualVerifyCompletedCount,
                dualVerifyFailedCount = run.DualVerifyFailedCount,
            }
            : new
            {
                id = run.Id,
                status = run.Status,
                workflowEngine = run.WorkflowEngine,
                regulPipelinePhase = run.RegulPipelinePhase,
                enableQualitative = run.EnableQualitative,
                regulReverseSectionTotal,
                regulReverseSectionCompleted,
                totalPointsCount = run.TotalPointsCount,
                processedPointsCount = run.ProcessedPointsCount,
                landingAiCompletedCount = run.LandingAiCompletedCount,
                dualVerifyCompletedCount = run.DualVerifyCompletedCount,
                dualVerifyFailedCount = run.DualVerifyFailedCount,
            };

    public static object MapResumeRun(NdAnalysisRun run) =>
        AnalysisWorkflowEngine.IsRegulFamily(run.WorkflowEngine)
            ? new
            {
                id = run.Id,
                name = run.Name,
                status = run.Status,
                libraryId = run.LibraryId,
                selectedPointsSnapshot = run.SelectedPointsSnapshot,
                selectedInternalDocIds = run.SelectedInternalDocIds,
                selectedRegulationDocIds = run.SelectedRegulationDocIds,
                workflowEngine = run.WorkflowEngine,
                regulPipelinePhase = run.RegulPipelinePhase,
                enableQualitative = run.EnableQualitative,
                regulClauseTotal = run.TotalPointsCount,
                regulClauseCompleted = run.LandingAiCompletedCount,
                regulClauseFailed = run.DualVerifyFailedCount,
                // Legacy aliases
                totalPointsCount = run.TotalPointsCount,
                processedPointsCount = run.ProcessedPointsCount,
                landingAiCompletedCount = run.LandingAiCompletedCount,
                dualVerifyCompletedCount = run.DualVerifyCompletedCount,
                dualVerifyFailedCount = run.DualVerifyFailedCount,
            }
            : new
            {
                id = run.Id,
                name = run.Name,
                status = run.Status,
                libraryId = run.LibraryId,
                selectedPointsSnapshot = run.SelectedPointsSnapshot,
                selectedInternalDocIds = run.SelectedInternalDocIds,
                selectedRegulationDocIds = run.SelectedRegulationDocIds,
                totalPointsCount = run.TotalPointsCount,
                processedPointsCount = run.ProcessedPointsCount,
                landingAiCompletedCount = run.LandingAiCompletedCount,
                dualVerifyCompletedCount = run.DualVerifyCompletedCount,
                dualVerifyFailedCount = run.DualVerifyFailedCount,
                workflowEngine = run.WorkflowEngine,
                regulPipelinePhase = run.RegulPipelinePhase,
                enableQualitative = run.EnableQualitative,
            };

    public static object MapPoint(
        Guid id,
        Guid? regulationPointId,
        string? pointSnapshot,
        string? landingAiStatus,
        string? landingAiResult,
        string? landingAiError,
        string? googleAiStatus,
        string? googleAiResult,
        string? googleAiError,
        string? dualVerifyStatus,
        string? finalStatus,
        string? finalActionPlan = null,
        string? originalAiActionPlan = null,
        string? workflowEngine = null)
    {
        var isRegul = AnalysisWorkflowEngine.IsRegulFamily(workflowEngine);
        if (!isRegul)
        {
            return new
            {
                id,
                regulationPointId,
                pointSnapshot,
                landingAiStatus,
                landingAiResult,
                landingAiError,
                googleAiStatus,
                googleAiResult,
                googleAiError,
                dualVerifyStatus,
                finalStatus,
                finalActionPlan,
                originalAiActionPlan,
            };
        }

        return new
        {
            id,
            regulationPointId,
            pointSnapshot,
            regulForwardStatus = landingAiStatus,
            regulForwardResult = landingAiResult,
            regulForwardError = landingAiError,
            finalStatus,
            finalActionPlan,
            originalAiActionPlan,
            // Legacy aliases
            landingAiStatus,
            landingAiResult,
            landingAiError,
            googleAiStatus,
            googleAiResult,
            googleAiError,
            dualVerifyStatus,
        };
    }

    public static object MapPoint(NdAnalysisPoint p, string? workflowEngine = null, string? pointSnapshotOverride = null) =>
        MapPoint(
            p.Id,
            p.RegulationPointId,
            pointSnapshotOverride ?? p.PointSnapshot,
            p.LandingAiStatus,
            p.LandingAiResult,
            p.LandingAiError,
            p.GoogleAiStatus,
            p.GoogleAiResult,
            p.GoogleAiError,
            p.DualVerifyStatus,
            p.FinalStatus,
            p.FinalActionPlan,
            p.OriginalAiActionPlan,
            workflowEngine);

    public static object MapPollResponse(
        NdAnalysisRun run,
        IReadOnlyList<NdAnalysisPoint> points,
        int? regulReverseSectionTotal,
        int? regulReverseSectionCompleted,
        int? regulReverseSectionFailed,
        List<object>? regulReverseSections)
    {
        var node = System.Text.Json.JsonSerializer.SerializeToNode(
            MapRunPoll(run, regulReverseSectionTotal, regulReverseSectionCompleted, regulReverseSectionFailed)) as System.Text.Json.Nodes.JsonObject
            ?? new System.Text.Json.Nodes.JsonObject();

        node["points"] = System.Text.Json.JsonSerializer.SerializeToNode(
            points.Select(p => MapPoint(
                p.Id,
                p.RegulationPointId,
                p.PointSnapshot,
                p.LandingAiStatus,
                p.LandingAiResult,
                p.LandingAiError,
                p.GoogleAiStatus,
                p.GoogleAiResult,
                p.GoogleAiError,
                p.DualVerifyStatus,
                p.FinalStatus,
                workflowEngine: run.WorkflowEngine)));

        if (regulReverseSections != null)
            node["regulReverseSections"] = System.Text.Json.JsonSerializer.SerializeToNode(regulReverseSections);

        return node;
    }

    public static object MapResumeResponse(NdAnalysisRun run, IReadOnlyList<NdAnalysisPoint> points)
    {
        var node = System.Text.Json.JsonSerializer.SerializeToNode(MapResumeRun(run)) as System.Text.Json.Nodes.JsonObject
            ?? new System.Text.Json.Nodes.JsonObject();
        node["points"] = System.Text.Json.JsonSerializer.SerializeToNode(
            points.Select(p => MapPoint(p, run.WorkflowEngine)));
        return node;
    }
}
