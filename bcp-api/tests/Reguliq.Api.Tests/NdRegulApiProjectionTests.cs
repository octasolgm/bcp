using Reguliq.Api.Data.NewDashboard.Entities;
using Reguliq.Api.Infrastructure.NewDashboard;
using Reguliq.Api.Services.NewDashboard;
using Xunit;

namespace Reguliq.Api.Tests;

public class NdRegulApiProjectionTests
{
    [Fact]
    public void MapPoint_RegulRun_uses_regul_forward_field_names()
    {
        var point = new NdAnalysisPoint
        {
            Id = Guid.NewGuid(),
            RegulationPointId = Guid.NewGuid(),
            PointSnapshot = "{}",
            LandingAiStatus = "failed",
            LandingAiError = "LLM credit exhausted",
            GoogleAiStatus = "pending",
            DualVerifyStatus = "pending",
        };

        var mapped = NdRegulApiProjection.MapPoint(point, AnalysisWorkflowEngine.RegulPipeline);
        var json = System.Text.Json.JsonSerializer.Serialize(mapped);
        Assert.Contains("regulForwardStatus", json);
        Assert.Contains("regulForwardError", json);
        Assert.Contains("LLM credit exhausted", json);
    }

    [Fact]
    public void MapRunPoll_RegulRun_includes_clause_and_reverse_counts()
    {
        var run = new NdAnalysisRun
        {
            Id = Guid.NewGuid(),
            WorkflowEngine = AnalysisWorkflowEngine.RegulPipeline,
            RegulPipelinePhase = "reverse",
            TotalPointsCount = 3,
            LandingAiCompletedCount = 2,
            DualVerifyFailedCount = 1,
            Status = "running",
        };

        var mapped = NdRegulApiProjection.MapRunPoll(run, 522, 68, 67);
        var json = System.Text.Json.JsonSerializer.Serialize(mapped);
        Assert.Contains("regulClauseTotal", json);
        Assert.Contains("regulClauseFailed", json);
        Assert.Contains("regulReverseSectionTotal", json);
        Assert.Contains("regulReverseSectionFailed", json);
    }
}
