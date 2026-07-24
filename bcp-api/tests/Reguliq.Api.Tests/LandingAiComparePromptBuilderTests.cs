using Reguliq.Api.Models;
using Reguliq.Api.Services.LandingAi;
using Xunit;

namespace Reguliq.Api.Tests;

public class LandingAiComparePromptBuilderTests
{
    private static readonly GovPoint SamplePoint = new(
        "3.2",
        "Confidentiality",
        "LFIs must keep STR information confidential.",
        "3");

    [Fact]
    public void Build_V1_PreservesOriginalTfsWording()
    {
        var prompt = LandingAiComparePromptBuilder.Build(
            SamplePoint,
            "markdown body",
            "policy.pdf",
            ComparePromptVersion.V1);

        Assert.Contains("CBUAE and TFS", prompt);
        Assert.DoesNotContain("compare-prompt-v2", prompt);
    }

    [Fact]
    public void Build_V2_UsesGenericFrameworkAndMultiDocCitationRules()
    {
        var prompt = LandingAiComparePromptBuilder.Build(
            SamplePoint,
            [
                ("implementation.pdf", "doc one"),
                ("branch-manual.pdf", "doc two"),
            ],
            ComparePromptVersion.V2);

        Assert.DoesNotContain("TFS frameworks", prompt);
        Assert.Contains("ALL attached internal process documents", prompt);
        Assert.Contains("one line per source", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fulfilled_clauses", prompt);
        Assert.Contains("reference_pdf", prompt);
        Assert.Contains("implementation.pdf", prompt);
        Assert.Contains("branch-manual.pdf", prompt);
    }

    [Fact]
    public void ToCacheKey_SeparatesVersions()
    {
        Assert.Equal("v1", ComparePromptVersion.V1.ToCacheKey(1));
        Assert.Equal("v2", ComparePromptVersion.V2.ToCacheKey(1));
        Assert.Equal("v2-multi", ComparePromptVersion.V2.ToCacheKey(2));
    }
}
