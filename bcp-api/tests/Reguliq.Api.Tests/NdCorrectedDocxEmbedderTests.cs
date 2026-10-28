using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Reguliq.Api.Services.NewDashboard.CorrectedDocs;
using Xunit;

namespace Reguliq.Api.Tests;

public class NdCorrectedDocxEmbedderTests
{
    private static byte[] BuildSourceDocx(params string[] paragraphTexts)
    {
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
        {
            var mainPart = doc.AddMainDocumentPart();
            mainPart.Document = new Document(new Body());
            foreach (var text in paragraphTexts)
                mainPart.Document.Body!.Append(new Paragraph(new Run(new Text(text))));
            mainPart.Document.Save();
        }
        return ms.ToArray();
    }

    private static List<string> ReadParagraphs(byte[] docx)
    {
        using var ms = new MemoryStream(docx);
        using var doc = WordprocessingDocument.Open(ms, false);
        return doc.MainDocumentPart!.Document.Body!.Elements<Paragraph>().Select(p => p.InnerText).ToList();
    }

    private static NdActionPlanEmbedTarget Target(string? anchor, string clauseNo) => new(
        Guid.NewGuid(), Page: null, AnchorText: anchor, clauseNo, "Clause title",
        "Gap text for " + clauseNo, "Action text for " + clauseNo,
        "Compliance", DateTimeOffset.UtcNow, "Tester");

    [Fact]
    public void No_targets_returns_the_source_bytes_unchanged()
    {
        var source = BuildSourceDocx("Section 7.5 Customer Risk Assessment", "Some other paragraph.");
        var result = NdCorrectedDocxEmbedder.Embed(source, []);
        Assert.Same(source, result);
    }

    [Fact]
    public void Inserts_a_real_new_paragraph_immediately_after_the_matching_passage()
    {
        var source = BuildSourceDocx(
            "Intro paragraph.",
            "Section 7.5 Customer Risk Assessment — record retention requirements apply here.",
            "Trailing paragraph.");

        var result = NdCorrectedDocxEmbedder.Embed(source, [Target(anchor: "record retention", clauseNo: "3.1")]);
        var paragraphs = ReadParagraphs(result);

        Assert.Equal(4, paragraphs.Count);
        Assert.Equal("Intro paragraph.", paragraphs[0]);
        Assert.Contains("record retention", paragraphs[1]);
        Assert.Contains("COMPLIANCE ACTION", paragraphs[2]);
        Assert.Contains("3.1", paragraphs[2]);
        Assert.Contains("Gap text for 3.1", paragraphs[2]);
        Assert.Equal("Trailing paragraph.", paragraphs[3]);
    }

    [Fact]
    public void Falls_back_to_the_clause_number_as_anchor_when_the_gap_excerpt_is_not_found()
    {
        var source = BuildSourceDocx("Clause 3.5 Money Laundering definitions.", "Unrelated paragraph.");
        var result = NdCorrectedDocxEmbedder.Embed(source, [Target(anchor: "text that does not exist anywhere", clauseNo: "3.5")]);
        var paragraphs = ReadParagraphs(result);

        Assert.Equal(3, paragraphs.Count);
        Assert.Contains("Clause 3.5", paragraphs[0]);
        Assert.Contains("COMPLIANCE ACTION", paragraphs[1]); // inserted right after the "3.5" paragraph, not at the end
    }

    [Fact]
    public void Appends_at_the_end_when_no_anchor_matches_anything()
    {
        var source = BuildSourceDocx("First paragraph.", "Second paragraph.");
        var result = NdCorrectedDocxEmbedder.Embed(source, [Target(anchor: "nothing matches this", clauseNo: "9.9")]);
        var paragraphs = ReadParagraphs(result);

        Assert.Equal(3, paragraphs.Count);
        Assert.Contains("COMPLIANCE ACTION", paragraphs[2]);
    }

    [Fact]
    public void Two_resolved_actions_both_land_in_the_document_in_order()
    {
        var source = BuildSourceDocx("Section 7.5 risk assessment.", "Section 14.4 record keeping.");
        var result = NdCorrectedDocxEmbedder.Embed(
            source,
            [Target(anchor: "risk assessment", clauseNo: "3.1"), Target(anchor: "record keeping", clauseNo: "3.9")]);
        var paragraphs = ReadParagraphs(result);

        Assert.Equal(4, paragraphs.Count);
        Assert.Contains("3.1", paragraphs[1]);
        Assert.Contains("3.9", paragraphs[3]);
    }
}
