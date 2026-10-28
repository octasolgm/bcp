using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;

namespace Reguliq.Api.Services.NewDashboard.CorrectedDocs;

/// <summary>
/// Writes resolved action-plan notes into a copy of a source PDF, as new pages inserted immediately
/// after the page each note's evidence was found on — real content, not the placeholder "points at the
/// same file" copy this replaces when a run has anything resolved to embed.
///
/// PdfSharpCore can move whole pages between documents and draw new content on a blank page, but it
/// cannot parse or reflow an existing page's own text, so a note can't be spliced into the middle of a
/// paragraph. A new page directly after the cited one is the closest a general-purpose PDF library can
/// get to "at the place the reference exists" without a full PDF content-stream editor.
/// </summary>
public static class NdCorrectedPdfEmbedder
{
    private static readonly double PageWidth = XUnit.FromInch(8.27).Point; // A4
    private static readonly double PageHeight = XUnit.FromInch(11.69).Point;
    private const double Margin = 48;
    private const double LineHeight = 16;
    private const int RasterFallbackDpi = 150;

    /// <param name="targets">Each target's Page is 1-based and may be null (unresolved location — its
    /// note is appended after the last page instead of guessed at).</param>
    public static byte[] Embed(byte[] sourcePdf, IReadOnlyList<NdActionPlanEmbedTarget> targets)
    {
        if (targets.Count == 0) return sourcePdf;

        try
        {
            return EmbedByImportingPages(sourcePdf, targets);
        }
        catch (PdfReaderException)
        {
            // PdfSharpCore's parser rejects some real-world PDFs outright — a broken or
            // non-standard cross-reference table from certain scan/export tools — that PDFium
            // (already used elsewhere for OCR rendering) opens fine. Rebuild the document from
            // rendered page images instead of giving up and falling back to the no-op placeholder
            // copy: the note still lands after the correct cited page, just without the
            // original's selectable text underneath it.
            return EmbedByRasterizingPages(sourcePdf, targets);
        }
    }

    private static byte[] EmbedByImportingPages(byte[] sourcePdf, IReadOnlyList<NdActionPlanEmbedTarget> targets)
    {
        using var sourceStream = new MemoryStream(sourcePdf);
        using var source = PdfReader.Open(sourceStream, PdfDocumentOpenMode.Import);
        var pageCount = source.PageCount;
        var byPage = GroupByPage(targets, pageCount);

        using var output = new PdfDocument();
        for (var i = 1; i <= pageCount; i++)
        {
            output.AddPage(source.Pages[i - 1]);
            if (byPage.TryGetValue(i, out var notesHere))
                AppendNotePages(output, notesHere);
        }

        using var resultStream = new MemoryStream();
        output.Save(resultStream, false);
        return resultStream.ToArray();
    }

    private static byte[] EmbedByRasterizingPages(byte[] sourcePdf, IReadOnlyList<NdActionPlanEmbedTarget> targets)
    {
        var pageCount = PDFtoImage.Conversion.GetPageCount(sourcePdf, password: null);
        var byPage = GroupByPage(targets, pageCount);
        var renderOptions = new PDFtoImage.RenderOptions(Dpi: RasterFallbackDpi);

        using var output = new PdfDocument();
        for (var i = 1; i <= pageCount; i++)
        {
            using var pngStream = new MemoryStream();
            PDFtoImage.Conversion.SavePng(pngStream, sourcePdf, (Index)(i - 1), password: null, renderOptions);
            var pngBytes = pngStream.ToArray();
            var ximg = XImage.FromStream(() => new MemoryStream(pngBytes));
            var widthPoints = ximg.PixelWidth * 72.0 / RasterFallbackDpi;
            var heightPoints = ximg.PixelHeight * 72.0 / RasterFallbackDpi;

            var page = output.AddPage();
            page.Width = XUnit.FromPoint(widthPoints);
            page.Height = XUnit.FromPoint(heightPoints);
            using (var gfx = XGraphics.FromPdfPage(page))
                gfx.DrawImage(ximg, 0, 0, widthPoints, heightPoints);

            if (byPage.TryGetValue(i, out var notesHere))
                AppendNotePages(output, notesHere);
        }

        using var resultStream = new MemoryStream();
        output.Save(resultStream, false);
        return resultStream.ToArray();
    }

    /// <summary>Groups notes onto the page they attach after; unresolved ones attach after the last page.</summary>
    private static Dictionary<int, List<NdActionPlanEmbedTarget>> GroupByPage(
        IReadOnlyList<NdActionPlanEmbedTarget> targets, int pageCount) =>
        targets
            .GroupBy(t => t.Page is > 0 && t.Page <= pageCount ? t.Page!.Value : pageCount)
            .ToDictionary(g => g.Key, g => g.ToList());

    private static void AppendNotePages(PdfDocument output, IReadOnlyList<NdActionPlanEmbedTarget> targets)
    {
        var font = new XFont("Arial", 10, XFontStyle.Regular);
        var headerFont = new XFont("Arial", 10, XFontStyle.Bold);
        var usableWidth = PageWidth - 2 * Margin;

        var page = output.AddPage();
        page.Width = XUnit.FromPoint(PageWidth);
        page.Height = XUnit.FromPoint(PageHeight);
        var gfx = XGraphics.FromPdfPage(page);
        double y = Margin;

        void NewPageIfNeeded(double neededHeight)
        {
            if (y + neededHeight <= PageHeight - Margin) return;
            gfx.Dispose();
            page = output.AddPage();
            page.Width = XUnit.FromPoint(PageWidth);
            page.Height = XUnit.FromPoint(PageHeight);
            gfx = XGraphics.FromPdfPage(page);
            y = Margin;
        }

        for (var i = 0; i < targets.Count; i++)
        {
            var lines = WrapText(NdActionPlanEmbedNote.Build(targets[i]), font, gfx, usableWidth);
            NewPageIfNeeded(lines.Count * LineHeight + LineHeight);
            foreach (var line in lines)
            {
                NewPageIfNeeded(LineHeight);
                var useFont = line.StartsWith("COMPLIANCE ACTION", StringComparison.Ordinal) ? headerFont : font;
                gfx.DrawString(line, useFont, XBrushes.Black, new XPoint(Margin, y));
                y += LineHeight;
            }
            if (i < targets.Count - 1)
            {
                NewPageIfNeeded(LineHeight);
                y += LineHeight; // blank line between stacked notes on the same page
            }
        }

        gfx.Dispose();
    }

    private static List<string> WrapText(string text, XFont font, XGraphics gfx, double maxWidth)
    {
        var result = new List<string>();
        foreach (var paragraph in text.Split('\n'))
        {
            if (paragraph.Length == 0) { result.Add(""); continue; }
            var words = paragraph.Split(' ');
            var line = "";
            foreach (var word in words)
            {
                var candidate = line.Length == 0 ? word : $"{line} {word}";
                if (gfx.MeasureString(candidate, font).Width > maxWidth && line.Length > 0)
                {
                    result.Add(line);
                    line = word;
                }
                else
                {
                    line = candidate;
                }
            }
            result.Add(line);
        }
        return result;
    }
}
