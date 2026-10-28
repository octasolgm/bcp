using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using Reguliq.Api.Services.NewDashboard.CorrectedDocs;
using Xunit;
using PdfPigDocument = UglyToad.PdfPig.PdfDocument;

namespace Reguliq.Api.Tests;

/// <summary>Builds a real small PDF, embeds into it, and reads the *result* back with PdfPig — an
/// independent library from the one that wrote it — so these assert what a reader actually sees,
/// not just that PdfSharpCore didn't throw.</summary>
public class NdCorrectedPdfEmbedderTests
{
    private static byte[] BuildSourcePdf(int pageCount, string textPerPage)
    {
        using var doc = new PdfDocument();
        var font = new XFont("Arial", 12, XFontStyle.Regular);
        for (var i = 1; i <= pageCount; i++)
        {
            var page = doc.AddPage();
            using var gfx = XGraphics.FromPdfPage(page);
            gfx.DrawString($"{textPerPage} {i}", font, XBrushes.Black, new XPoint(40, 40));
        }
        using var ms = new MemoryStream();
        doc.Save(ms, false);
        return ms.ToArray();
    }

    private static NdActionPlanEmbedTarget Target(int? page, string clauseNo) => new(
        Guid.NewGuid(), page, "anchor", clauseNo, "Clause title",
        "Gap text for " + clauseNo, "Action text for " + clauseNo,
        "Compliance", DateTimeOffset.UtcNow, "Tester");

    [Fact]
    public void No_targets_returns_the_source_bytes_unchanged()
    {
        var source = BuildSourcePdf(2, "Original page");
        var result = NdCorrectedPdfEmbedder.Embed(source, []);
        Assert.Same(source, result);
    }

    [Fact]
    public void Inserts_one_extra_page_right_after_the_cited_page()
    {
        var source = BuildSourcePdf(3, "Original page");
        var result = NdCorrectedPdfEmbedder.Embed(source, [Target(page: 2, clauseNo: "3.1")]);

        using var pdf = PdfPigDocument.Open(result);
        Assert.Equal(4, pdf.NumberOfPages);

        var page1 = pdf.GetPage(1).Text;
        var page2 = pdf.GetPage(2).Text;
        var page3 = pdf.GetPage(3).Text; // the inserted note page
        var page4 = pdf.GetPage(4).Text;

        Assert.Contains("Original page 1", page1);
        Assert.Contains("Original page 2", page2);
        Assert.Contains("COMPLIANCE ACTION", page3);
        Assert.Contains("3.1", page3);
        Assert.Contains("Gap text for 3.1", page3.Replace("\r", "").Replace("\n", " "));
        Assert.Contains("Original page 3", page4); // original page 3 pushed one slot later
    }

    [Fact]
    public void Two_resolved_actions_on_the_same_page_are_stacked_on_one_inserted_page()
    {
        var source = BuildSourcePdf(1, "Original page");
        var result = NdCorrectedPdfEmbedder.Embed(source, [Target(1, "3.1"), Target(1, "3.5")]);

        using var pdf = PdfPigDocument.Open(result);
        Assert.Equal(2, pdf.NumberOfPages);
        var notePage = pdf.GetPage(2).Text;
        Assert.Contains("3.1", notePage);
        Assert.Contains("3.5", notePage);
    }

    [Fact]
    public void An_unresolved_page_appends_its_note_after_the_last_page_instead_of_guessing()
    {
        var source = BuildSourcePdf(2, "Original page");
        var result = NdCorrectedPdfEmbedder.Embed(source, [Target(page: null, clauseNo: "9.9")]);

        using var pdf = PdfPigDocument.Open(result);
        Assert.Equal(3, pdf.NumberOfPages);
        Assert.Contains("Original page 1", pdf.GetPage(1).Text);
        Assert.Contains("Original page 2", pdf.GetPage(2).Text);
        Assert.Contains("9.9", pdf.GetPage(3).Text);
    }

    [Fact]
    public void A_page_number_beyond_the_document_falls_back_to_the_last_page()
    {
        var source = BuildSourcePdf(2, "Original page");
        var result = NdCorrectedPdfEmbedder.Embed(source, [Target(page: 99, clauseNo: "3.1")]);

        using var pdf = PdfPigDocument.Open(result);
        Assert.Equal(3, pdf.NumberOfPages);
    }
}
