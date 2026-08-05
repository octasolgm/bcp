using Reguliq.Api.Services.LandingAi;
using Reguliq.Api.Services.Pdf;
using Xunit;

namespace Reguliq.Api.Tests;

public class PdfGroundedMarkdownBuilderTests
{
    [Fact]
    public void TryGround_injects_real_pdf_page_markers_into_landing_text()
    {
        var landing = """
            Introduction text for the manual.
            6.2 Independence of the Compliance Department.
            The department has complete independence in relation to investigations.
            Internal Audit Review AML Rule 9.4.1 covers compliance testing in all reviews.
            """;

        var nativeMd = """
            <!-- BCP_PDF_PAGE:10 -->
            Introduction text for the manual.
            <!-- BCP_PDF_PAGE:14 -->
            6.2 Independence of the Compliance Department.
            The department has complete independence in relation to investigations.
            <!-- BCP_PDF_PAGE:49 -->
            Internal Audit Review AML Rule 9.4.1 covers compliance testing in all reviews.
            """;

        var native = PdfNativePageDocument.FromMarkdown(nativeMd, 63);
        var grounded = PdfGroundedMarkdownBuilder.TryGround(landing, native);
        Assert.NotNull(grounded);
        Assert.Contains("<!-- BCP_PDF_PAGE:14 -->", grounded);
        Assert.Contains("<!-- BCP_PDF_PAGE:49 -->", grounded);

        var page = PolicyPageResolver.ResolveGovPointPage(
            grounded!,
            "9.4.1",
            "9.4.1",
            null,
            "Internal Audit Review AML Rule 9.4.1 covers compliance testing in all reviews.",
            null);
        Assert.Equal(49, page);
    }
}
