using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Reguliq.Api.Services.LocalDocs;

/// <summary>
/// Real page numbers for Word files. Azure Document Intelligence does not paginate .docx (the whole
/// file comes back as page 1), so every section would otherwise cite page 1. Word stores where it
/// last laid out each page break (<c>w:lastRenderedPageBreak</c>); reading those gives the page each
/// paragraph appeared on when the file was last saved in Word. Files saved without layout information
/// fall back to explicit page breaks.
/// </summary>
public static class DocxRenderedPages
{
    public sealed record PagedText(string NormalizedText, IReadOnlyList<(int Start, int Page)> PageStarts, int PageCount);

    public static PagedText? Read(byte[] docxBytes)
    {
        try
        {
            using var stream = new MemoryStream(docxBytes);
            using var doc = WordprocessingDocument.Open(stream, false);
            var body = doc.MainDocumentPart?.Document?.Body;
            if (body == null) return null;

            var useRendered = body.Descendants<LastRenderedPageBreak>().Any();
            var raw = new StringBuilder();
            var rawStarts = new List<(int Start, int Page)> { (0, 1) };
            var page = 1;

            foreach (var para in body.Descendants<Paragraph>())
            {
                if (!useRendered && para.ParagraphProperties?.PageBreakBefore != null && raw.Length > 0)
                    rawStarts.Add((raw.Length, ++page));

                foreach (var el in para.Descendants())
                {
                    var isBreak = useRendered
                        ? el is LastRenderedPageBreak
                        : el is Break br && br.Type?.Value == BreakValues.Page;
                    if (isBreak && raw.Length > 0) rawStarts.Add((raw.Length, ++page));
                    else if (el is Text t) raw.Append(t.Text);
                }
                raw.Append('\n');
            }

            var (normalized, map) = NormalizeWithMap(raw.ToString());
            var starts = rawStarts
                .Select(s =>
                {
                    var idx = map.BinarySearch(s.Start);
                    return (Start: idx < 0 ? ~idx : idx, s.Page);
                })
                .ToList();
            return new PagedText(normalized, starts, page);
        }
        catch
        {
            return null;
        }
    }

    public static bool IsWordFile(string? fileName) =>
        string.Equals(Path.GetExtension(fileName ?? ""), ".docx", StringComparison.OrdinalIgnoreCase);

    /// <summary>Re-pages an extract from the Word file's own layout; unchanged when the file has none.</summary>
    public static LocalExtractionResult Apply(byte[] docxBytes, LocalExtractionResult result)
    {
        var paged = Read(docxBytes);
        if (paged == null || paged.PageCount <= 1) return result;
        return result with { Sections = Apply(paged, result.Sections), TotalPages = paged.PageCount };
    }

    /// <summary>Re-pages sections by finding where each one's text starts in the Word file.</summary>
    public static List<LocalSection> Apply(PagedText paged, IReadOnlyList<LocalSection> sections)
    {
        var result = new List<LocalSection>(sections.Count);
        var cursor = 0;
        foreach (var section in sections)
        {
            var at = Find(paged.NormalizedText, section.ClauseText, cursor);
            if (at < 0)
            {
                result.Add(section);
                continue;
            }
            cursor = at;
            result.Add(section with { SourcePage = PageAt(paged, at) });
        }
        return result;
    }

    /// <summary>Page the given text first appears on, or null when it cannot be found.</summary>
    public static int? PageOf(PagedText paged, string text)
    {
        var at = Find(paged.NormalizedText, text, 0);
        return at < 0 ? null : PageAt(paged, at);
    }

    private static int Find(string haystack, string text, int from)
    {
        var words = NormalizeWithMap(text).Normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return -1;
        // Word keeps list numbering out of the text, so a section that opens "3.2 Title" may read
        // "Title" in the file — retry without the first few tokens, and with shorter openings.
        for (var skip = 0; skip <= Math.Min(3, words.Length - 1); skip++)
        {
            foreach (var take in new[] { 14, 8, 5 })
            {
                if (words.Length - skip < Math.Min(take, 3)) continue;
                var probe = string.Join(' ', words.Skip(skip).Take(take));
                if (probe.Length < 12) continue;
                var at = haystack.IndexOf(probe, from, StringComparison.Ordinal);
                if (at < 0 && from > 0) at = haystack.IndexOf(probe, StringComparison.Ordinal);
                if (at >= 0) return at;
            }
        }
        return -1;
    }

    private static int PageAt(PagedText paged, int offset)
    {
        var page = 1;
        foreach (var (start, p) in paged.PageStarts)
        {
            if (start > offset) break;
            page = p;
        }
        return page;
    }

    /// <summary>Lower-case letters and digits, every other run of characters collapsed to one space;
    /// <c>map[i]</c> is the original index of normalized character <c>i</c>.</summary>
    private static (string Normalized, List<int> Map) NormalizeWithMap(string text)
    {
        var sb = new StringBuilder(text.Length);
        var map = new List<int>(text.Length);
        var pendingSpace = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = char.ToLowerInvariant(text[i]);
            if (char.IsLetterOrDigit(c))
            {
                if (pendingSpace && sb.Length > 0)
                {
                    sb.Append(' ');
                    map.Add(i);
                }
                pendingSpace = false;
                sb.Append(c);
                map.Add(i);
            }
            else
            {
                pendingSpace = true;
            }
        }
        return (sb.ToString(), map);
    }
}
