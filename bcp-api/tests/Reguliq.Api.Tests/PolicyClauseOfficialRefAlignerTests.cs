using Reguliq.Api.Services.LandingAi;
using Xunit;

namespace Reguliq.Api.Tests;

public class PolicyClauseOfficialRefAlignerTests
{
    [Theory]
    [InlineData("1-14", true)]
    [InlineData("1.-15", true)]
    [InlineData("7.4", false)]
    [InlineData("2.3-a", false)]
    [InlineData("6.3-b", false)]
    public void LooksInvented_detects_numeric_dash_ids(string clauseNo, bool expected)
    {
        Assert.Equal(expected, PolicyClauseOfficialRefAligner.LooksInvented(clauseNo));
    }

    [Fact]
    public void Align_replaces_invented_ids_from_markdown_headings()
    {
        var markdown = """
            7.4 General Requirements Applicable for All Relationships
            RMs must follow the below minimum procedures when accepting new relationships.
            """;
        var extracted = new List<PolicyClause>
        {
            new("1-14", "RMs must follow the below minimum procedures when accepting new relationships and/or processing transactions.", 0),
        };

        var aligned = PolicyClauseOfficialRefAligner.Align(extracted, markdown);

        Assert.Equal("7.4", aligned[0].ClauseNo);
    }

    [Fact]
    public void ExpandTextsFromMarkdown_replaces_short_blurb_with_full_heading_body()
    {
        var markdown = """
            2. Purpose
            This document represents the AML/CTF Compliance Program, and the primary purpose of this document is to regulate the responsibilities of the bank.

            The objectives of this document are represented in the following:
            a. Establishing the principle of Compliance is everyone's responsibility.

            3. Scope
            Unless otherwise noted, this document applies to DIFC.
            """;
        var extracted = new List<PolicyClause>
        {
            new("2", "2. Purpose. This document represents the AML/CTF Compliance Program.", 11),
        };

        var expanded = PolicyClauseOfficialRefAligner.ExpandTextsFromMarkdown(extracted, markdown);

        Assert.Contains("objectives of this document", expanded[0].ClauseText, StringComparison.OrdinalIgnoreCase);
        Assert.True(expanded[0].ClauseText.Length > extracted[0].ClauseText.Length);
    }

    [Fact]
    public void AlignThenMerge_keeps_extract_text_instead_of_parse_tags()
    {
        var markdown = """
            1. Introduction
            The international and regional impact of AML/CTF.
            <a id='6ba43595-c105-445c-88cd-4c33b7771376'></a>
            Page | 41
            """;
        var extracted = new List<PolicyClause>
        {
            new("1", "1. Introduction. The international and regional impact of Anti-Money Laundering and Terrorist Financing.", 10),
        };

        var merged = PolicyClauseOfficialRefAligner.AlignThenMerge(extracted, markdown);

        Assert.DoesNotContain("<a id", merged[0].ClauseText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Page |", merged[0].ClauseText, StringComparison.Ordinal);
        Assert.Contains("international", merged[0].ClauseText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Align_keeps_official_ids()
    {
        var markdown = "7.4 General Requirements\nRMs must follow procedures.";
        var extracted = new List<PolicyClause>
        {
            new("7.4", "RMs must follow the below minimum procedures when accepting new relationships.", 27),
        };

        var aligned = PolicyClauseOfficialRefAligner.Align(extracted, markdown);
        Assert.Equal("7.4", aligned[0].ClauseNo);
    }
}
