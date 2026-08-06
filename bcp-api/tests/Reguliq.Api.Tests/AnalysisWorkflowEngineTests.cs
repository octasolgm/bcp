using Reguliq.Api.Services.NewDashboard;
using Xunit;

namespace Reguliq.Api.Tests;

public class AnalysisWorkflowEngineTests
{
    [Theory]
    [InlineData("regul_pipeline_full", "regul_pipeline_full")]
    [InlineData("REGUL_PIPELINE_FULL", "regul_pipeline_full")]
    [InlineData("regul_pipeline", "regul_pipeline")]
    [InlineData(null, "bcp_landing")]
    [InlineData("", "bcp_landing")]
    [InlineData("bcp_landing", "bcp_landing")]
    public void ResolveForCreate_maps_workflow_engine(string? input, string expected) =>
        Assert.Equal(expected, AnalysisWorkflowEngine.ResolveForCreate(input));
}
