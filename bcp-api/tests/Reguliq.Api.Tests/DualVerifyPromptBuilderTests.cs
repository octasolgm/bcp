using Reguliq.Api.Models;
using Reguliq.Api.Services;
using Reguliq.Api.Services.LandingAi;
using Xunit;

namespace Reguliq.Api.Tests;

public class DualVerifyPromptBuilderTests
{
    [Fact]
    public void Build_MentionsInternalPdfAndGovPoint()
    {
        var point = new GovPoint("2.1.1", "Customer due diligence", "Banks must verify customers.", "2.1");
        var prompt = DualVerifyPromptBuilder.Build(point, "Pass 1 landing output");

        Assert.Contains("attached internal PDF", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("2.1.1", prompt);
        Assert.Contains("Customer due diligence", prompt);
        Assert.Contains("Pass 1 landing output", prompt);
    }

    [Fact]
    public void Build_V2_IncludesStructuredOutputFormat()
    {
        var point = new GovPoint("2.1.1", "Customer due diligence", "Banks must verify customers.", "2.1");
        var prompt = DualVerifyPromptBuilder.Build(
            point,
            "Pass 1 landing output",
            markdownSupplement: "internal md",
            attachedFileNames: ["a.pdf", "b.pdf"],
            version: ComparePromptVersion.V2);

        Assert.Contains("Pass 2 rules (V2)", prompt);
        Assert.Contains("Output/Response :", prompt);
        Assert.Contains("Fulfilled clauses :", prompt);
        Assert.Contains("a.pdf", prompt);
        Assert.Contains("b.pdf", prompt);
    }

    [Fact]
    public void Build_V3_IncludesRegulPass2Rules()
    {
        var point = new GovPoint("2.1.1", "Customer due diligence", "Banks must verify customers.", "2.1");
        var prompt = DualVerifyPromptBuilder.Build(
            point,
            "Pass 1 landing output",
            version: ComparePromptVersion.V3);

        Assert.Contains("Pass 2 rules (V3", prompt);
        Assert.Contains("Document-perspective", prompt);
        Assert.Contains("Element-level checking", prompt);
        Assert.Contains("Output/Response :", prompt);
    }
}
