namespace Reguliq.Api.Services.NewDashboard;

/// <summary>Identifies which analysis engine processes a run after setup.</summary>
public static class AnalysisWorkflowEngine
{
    public const string BcpLanding = "bcp_landing";

    /// <summary>V3 — Regul forward/reverse/qualitative with ≤50-page full manual else keyword retrieval.</summary>
    public const string RegulPipeline = "regul_pipeline";

    /// <summary>V4 — Regul forward-only: always full parsed markdown per internal file (no page/section limit).</summary>
    public const string RegulPipelineFull = "regul_pipeline_full";

    /// <summary>V5 — Regul forward-only, same as V4, plus Step 1/4 dictionary+embedding retrieval preview.</summary>
    public const string RegulPipelineHybrid = "regul_pipeline_hybrid_v5";

    public static bool IsRegulPipeline(string? raw) =>
        string.Equals(raw?.Trim(), RegulPipeline, StringComparison.OrdinalIgnoreCase);

    public static bool IsRegulPipelineFull(string? raw) =>
        string.Equals(raw?.Trim(), RegulPipelineFull, StringComparison.OrdinalIgnoreCase);

    public static bool IsRegulPipelineHybrid(string? raw) =>
        string.Equals(raw?.Trim(), RegulPipelineHybrid, StringComparison.OrdinalIgnoreCase);

    public static bool IsRegulFamily(string? raw) =>
        IsRegulPipeline(raw) || IsRegulPipelineFull(raw) || IsRegulPipelineHybrid(raw);

    /// <summary>V4 and V5 both run forward-only against full parsed internal markdown.</summary>
    public static bool IsForwardOnlyFullMarkdown(string? raw) =>
        IsRegulPipelineFull(raw) || IsRegulPipelineHybrid(raw);

    /// <summary>Maps API create-run payload to a stored workflow engine id.</summary>
    public static string ResolveForCreate(string? raw)
    {
        if (IsRegulPipelineHybrid(raw))
            return RegulPipelineHybrid;
        if (IsRegulPipelineFull(raw))
            return RegulPipelineFull;
        if (IsRegulPipeline(raw))
            return RegulPipeline;
        return BcpLanding;
    }
}
