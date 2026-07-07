using Reguliq.Api.Models;
using Reguliq.Api.Services;
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
}
