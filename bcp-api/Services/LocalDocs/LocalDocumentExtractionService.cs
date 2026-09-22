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
    AzureDocumentIntelligenceClient azureDocIntelligence,
    AzureOpenAIEmbeddingClient azureOpenAI,
    ILogger<LocalDocumentExtractionService> logger)
{
    /// <summary>Consecutive sentences whose embedding cosine similarity falls below this cut a new
    /// chunk. Not empirically tuned yet — a starting point, expected to need adjustment once compared
    /// against real documents (that comparison is the whole point of this feature). See
    /// docs/pipeline/STRUCTURAL-EXTRACTION-NESTED-NUMBERING-BUG.md for the case this is meant to fix.</summary>
    private const double SemanticSimilarityThreshold = 0.5;

    /// <summary>A real clause can legitimately run to any length — this is deliberately NOT a normal
    /// operating limit, only a last-resort safety net against a genuinely pathological case (e.g. an
    /// embedding-quality issue that makes the whole document register as "similar" and never cuts at
    /// all). Kept high enough that it should essentially never fire on real documents — if it ever does,
    /// that's a sign the similarity threshold itself needs attention, not that this number needs
    /// tuning. A compliance clause must never be silently split just because it ran long.</summary>
    private const int SemanticMaxSentencesPerChunk = 60;

    private static readonly Regex SentenceSplitPattern = new(@"(?<=[.!?:;])\s+(?=[A-Z0-9])", RegexOptions.Compiled);
    /// <summary>Azure's markdown output (outputContentFormat=markdown) inserts this literal comment
    /// between pages — real page boundaries, unlike Docling's output which carries none at all.</summary>
    private const string AzurePageBreakMarker = "<!-- PageBreak -->";

    /// <summary>Layout metadata Azure embeds inline (repeated header/footer text, printed page numbers) —
    /// noise for clause/section extraction, so strip it rather than let it pollute regex matching.</summary>
    private static readonly Regex AzureLayoutCommentPattern =
        new(@"<!--\s*Page(Header|Footer|Number)=""[^""]*""\s*-->\n?", RegexOptions.Compiled);

    /// <summary>
    /// Step 1, via Azure AI Document Intelligence instead of a local OCR engine — same whole-document
    /// shape as <see cref="ParseWithDoclingAsync"/> (Azure does its own layout analysis), but unlike
    /// Docling, Azure's markdown output carries real page-boundary markers (<see cref="AzurePageBreakMarker"/>)
    /// we can translate into our own BCP_PDF_PAGE:N markers — so this engine gets genuine per-page
    /// references, not a single-page wrapper.
    /// </summary>
    public async Task<LocalParseResult> ParseWithAzureDocIntelligenceAsync(
        string sourceUrl, string fileName, CancellationToken ct = default)
    {
        var result = await azureDocIntelligence.ConvertAsync(sourceUrl, ct);
        var rawPages = result.Markdown.Split(AzurePageBreakMarker);

        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < rawPages.Length; i++)
        {
            var pageText = AzureLayoutCommentPattern.Replace(rawPages[i], "").Trim();
            sb.Append(PolicyPageResolver.PageMarkerPrefix).Append(i + 1).Append(" -->\n");
            sb.Append(pageText).Append('\n');
        }
        var markdown = sb.ToString();

        var warnings = new List<string>
        {
            $"Parsed via Azure Document Intelligence in {result.ElapsedSeconds:F1}s ({rawPages.Length} page(s)).",
        };

        logger.LogInformation(
            "Azure Document Intelligence parse for {File}: {Pages} pages, {Elapsed:F1}s",
            fileName, rawPages.Length, result.ElapsedSeconds);

        return new LocalParseResult(fileName, rawPages.Length, rawPages.Length, markdown, warnings);
    }

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

    /// <summary>
    /// Step 2, alternative method — splits already-parsed markdown by meaning instead of by numbering.
    /// Embeds each sentence (via Azure OpenAI, <see cref="AzureOpenAIEmbeddingClient"/>) and starts a new
    /// chunk wherever consecutive sentences' cosine similarity drops below
    /// <see cref="SemanticSimilarityThreshold"/> — so a numbered sub-list nested inside a clause (which
    /// the regex splitter in <see cref="LocalSectionSplitter"/> mistakes for a new top-level clause,
    /// see docs/pipeline/STRUCTURAL-EXTRACTION-NESTED-NUMBERING-BUG.md) stays together here as long as
    /// it's still talking about the same thing. Never rewrites or summarizes — every chunk's text is the
    /// original sentences, verbatim, just grouped differently than structural extraction groups them.
    /// Runs independently of <see cref="ExtractFromMarkdown"/> and does not require it to have run.
    /// </summary>
    private static readonly Regex HtmlTagPattern = new(@"<[^>]+>", RegexOptions.Compiled);

    /// <summary>A "sentence" with fewer words than this is never real clause prose on its own — a
    /// letterhead word, a stray OCR token, a lone heading fragment. Merged into the neighboring
    /// sentence rather than left to become its own meaningless chunk. Nothing is discarded — only
    /// regrouped — so no clause text is ever lost.</summary>
    private const int SemanticMinWordsPerSentence = 4;

    /// <summary>A finished chunk this short (e.g. a cover-page bank letterhead line, a document title)
    /// is never a real clause either — merged into the adjacent chunk after chunking rather than kept
    /// as its own standalone result. Bilingual letterhead lines (Arabic + English bank name) can still
    /// clear a low word count, so this is set generously above what any single title/letterhead line
    /// realistically reaches — a real clause is virtually always well past this.</summary>
    private const int SemanticMinWordsPerChunk = 20;

    public async Task<LocalExtractionResult> ExtractSemanticAsync(
        string fileName, string markdown, int totalPages, int ocrPageCount, CancellationToken ct = default)
    {
        var pages = SplitMarkdownIntoPages(markdown);

        // Line breaks alone are NOT sentence breaks — treating each line independently (as before) let
        // a bare header line or a single stray word become its own "sentence", indistinguishable from a
        // real one. Join each page's lines into continuous prose first, then split on real sentence
        // punctuation only.
        var sentences = new List<(string Text, int? Page)>();
        foreach (var page in pages)
        {
            // Tables (Azure's markdown output for headings/TOC-style content, e.g. this document's own
            // table of contents) are structural data, not prose — meaning-based chunking on individual
            // <td>/<tr> fragments produces meaningless chunks ("<td>6</td>", "Purpose"), so skip table
            // content entirely for this method rather than feed it in as if it were sentences.
            var inTable = false;
            var pageLines = new List<string>();
            foreach (var rawLine in page.Text.Split('\n'))
            {
                if (rawLine.Contains("<table", StringComparison.OrdinalIgnoreCase)) { inTable = true; continue; }
                if (rawLine.Contains("</table", StringComparison.OrdinalIgnoreCase)) { inTable = false; continue; }
                if (inTable) continue;

                // Strip any remaining inline HTML (Azure occasionally emits tags like <figure>/</figure>
                // outside tables too) — never chunk the markup itself, only the real text it wraps.
                var trimmed = HtmlTagPattern.Replace(rawLine, "").Trim();
                if (trimmed.Length >= 3) pageLines.Add(trimmed);
            }

            var pageText = string.Join(' ', pageLines);
            foreach (var sentence in SentenceSplitPattern.Split(pageText))
            {
                var s = sentence.Trim();
                if (s.Length >= 3) sentences.Add((s, page.PageNumber));
            }
        }

        // Fold fragment "sentences" (too few words to be real clause prose) into the next sentence.
        for (var i = 0; i < sentences.Count - 1; i++)
        {
            var wordCount = sentences[i].Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            if (wordCount >= SemanticMinWordsPerSentence) continue;
            sentences[i + 1] = ($"{sentences[i].Text} {sentences[i + 1].Text}", sentences[i].Page ?? sentences[i + 1].Page);
            sentences.RemoveAt(i);
            i--;
        }
        if (sentences.Count > 1)
        {
            var lastIdx = sentences.Count - 1;
            var lastWordCount = sentences[lastIdx].Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            if (lastWordCount < SemanticMinWordsPerSentence)
            {
                sentences[lastIdx - 1] = ($"{sentences[lastIdx - 1].Text} {sentences[lastIdx].Text}", sentences[lastIdx - 1].Page);
                sentences.RemoveAt(lastIdx);
            }
        }

        var warnings = new List<string>();
        if (sentences.Count == 0)
        {
            warnings.Add("No readable text found to chunk semantically.");
            return new LocalExtractionResult(fileName, totalPages, ocrPageCount, markdown, [], warnings);
        }

        // Embedding calls are independent of each other (order only matters for the cosine-similarity
        // pass afterward, done separately below) — running them one at a time was the real reason this
        // was slow enough to hit client timeouts on a real document. Bounded concurrency here keeps this
        // fast without hammering Azure OpenAI's rate limit with dozens of simultaneous requests.
        var embeddings = new float[sentences.Count][];
        using (var gate = new SemaphoreSlim(8))
        {
            var tasks = new Task[sentences.Count];
            for (var i = 0; i < sentences.Count; i++)
            {
                var index = i;
                tasks[index] = Task.Run(async () =>
                {
                    await gate.WaitAsync(ct);
                    try
                    {
                        embeddings[index] = await azureOpenAI.EmbedAsync(sentences[index].Text, ct);
                    }
                    finally
                    {
                        gate.Release();
                    }
                }, ct);
            }
            await Task.WhenAll(tasks);
        }

        var sections = new List<LocalSection>();
        var chunkText = new System.Text.StringBuilder();
        var chunkStartPage = sentences[0].Page;
        var chunkIndex = 1;
        var chunkSentenceCount = 1;

        void FlushChunk()
        {
            var text = chunkText.ToString().Trim();
            if (text.Length > 0)
                sections.Add(new LocalSection($"Chunk {chunkIndex}", text, chunkStartPage));
            chunkText.Clear();
            chunkIndex++;
            chunkSentenceCount = 0;
        }

        chunkText.Append(sentences[0].Text);
        for (var i = 1; i < sentences.Count; i++)
        {
            var similarity = CosineSimilarity(embeddings[i - 1], embeddings[i]);
            if (similarity < SemanticSimilarityThreshold || chunkSentenceCount >= SemanticMaxSentencesPerChunk)
            {
                FlushChunk();
                chunkStartPage = sentences[i].Page;
            }
            else
            {
                chunkText.Append(' ');
            }
            chunkText.Append(sentences[i].Text);
            chunkSentenceCount++;
        }
        FlushChunk();

        // A chunk can still end up tiny even after the sentence-level fold above — a short cover-page
        // letterhead line ("CENTRAL BANK OF THE U.A.E.") has enough words to survive that filter but is
        // still not real clause prose, and gets cut into its own chunk purely because it's semantically
        // unrelated to whatever text sits next to it. Fold any such leftover short chunk into its
        // neighbor rather than report it as if it were a real clause. Text is merged, never dropped.
        for (var i = 0; i < sections.Count - 1; i++)
        {
            var wordCount = sections[i].ClauseText.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            if (wordCount >= SemanticMinWordsPerChunk) continue;
            sections[i + 1] = sections[i + 1] with
            {
                ClauseText = $"{sections[i].ClauseText} {sections[i + 1].ClauseText}",
                SourcePage = sections[i].SourcePage ?? sections[i + 1].SourcePage,
            };
            sections.RemoveAt(i);
            i--;
        }
        if (sections.Count > 1)
        {
            var lastIdx = sections.Count - 1;
            var lastWordCount = sections[lastIdx].ClauseText.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            if (lastWordCount < SemanticMinWordsPerChunk)
            {
                sections[lastIdx - 1] = sections[lastIdx - 1] with
                {
                    ClauseText = $"{sections[lastIdx - 1].ClauseText} {sections[lastIdx].ClauseText}",
                };
                sections.RemoveAt(lastIdx);
            }
        }
        for (var i = 0; i < sections.Count; i++)
            sections[i] = sections[i] with { ClauseNo = $"Chunk {i + 1}" };

        logger.LogInformation(
            "Semantic extract for {File}: {Sentences} sentence(s) -> {Chunks} chunk(s)",
            fileName, sentences.Count, sections.Count);

        return new LocalExtractionResult(fileName, totalPages, ocrPageCount, markdown, sections, warnings);
    }

    private static double CosineSimilarity(float[] a, float[] b)
    {
        double dot = 0, magA = 0, magB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            magA += a[i] * a[i];
            magB += b[i] * b[i];
        }
        if (magA == 0 || magB == 0) return 0;
        return dot / (Math.Sqrt(magA) * Math.Sqrt(magB));
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
