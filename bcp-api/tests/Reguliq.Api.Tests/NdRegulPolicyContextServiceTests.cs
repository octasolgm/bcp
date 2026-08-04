using Reguliq.Api.Models;
using Reguliq.Api.Services.NewDashboard;
using Xunit;

namespace Reguliq.Api.Tests;

public class NdRegulPolicyContextServiceTests
{
    [Fact]
    public void BuildContextForClause_uses_full_manual_when_pages_le_50()
    {
        var bundle = NdRegulPolicyContextService.FromPayloads([
            new InternalDocPayload("h", "Manual.pdf", "annual sanctions review policy text", null),
        ]);

        Assert.True(bundle.TotalPages <= NdRegulPolicyContextService.FullManualMaxPages);
        var ctx = bundle.BuildContextForClause("sanctions review annually");
        Assert.Contains("=== DOCUMENT: Manual.pdf ===", ctx);
        Assert.Contains("annual sanctions review", ctx);
    }

    [Fact]
    public void BuildContextForClause_retrieves_keyword_chunks_when_pages_gt_50()
    {
        var longMarkdown = string.Join(
            "\n",
            Enumerable.Range(1, 60).Select(p =>
                $"<!-- Page {p} -->\nPage {p} content about topic {p % 7}."));
        longMarkdown += "\n<!-- Page 61 -->\nBeneficial ownership threshold is fifty percent for all customers.";

        var bundle = NdRegulPolicyContextService.FromPayloads([
            new InternalDocPayload("h", "BigManual.pdf", longMarkdown, null),
        ]);

        Assert.True(bundle.TotalPages > NdRegulPolicyContextService.FullManualMaxPages);
        var ctx = bundle.BuildContextForClause(
            "beneficial ownership fifty percent threshold requirement");
        Assert.Contains("fifty percent", ctx, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Page 1 content", ctx);
    }

    [Fact]
    public void FromInternalSections_builds_bundle_from_section_texts()
    {
        var sections = new[]
        {
            new Reguliq.Api.Data.NewDashboard.Entities.NdRegulInternalSection
            {
                SourceDoc = "Manual.pdf",
                SectionRef = "4.2",
                SectionText = "Annual sanctions review is required for all high-risk customers.",
                SourcePage = 12,
            },
            new Reguliq.Api.Data.NewDashboard.Entities.NdRegulInternalSection
            {
                SourceDoc = "Manual.pdf",
                SectionRef = "4.3",
                SectionText = "Beneficial ownership must be verified above fifty percent.",
                SourcePage = 13,
            },
        };

        var bundle = NdRegulPolicyContextService.FromInternalSections(sections);
        var ctx = bundle.BuildContextForClause("annual sanctions review fifty percent beneficial ownership");
        Assert.Contains("Annual sanctions review", ctx);
        Assert.Contains("fifty percent", ctx, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FromPayloads_splits_on_bcp_pdf_page_markers()
    {
        var markdown =
            "<!-- BCP_PDF_PAGE:5 -->\n" +
            "Section five content about wire transfers.\n" +
            "<!-- BCP_PDF_PAGE:6 -->\n" +
            "Section six content about beneficial ownership.";
        var bundle = NdRegulPolicyContextService.FromPayloads([
            new Reguliq.Api.Models.InternalDocPayload("h", "Manual.pdf", markdown, null),
        ]);

        Assert.Equal(2, bundle.Chunks.Count);
        Assert.Contains(bundle.Chunks, c => c.SourcePage == 5 && c.Text.Contains("wire transfers"));
        Assert.Contains(bundle.Chunks, c => c.SourcePage == 6 && c.Text.Contains("beneficial ownership"));
    }
}
