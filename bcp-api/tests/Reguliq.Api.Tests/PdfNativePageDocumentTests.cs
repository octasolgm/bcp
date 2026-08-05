using Reguliq.Api.Services.LandingAi;
using Reguliq.Api.Services.Pdf;
using Xunit;

namespace Reguliq.Api.Tests;

public class PdfNativePageDocumentTests
{
    [Fact]
    public void TryCreate_rejects_empty_bytes()
    {
        Assert.Null(PdfNativePageDocument.TryCreate([]));
    }

    [Fact]
    public void ResolveSectionPage_prefers_body_over_toc_on_dense_pages()
    {
        var md = $"""
            {PolicyPageResolver.PageMarkerPrefix}12 -->
            Table of contents 6.1 Cooperation with Local Authorities .... 52
            {PolicyPageResolver.PageMarkerPrefix}14 -->
            6.1 Cooperation with Local Authorities:
            Compliance Department at the DIFC is the only authorized party to exchange information with the FIU.
            {PolicyPageResolver.PageMarkerPrefix}15 -->
            next section
            """;

        var native = PdfNativePageDocument.FromMarkdown(md, 15);
        var page = native.ResolveSectionPage(
            "6.1",
            "Cooperation with Local Authorities",
            "Compliance Department at the DIFC is the only authorized party to exchange information with the FIU.");

        Assert.Equal(14, page);
    }

    [Fact]
    public void ResolveSectionPage_finds_section_6_2_on_correct_page()
    {
        var body =
            "The department has complete independence in relation to the investigation of Anti-Money Laundering";
        var md = $"""
            {PolicyPageResolver.PageMarkerPrefix}12 -->
            6.2 Independence (contents)
            {PolicyPageResolver.PageMarkerPrefix}14 -->
            6.2 Independence of the Compliance / AML Department:
            {body}
            """;

        var native = PdfNativePageDocument.FromMarkdown(md, 63);
        var page = native.ResolveSectionPage("6.2", null, body);

        Assert.Equal(14, page);
    }
}
