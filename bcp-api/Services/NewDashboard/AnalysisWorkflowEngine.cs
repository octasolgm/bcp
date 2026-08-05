namespace Reguliq.Api.Services.NewDashboard;

/// <summary>Identifies which analysis engine processes a run after setup.</summary>
public static class AnalysisWorkflowEngine
{
    public const string BcpLanding = "bcp_landing";

    /// <summary>V3 — Regul forward/reverse/qualitative with ≤50-page full manual else keyword retrieval.</summary>
    public const string RegulPipeline = "regul_pipeline";

    /// <summary>V4 — Regul forward-only with full markdown for all internal files (no retrieval cap).</summary>
    public const string RegulPipelineFull = "regul_pipeline_full";

    public static bool IsRegulPipeline(string? raw) =>
        string.Equals(raw?.Trim(), RegulPipeline, StringComparison.OrdinalIgnoreCase);

    public static bool IsRegulPipelineFull(string? raw) =>
        string.Equals(raw?.Trim(), RegulPipelineFull, StringComparison.OrdinalIgnoreCase);

    public static bool IsRegulFamily(string? raw) =>
        IsRegulPipeline(raw) || IsRegulPipelineFull(raw);
}
