using System.Text.RegularExpressions;
using Reguliq.Api.Data.NewDashboard.Entities;
using Reguliq.Api.Models;
using Reguliq.Api.Services.LandingAi;

namespace Reguliq.Api.Services.NewDashboard;

/// <summary>
/// Regul.ai policy context for forward judgment: full manual when ≤50 pages, else keyword retrieval per clause.
/// </summary>
public static class NdRegulPolicyContextService
{
    public const int FullManualMaxPages = 50;
    public const int RetrievalTopChunks = 12;

    public sealed record PolicyChunk(
        string Label,
        string Text,
        string? SourceDoc,
        string? SectionRef,
        int? SourcePage);

    public sealed record PolicyBundle(
        IReadOnlyList<PolicyChunk> Chunks,
        int TotalPages,
        string SourceTextForQuotes,
        IReadOnlyDictionary<string, string> MarkdownByFile)
    {
        public string BuildFullContext()
        {
            if (MarkdownByFile.Count > 0)
                return string.Join(
                    "\n\n",
                    MarkdownByFile.Select(kv => $"=== DOCUMENT: {kv.Key} ===\n{kv.Value}"));

            return string.Join("\n\n", Chunks.Select(c => $"[{c.Label}]\n{c.Text}"));
        }

        public string BuildContextForClause(string clauseText) =>
            TotalPages <= FullManualMaxPages
                ? BuildFullContext()
                : BuildRetrievedContext(clauseText);

        public IReadOnlyList<PolicyChunk> GetChunksForClause(string clauseText) =>
            TotalPages <= FullManualMaxPages
                ? Chunks
                : RankPolicyChunks(Chunks, clauseText).Take(RetrievalTopChunks).ToList();

        public PolicyBundle WithMarkdownFromPayloads(IReadOnlyList<InternalDocPayload> payloads)
        {
            var dict = new Dictionary<string, string>(MarkdownByFile, StringComparer.OrdinalIgnoreCase);
            foreach (var p in payloads)
            {
                var fileName = (p.FileName ?? "").Trim();
                if (fileName.Length > 0 && !string.IsNullOrWhiteSpace(p.Markdown))
                    dict[fileName] = p.Markdown;
            }

            return new PolicyBundle(Chunks, TotalPages, SourceTextForQuotes, dict);
        }

        private string BuildRetrievedContext(string clauseText)
        {
            var ranked = GetChunksForClause(clauseText);
            if (ranked.Count == 0)
                return BuildFullContext();

            return string.Join("\n\n", ranked.Select(c => $"[{c.Label}]\n{c.Text}"));
        }
    }

    public static PolicyBundle FromPayloads(IReadOnlyList<InternalDocPayload> payloads)
    {
        var chunks = new List<PolicyChunk>();
        var markdownByFile = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var totalPages = 0;

        foreach (var p in payloads)
        {
            var fileName = string.IsNullOrWhiteSpace(p.FileName) ? "internal policy" : p.FileName.Trim();
            var markdown = p.Markdown ?? "";
            if (markdown.Length > 0)
                markdownByFile[fileName] = markdown;

            var pages = PolicyPageResolver.EstimatePageCount(markdown) ?? 1;
            totalPages += Math.Max(1, pages);

            foreach (var (page, text) in PolicyPageResolver.SplitMarkdownIntoPageSegments(markdown))
            {
                if (string.IsNullOrWhiteSpace(text)) continue;
                chunks.Add(new PolicyChunk($"{fileName} p.{page}", text.Trim(), fileName, null, page));
            }
        }

        if (chunks.Count == 0 && markdownByFile.Count > 0)
        {
            foreach (var kv in markdownByFile)
                chunks.Add(new PolicyChunk(kv.Key, kv.Value.Trim(), kv.Key, null, null));
        }

        var sourceText = string.Join("\n\n", markdownByFile.Values);
        return new PolicyBundle(chunks, totalPages, sourceText, markdownByFile);
    }

    /// <summary>
    /// Forward judgment context from per-run internal sections (same corpus reverse uses).
    /// Each section is its own retrieval chunk so keyword ranking can surface relevant text.
    /// </summary>
    public static PolicyBundle FromInternalSections(IReadOnlyList<NdRegulInternalSection> sections)
    {
        if (sections.Count == 0)
            return FromPayloads([
                new InternalDocPayload("", "policy", "No internal policy text was attached to this run.", null),
            ]);

        var chunks = sections
            .Where(s => !string.IsNullOrWhiteSpace(s.SectionText))
            .OrderBy(s => s.SourceDoc, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.SectionRef, StringComparer.Ordinal)
            .Select(s =>
            {
                var fileName = s.SourceDoc ?? "internal policy";
                var sectionRef = string.IsNullOrWhiteSpace(s.SectionRef) ? "Section" : s.SectionRef.Trim();
                var pageLabel = s.SourcePage.HasValue ? $" p.{s.SourcePage}" : "";
                var label = $"{fileName} — {sectionRef}{pageLabel}";
                return new PolicyChunk(label, s.SectionText!.Trim(), fileName, sectionRef, s.SourcePage);
            })
            .ToList();

        if (chunks.Count == 0)
            return FromPayloads([
                new InternalDocPayload("", "policy", "Internal sections exist but contain no extractable text.", null),
            ]);

        var maxPage = sections.Where(s => s.SourcePage is > 0).Select(s => s.SourcePage!.Value).DefaultIfEmpty(0).Max();
        var totalPages = Math.Max(chunks.Count, maxPage);
        var sourceText = string.Join("\n\n", sections.Select(s => s.SectionText ?? ""));
        return new PolicyBundle(chunks, totalPages, sourceText, new Dictionary<string, string>());
    }

    private static List<PolicyChunk> RankPolicyChunks(
        IReadOnlyList<PolicyChunk> chunks,
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
}
