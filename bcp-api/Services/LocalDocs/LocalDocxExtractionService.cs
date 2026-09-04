using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Reguliq.Api.Services.LandingAi;

namespace Reguliq.Api.Services.LocalDocs;

/// <summary>
/// Local, offline .docx text extraction (Microsoft's own open-source OpenXml SDK — no AI, no conversion step).
/// Word has no native PDF-style page concept, so output is marked as one logical page ("page 1") — page
/// references for Word uploads stay approximate, matching the existing system's documented limitation
/// (see docs/PAGE-REFERENCES.md: "Word uploads: Grounding uses converted PDF in storage when available").
/// </summary>
public sealed class LocalDocxExtractionService
{
    public LocalPdfResult Extract(byte[] docxBytes)
    {
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
}
