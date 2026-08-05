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

    [Fact]
    public void FromInternalSections_uses_pdf_page_count_not_section_count()
    {
        var sections = Enumerable.Range(1, 854).Select(i => new Reguliq.Api.Data.NewDashboard.Entities.NdRegulInternalSection
        {
            SourceDoc = "Manual.pdf",
            SectionRef = $"6.{i}",
            SectionText = $"Section {i} text.",
            SourcePage = i <= 63 ? i : 63,
        }).ToArray();

        var bundle = NdRegulPolicyContextService.FromInternalSections(sections);
        Assert.Equal(63, bundle.TotalPages);
    }

    [Fact]
    public void WithMarkdownFromPayloads_overrides_total_pages_from_pdf_markers()
    {
        var sections = new[]
        {
            new Reguliq.Api.Data.NewDashboard.Entities.NdRegulInternalSection
            {
                SourceDoc = "Manual.pdf",
                SectionRef = "6.18-a",
                SectionText = "Internal audit testing programme.",
                SourcePage = 1,
            },
        };
        var markdown =
            string.Join(
                "\n",
                Enumerable.Range(1, 63).Select(p => $"<!-- BCP_PDF_PAGE:{p} -->\nPage {p} content."));

        var bundle = NdRegulPolicyContextService.FromInternalSections(sections)
            .WithMarkdownFromPayloads([
                new InternalDocPayload("h", "Manual.pdf", markdown, null),
            ]);

        Assert.Equal(63, bundle.TotalPages);
    }

    [Fact]
    public void FullMarkdown_mode_always_sends_complete_manual_regardless_of_page_count()
    {
        var sections = Enumerable.Range(1, 200).Select(i => new Reguliq.Api.Data.NewDashboard.Entities.NdRegulInternalSection
        {
            SourceDoc = "Manual.pdf",
            SectionRef = $"1.{i}",
            SectionText = $"Section {i} about wire transfers.",
            SourcePage = i,
        }).ToArray();

        var markdown = string.Join(
            "\n",
            Enumerable.Range(1, 150).Select(p => $"<!-- BCP_PDF_PAGE:{p} -->\nPage {p} wire transfer controls."));

        var bundle = NdRegulPolicyContextService.FromInternalSections(
                sections,
                NdRegulPolicyContextService.RegulPolicyContextMode.FullMarkdown)
            .WithMarkdownFromPayloads([
                new InternalDocPayload("h", "Manual.pdf", markdown, null),
            ]);

        Assert.Equal(150, bundle.TotalPages);
        Assert.True(bundle.UsesFullMarkdown);
        var ctx = bundle.BuildContextForClause("wire transfer controls");
        Assert.Contains("=== DOCUMENT: Manual.pdf ===", ctx);
        Assert.Contains("Page 120 wire transfer", ctx);
        Assert.DoesNotContain("[Manual.pdf — 1.1", ctx);
    }

    [Fact]
    public void BuildContextForClause_retrieves_sections_matching_clause_phrases()
    {
        var sections = new[]
        {
            new Reguliq.Api.Data.NewDashboard.Entities.NdRegulInternalSection
            {
                SourceDoc = "Internal AML Manual.pdf",
                SectionRef = "14.4",
                SectionText = "It constitutes an easy reference for internal and external auditors.",
                SourcePage = 1,
            },
            new Reguliq.Api.Data.NewDashboard.Entities.NdRegulInternalSection
            {
                SourceDoc = "Internal AML Manual.pdf",
                SectionRef = "9.4.1",
                SectionText =
                    "Internal Audit AML Rule 9.4.1: The internal audit function shall periodically test the AML/CFT programme, including staffing, competencies, subsidiaries, and audit frequency factors.",
                SourcePage = 88,
            },
        };

        var bundle = NdRegulPolicyContextService.FromInternalSections(sections);
        bundle = new NdRegulPolicyContextService.PolicyBundle(
            bundle.Chunks,
            60,
            bundle.SourceTextForQuotes,
            bundle.MarkdownByFile,
            NdRegulPolicyContextService.RegulPolicyContextMode.Standard);

        var clause =
            "A robust and independent audit function is required to test the effectiveness and adequacy of AML/CFT policies, controls and procedures.";
        var ctx = bundle.BuildContextForClause(clause);

        Assert.Contains("9.4.1", ctx, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Internal Audit AML", ctx, StringComparison.OrdinalIgnoreCase);
    }
}
