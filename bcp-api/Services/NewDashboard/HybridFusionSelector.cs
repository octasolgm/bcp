namespace Reguliq.Api.Services.NewDashboard;

/// <summary>
/// Hybrid pipeline Step 5 + Step 6 (V5 / RegulPipelineHybrid engine only, internal side only —
/// never reads the gov clause itself, only the two match lists Step 3/4 already produced for it).
///
/// Step 5 (fusion): unions the BM25 list and the embedding list into one ranked list per clause,
/// scoring each section as 0.4 * BM25 + 0.6 * embedding (matching the weighting in
/// docs/pipeline/HYBRID-ANALYSIS-PIPELINE-PLAN.md) — a section that scored well on both sides
/// ranks above one that only cleared a single search. Raw BM25 scores and cosine similarities
/// aren't on the same scale, so each list is normalized against its own best score for this
/// clause before combining (best BM25 match -> 1.0, best embedding match -> 1.0) rather than
/// combining the raw numbers directly.
///
/// Step 6 (adaptive select): trims the fused, ranked list with the same dynamic-cutoff principle
/// used everywhere else in this pipeline (see Bm25Scorer.SelectDynamic) — not a fixed count.
/// Sections within RelativeThreshold of the clause's own best fused score are kept; a soft floor
/// and ceiling only guard against the two extremes (a clause with one obvious match still gets a
/// usable handful of context, and one generic clause can't pull in the whole corpus).
/// </summary>
public static class HybridFusionSelector
{
    private const double Bm25Weight = 0.4;
    private const double EmbeddingWeight = 0.6;
    private const double RelativeThreshold = 0.5;
    private const int MinKeep = 5;
    private const int MaxKeep = 60;

    public sealed record FusedMatch(
        Guid SectionId,
        string? ClauseNo,
        string TextPreview,
        Guid SourceDocumentId,
        string? SourceDocumentName,
        int? SourcePage,
        double FusedScore,
        double? Bm25Score,
        double? EmbeddingSimilarity,
        string? MatchedSubObligation);

    public static List<FusedMatch> Fuse(
        IReadOnlyList<RegulEmbeddingRetrievalService.Bm25Match> bm25Matches,
        IReadOnlyList<RegulEmbeddingRetrievalService.RetrievalMatch> embeddingMatches)
    {
        var bestBm25 = bm25Matches.Count == 0 ? 0 : bm25Matches.Max(m => m.Score);
        var bestEmbedding = embeddingMatches.Count == 0 ? 0 : embeddingMatches.Max(m => m.Similarity);

        var bySection = new Dictionary<Guid, (
            string? ClauseNo, string TextPreview, Guid SourceDocumentId, string? SourceDocumentName,
            int? SourcePage, double? Bm25, double? Embedding, string? SubObligation)>();

        foreach (var m in bm25Matches)
        {
            bySection[m.SectionId] = (m.ClauseNo, m.TextPreview, m.SourceDocumentId, m.SourceDocumentName,
                m.SourcePage, m.Score, null, m.MatchedSubObligation);
        }
        foreach (var m in embeddingMatches)
        {
            if (bySection.TryGetValue(m.SectionId, out var existing))
            {
                bySection[m.SectionId] = existing with
                {
                    Embedding = m.Similarity,
                    // Prefer whichever side's sub-obligation tag is present; if both, keep BM25's
                    // (arbitrary but stable — the two searches usually agree on which sub-obligation
                    // drove the match anyway).
                    SubObligation = existing.SubObligation ?? m.MatchedSubObligation,
                };
            }
            else
            {
                bySection[m.SectionId] = (m.ClauseNo, m.TextPreview, m.SourceDocumentId, m.SourceDocumentName,
                    m.SourcePage, null, m.Similarity, m.MatchedSubObligation);
            }
        }

        var fused = bySection.Select(kv =>
        {
            var (clauseNo, preview, docId, docName, page, bm25, embedding, subObligation) = kv.Value;
            var normBm25 = bestBm25 > 0 ? (bm25 ?? 0) / bestBm25 : 0;
            var normEmbedding = bestEmbedding > 0 ? (embedding ?? 0) / bestEmbedding : 0;
            var score = Bm25Weight * normBm25 + EmbeddingWeight * normEmbedding;
            return new FusedMatch(kv.Key, clauseNo, preview, docId, docName, page,
                Math.Round(score, 4), bm25, embedding, subObligation);
        }).ToList();

        fused.Sort((a, b) => b.FusedScore.CompareTo(a.FusedScore));
        return fused;
    }

    /// <summary>Not a fixed count — see class doc comment. MinKeep/MaxKeep are a soft envelope, not
    /// a target: a clause with only 2 real matches still returns 2, and one with 40 equally strong
    /// matches returns up to MaxKeep rather than an arbitrary round number.</summary>
    public static List<FusedMatch> SelectDynamic(List<FusedMatch> ranked)
    {
        if (ranked.Count <= MinKeep) return ranked;

        var best = ranked[0].FusedScore;
        var cutoff = best * RelativeThreshold;
        var selected = ranked.Where(m => m.FusedScore >= cutoff).ToList();

        if (selected.Count < MinKeep) selected = ranked.Take(MinKeep).ToList();
        if (selected.Count > MaxKeep) selected = selected.Take(MaxKeep).ToList();
        return selected;
    }

    // ---------------------------------------------------------------- pipeline v3

    /// <summary>Standard reciprocal-rank-fusion constant: rank 1 adds 1/61, rank 10 adds 1/70.</summary>
    private const double RankConstant = 60;

    /// <summary>How far above the documents' average a section must score for one query to count as relevant,
    /// in standard deviations of that query's scores over every section. A statistical cut, not a count:
    /// it follows each query's own score spread, which also makes it work for any embedding model.</summary>
    public const double RelevanceZ = 2.5;

    public static double RankScore(int zeroBasedRank) => 1.0 / (RankConstant + zeroBasedRank + 1);

    /// <summary>
    /// v3 relevance gate for one query's scores (BM25 or embedding similarity), best first. A section is kept
    /// when its score is at least <see cref="RelevanceZ"/> standard deviations above the mean score of all
    /// <paramref name="populationSize"/> sections (sections missing from <paramref name="ranked"/> scored 0).
    /// The best section is always kept when it scored anything. No minimum or maximum count.
    /// </summary>
    public static List<(Guid SectionId, double Score)> SelectRelevant(
        IReadOnlyList<(Guid SectionId, double Score)> ranked, int populationSize)
    {
        var ordered = ranked.Where(r => r.Score > 0).OrderByDescending(r => r.Score).ToList();
        if (ordered.Count == 0) return [];

        var n = Math.Max(populationSize, ordered.Count);
        var mean = ordered.Sum(r => r.Score) / n;
        var variance = (ordered.Sum(r => (r.Score - mean) * (r.Score - mean))
            + (n - ordered.Count) * mean * mean) / n;
        var std = Math.Sqrt(variance);
        if (std <= 0) return [ordered[0]];

        var cutoff = mean + RelevanceZ * std;
        var selected = ordered.Where(r => r.Score >= cutoff).ToList();
        return selected.Count > 0 ? selected : [ordered[0]];
    }

    /// <summary>
    /// v3 Steps 5 + 6: every section that passed the relevance gate for at least one query, ordered by its
    /// summed reciprocal-rank score (<paramref name="rankScores"/>), so a section found only by keywords or only
    /// by meaning is never dropped for lacking the other side. FusedScore is the rank score scaled so the
    /// best section is 1.0. Nothing is trimmed.
    /// </summary>
    public static List<FusedMatch> FuseByRank(
        IReadOnlyList<RegulEmbeddingRetrievalService.Bm25Match> bm25Matches,
        IReadOnlyList<RegulEmbeddingRetrievalService.RetrievalMatch> embeddingMatches,
        IReadOnlyDictionary<Guid, double> rankScores)
    {
        var bm25ById = bm25Matches.ToDictionary(m => m.SectionId);
        var embeddingById = embeddingMatches.ToDictionary(m => m.SectionId);
        var best = rankScores.Count == 0 ? 0 : rankScores.Values.Max();

        var fused = new List<FusedMatch>();
        foreach (var id in bm25ById.Keys.Union(embeddingById.Keys))
        {
            bm25ById.TryGetValue(id, out var bm25);
            embeddingById.TryGetValue(id, out var embedding);
            var score = best > 0 ? rankScores.GetValueOrDefault(id) / best : 0;
            fused.Add(new FusedMatch(
                id,
                bm25?.ClauseNo ?? embedding!.ClauseNo,
                bm25?.TextPreview ?? embedding!.TextPreview,
                bm25?.SourceDocumentId ?? embedding!.SourceDocumentId,
                bm25?.SourceDocumentName ?? embedding!.SourceDocumentName,
                bm25?.SourcePage ?? embedding!.SourcePage,
                Math.Round(score, 4),
                bm25?.Score,
                embedding?.Similarity,
                bm25?.MatchedSubObligation ?? embedding?.MatchedSubObligation));
        }

        fused.Sort((a, b) => b.FusedScore.CompareTo(a.FusedScore));
        return fused;
    }
}
