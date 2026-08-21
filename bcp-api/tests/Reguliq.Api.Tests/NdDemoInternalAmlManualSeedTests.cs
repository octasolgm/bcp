using Reguliq.Api.Services.LandingAi;
using Reguliq.Api.Services.NewDashboard.Demo;
using Xunit;

namespace Reguliq.Api.Tests;

public class NdDemoInternalAmlManualSeedTests
{
    [Fact]
    public void Seed_uses_official_refs_and_viewer_pages()
    {
        Assert.Equal(63, NdDemoInternalAmlManualSeed.TotalPages);
        Assert.Contains(NdDemoInternalAmlManualSeed.Sections, s => s.ClauseNo == "1" && s.SourcePage == 10);
        Assert.Contains(NdDemoInternalAmlManualSeed.Sections, s => s.ClauseNo == "7.4" && s.SourcePage == 27);
        Assert.Contains(NdDemoInternalAmlManualSeed.Sections, s => s.ClauseNo == "7.7-b" && s.SourcePage == 39);
        Assert.Contains(NdDemoInternalAmlManualSeed.Sections, s => s.ClauseNo == "7.7-d" && s.SourcePage == 41);
        Assert.Contains(NdDemoInternalAmlManualSeed.Sections, s => s.ClauseNo == "8" && s.SourcePage == 48);
        Assert.DoesNotContain(
            NdDemoInternalAmlManualSeed.Sections,
            s => PolicyClauseOfficialRefAligner.LooksInvented(s.ClauseNo));
    }

    [Fact]
    public void Seed_preview_is_extract_style_without_parse_tags()
    {
        var purpose = Assert.Single(NdDemoInternalAmlManualSeed.Sections, s => s.ClauseNo == "2");

        Assert.Equal(11, purpose.SourcePage);
        Assert.Contains("objectives of this document", purpose.ClauseText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("risk-based approach", purpose.ClauseText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<a id", purpose.ClauseText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Page |", purpose.ClauseText, StringComparison.Ordinal);
        Assert.DoesNotContain("3. Scope", purpose.ClauseText, StringComparison.Ordinal);

        Assert.All(
            NdDemoInternalAmlManualSeed.Sections,
            s =>
            {
                Assert.DoesNotContain("<a id", s.ClauseText, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("Page |", s.ClauseText, StringComparison.Ordinal);
            });
    }
}
