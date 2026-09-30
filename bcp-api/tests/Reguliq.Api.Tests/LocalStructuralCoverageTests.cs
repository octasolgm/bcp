using Reguliq.Api.Services.LandingAi;
using Reguliq.Api.Services.LocalDocs;
using Xunit;

namespace Reguliq.Api.Tests;

public class LocalStructuralCoverageTests
{
    [Fact]
    public void Full_sections_cover_parse_tokens()
    {
        var md = $"{PolicyPageResolver.PageMarkerPrefix}1 -->\nBanks must maintain adequate liquidity reserves at all times.";
        var sections = new List<LocalSection>
        {
            new("1", "Banks must maintain adequate liquidity reserves at all times.", 1),
        };
        var report = LocalStructuralCoverage.Compute(md, sections);
        Assert.True(report.CoverageRatio >= 0.98);
    }

    [Fact]
    public void Missing_table_text_lowers_coverage()
    {
        var md = """
            Banks must report monthly.
            <table><tr><td>Schedule A detail line one</td><td>Schedule A detail line two</td></tr></table>
            End of policy.
            """;
        var sections = new List<LocalSection>
        {
            new("1", "Banks must report monthly.\nEnd of policy.", 1),
        };
        var report = LocalStructuralCoverage.Compute(md, sections);
        Assert.True(report.CoverageRatio < LocalStructuralCoverage.LowCoverageThreshold);
        Assert.NotNull(report.OrphanSnippet);
        Assert.Contains("schedule", report.OrphanSnippet!, StringComparison.OrdinalIgnoreCase);
    }
}
