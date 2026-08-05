using System.Text.RegularExpressions;
using Reguliq.Api.Services.LandingAi;

namespace Reguliq.Api.Services.NewDashboard;

/// <summary>Regul.ai post-judgment steps: quote verification, gap_description retry, needs_review.</summary>
public static class NdRegulJudgmentPostProcessor
{
    public const double LowConfidenceThreshold = 0.7;
    public const int MaxGapDescriptionRetries = 2;

    public static RegulJudgmentResult ApplyQuoteVerification(
        RegulJudgmentResult judgment,
        string policySourceText)
    {
        if (judgment.PolicyExtract.Count == 0)
            return judgment;

        var unverified = judgment.PolicyExtract
            .Where(q => !string.IsNullOrWhiteSpace(q) && !VerifyQuote(q, policySourceText))
            .ToList();

        if (unverified.Count == 0)
            return judgment;

        return DowngradeForUnverifiedQuotes(judgment);
    }

    public static bool NeedsReview(RegulJudgmentResult judgment, bool hadUnverifiedQuotes)
    {
        if (judgment.Confidence < LowConfidenceThreshold)
            return true;
        if (hadUnverifiedQuotes)
            return true;
        var status = judgment.OverallStatus.Trim().ToLowerInvariant();
        return status is "partial" or "non_compliant" or "non-compliant" or "noncompliant";
    }

    public static bool RequiresGapDescriptionRetry(RegulJudgmentResult judgment)
    {
        var status = judgment.OverallStatus.Trim().ToLowerInvariant();
        if (status is not ("partial" or "non_compliant" or "non-compliant" or "noncompliant"))
            return false;
        return string.IsNullOrWhiteSpace(judgment.GapDescription);
    }

    public static bool VerifyQuote(string quote, string sourceText)
    {
        var normQuote = NdRegulPolicyContextService.NormalizeForMatching(quote);
        if (normQuote.Length < 8)
            return false;
        var normSource = NdRegulPolicyContextService.NormalizeForMatching(sourceText);
        if (normSource.Contains(normQuote, StringComparison.Ordinal))
            return true;

        // Near-match: allow truncated quotes (min 40 chars of quote prefix)
        if (normQuote.Length >= 40)
        {
            var prefix = normQuote[..40];
            return normSource.Contains(prefix, StringComparison.Ordinal);
        }

        return false;
    }

    /// <summary>
    /// Replace LLM-hallucinated document_reference with page/section resolved from the excerpt that contains policy_extract.
    /// </summary>
    public static RegulJudgmentResult ApplyGroundedDocumentReference(
        RegulJudgmentResult judgment,
        IReadOnlyList<NdRegulPolicyContextService.PolicyChunk> contextChunks,
        IReadOnlyDictionary<string, string> markdownByFile)
    {
        if (judgment.PolicyExtract.Count == 0)
            return judgment;

        var refs = new List<string>();
        foreach (var quote in judgment.PolicyExtract.Where(q => !string.IsNullOrWhiteSpace(q)))
        {
            var grounded = GroundQuoteReference(quote, contextChunks, markdownByFile);
            if (!string.IsNullOrWhiteSpace(grounded))
                refs.Add(grounded);
        }

        if (refs.Count == 0)
            return judgment;

        judgment.DocumentReference = string.Join(
            "\n",
            refs.Distinct(StringComparer.OrdinalIgnoreCase));
        return judgment;
    }

    private static string GroundQuoteReference(
        string quote,
        IReadOnlyList<NdRegulPolicyContextService.PolicyChunk> chunks,
        IReadOnlyDictionary<string, string> markdownByFile)
    {
        var located = LocateQuoteInMarkdown(quote, markdownByFile);
        if (located is { Page: > 0 } or { Section: not null })
            return FormatGroundedReference(located.DocName, located.Section, located.Page);

        var chunk = FindBestMatchingChunk(quote, chunks);
        if (chunk != null && ChunkContainsQuote(quote, chunk.Text))
            return FormatGroundedReference(chunk.SourceDoc, chunk.SectionRef, chunk.SourcePage);

        if (chunk != null)
            return FormatGroundedReference(chunk.SourceDoc, null, chunk.SourcePage);

        return "";
    }

    private sealed record LocatedQuote(string? DocName, string? Section, int? Page);

    private static LocatedQuote LocateQuoteInMarkdown(
        string quote,
        IReadOnlyDictionary<string, string> markdownByFile)
    {
        LocatedQuote? best = null;
        foreach (var kv in markdownByFile)
        {
            if (string.IsNullOrWhiteSpace(kv.Value)) continue;
            var (page, section) = PolicyPageResolver.ResolveQuoteLocation(kv.Value, quote);
            if (page is not > 0 && string.IsNullOrWhiteSpace(section))
                continue;

            var candidate = new LocatedQuote(kv.Key, section, page);
            if (page is > 0 && !string.IsNullOrWhiteSpace(section))
                return candidate;
            best ??= candidate;
        }

        return best ?? new LocatedQuote(null, null, null);
    }

    private static bool ChunkContainsQuote(string quote, string chunkText)
    {
        var normQuote = NdRegulPolicyContextService.NormalizeForMatching(quote);
        if (normQuote.Length < 8) return false;
        var normChunk = NdRegulPolicyContextService.NormalizeForMatching(chunkText);
        if (normChunk.Contains(normQuote, StringComparison.Ordinal)) return true;
        return normQuote.Length >= 40 && normChunk.Contains(normQuote[..40], StringComparison.Ordinal);
    }

    private static NdRegulPolicyContextService.PolicyChunk? FindBestMatchingChunk(
        string quote,
        IReadOnlyList<NdRegulPolicyContextService.PolicyChunk> chunks)
    {
        if (chunks.Count == 0) return null;

        var normQuote = NdRegulPolicyContextService.NormalizeForMatching(quote);
        if (normQuote.Length < 8) return null;

        foreach (var chunk in chunks)
        {
            if (ChunkContainsQuote(quote, chunk.Text))
                return chunk;
        }

        var keywords = ExtractQuoteKeywords(quote);
        if (keywords.Count == 0) return null;

        NdRegulPolicyContextService.PolicyChunk? best = null;
        var bestScore = 0;
        foreach (var chunk in chunks)
        {
            var score = ScoreChunkKeywords(chunk.Text, keywords);
            if (score > bestScore)
            {
                bestScore = score;
                best = chunk;
            }
        }

        return bestScore >= 3 ? best : null;
    }

    private static string FormatGroundedReference(string? docName, string? section, int? page)
    {
        var doc = docName?.Trim() ?? "Internal policy manual";
        var sectionRef = section?.Trim();
        var resolvedPage = page is > 0 ? page : null;

        if (!string.IsNullOrWhiteSpace(sectionRef) && resolvedPage.HasValue)
            return $"{doc} — section {sectionRef}, p.{resolvedPage.Value}";
        if (!string.IsNullOrWhiteSpace(sectionRef))
            return $"{doc} — section {sectionRef}";
        if (resolvedPage.HasValue)
            return $"{doc}, p.{resolvedPage.Value}";
        return doc;
    }

    private static HashSet<string> ExtractQuoteKeywords(string text)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(text, @"[A-Za-z][A-Za-z0-9'-]{2,}"))
        {
            var w = m.Value.ToLowerInvariant();
            if (w.Length >= 4)
                set.Add(NdRegulPolicyContextService.NormalizeForMatching(w));
        }
        return set;
    }

    private static int ScoreChunkKeywords(string text, HashSet<string> keywords)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        var normalized = NdRegulPolicyContextService.NormalizeForMatching(text);
        var score = 0;
        foreach (var kw in keywords)
        {
            if (normalized.Contains(kw, StringComparison.Ordinal))
                score++;
        }
        return score;
    }

    private static RegulJudgmentResult DowngradeForUnverifiedQuotes(RegulJudgmentResult judgment)
    {
        judgment.DesignStatus = CapStatus(judgment.DesignStatus, "partial");
        judgment.OperatingStatus = judgment.DesignStatus;
        judgment.OverallStatus = judgment.DesignStatus;
        if (string.IsNullOrWhiteSpace(judgment.GapDirection))
            judgment.GapDirection = "basis_not_verifiable";
        judgment.Confidence = Math.Min(judgment.Confidence, 0.65);
        return judgment;
    }

    private static string CapStatus(string current, string cap)
    {
        var c = current.Trim().ToLowerInvariant();
        if (c is "non_compliant" or "non-compliant" or "noncompliant")
            return cap;
        if (c is "partial")
            return "partial";
        return cap;
    }
}
