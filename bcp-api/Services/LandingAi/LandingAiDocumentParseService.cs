using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;

using Reguliq.Api.Services.NewDashboard;

namespace Reguliq.Api.Services.LandingAi;

/// <summary>
/// Parse uploads to markdown via Landing AI ADE.
/// PDFs longer than the API page cap are split automatically — there is no app-side page limit on total document size.
/// </summary>
public sealed class LandingAiDocumentParseService(
    LandingAiHttpClient client,
    IOptions<LandingAiOptions> options,
    ILogger<LandingAiDocumentParseService> logger)
{
    private readonly LandingAiOptions _opts = options.Value;

    public Task<string> ParseToMarkdownAsync(
        byte[] bytes,
        string fileName,
        CancellationToken ct = default)
        => ParseToMarkdownAsync(bytes, fileName, reportProgress: null, ct);

    public Task<string> ParseToMarkdownAsync(
        byte[] bytes,
        string fileName,
        Func<ExtractionProgressUpdate, Task>? reportProgress,
        RegulationParseCheckpoint? parseCheckpoint,
        CancellationToken ct = default)
    {
        if (bytes.Length == 0)
            throw new InvalidOperationException("Empty file.");

        if (LandingAiDocumentFormats.IsWordDocument(fileName))
        {
            return ParseWordAsync(bytes, fileName, reportProgress, ct);
        }

        if (LandingAiDocumentFormats.IsPdf(fileName, bytes))
            return ParsePdfToMarkdownAsync(bytes, fileName, reportProgress, parseCheckpoint, ct);

        throw new InvalidOperationException("Unsupported file type. Upload PDF or Word (.doc, .docx).");
    }

    public Task<string> ParseToMarkdownAsync(
        byte[] bytes,
        string fileName,
        Func<ExtractionProgressUpdate, Task>? reportProgress,
        CancellationToken ct = default)
        => ParseToMarkdownAsync(bytes, fileName, reportProgress, parseCheckpoint: null, ct);

    private async Task<string> ParseWordAsync(
        byte[] bytes,
        string fileName,
        Func<ExtractionProgressUpdate, Task>? reportProgress,
        CancellationToken ct)
    {
            await ReportAsync(reportProgress, "Parsing Word document…", 15);
            logger.LogInformation(
                "Landing AI Word parse ({File}, {Kb} KB)",
                fileName,
                bytes.Length / 1024);
            var md = await client.ParseDocumentAsync(bytes, fileName, ct);
            await ReportAsync(reportProgress, "Document parsed", 55);
            return md;
    }

    public Task<string> ParsePdfToMarkdownAsync(
        byte[] pdfBytes,
        string fileName,
        CancellationToken ct = default)
        => ParsePdfToMarkdownAsync(pdfBytes, fileName, reportProgress: null, parseCheckpoint: null, ct);

    private async Task<string> ParsePdfToMarkdownAsync(
        byte[] pdfBytes,
        string fileName,
        Func<ExtractionProgressUpdate, Task>? reportProgress,
        RegulationParseCheckpoint? parseCheckpoint,
        CancellationToken ct)
    {
        var maxPages = Math.Clamp(_opts.MaxParsePagesPerRequest, 1, 99);

        int? pageCount = TryGetPdfPageCount(pdfBytes, fileName);

        if (pageCount.HasValue && pageCount.Value > maxPages)
        {
            return await ParsePdfInChunksAsync(
                pdfBytes, fileName, maxPages, pageCount.Value, reportProgress, parseCheckpoint, ct);
        }

        if (pageCount.HasValue && pageCount.Value <= maxPages)
        {
            try
            {
                await ReportAsync(reportProgress, $"Parsing PDF ({pageCount} pages)…", 20);
                logger.LogInformation(
                    "Landing AI PDF parse ({File}, {Pages} pages, {Kb} KB)",
                    fileName,
                    pageCount,
                    pdfBytes.Length / 1024);
                ct.ThrowIfCancellationRequested();
                return await client.ParseDocumentAsync(pdfBytes, fileName, ct);
            }
            catch (InvalidOperationException ex) when (IsLandingAiPageLimitError(ex))
            {
                logger.LogWarning(
                    "Landing AI rejected {File} for page limit (local count {Pages}); retrying in chunks",
                    fileName,
                    pageCount);
                var total = pageCount > 0 ? pageCount.Value : RequirePdfPageCount(pdfBytes, fileName);
                return await ParsePdfInChunksAsync(
                    pdfBytes, fileName, maxPages, total, reportProgress, parseCheckpoint, ct);
            }
        }

        // Could not count pages locally — try once, then chunk if ADE returns 422.
        try
        {
            await ReportAsync(reportProgress, "Parsing PDF…", 20);
            logger.LogInformation(
                "Landing AI PDF parse ({File}, page count unknown, {Kb} KB)",
                fileName,
                pdfBytes.Length / 1024);
            ct.ThrowIfCancellationRequested();
            return await client.ParseDocumentAsync(pdfBytes, fileName, ct);
        }
        catch (InvalidOperationException ex) when (IsLandingAiPageLimitError(ex))
        {
            var total = RequirePdfPageCount(pdfBytes, fileName);
            return await ParsePdfInChunksAsync(
                pdfBytes, fileName, maxPages, total, reportProgress, parseCheckpoint, ct);
        }
    }

    private static async Task ReportAsync(
        Func<ExtractionProgressUpdate, Task>? reportProgress,
        string label,
        int? percent)
    {
        if (reportProgress == null) return;
        await reportProgress(new ExtractionProgressUpdate(label, percent));
    }

    private async Task<string> ParsePdfInChunksAsync(
        byte[] pdfBytes,
        string fileName,
        int maxPagesPerChunk,
        int totalPages,
        Func<ExtractionProgressUpdate, Task>? reportProgress,
        RegulationParseCheckpoint? parseCheckpoint,
        CancellationToken ct)
    {
        logger.LogInformation(
            "Landing AI chunked PDF parse ({File}, {TotalPages} pages, {ChunkSize} pages per request)",
            fileName,
            totalPages,
            maxPagesPerChunk);

        var chunks = SplitPdf(pdfBytes, maxPagesPerChunk);
        var startChunk = Math.Clamp(parseCheckpoint?.ResumeFromChunkIndex ?? 0, 0, Math.Max(0, chunks.Count - 1));
        var merged = new System.Text.StringBuilder(parseCheckpoint?.PartialMarkdown?.Trim() ?? "");
        if (merged.Length > 0 && startChunk > 0)
            merged.AppendLine();

        for (var i = startChunk; i < chunks.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var startPage = i * maxPagesPerChunk + 1;
            var endPage = Math.Min(startPage + maxPagesPerChunk - 1, totalPages);
            var pct = (int)Math.Round(((i + 0.15) / chunks.Count) * 55.0);
            await ReportAsync(
                reportProgress,
                $"Parsing pages {startPage}–{endPage} of {totalPages} (part {i + 1}/{chunks.Count})",
                pct);
            logger.LogInformation(
                "Landing AI PDF chunk {Index}/{Total} for {File} (pages {Start}-{End})",
                i + 1,
                chunks.Count,
                fileName,
                startPage,
                endPage);

            var chunkName = $"{Path.GetFileNameWithoutExtension(fileName)}_pages_{startPage}.pdf";
            var rawJson = await client.ParseDocumentRawAsync(chunks[i], chunkName, ct);
            var chunkMd = PolicyPageResolver.InjectPageMarkersFromParseJson(rawJson, "");
            if (string.IsNullOrWhiteSpace(chunkMd))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(rawJson);
                if (doc.RootElement.TryGetProperty("markdown", out var mdProp))
                    chunkMd = mdProp.GetString() ?? "";
            }

            if (string.IsNullOrWhiteSpace(chunkMd))
                throw new InvalidOperationException(
                    $"Landing AI returned no markdown for {fileName} pages {startPage}-{endPage}.");

            chunkMd = ShiftPageMarkers(chunkMd, startPage - 1);
            if (merged.Length > 0) merged.AppendLine();
            merged.AppendLine(chunkMd);

            if (parseCheckpoint?.OnChunkParsedAsync != null)
                await parseCheckpoint.OnChunkParsedAsync(i, merged.ToString().Trim());
        }

        await ReportAsync(reportProgress, "Document parsed", 58);
        return merged.ToString().Trim();
    }

    private int? TryGetPdfPageCount(byte[] pdfBytes, string fileName)
    {
        try
        {
            return GetPdfPageCount(pdfBytes);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read PDF page count for {File}", fileName);
            return null;
        }
    }

    private static int RequirePdfPageCount(byte[] pdfBytes, string fileName)
    {
        try
        {
            return GetPdfPageCount(pdfBytes);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Could not split {fileName} for Landing AI: PDF page count unavailable ({ex.Message}).",
                ex);
        }
    }

    internal static bool IsLandingAiPageLimitError(InvalidOperationException ex)
    {
        var msg = ex.Message;
        return msg.Contains("100", StringComparison.Ordinal)
            && msg.Contains("page", StringComparison.OrdinalIgnoreCase);
    }

    internal static int GetPdfPageCount(byte[] pdfBytes)
    {
        using var stream = new MemoryStream(pdfBytes);
        using var doc = PdfReader.Open(stream, PdfDocumentOpenMode.Import);
        return doc.PageCount;
    }

    internal static List<byte[]> SplitPdf(byte[] pdfBytes, int maxPagesPerChunk)
    {
        if (maxPagesPerChunk < 1) maxPagesPerChunk = 99;

        using var inputStream = new MemoryStream(pdfBytes);
        using var input = PdfReader.Open(inputStream, PdfDocumentOpenMode.Import);
        var total = input.PageCount;
        var chunks = new List<byte[]>();

        for (var start = 0; start < total; start += maxPagesPerChunk)
        {
            var output = new PdfDocument();
            var end = Math.Min(start + maxPagesPerChunk, total);
            for (var i = start; i < end; i++)
                output.AddPage(input.Pages[i]);

            using var ms = new MemoryStream();
            output.Save(ms, false);
            chunks.Add(ms.ToArray());
        }

        return chunks;
    }

    public static string ShiftPageMarkers(string markdown, int pageOffset)
    {
        if (pageOffset == 0 || string.IsNullOrWhiteSpace(markdown)) return markdown;

        var pattern = Regex.Escape(PolicyPageResolver.PageMarkerPrefix) + @"(\d+)\s*-->";
        return Regex.Replace(
            markdown,
            pattern,
            m =>
            {
                if (!int.TryParse(m.Groups[1].Value, out var page)) return m.Value;
                return $"{PolicyPageResolver.PageMarkerPrefix}{page + pageOffset} -->";
            });
    }
}
