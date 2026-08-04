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
            var chunk = FindBestMatchingChunk(quote, contextChunks);
            var page = ResolvePageForQuote(quote, chunk, markdownByFile);
            var formatted = FormatGroundedReference(chunk, page);
            if (!string.IsNullOrWhiteSpace(formatted))
                refs.Add(formatted);
        }

        if (refs.Count == 0)
            return judgment;

        judgment.DocumentReference = string.Join(
            "; ",
            refs.Distinct(StringComparer.OrdinalIgnoreCase));
        return judgment;
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
            var normChunk = NdRegulPolicyContextService.NormalizeForMatching(chunk.Text);
            if (normChunk.Contains(normQuote, StringComparison.Ordinal))
                return chunk;
            if (normQuote.Length >= 40 && normChunk.Contains(normQuote[..40], StringComparison.Ordinal))
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

        return bestScore >= 2 ? best : null;
    }

    private static int? ResolvePageForQuote(
        string quote,
        NdRegulPolicyContextService.PolicyChunk? chunk,
        IReadOnlyDictionary<string, string> markdownByFile)
    {
        string? markdown = null;
        if (!string.IsNullOrWhiteSpace(chunk?.SourceDoc)
            && markdownByFile.TryGetValue(chunk.SourceDoc, out var byFile))
            markdown = byFile;

        if (!string.IsNullOrWhiteSpace(markdown))
        {
            var fromQuote = PolicyPageResolver.Resolve(markdown, quote);
            if (fromQuote is > 0) return fromQuote;

            var totalPages = PolicyPageResolver.EstimatePageCount(markdown);
            var fromClause = PolicyPageResolver.ResolveGovPointPage(
                markdown,
                chunk?.SectionRef ?? "",
                null,
                null,
                quote,
                chunk?.SourcePage,
                totalPages);
            if (fromClause is > 0) return fromClause;
        }

        return chunk?.SourcePage is > 0 ? chunk.SourcePage : null;
    }

    private static string FormatGroundedReference(
        NdRegulPolicyContextService.PolicyChunk? chunk,
        int? page)
    {
        if (chunk == null) return "";
        var doc = chunk.SourceDoc?.Trim() ?? "Internal policy manual";
        var section = chunk.SectionRef?.Trim();
        var resolvedPage = page is > 0 ? page : chunk.SourcePage is > 0 ? chunk.SourcePage : null;

        if (!string.IsNullOrWhiteSpace(section) && resolvedPage.HasValue)
            return $"{doc} — section {section}, p.{resolvedPage.Value}";
        if (!string.IsNullOrWhiteSpace(section))
            return $"{doc} — section {section}";
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
