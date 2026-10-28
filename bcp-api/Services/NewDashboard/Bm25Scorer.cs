using System.Text.RegularExpressions;

namespace Reguliq.Api.Services.NewDashboard;

/// <summary>
/// Hybrid pipeline Step 3 — keyword/lexical retrieval (BM25), see
/// docs/pipeline/HYBRID-ANALYSIS-PIPELINE-PLAN.md. Pure in-process scoring over the run's own
/// internal sections — no external search engine, since the corpus is small enough (a run's
/// attached internal documents, not the whole library) that plain term-frequency math is fast
/// and needs no index of its own. Same "check the shape, no AI call" philosophy as
/// <see cref="LocalDocs.AcronymHarvester"/>.
///
/// <see cref="SelectDynamic"/> is deliberately not a fixed top-N: how many sections come back
/// depends on how strong the match actually is for that clause — a clause with only a handful of
/// genuinely relevant sections returns only those, and a clause matching many strongly relevant
/// sections returns all of them, not an arbitrary round number either way.
/// </summary>
public static partial class Bm25Scorer
{
    private const double K1 = 1.5;
    private const double B = 0.75;

    public sealed record CorpusDoc(Guid SectionId, IReadOnlyDictionary<string, int> TermFreq, int Length);

    /// <summary><paramref name="Stemmed"/> (pipeline v6): words reduced to their root on both sides (see
    /// <see cref="EnglishStemmer"/>), and query words found in more than <see cref="CommonWordShare"/> of the documents
    /// are ignored.</summary>
    public sealed record Corpus(IReadOnlyList<CorpusDoc> Docs, IReadOnlyDictionary<string, int> DocFreq, double AvgDocLength, bool Stemmed = false);

    /// <summary>Stemmed mode only: a query word present in more than this share of the documents carries no signal.</summary>
    public const double CommonWordShare = 0.6;

    /// <summary>Builds the term-frequency corpus once per run — reused across every clause's
    /// query in that run instead of re-tokenizing the same sections per clause.</summary>
    public static Corpus BuildCorpus(IReadOnlyList<(Guid SectionId, string Text)> sections, bool stem = false)
    {
        var docs = new List<CorpusDoc>();
        var docFreq = new Dictionary<string, int>(StringComparer.Ordinal);
        long totalLength = 0;

        foreach (var (id, text) in sections)
        {
            var tokens = Tokenize(text, stem);
            if (tokens.Count == 0) continue;

            var tf = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var t in tokens)
                tf[t] = tf.GetValueOrDefault(t) + 1;
            foreach (var t in tf.Keys)
                docFreq[t] = docFreq.GetValueOrDefault(t) + 1;

            docs.Add(new CorpusDoc(id, tf, tokens.Count));
            totalLength += tokens.Count;
        }

        var avgLen = docs.Count == 0 ? 0 : (double)totalLength / docs.Count;
        return new Corpus(docs, docFreq, avgLen, stem);
    }

    /// <summary>Scores every corpus section against a query, BM25-ranked descending. Sections
    /// scoring 0 (no query term present at all) are dropped — a non-match, not a weak match.</summary>
    public static IReadOnlyList<(Guid SectionId, double Score)> Score(Corpus corpus, string query)
    {
        var queryTerms = Tokenize(query, corpus.Stemmed).Distinct(StringComparer.Ordinal).ToList();
        if (queryTerms.Count == 0 || corpus.Docs.Count == 0) return [];

        var n = corpus.Docs.Count;
        if (corpus.Stemmed)
            queryTerms = queryTerms.Where(t => corpus.DocFreq.GetValueOrDefault(t) <= n * CommonWordShare).ToList();
        var results = new List<(Guid, double)>();
        foreach (var doc in corpus.Docs)
        {
            double score = 0;
            foreach (var term in queryTerms)
            {
                if (!doc.TermFreq.TryGetValue(term, out var f) || f == 0) continue;
                var df = corpus.DocFreq.GetValueOrDefault(term);
                var idf = Math.Log(1 + (n - df + 0.5) / (df + 0.5));
                var denom = f + K1 * (1 - B + B * doc.Length / Math.Max(corpus.AvgDocLength, 1));
                score += idf * (f * (K1 + 1)) / denom;
            }
            if (score > 0) results.Add((doc.SectionId, score));
        }

        return results.OrderByDescending(r => r.Item2).ToList();
    }

    /// <summary>Dynamic cutoff — keeps every section whose score is at least
    /// <paramref name="relativeThreshold"/> of the clause's own best score, instead of a fixed
    /// count: a clause with one strong hit and nothing else close returns just that hit; a clause
    /// with dozens of strongly relevant sections returns all of them, up to
    /// <paramref name="maxKeep"/> as a sanity ceiling, not a target.</summary>
    public static IReadOnlyList<(Guid SectionId, double Score)> SelectDynamic(
        IReadOnlyList<(Guid SectionId, double Score)> ranked,
        double relativeThreshold = 0.5,
        int maxKeep = 300)
    {
        if (ranked.Count == 0) return ranked;
        var top = ranked[0].Score;
        if (top <= 0) return [];
        var cutoff = top * relativeThreshold;
        return ranked.Where(r => r.Score >= cutoff).Take(maxKeep).ToList();
    }

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "the", "and", "for", "of", "to", "in", "on", "at", "by", "or", "a", "an", "is", "are",
        "be", "this", "that", "with", "as", "shall", "must", "should", "not", "any", "all", "its",
    };

    [GeneratedRegex(@"[a-z0-9]{2,}")]
    private static partial Regex Token();

    private static List<string> Tokenize(string text, bool stem = false) =>
        Token().Matches(text.ToLowerInvariant())
            .Select(m => m.Value)
            .Where(t => !StopWords.Contains(t))
            .Select(t => stem ? EnglishStemmer.Stem(t) : t)
            .ToList();
}
