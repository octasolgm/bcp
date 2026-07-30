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
