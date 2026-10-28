using Reguliq.Api.Services.LandingAi;
using Xunit;

namespace Reguliq.Api.Tests;

public class PolicyExtractTextSanitizerTests
{
    [Fact]
    public void Clean_strips_landing_anchor_tags_and_page_footers()
    {
        var raw = """
            1. Introduction
            The international and regional impact of AML/CTF.
            <a id='6ba43595-c105-445c-88cd-4c33b7771376'></a>
            Page | 41
            <a id="0235ada9-5ca9-4586-bbf3-7ed984aff07d"></a>
            DFSA have adopted reforms.
            """;

        var clean = PolicyExtractTextSanitizer.Clean(raw);

        Assert.DoesNotContain("<a id", clean, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Page |", clean, StringComparison.Ordinal);
        Assert.Contains("international", clean, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DFSA have adopted reforms", clean, StringComparison.Ordinal);
    }
}
