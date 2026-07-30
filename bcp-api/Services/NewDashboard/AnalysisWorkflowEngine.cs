namespace Reguliq.Api.Services.NewDashboard;

/// <summary>Identifies which analysis engine processes a run after setup.</summary>
public static class AnalysisWorkflowEngine
{
    public const string BcpLanding = "bcp_landing";
    public const string RegulPipeline = "regul_pipeline";

    public static bool IsRegulPipeline(string? raw) =>
        string.Equals(raw?.Trim(), RegulPipeline, StringComparison.OrdinalIgnoreCase);
}
