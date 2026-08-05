using Reguliq.Api.Services.NewDashboard;
using Reguliq.Api.Services.LandingAi;
using Xunit;

namespace Reguliq.Api.Tests;

public class NdInternalDocumentSectionPageServiceTests
{
    [Fact]
    public void ResolveSectionPage_OverridesWrongAiHintOnPage1()
    {
        var markdown = $"""
            {PolicyPageResolver.PageMarkerPrefix}6 -->
            1-1 INTRODUCTION
            Paying particular attention to their foreign branches and subsidiaries.
            {PolicyPageResolver.PageMarkerPrefix}12 -->
            1.3.1 Establishing normal pattern of expected activities
            """;

        var intro = NdInternalDocumentSectionPageService.ResolveSectionPage(
            markdown, "1-1", "INTRODUCTION", 1);
        var branch = NdInternalDocumentSectionPageService.ResolveSectionPage(
            markdown, "1-2", "Paying particular attention to their foreign branches", 1);
        var kyc = NdInternalDocumentSectionPageService.ResolveSectionPage(
            markdown, "1.3.1", "Establishing normal pattern of expected activities", 1);

        Assert.Equal(6, intro);
        Assert.Equal(6, branch);
        Assert.Equal(12, kyc);
    }

    [Fact]
    public void ResolveSectionPage_ResolvesSuffixClauseRefs()
    {
        var markdown = $"""
            {PolicyPageResolver.PageMarkerPrefix}40 -->
            6.3 AML monitoring programme overview
            6.3-b Customer due diligence for high-risk relationships must be refreshed annually.
            6.3-c Enhanced monitoring applies to PEP accounts.
            {PolicyPageResolver.PageMarkerPrefix}41 -->
            6.3-d Transaction monitoring thresholds are calibrated quarterly.
            """;

        var b = NdInternalDocumentSectionPageService.ResolveSectionPage(
            markdown,
            "6.3-b",
            "Customer due diligence for high-risk relationships must be refreshed annually.",
            1);
        var d = NdInternalDocumentSectionPageService.ResolveSectionPage(
            markdown,
            "6.3-d",
            "Transaction monitoring thresholds are calibrated quarterly.",
            1);

        Assert.Equal(40, b);
        Assert.Equal(41, d);
    }

    [Fact]
    public void ResolveSectionPage_EstimatesFromPositionWhenMarkdownIsMonolithic()
    {
        var body = string.Join(' ', Enumerable.Repeat("policy filler text segment.", 400));
        var markdown = $"{body}\n6.3-b Customer due diligence for high-risk relationships must be refreshed annually.\n{body}";

        var page = NdInternalDocumentSectionPageService.ResolveSectionPage(
            markdown,
            "6.3-b",
            "Customer due diligence for high-risk relationships must be refreshed annually.",
            1,
            totalPages: 63);

        Assert.True(page is > 1 and <= 63, $"Expected page in 2..63, got {page}");
    }
}
