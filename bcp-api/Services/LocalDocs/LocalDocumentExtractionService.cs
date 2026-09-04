using System.Text.RegularExpressions;
using Reguliq.Api.Services.LandingAi;

namespace Reguliq.Api.Services.LocalDocs;

/// <summary>Step 1 output — just text, with page references. No section/point splitting yet.</summary>
public sealed record LocalParseResult(
    string FileName,
    int TotalPages,
    int OcrPageCount,
    string Markdown,
    IReadOnlyList<string> Warnings);

/// <summary>Step 2 output — sections/points split out of already-parsed markdown.</summary>
public sealed record LocalExtractionResult(
    string FileName,
    int TotalPages,
    int OcrPageCount,
    string Markdown,
    IReadOnlyList<LocalSection> Sections,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Local (non-AI) parse and extract — two genuinely separate steps, not one combined action:
///   1. Parse: PdfPig/Tesseract/OpenXml -> plain text with page references (BCP_PDF_PAGE:N markers),
///      same format the rest of this codebase already uses for page-accurate citations.
///   2. Extract: regex-based clause/point splitting, run against already-parsed text — cheap and
///      instant, safe to re-run any time without re-parsing or re-OCR'ing.
/// Costs $0 and sends nothing to any external service. See docs/discussion/REGUL-PIPELINE-BUILD-PLAN.md.
/// </summary>
public sealed class LocalDocumentExtractionService(
    LocalPdfExtractionService pdf,
    LocalDocxExtractionService docx,
    DoclingClient docling,
    ILogger<LocalDocumentExtractionService> logger)
{
    /// <summary>
    /// Step 1, via Docling instead of PdfPig/Tesseract/RapidOCR — Docling converts the whole PDF itself
    /// (its own page splitting/layout analysis), so unlike <see cref="ParseAsync"/> there's no per-page
    /// IOcrEngine involved. Local testing only, via the Python service in docling-service/ — see
    /// <see cref="DoclingClient"/>. The result is wrapped under a single page marker (page 1) rather than
    /// real per-page markers, since Docling's own output doesn't preserve our page-boundary format — a
    /// known simplification for this first version, not a limitation of Docling itself.
    /// </summary>
    public async Task<LocalParseResult> ParseWithDoclingAsync(
        byte[] bytes, string fileName, string mode, CancellationToken ct = default)
    {
        if (!fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Docling mode currently only supports PDF in this local test setup.");

        var result = await docling.ConvertAsync(bytes, fileName, mode, ct);
        var markdown = $"{PolicyPageResolver.PageMarkerPrefix}1 -->\n{result.Markdown}";
        var warnings = new List<string>
        {
            $"Parsed via Docling ({mode}) in {result.ElapsedSeconds:F1}s — page references are not yet " +
            "per-page for Docling (whole document treated as one page); this is a known simplification.",
        };

        logger.LogInformation(
            "Docling parse ({Mode}) for {File}: {Pages} pages, {Elapsed:F1}s",
            mode, fileName, result.Pages, result.ElapsedSeconds);

        return new LocalParseResult(fileName, result.Pages, result.Pages, markdown, warnings);
    }

    /// <summary>Step 1 only — parse to text with page references. Does not detect clauses/points.
    /// <paramref name="ocr"/> selects which engine reads scanned pages — see <see cref="OcrEngineRegistry"/>.</summary>
    public async Task<LocalParseResult> ParseAsync(byte[] bytes, string fileName, IOcrEngine ocr, CancellationToken ct = default)
    {
        if (!SupportedDocumentTypes.IsSupported(fileName))
            throw new NotSupportedException(
                $"'{Path.GetExtension(fileName)}' is not supported. Allowed types: {SupportedDocumentTypes.DescribeAllowed()}.");

        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        var warnings = new List<string>();

        LocalPdfResult parsed = ext switch
        {
            ".pdf" => await pdf.ExtractAsync(bytes, ocr, ct),
            ".docx" => docx.Extract(bytes),
            _ => throw new NotSupportedException($"Unhandled extension '{ext}'."),
        };

        if (parsed.OcrPageCount > 0)
            warnings.Add($"{parsed.OcrPageCount} of {parsed.TotalPages} page(s) needed OCR (scanned or image content) — review these for accuracy.");

        var emptyPages = parsed.Pages.Count(p => p.Method == PageExtractionMethod.Empty);
        if (emptyPages > 0)
            warnings.Add($"{emptyPages} page(s) produced no readable text at all (blank page, or OCR could not read it).");

        logger.LogInformation(
            "Local parse for {File}: {Pages} pages ({Ocr} via OCR)",
            fileName, parsed.TotalPages, parsed.OcrPageCount);

        return new LocalParseResult(fileName, parsed.TotalPages, parsed.OcrPageCount, parsed.Markdown, warnings);
    }

    /// <summary>
    /// Step 2 — split already-parsed markdown (from <see cref="Parse"/>) into clauses/points. Does not
    /// touch the PDF again; reconstructs per-page text from the BCP_PDF_PAGE:N markers Parse wrote.
    /// </summary>
    public LocalExtractionResult ExtractFromMarkdown(string fileName, string markdown, int totalPages, int ocrPageCount)
    {
        var pages = SplitMarkdownIntoPages(markdown);
        var sections = LocalSectionSplitter.Split(pages);

        var warnings = new List<string>();
        if (sections.Count == 0)
            warnings.Add("No numbered clauses/sections were detected — this document may not use a numbering convention this splitter recognizes.");

        logger.LogInformation("Local extract for {File}: {Sections} section(s) detected", fileName, sections.Count);

        return new LocalExtractionResult(fileName, totalPages, ocrPageCount, markdown, sections, warnings);
    }

    /// <summary>Reverses Parse's own markdown format — splits on the BCP_PDF_PAGE:N markers it wrote.</summary>
    private static List<LocalPageResult> SplitMarkdownIntoPages(string markdown)
    {
        var pattern = Regex.Escape(PolicyPageResolver.PageMarkerPrefix) + @"(\d+)\s*-->";
        var matches = Regex.Matches(markdown, pattern);
        var pages = new List<LocalPageResult>();

        for (var i = 0; i < matches.Count; i++)
        {
            var start = matches[i].Index + matches[i].Length;
            var end = i + 1 < matches.Count ? matches[i + 1].Index : markdown.Length;
            var pageNum = int.Parse(matches[i].Groups[1].Value);
            var text = markdown[start..end].Trim();
            pages.Add(new LocalPageResult(pageNum, text, PageExtractionMethod.Native));
        }

        return pages;
    }
}
