using Reguliq.Api.Services.LandingAi;
using Xunit;

namespace Reguliq.Api.Tests;

public class PolicyPageResolverTests
{
    [Fact]
    public void ResolveGovPointPage_PrefersLastNumberedClauseOverToc()
    {
        var md = $"""
            {PolicyPageResolver.PageMarkerPrefix}1 -->
            Table of contents 11.2 Useful Links .......... 113
            {PolicyPageResolver.PageMarkerPrefix}113 -->
            11.2 Useful Links
            Institution URL Abu Dhabi Global Market https://example.com
            """;

        var page = PolicyPageResolver.ResolveGovPointPage(
            md,
            "11.2",
            section: null,
            title: "Useful Links",
            text: "Institution URL Abu Dhabi Global Market",
            aiPageHint: 1,
            maxPageOverride: 114);

        Assert.Equal(113, page);
    }

    [Fact]
    public void ResolveGovPointPage_MonolithicMarkdown_UsesDocumentPageCount()
    {
        var prefix = new string('x', 8500);
        var md = prefix + "11.2 Useful Links Institution URL Abu Dhabi Global Market https://example.com";

        var page = PolicyPageResolver.ResolveGovPointPage(
            md,
            "11.2",
            section: null,
            title: "Useful Links",
            text: "Institution URL Abu Dhabi Global Market",
            aiPageHint: 1,
            maxPageOverride: 114);

        Assert.True(page is >= 100, $"expected late page, got {page}");
    }

    [Fact]
    public void ResolveGovPointPage_7_4_PrefersBodyOverEarlierTextMatch()
    {
        var md = $"""
            {PolicyPageResolver.PageMarkerPrefix}49 -->
            6.3.1 Customer identification. Some useful links to sources of AML/CFT suspicious transaction indicators are provided in section 7.4 below.
            {PolicyPageResolver.PageMarkerPrefix}77 -->
            7.4 Identification of Suspicious Transactions
            (AML-CFT Decision 16)
            FIs are obliged to put in place indicators that can be used to identify suspicious transactions.
            Some useful links to sources of AML/CFT suspicious transaction indicators are provided below.
            """;

        var page = PolicyPageResolver.ResolveGovPointPage(
            md,
            "7.4",
            section: null,
            title: "Identification of Suspicious Transactions",
            text: "Some useful links to sources of AML/CFT suspicious transaction indicators are provided",
            aiPageHint: 49,
            maxPageOverride: 114);

        Assert.Equal(77, page);
    }

    [Fact]
    public void ResolveGovPointPage_7_4_RefinesPageInsideSingleChunkMarker()
    {
        // One ADE chunk tagged page 49 covering PDF pages 49–114; heading ~42% into chunk ≈ page 76–77.
        const int segLen = 100_000;
        var beforeLen = (int)(segLen * 0.42);
        var head = "7.4 Identification of Suspicious Transactions\n(AML-CFT Decision 16)\n";
        var md = $"""
            {PolicyPageResolver.PageMarkerPrefix}49 -->
            {new string('x', beforeLen)}
            {head}
            {new string('y', segLen - beforeLen)}
            Some useful links to sources of AML/CFT suspicious transaction indicators are provided below.
            """;

        var page = PolicyPageResolver.ResolveGovPointPage(
            md,
            "7.4",
            section: null,
            title: "Identification of Suspicious Transactions",
            text: "Some useful links to sources of AML/CFT suspicious transaction indicators are provided",
            aiPageHint: 69,
            maxPageOverride: 114);

        Assert.True(page is >= 76 and <= 78, $"expected ~77, got {page}");
    }

    [Fact]
    public void ResolveGovPointPage_6_2_PrefersBodyTextInsideMultiPageChunk()
    {
        const int segLen = 20_000;
        var tocAt = (int)(segLen * 0.05);
        var bodyAt = (int)(segLen * 0.42);
        var body =
            "The department has complete independence in relation to the investigation of Anti-Money Laundering and Terrorist Financing cases";
        var md = $"""
            {PolicyPageResolver.PageMarkerPrefix}12 -->
            {new string('a', tocAt)}
            6.2 Independence of the Compliance / AML Department (contents)
            {new string('b', bodyAt - tocAt - 60)}
            6.2 Independence of the Compliance / AML Department:
            * {body}
            {new string('c', segLen - bodyAt - body.Length - 80)}
            {PolicyPageResolver.PageMarkerPrefix}18 -->
            next chapter
            """;

        var page = PolicyPageResolver.ResolveGovPointPage(
            md,
            "6.2",
            section: "6.2",
            title: null,
            text: body,
            aiPageHint: 12,
            maxPageOverride: 63);

        Assert.True(page is >= 14 and <= 15, $"expected ~14, got {page}");
    }

    [Fact]
    public void InjectPageMarkersFromParseJson_SplitsMultiPageArraysProportionally()
    {
        var json = """
            {
              "splits": [
                {
                  "pages": [12, 13, 14],
                  "markdown": "AAAAABBBBBCCCCC"
                }
              ]
            }
            """;

        var md = PolicyPageResolver.InjectPageMarkersFromParseJson(json, "fallback");
        Assert.Contains("<!-- BCP_PDF_PAGE:12 -->", md);
        Assert.Contains("<!-- BCP_PDF_PAGE:13 -->", md);
        Assert.Contains("<!-- BCP_PDF_PAGE:14 -->", md);
        Assert.Contains("AAAAA", md);
        Assert.Contains("CCCCC", md);
    }

    [Theory]
    [InlineData("2).", "2")]
    [InlineData("7.2):", "7.2")]
    [InlineData("Legal Basis).", "Legal Basis")]
    [InlineData("31e325e2-5e6f-4df1-859b-203afe942c0c International Legislative and Regulatory Framework)", null)]
    [InlineData("7.2 Identification of Suspicious Transactions", "7.2")]
    public void SanitizeSectionLabel_CleansAiJunk(string input, string? expected)
    {
        Assert.Equal(expected, PolicyPageResolver.SanitizeSectionLabel(input));
    }

    [Fact]
    public void Resolve_PrefersQuoteLastMatch_AndIgnoresUuidSection()
    {
        var md = $"""
            {PolicyPageResolver.PageMarkerPrefix}2 -->
            founding members appear in the table of contents summary
            {PolicyPageResolver.PageMarkerPrefix}6 -->
            The UAE is one of the founding members of MENA FATF regional body.
            """;

        var line =
            """"Page 2, Section 31e325e2-5e6f-4df1-859b-203afe942c0c International Legislative): "The UAE is one of the founding members of MENA"""";

        var page = PolicyPageResolver.Resolve(md, line);
        Assert.Equal(6, page);
    }
}
