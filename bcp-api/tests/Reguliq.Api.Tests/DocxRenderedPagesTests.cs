using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Reguliq.Api.Services.LocalDocs;
using Xunit;

namespace Reguliq.Api.Tests;

public class DocxRenderedPagesTests
{
    private static byte[] BuildDocx(bool rendered)
    {
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            Paragraph Para(string text, bool breakBefore = false)
            {
                var run = new Run();
                if (breakBefore)
                    run.Append(rendered ? new LastRenderedPageBreak() : new Break { Type = BreakValues.Page });
                run.Append(new Text(text) { Space = SpaceProcessingModeValues.Preserve });
                return new Paragraph(run);
            }

            main.Document = new Document(new Body(
                Para("Introduction to the anti money laundering manual and its purpose."),
                Para("Roles and responsibilities of the compliance officer are set out here.", breakBefore: true),
                Para("Annex 5. Synopsis of the Guidance on suspicious transaction reporting.", breakBefore: true),
                Para("The legal basis addresses (i) the consequences for failure to disclose suspicious activity.")));
        }
        return ms.ToArray();
    }

    private static LocalExtractionResult Extract() => new(
        "manual.docx",
        1,
        0,
        "",
        [
            new LocalSection("1", "1. Introduction to the anti money laundering manual and its purpose.", 1),
            new LocalSection("2", "2. Roles and responsibilities of the compliance officer are set out here.", 1),
            new LocalSection("Annex 5", "Annex 5. Synopsis of the Guidance on suspicious transaction reporting. The legal basis addresses", 1),
        ],
        []);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Sections_get_the_page_word_laid_them_out_on(bool rendered)
    {
        var result = DocxRenderedPages.Apply(BuildDocx(rendered), Extract());

        Assert.Equal(3, result.TotalPages);
        Assert.Equal([1, 2, 3], result.Sections.Select(s => s.SourcePage ?? 0).ToArray());
    }

    [Fact]
    public void Quote_is_found_on_its_page_even_with_different_punctuation()
    {
        var paged = DocxRenderedPages.Read(BuildDocx(rendered: true))!;

        Assert.Equal(3, DocxRenderedPages.PageOf(paged, "The legal basis addresses (i) the consequences for failure"));
    }
}
