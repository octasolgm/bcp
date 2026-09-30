using Reguliq.Api.Services.NewDashboard.CorrectedDocs;
using Xunit;

namespace Reguliq.Api.Tests;

public class NdFinalizeEmbedPromptTests
{
    [Fact]
    public void Prompt_includes_gap_action_and_policy_excerpts()
    {
        var target = new NdActionPlanEmbedTarget(
            Guid.NewGuid(),
            12,
            "risk assessment",
            "3.5",
            "Risk assessment",
            "Missing FATF predicate categories in the NRA weighting.",
            "Revise enterprise-wide risk assessment methodology to reflect…",
            "Compliance",
            DateTimeOffset.UtcNow,
            "Reviewer",
            "The manual does not weight NRA top threats.",
            ["The Bank shall assess ML/FT risk annually."]);

        var prompt = NdFinalizeEmbedContentService.BuildPrompt(target);
        Assert.Contains("Regulatory clause: 3.5", prompt);
        Assert.Contains("Missing FATF predicate categories", prompt);
        Assert.Contains("Revise enterprise-wide", prompt);
        Assert.Contains("assess ML/FT risk annually", prompt);
        Assert.Contains("Do NOT include operational instructions", prompt);
    }
}
