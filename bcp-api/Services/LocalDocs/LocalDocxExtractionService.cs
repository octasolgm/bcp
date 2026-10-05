using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Reguliq.Api.Services.LandingAi;

namespace Reguliq.Api.Services.LocalDocs;

/// <summary>
/// Local, offline .docx text extraction (OpenXml SDK). When Word saved page-break layout in the file,
/// output uses the same BCP_PDF_PAGE markers as PDF parse; otherwise a single page-1 marker.
/// </summary>
public sealed class LocalDocxExtractionService
{
    public LocalPdfResult Extract(byte[] docxBytes)
    {
        var enhanced = DocxRenderedPages.EnhanceParseResult(
            docxBytes,
            new LocalParseResult("document.docx", 1, 0, "", []));
        if (enhanced != null)
        {
            var pages = SplitMarkdownIntoPages(enhanced.Markdown);
            return new LocalPdfResult(enhanced.TotalPages, enhanced.Markdown, pages);
        }

        using var stream = new MemoryStream(docxBytes);
        using var doc = WordprocessingDocument.Open(stream, isEditable: false);

        var body = doc.MainDocumentPart?.Document?.Body;
        var text = body == null
            ? ""
            : string.Join("\n", body.Descendants<Paragraph>().Select(p => p.InnerText).Where(t => t.Length > 0));

        var page = new LocalPageResult(1, text, text.Length > 0 ? PageExtractionMethod.Native : PageExtractionMethod.Empty);
        var markdown = $"{PolicyPageResolver.PageMarkerPrefix}1 -->\n{text}";
        return new LocalPdfResult(1, markdown, [page]);
    }

    private static List<LocalPageResult> SplitMarkdownIntoPages(string markdown)
    {
        var pattern = System.Text.RegularExpressions.Regex.Escape(PolicyPageResolver.PageMarkerPrefix) + @"(\d+)\s*-->";
        var matches = System.Text.RegularExpressions.Regex.Matches(markdown, pattern);
        var pages = new List<LocalPageResult>();
        for (var i = 0; i < matches.Count; i++)
        {
            var start = matches[i].Index + matches[i].Length;
            var end = i + 1 < matches.Count ? matches[i + 1].Index : markdown.Length;
            var pageNum = int.Parse(matches[i].Groups[1].Value);
            pages.Add(new LocalPageResult(pageNum, markdown[start..end].Trim(), PageExtractionMethod.Native));
        }

        return pages;
    }
}
