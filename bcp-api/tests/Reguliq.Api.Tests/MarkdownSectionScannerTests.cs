using Reguliq.Api.Services.LandingAi;
using Xunit;

namespace Reguliq.Api.Tests;

public class MarkdownSectionScannerTests
{
    [Fact]
    public void Scan_reads_major_heading_introduction()
    {
        var markdown = """
            1. Introduction
            The international and regional impact of Anti-Money Laundering and Terrorist Financing.
            """;

        var scanned = MarkdownSectionScanner.Scan(markdown);

        Assert.Contains(scanned, s => s.SectionRef == "1");
        Assert.Contains(scanned, s => s.SectionText.Contains("international", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Scan_reads_dotted_heading()
    {
        var markdown = """
            7.4 General Requirements Applicable for All Relationships
            RMs must follow the below minimum procedures when accepting new relationships.
            """;

        var scanned = MarkdownSectionScanner.Scan(markdown);
        Assert.Contains(scanned, s => s.SectionRef == "7.4");
    }

    [Fact]
    public void Scan_skips_numbered_list_items_and_sentences()
    {
        var markdown = """
            7.11 International and Local Payments
            The outgoing transfer must include the following information as a minimum:
            1. Originator information (full name - account number - address).
            1. Any person who knows that funds are proceeded by an original offence and must report it.
            """;

        var scanned = MarkdownSectionScanner.Scan(markdown);
        Assert.DoesNotContain(scanned, s => s.SectionRef == "1");
        Assert.Contains(scanned, s => s.SectionRef == "7.11");
    }

    [Fact]
    public void Scan_does_not_treat_citation_line_as_section_one()
    {
        var markdown = """
            1 DFSA AML rule 13.3.3 <a id='6ba43595-c105-445c-88cd-4c33b7771376'></a>
            Page | 41
            Submit all requested information and supporting documentation with the SAR.
            """;

        var scanned = MarkdownSectionScanner.Scan(markdown);
        Assert.DoesNotContain(scanned, s => s.SectionRef == "1");
    }

    [Fact]
    public void Scan_strips_anchor_tags_and_page_footers()
    {
        var markdown = """
            1. Introduction
            The international and regional impact of AML/CTF.
            <a id='6ba43595-c105-445c-88cd-4c33b7771376'></a>
            Page | 10
            DFSA have adopted reforms.
            """;

        var scanned = MarkdownSectionScanner.Scan(markdown);
        var intro = Assert.Single(scanned, s => s.SectionRef == "1");
        Assert.DoesNotContain("<a id", intro.SectionText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Page |", intro.SectionText, StringComparison.Ordinal);
        Assert.Contains("international", intro.SectionText, StringComparison.OrdinalIgnoreCase);
    }
}
