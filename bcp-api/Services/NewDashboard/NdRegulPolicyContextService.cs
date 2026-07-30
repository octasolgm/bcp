using System.Text.RegularExpressions;
using Reguliq.Api.Models;
using Reguliq.Api.Services.LandingAi;

namespace Reguliq.Api.Services.NewDashboard;

/// <summary>
/// Regul.ai policy context for forward judgment: full manual when ≤50 pages, else keyword retrieval per clause.
/// </summary>
public static class NdRegulPolicyContextService
{
    public const int FullManualMaxPages = 50;
    public const int RetrievalTopChunks = 8;

    private static readonly Regex PageMarkerRegex = new(
        @"<!--\s*Page\s+(\d+)\s*-->",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public sealed record PolicyDoc(string FileName, string Markdown, int PageCount);

    public sealed record PolicyBundle(IReadOnlyList<PolicyDoc> Docs, int TotalPages, string SourceTextForQuotes)
    {
        public string BuildFullContext() =>
            string.Join(
                "\n\n",
                Docs.Select(d => $"=== DOCUMENT: {d.FileName} ===\n{d.Markdown}"));

        public string BuildContextForClause(string clauseText) =>
            TotalPages <= FullManualMaxPages
                ? BuildFullContext()
                : BuildRetrievedContext(clauseText);

        private string BuildRetrievedContext(string clauseText)
        {
            var chunks = new List<(string Label, string Text)>();
            foreach (var doc in Docs)
            {
                foreach (var (page, text) in SplitIntoPageChunks(doc.Markdown))
                {
                    var label = $"{doc.FileName} p.{page}";
                    chunks.Add((label, text));
                }
            }

            if (chunks.Count == 0)
                return BuildFullContext();

            var ranked = RankChunks(chunks, clauseText).Take(RetrievalTopChunks).ToList();
            return string.Join(
                "\n\n",
                ranked.Select(c => $"[{c.Label}]\n{c.Text}"));
        }
    }

    public static PolicyBundle FromPayloads(IReadOnlyList<InternalDocPayload> payloads)
    {
        var docs = payloads.Select(p =>
        {
            var pages = PolicyPageResolver.EstimatePageCount(p.Markdown) ?? 1;
            return new PolicyDoc(p.FileName, p.Markdown, Math.Max(1, pages));
        }).ToList();

        var totalPages = docs.Sum(d => d.PageCount);
        var sourceText = string.Join("\n\n", docs.Select(d => d.Markdown));
        return new PolicyBundle(docs, totalPages, sourceText);
    }

    private static List<(string Label, string Text)> RankChunks(
        IReadOnlyList<(string Label, string Text)> chunks,
        string clauseText)
    {
        var keywords = ExtractKeywords(clauseText);
        if (keywords.Count == 0)
            return chunks.Take(RetrievalTopChunks).ToList();

        return chunks
            .Select(c => (c, ScoreChunk(c.Text, keywords)))
            .OrderByDescending(x => x.Item2)
            .ThenBy(x => x.c.Label, StringComparer.Ordinal)
            .Select(x => x.c)
            .ToList();
    }

    private static int ScoreChunk(string text, HashSet<string> keywords)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        var normalized = NormalizeForMatching(text);
        var score = 0;
        foreach (var kw in keywords)
        {
            if (normalized.Contains(kw, StringComparison.Ordinal))
                score++;
        }
        return score;
    }

    private static HashSet<string> ExtractKeywords(string text)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(text, @"[A-Za-z][A-Za-z0-9'-]{2,}"))
        {
            var w = m.Value.ToLowerInvariant();
            if (w.Length >= 4)
                set.Add(NormalizeForMatching(w));
        }
        return set;
    }

    public static string NormalizeForMatching(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var s = text.ToLowerInvariant();
        s = Regex.Replace(s, @"[\u2018\u2019\u201A\u201B'`]", "'");
        s = Regex.Replace(s, @"[\u201C\u201D\u201E\u201F""]", "\"");
        s = Regex.Replace(s, @"[^\w\s'""-]", " ");
        s = Regex.Replace(s, @"\s+", " ").Trim();
        return s;
    }

    private static List<(int Page, string Text)> SplitIntoPageChunks(string markdown)
    {
        var matches = PageMarkerRegex.Matches(markdown);
        if (matches.Count == 0)
        {
            return ChunkBySize(markdown, 1);
        }

        var segments = new List<(int Page, string Text)>();
        for (var i = 0; i < matches.Count; i++)
        {
            var page = int.TryParse(matches[i].Groups[1].Value, out var p) ? p : i + 1;
            var start = matches[i].Index + matches[i].Length;
            var end = i + 1 < matches.Count ? matches[i + 1].Index : markdown.Length;
            var slice = markdown.Substring(start, end - start).Trim();
            if (!string.IsNullOrWhiteSpace(slice))
                segments.Add((page, slice));
        }

        return segments.Count > 0 ? segments : ChunkBySize(markdown, 1);
    }

    private static List<(int Page, string Text)> ChunkBySize(string markdown, int startPage)
    {
        const int chunkSize = 3500;
        var list = new List<(int, string)>();
        var text = markdown.Trim();
        if (string.IsNullOrEmpty(text))
            return list;

        var page = startPage;
        for (var i = 0; i < text.Length; i += chunkSize)
        {
            var slice = text.Substring(i, Math.Min(chunkSize, text.Length - i)).Trim();
            if (!string.IsNullOrWhiteSpace(slice))
                list.Add((page++, slice));
        }
        return list;
    }
}
