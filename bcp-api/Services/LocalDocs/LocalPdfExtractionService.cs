using System.Text;
using Reguliq.Api.Services.LandingAi;
using SkiaSharp;
using UglyToad.PdfPig;

namespace Reguliq.Api.Services.LocalDocs;

public enum PageExtractionMethod { Native, Ocr, Mixed, Empty }

public sealed record LocalPageResult(int PageNumber, string Text, PageExtractionMethod Method);

public sealed record LocalPdfResult(
    int TotalPages,
    string Markdown,
    IReadOnlyList<LocalPageResult> Pages)
{
    public int OcrPageCount => Pages.Count(p => p.Method is PageExtractionMethod.Ocr or PageExtractionMethod.Mixed);
}

/// <summary>
/// Local, offline PDF text extraction: PdfPig for born-digital text, Tesseract OCR for scanned pages.
/// Nothing leaves the server, no per-page cost. OCR renders the FULL PAGE to a bitmap (via PDFtoImage/
/// PDFium) rather than relying on PdfPig's embedded-image extraction — many scanned PDFs use image
/// encodings (JBIG2, CCITT fax) PdfPig doesn't reliably enumerate, so page rendering is the reliable
/// path regardless of how the scan was encoded.
/// </summary>
public sealed class LocalPdfExtractionService(ILogger<LocalPdfExtractionService> logger)
{
    /// <summary>Below this many characters of native text, a page is treated as scanned (needs OCR).</summary>
    private const int NativeTextSufficiencyThreshold = 20;

    /// <summary>Which OCR engine to use is a per-call choice (see <see cref="OcrEngineRegistry"/>), not
    /// something this service is bound to — the same PDF can be run through more than one engine.</summary>
    public async Task<LocalPdfResult> ExtractAsync(byte[] pdfBytes, IOcrEngine ocr, CancellationToken ct = default)
    {
        // Phase 1 — sequential: read native text and render any page that needs OCR to a PNG. PdfPig and
        // PDFium are not safe to call concurrently against the same document, so this stays single-threaded —
        // it's also the cheap part; OCR inference is what actually dominates runtime on a scanned document.
        var work = new List<(int PageNumber, string NativeText, byte[]? Png)>();
        int totalPages;
        using (var pdf = PdfDocument.Open(pdfBytes))
        {
            totalPages = pdf.NumberOfPages;
            foreach (var page in pdf.GetPages())
            {
                var nativeText = (page.Text ?? "").Trim();
                var needsOcr = nativeText.Length < NativeTextSufficiencyThreshold && ocr.IsAvailable;
                var png = needsOcr ? SafeRenderPageToPng(pdfBytes, page.Number) : null;
                work.Add((page.Number, nativeText, png));
            }
        }

        // Phase 2 — parallel: OCR every page that needs it across a small pool of Tesseract engines
        // instead of one page at a time. This is the single biggest lever on a multi-page scanned document.
        var results = new LocalPageResult[work.Count];
        await Parallel.ForEachAsync(
            work,
            new ParallelOptions { MaxDegreeOfParallelism = ocr.MaxConcurrency, CancellationToken = ct },
            async (item, token) =>
            {
                var ocrText = item.Png != null
                    ? OcrArtifactCleaner.Clean((await ocr.OcrImageAsync(item.Png, token)).Trim())
                    : "";
                var hasSufficientNativeText = item.NativeText.Length >= NativeTextSufficiencyThreshold;

                var method = (hasSufficientNativeText, ocrText.Length > 0) switch
                {
                    (true, true) => PageExtractionMethod.Mixed,
                    (true, false) => PageExtractionMethod.Native,
                    (false, true) => PageExtractionMethod.Ocr,
                    (false, false) => PageExtractionMethod.Empty,
                };

                var combinedText = method switch
                {
                    PageExtractionMethod.Mixed => item.NativeText + "\n\n" + ocrText,
                    PageExtractionMethod.Native => item.NativeText,
                    PageExtractionMethod.Ocr => ocrText,
                    _ => "",
                };

                results[item.PageNumber - 1] = new LocalPageResult(item.PageNumber, combinedText, method);
            });

        var sb = new StringBuilder();
        foreach (var p in results)
        {
            sb.AppendLine($"{PolicyPageResolver.PageMarkerPrefix}{p.PageNumber} -->");
            if (p.Text.Length > 0)
                sb.AppendLine(p.Text);
            sb.AppendLine();
        }

        if (results.Any(p => p.Method == PageExtractionMethod.Empty))
            logger.LogInformation(
                "{Count} of {Total} PDF page(s) had no readable text (native or OCR) — likely blank pages or unreadable scans",
                results.Count(p => p.Method == PageExtractionMethod.Empty), totalPages);

        return new LocalPdfResult(totalPages, sb.ToString().Trim(), results);
    }

    /// <summary>
    /// 200 DPI is the standard floor for reliable OCR (Tesseract's own guidance is 200-300).
    /// PDFium's unset default renders considerably higher, which — combined with the "best" (slow,
    /// high-accuracy) Tesseract model — multiplies runtime across every scanned page for no accuracy
    /// gain. Capping this is a lever on a multi-page scanned document.
    /// </summary>
    private static readonly PDFtoImage.RenderOptions OcrRenderOptions = new(Dpi: 200);

    /// <summary>Render one page (1-based) to PNG bytes via PDFium — works regardless of the page's
    /// internal image encoding, unlike PdfPig's embedded-image enumeration.</summary>
    private byte[]? SafeRenderPageToPng(byte[] pdfBytes, int pageNumber)
    {
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var bitmap = PDFtoImage.Conversion.ToImage(
                pdfBytes, password: null, page: (System.Index)(pageNumber - 1), options: OcrRenderOptions);
            using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            logger.LogInformation(
                "Rendered page {Page} ({W}x{H}) in {Ms}ms",
                pageNumber, bitmap.Width, bitmap.Height, sw.ElapsedMilliseconds);
            return data.ToArray();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not render page {Page} for OCR", pageNumber);
            return null;
        }
    }
}
